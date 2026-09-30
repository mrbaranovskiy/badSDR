using System.Text;
using System.Diagnostics;
using IqChannelizer.Abstractions;
using static BadSdr.Core.Sources.UhdNative;

namespace BadSdr.Core.Sources;

// Native transport only. UsrpSource adds bounded recovery when UHD stops delivering IQ.
internal sealed class UhdSource : IIqSource
{
    private IntPtr device, streamer, metadata;
    private bool started, gapPending;
    public SourceInfo Info { get; }

    public static IReadOnlyList<string> Discover(string args = "")
    {
        int error = uhd_string_vector_make(out var vector);
        if (error != 0) throw new InvalidOperationException($"UHD discovery initialization failed ({error}).");
        try
        {
            error = uhd_usrp_find(args, ref vector);
            if (error != 0) throw new InvalidOperationException($"UHD discovery failed ({error}).");
            if (uhd_string_vector_size(vector, out var count) != 0)
                throw new InvalidOperationException("Cannot read UHD discovery results.");
            var result = new List<string>();
            for (nuint i = 0; i < count; i++)
            {
                var value = new StringBuilder(4096);
                if (uhd_string_vector_at(vector, i, value, 4096) != 0)
                    throw new InvalidOperationException("Cannot read a UHD device address.");
                result.Add(value.ToString());
            }
            return result;
        }
        finally { uhd_string_vector_free(ref vector); }
    }

    public unsafe UhdSource(RadioSettings settings)
    {
        try
        {
            Check(uhd_usrp_make(out device, settings.DeviceArguments), "Open USRP");
            nuint channel = settings.RxChannel;
            Check(uhd_usrp_set_rx_rate(device, settings.SampleRate, channel), "Set sample rate");
            var tune = new TuneRequest { Target = settings.CenterFrequencyHz, RfPolicy = 65, DspPolicy = 65, Args = "" };
            Check(uhd_usrp_set_rx_freq(device, ref tune, channel, out _), "Tune");
            Check(uhd_usrp_set_rx_gain(device, settings.GainDb, channel, ""), "Set gain");
            Check(uhd_usrp_set_rx_bandwidth(device, settings.BandwidthHz, channel), "Set bandwidth");
            if (!string.IsNullOrWhiteSpace(settings.Antenna))
                Check(uhd_usrp_set_rx_antenna(device, settings.Antenna, channel), "Select antenna");
            Check(uhd_usrp_get_rx_rate(device, channel, out var rate), "Read actual sample rate");
            Check(uhd_usrp_get_rx_freq(device, channel, out var frequency), "Read actual frequency");
            Info = new(rate, frequency, $"USRP · RX {channel} · {settings.WireFormat} · {settings.DeviceArguments}");
            Check(uhd_rx_streamer_make(out streamer), "Create RX streamer");
            var args = new StreamArgs { CpuFormat = "fc32", WireFormat = settings.WireFormat, Args = "", Channels = (IntPtr)(&channel), Count = 1 };
            Check(uhd_usrp_get_rx_stream(device, ref args, streamer), "Configure RX stream");
            Check(uhd_rx_metadata_make(out metadata), "Create RX metadata");
        }
        catch { Dispose(); throw; }
    }

    public void Start()
    {
        var command = new StreamCommand { Mode = 97, Now = true };
        Check(uhd_rx_streamer_issue_stream_cmd(streamer, ref command), "Start continuous RX", true);
        started = true;
    }

    public unsafe SourceRead Read(Span<ComplexF> destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        nuint received;
        fixed (ComplexF* pointer = destination)
        {
            void* buffer = pointer;
            Check(uhd_rx_streamer_recv(streamer, &buffer, (nuint)destination.Length, ref metadata, .1, false, out received), "Receive", true);
        }
        Check(uhd_rx_metadata_error_code(metadata, out var error), "Read RX metadata");
        if (error is not (0 or 1 or 8)) throw new IOException($"UHD RX metadata error 0x{error:X}.");
        bool discontinuity = gapPending;
        if (received > 0) gapPending = false;
        // Overflow counts describe device events, not a known number of missing samples.
        // Conservatively reset on both sides if UHD returned samples with the overflow.
        if (error == 8) { discontinuity = true; gapPending = true; }
        return new(checked((int)received), Discontinuity: discontinuity, Overflow: error == 8, Timeout: error == 1);
    }

    public unsafe void Stop()
    {
        if (!started) return;
        started = false;
        var command = new StreamCommand { Mode = 111, Now = true };
        Check(uhd_rx_streamer_issue_stream_cmd(streamer, ref command), "Stop continuous RX", true);
        // Clear device/transport buffers before a later Start, so a channel rebuild cannot
        // label pre-stop IQ as a new capture. Only this producer owns the streamer here.
        var scratch = new ComplexF[4096];
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        fixed (ComplexF* pointer = scratch)
        {
            while (Stopwatch.GetTimestamp() < deadline)
            {
                void* buffer = pointer;
                Check(uhd_rx_streamer_recv(streamer, &buffer, (nuint)scratch.Length, ref metadata, .01, false, out var count), "Drain stopped RX", true);
                Check(uhd_rx_metadata_error_code(metadata, out var error), "Read drain metadata");
                Check(uhd_rx_metadata_end_of_burst(metadata, out var ended), "Read burst end");
                if (ended || (count == 0 && error == 1)) { gapPending = false; return; }
                if (error is not (0 or 1 or 8)) throw new IOException($"UHD drain metadata error 0x{error:X}.");
            }
        }
        throw new IOException("UHD continued returning samples for more than one second after stop.");
    }

    private void Check(int error, string operation, bool stream = false)
    {
        if (error == 0) return;
        var message = new StringBuilder(2048);
        if (stream && streamer != IntPtr.Zero) uhd_rx_streamer_last_error(streamer, message, 2048);
        else if (device != IntPtr.Zero) uhd_usrp_last_error(device, message, 2048);
        throw new IOException($"{operation}: UHD error {error}. {message}");
    }

    public void Dispose()
    {
        try { Stop(); }
        finally
        {
            if (metadata != IntPtr.Zero) uhd_rx_metadata_free(ref metadata);
            if (streamer != IntPtr.Zero) uhd_rx_streamer_free(ref streamer);
            if (device != IntPtr.Zero) uhd_usrp_free(ref device);
        }
    }
}
