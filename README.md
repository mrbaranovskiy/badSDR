# badSDR

A small Avalonia desktop receiver for exercising **IqChannelizer** and **Dmr** with live USRP IQ. It shows the spectrum, lets you add and drag listening channels, and stores DMR signalling and call sessions in SQLite. Voice playback and decryption are outside this application's scope.

## Build and run

Windows x64 is the tested target. Install the .NET 10 SDK; this workspace uses `10.0.400`.

Keep the repositories in this layout:

```text
source/
  badSDR/
  dmr/src/Dmr/Dmr.csproj
  iq-channelizer/src/IqChannelizer/IqChannelizer.csproj
```

```powershell
dotnet build BadSdr.slnx -c Release
dotnet run --project src/BadSdr.App -c Release
```

To publish a runnable folder (requires the .NET 10 runtime):

```powershell
dotnet publish src/BadSdr.App -c Release -r win-x64 --self-contained false -o artifacts/publish
./artifacts/publish/BadSdr.App.exe
```

Avalonia and SQLite packages restore from NuGet. Both DSP libraries are **project references**, so changes in the sibling repositories are compiled into the app. Their source files are not modified. The channelizer project copies its native DSP/FFT runtimes into the output directory.

### USRP setup

Install a compatible 64-bit [UHD runtime](https://files.ettus.com/manual/page_install.html), the firmware/FPGA images for the device, and its USB driver if applicable. The app loads `uhd.dll` beside the executable or from `C:\Program Files\UHD\bin`; normal native library resolution is the final fallback. A custom installation can be selected **before starting the app**:

```powershell
$env:UHD_LIBRARY_PATH = 'C:\your-uhd\bin\uhd.dll'
dotnet run --project src/BadSdr.App -c Release
```

**USB runtime on this workstation:** the installed UHD 4.1.0 package includes libusb 1.0.22, which caused a native access violation during .NET discovery. libusb 1.0.29 from `../LibUSB/VS2022/MS64/dll/libusb-1.0.dll` passed the same probe. When that SDK path exists, the build copies its DLL beside the app. No system files are replaced. Elsewhere, use a current compatible x64 libusb from the [libusb project](https://github.com/libusb/libusb/releases), supply it with `-p:BadSdrLibUsbPath=C:\path\libusb-1.0.dll` at build time, or set:

```powershell
$env:UHD_LIBUSB_PATH = 'C:\your-libusb\libusb-1.0.dll'
```

An explicitly configured missing library is an error. The known crashing libusb 1.0.22 deployment is rejected with a setup message. Restart the app after changing native runtime paths.

## Workflow

1. Choose **Usrp**, enter device arguments (for example `type=b200` or `addr=192.168.10.2`), and optionally use **Discover USRP devices**.
2. Set centre frequency, sample rate, gain, analog bandwidth, antenna, and the zero-based RX channel. An empty antenna field leaves the device default. **Connect / apply settings** opens/configures the source and displays its actual frequency and sample rate. **Start receiver** can connect directly as well.
3. Add frequencies in the channel panel, or double-click the spectrum. Starting with zero channels shows the spectrum without running DMR decoders.
4. Drag a channel marker to retune. Changes commit on pointer release. Right-click a marker, or select its list entry and press **Remove selected**, to remove it. Tuning snaps to 125 Hz; hold Shift for unsnapped pointer tuning. The mouse wheel zooms around the pointer.
5. Add/remove/retune works before or during capture. While running, the app stops the IQ source, drains accepted buffers, closes current calls with a discontinuity, rebuilds the channelizer, and restarts. A failed plan leaves reception stopped with an error so you can correct the configuration. Applying tuner settings during reception follows the same stop/restart sequence.
6. Sessions appear in the shared grid. Select a row and expand **raw metadata** to inspect vendor fields, raw LC, provenance and privacy parameters. Stop flushes pending database writes. Closing the window stops capture and drains storage.

The current grid shows the latest 500 sessions, including history from previous runs. It is read-only; all saved history remains in SQLite. Channel and tuner settings are restored at the next launch. Reception does not start automatically.

### Try it without hardware

- **Replay → DMR fixture** loads `../dmr/dmr_test.wav`, configures its 54,688 S/s input and −12.5 kHz DMR channel, and selects Direct processing. Press Start. The supplied recording produces a session with CC 15, 216 voice frames and `unknown_vendor` privacy; source/destination and absolute slot remain unknown.
- Browse to a stereo I/Q WAV (PCM16/24/32 or float32) or raw `cf32`, `cs16`, `cs32`, `cu8`. Raw input uses the entered sample rate; WAV uses the header. Uncheck real-time replay to stress the bounded pipeline: fast replay can deliberately drop samples.
- **Synthetic** supplies two tones and noise for spectrum/pipeline checks. It does not generate DMR calls.

Replay restarts from the beginning after EOF. Rebuilding listening channels during an active replay resumes from the current file position.

## Processing and performance

```text
USRP / file / synthetic source
  → dedicated acquisition thread, pooled complex-float buffers
  → bounded Channel<IQ block>, TryWrite, drop incoming blocks when full
  → one dedicated processing thread
      → exact [history | chunk] adapter → IqChannelizer → DmrReceiver per channel
      → small lossy spectrum mailbox → separate FFT worker → UI at 5 Hz
      → bounded Channel<signalling event> → SQLite writer → UI queries at 1 Hz
```

No database operations, rendering, decoding, or FFT planning run on the acquisition thread. UHD writes `fc32` directly into pinned pooled arrays; the wire format is `sc16`. A dropped block's buffer is reused. Sample indices and generation markers survive rejected blocks, so neither library joins unrelated RF intervals. Channelizer resets clear retained history and partial input. The conversion to `System.Numerics.Complex` happens only at the DMR boundary using reusable arrays.

**Routing modes**

- **Auto:** try the channelizer; if creation is unsupported and input is 24–384 kS/s, use independent DMR receivers on the original stream. The displayed plan and tooltip report the fallback reason.
- **Channelizer:** require the selected strategy/backend. Each channel requests a 12.5 kHz passband and exact 48 kS/s output. Block geometry and variable output lengths come from the resolved plan. FFT estimate planning keeps rebuild latency short. CPU, CUDA, Intel GPU and AMD GPU choices are forwarded to the library; optional GPU support needs its native helper and vendor runtime.
- **Direct:** pass the input to each receiver with its channel offset. Limited to 24–384 kS/s by the DMR library. Useful as a comparison baseline and when native channelization is unavailable. It repeats front-end work per channel.

**Metrics**

- Application IQ loss: cumulative dropped / received sample pairs; the detail shows samples dropped per second. Dropping never blocks the tuner. The hover text gives loss over the latest display interval.
- UHD overflows: hardware overflow/sequence error **events**. Their missing sample count is unknown and is not included in the percentage. UHD timeouts are counted separately. Any overflow resets downstream state.
- Processing rate: consumed input sample pairs / processing wall time. Its real-time multiple divides that capacity by the actual input rate. This measures average DSP capacity, not total CPU usage or a guarantee against overruns.
- Channelizer and DMR timings: cumulative wall time in each library, with decoder callback time removed from the channelizer total. Rebuild/planning and database time are outside these counters.
- Queue occupancy, SQLite backlog, saved signalling-event count, and discarded partial channelizer input (`tail`) are visible. Partial input at a discontinuity/EOF is discarded, never padded with invented samples. Tail samples are separate from queue drops; they have already reached the processing worker.

Counters restart when a processing run is rebuilt. At the end of each run, its settings, channels, plan description, counters and any error are saved in the `runs` table. Hardware gaps and deliberate stop/rebuild intervals have unknown lengths; timestamps across those gaps are approximate. RF times within each DMR capture remain relative to that capture's input.

## Local data

```text
%LOCALAPPDATA%\BadSdr\settings.json
%LOCALAPPDATA%\BadSdr\sessions.db
```

- `sessions`: one row per decoded call, keyed by the unique run/channel/decoder session ID.
- `events`: full JSON signalling events, channel/frequency and observation time. High-volume `vocoder_frame`, `burst` and `symbols` payloads are excluded; this app logs metadata. Voice-frame totals come from the decoder's call-end event.
- `runs`: configuration, selected plan, final performance counters and failure details.

The single writer uses transactions (up to 256 queued items per batch) and SQLite WAL with `synchronous=NORMAL`. A successful Stop/close drains and commits the queue; sudden power loss has SQLite NORMAL's durability limits. If the bounded metadata queue fills or SQLite fails, capture stops with an error rather than silently losing signalling. A file lease prevents two app instances from owning the same database. On reopening after an interrupted process, previously Active sessions are marked Interrupted.

The grid uses the run start plus RF-relative time, not a timestamp recovered from a WAV recording. `? (track 0/1)` is a relative TDMA track, not an inferred absolute slot. The app preserves the DMR decoder's unknown IDs and vendor-specific privacy status.

## Validation

```powershell
dotnet run --project tests/BadSdr.Tests -c Release
dotnet run --project tests/BadSdr.Tests -c Release -- --uhd-probe
dotnet run --project src/BadSdr.App -c Release -- --smoke-test --output artifacts/ui-smoke
```

The console harness returns a failing exit code on any failed test. It checks fixture decoding and SQLite reopening, two-channel wideband extraction, exact overflow accounting and buffer ownership, discontinuities, cancellation, errors, live rebuilds, null metadata identity, database backpressure/failure, and invalid configurations. The headless Avalonia check uses the real Skia renderer, replays the fixture through the complete app, operates channel controls, and saves desktop/compact screenshots plus a result file. Tests use separate databases under `artifacts`; they do not change normal app data.

Validated here: Release build with zero warnings; **14 core tests passed**; UI replay and channel interaction check passed; two wideband channels each recovered **216 frames**. UHD discovery completed with the app-local USB runtime and found no attached device. Actual USRP streaming, hardware overflow behavior, sustained high-rate load, and GPU execution still need validation on the target equipment.

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) before redistributing a published folder.
