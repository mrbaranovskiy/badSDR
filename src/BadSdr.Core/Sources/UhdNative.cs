using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;

namespace BadSdr.Core.Sources;

internal static class UhdNative
{
    private const string Library = "uhd";
    private static readonly Lazy<IntPtr> LoadedLibrary = new(Load);
    static UhdNative() => NativeLibrary.SetDllImportResolver(typeof(UhdNative).Assembly, Resolve);

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library) return IntPtr.Zero;
        return LoadedLibrary.Value;
    }

    private static IntPtr Load()
    {
        bool localUsb = false;
        if (OperatingSystem.IsWindows())
        {
            var usbPath = Environment.GetEnvironmentVariable("UHD_LIBUSB_PATH");
            if (!string.IsNullOrWhiteSpace(usbPath) && !File.Exists(usbPath))
                throw new FileNotFoundException("UHD_LIBUSB_PATH must name an existing 64-bit libusb DLL.", usbPath);
            if (string.IsNullOrWhiteSpace(usbPath)) usbPath = Path.Combine(AppContext.BaseDirectory, "libusb-1.0.dll");
            if (File.Exists(usbPath))
            {
                CheckUsbVersion(usbPath);
                if (LoadLibraryExW(Path.GetFullPath(usbPath), IntPtr.Zero, 0x100 | 0x1000) == IntPtr.Zero)
                    throw new DllNotFoundException($"Cannot load the configured 64-bit libusb runtime: {usbPath}");
                localUsb = true;
            }
        }
        var configured = Environment.GetEnvironmentVariable("UHD_LIBRARY_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && !File.Exists(configured))
            throw new FileNotFoundException("UHD_LIBRARY_PATH must name an existing native UHD library.", configured);
        var paths = !string.IsNullOrWhiteSpace(configured) ? new[] { configured } : new[] { Path.Combine(AppContext.BaseDirectory, "uhd.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "UHD", "bin", "uhd.dll") };
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            if (OperatingSystem.IsWindows())
            {
                if (!localUsb) CheckUsbVersion(Path.Combine(Path.GetDirectoryName(path)!, "libusb-1.0.dll"));
                var handle = LoadLibraryExW(Path.GetFullPath(path), IntPtr.Zero, 0x100 | 0x1000);
                if (handle != IntPtr.Zero) return handle;
            }
            else if (NativeLibrary.TryLoad(path, out var handle)) return handle;
        }
        if (!string.IsNullOrWhiteSpace(configured))
            throw new DllNotFoundException($"Cannot load UHD_LIBRARY_PATH '{configured}'. Check its x64 architecture and dependent libraries.");
        if (NativeLibrary.TryLoad(Library, typeof(UhdNative).Assembly, null, out var fallback)) return fallback;
        throw new DllNotFoundException("UHD could not be loaded. Install 64-bit UHD, or set UHD_LIBRARY_PATH to its native library. On Windows its dependent DLLs must be alongside uhd.dll.");
    }

    private static void CheckUsbVersion(string path)
    {
        if (!File.Exists(path)) return;
        var version = FileVersionInfo.GetVersionInfo(path);
        if (version.FileMajorPart == 1 && version.FileMinorPart == 0 && version.FileBuildPart == 22)
            throw new NotSupportedException("This UHD installation uses libusb 1.0.22, which crashed during .NET device discovery on this host. Supply a newer x64 libusb (validated with 1.0.29) beside the app or through UHD_LIBUSB_PATH. See README.md.");
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    internal struct StreamArgs
    {
        [MarshalAs(UnmanagedType.LPStr)] public string CpuFormat;
        [MarshalAs(UnmanagedType.LPStr)] public string WireFormat;
        [MarshalAs(UnmanagedType.LPStr)] public string Args;
        public IntPtr Channels;
        public int Count;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct StreamCommand
    {
        public int Mode;
        public nuint Samples;
        [MarshalAs(UnmanagedType.I1)] public bool Now;
        public long Seconds;
        public double Fraction;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct TuneRequest
    {
        public double Target;
        public int RfPolicy;
        public double RfFrequency;
        public int DspPolicy;
        public double DspFrequency;
        [MarshalAs(UnmanagedType.LPStr)] public string Args;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct TuneResult
    {
        public double ClippedRf, TargetRf, ActualRf, TargetDsp, ActualDsp;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int uhd_usrp_make(out IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string args);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int uhd_usrp_free(ref IntPtr handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int uhd_usrp_find([MarshalAs(UnmanagedType.LPStr)] string args, ref IntPtr vector);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int uhd_string_vector_make(out IntPtr handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int uhd_string_vector_free(ref IntPtr handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int uhd_string_vector_size(IntPtr handle, out nuint size);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int uhd_string_vector_at(IntPtr handle, nuint index, StringBuilder value, nuint capacity);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int uhd_usrp_last_error(IntPtr handle, StringBuilder error, nuint capacity);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int uhd_rx_streamer_last_error(IntPtr handle, StringBuilder error, nuint capacity);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int uhd_usrp_set_rx_rate(IntPtr handle, double rate, nuint channel);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int uhd_usrp_get_rx_rate(IntPtr handle, nuint channel, out double rate);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int uhd_usrp_set_rx_freq(IntPtr handle, ref TuneRequest request, nuint channel, out TuneResult result);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int uhd_usrp_get_rx_freq(IntPtr handle, nuint channel, out double frequency);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int uhd_usrp_set_rx_gain(IntPtr handle, double gain, nuint channel, [MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int uhd_usrp_set_rx_bandwidth(IntPtr handle, double bandwidth, nuint channel);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int uhd_usrp_set_rx_antenna(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string antenna, nuint channel);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int uhd_rx_streamer_make(out IntPtr handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int uhd_rx_streamer_free(ref IntPtr handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int uhd_usrp_get_rx_stream(IntPtr handle, ref StreamArgs args, IntPtr streamer);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int uhd_rx_metadata_make(out IntPtr handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int uhd_rx_metadata_free(ref IntPtr handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int uhd_rx_metadata_error_code(IntPtr handle, out int error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int uhd_rx_metadata_end_of_burst(IntPtr handle, [MarshalAs(UnmanagedType.I1)] out bool endOfBurst);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int uhd_rx_streamer_issue_stream_cmd(IntPtr handle, ref StreamCommand command);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern unsafe int uhd_rx_streamer_recv(IntPtr handle, void** buffers, nuint samples,
        ref IntPtr metadata, double timeout, [MarshalAs(UnmanagedType.I1)] bool onePacket, out nuint received);
}
