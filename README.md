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

Avalonia and SQLite packages restore from NuGet. Both DSP libraries are **project references**, so changes in the sibling repositories are compiled into the app. The channelizer project copies its native DSP/FFT runtimes into the output directory.

The UI can run in **Debug**, while the Core DSP loops remain optimized and the Dmr/IqChannelizer dependencies build in **Release**. This applies to both solution builds and `dotnet run --project`. The status bar reports the loaded DSP assemblies' optimization flags, and run records save their build configurations. Run without an attached debugger when comparing throughput.

The app and performance harness also disable tiered JIT compilation, so live DSP starts with optimized code instead of briefly running the slower initial tier. This trades some startup compilation work and dynamic PGO for predictable initial throughput. The setting is local to these executables; standalone library builds keep their own runtime settings. See [.NET's compilation settings](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/compilation).

To step through unoptimized DSP code, build the app project directly with `-c Debug -p:BadSdrDspConfiguration=Debug`. The solution's Debug configuration deliberately maps both library projects to Release. The optimization regression check will fail when DSP optimization is disabled.

This workspace also includes a performance fix in the sibling **dmr** repository: `DmrReceiver.Discontinuity` clears existing DSP state instead of rebuilding filters and buffers. Keep that change with the app; otherwise frequent gaps can cause a recovery/drop feedback loop. See the [performance investigation](docs/performance-2026-09-26.md).

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
   **USRP link format** defaults to `sc16`. Optional `sc8` halves USB/network sample traffic, at the cost of dynamic range. UHD still supplies complex floats to both DSP libraries. At 40 MS/s the sample payload is 160 MB/s with `sc16`, or 80 MB/s with `sc8`, before protocol overhead. See the [40 MS/s investigation](docs/rate40-investigation.md) for measurements on this host.
   On this B210/Windows host, 40 MS/s with 50 channels passed a 20-second live UI test using `sc8` and `num_recv_frames=512` appended to the UHD device arguments. Leave `recv_frame_size` at its default for that configuration.
3. Add frequencies in the channel panel, or double-click the spectrum. Starting with zero channels shows the spectrum without running DMR decoders.
4. Drag a channel marker to retune. Changes commit on pointer release. Right-click a marker, or select its list entry and press **Remove selected**, to remove it. Tuning snaps to 125 Hz; hold Shift for unsnapped pointer tuning. The mouse wheel zooms around the pointer.
5. Add/remove/retune works before or during capture. While running, the app stops the IQ source, drains accepted buffers, closes current calls with a discontinuity, rebuilds the channelizer, and restarts. A failed plan leaves reception stopped with an error so you can correct the configuration. Applying tuner settings during reception follows the same stop/restart sequence.
6. Sessions appear in the shared grid. Select a row and expand **raw metadata** to inspect vendor fields, raw LC, provenance and privacy parameters. Stop flushes pending database writes. Closing the window stops capture and drains storage.

The waterfall below the spectrum follows its frequency range and mouse-wheel zoom. Newest rows appear at the top; the fixed colour scale covers −110 to −20 dBFS. It retains 256 display frames (about 51 seconds at the UI's 5 Hz refresh), pauses when no new FFT frames arrive, and clears on Start, Connect/apply, or an input frequency/sample-rate change. It uses the existing FFT mailbox and a bounded bitmap ring; no additional work runs on the IQ or DMR threads. This is display history, so gaps and accelerated replay do not represent a continuous RF time axis.

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
  → dedicated channelizer/dispatch thread
      → exact [history | chunk] adapter → IqChannelizer
      → bounded FIFO per channel → one dedicated DmrReceiver worker per channel
          → shared bounded Channel<signalling event> → SQLite writer → UI queries at 1 Hz
      → small lossy spectrum mailbox → separate FFT worker → UI at 5 Hz
```

No database operations, rendering, decoding, or FFT planning run on the acquisition thread. UHD writes `fc32` directly into pinned pooled arrays; the wire format defaults to `sc16`, with optional `sc8`. The first pooled input buffer is acquired before starting the radio. A dropped block's buffer is reused. Sample indices and generation markers survive rejected blocks, so neither library joins unrelated RF intervals. Channelizer resets clear retained history and partial input. The conversion to `System.Numerics.Complex` happens only at the DMR boundary using reusable arrays.

**DMR receivers run concurrently**, each on its own dedicated thread. Channelization overlaps decoding. Borrowed channelizer spans are copied into reusable staging buffers, then distributed in turns into pooled batches of about 10 ms. Each receiver has at most four queued batches, one active batch and one partial batch. Direct mode uses the same parallel workers. This keeps buffer ownership explicit and avoids a task allocation per tiny channelizer output.

If a decoder falls behind, its bounded queue backpressures dispatch; the acquisition thread continues reading and the existing IQ queue accounts for overload drops. Gap markers stay in each receiver's FIFO after preceding samples. Stop/rebuild flushes partial decoder batches, joins every worker, closes sessions and then flushes SQLite. A worker failure cancels acquisition immediately, unblocks pending queue writers, and preserves the original error. Event callbacks may run concurrently across channels; each receiver's event order is preserved. Custom `DmrProcessor` sinks must be thread-safe.

**Routing modes**

- **Auto:** try the channelizer; if creation is unsupported and input is 24–384 kS/s, use independent DMR receivers on the original stream. The displayed plan and tooltip report the fallback reason.
- **Channelizer:** require the selected strategy/backend. Each channel requests a 12.5 kHz passband and exact 48 kS/s output. Block geometry and variable output lengths come from the resolved plan. FFT estimate planning keeps rebuild latency short. CPU, CUDA, Intel GPU and AMD GPU choices are forwarded to the library; optional GPU support needs its native helper and vendor runtime.
- **Direct:** pass the input to each receiver with its channel offset. Limited to 24–384 kS/s by the DMR library. Useful as a comparison baseline and when native channelization is unavailable. It repeats front-end work per channel.

**Metrics**

- Application IQ loss: cumulative dropped / received sample pairs; the detail shows samples dropped per second. Dropping never blocks the tuner. The hover text gives loss over the latest display interval.
- UHD overflows: hardware overflow/sequence error **events**. Their missing sample count is unknown and is not included in the percentage. The detail counts timeouts and automatic **RX restarts** separately. Any overflow marks a downstream discontinuity.
- Processing rate: an estimate using consumed input sample pairs divided by the larger of channelizer/dispatch wall time (including backpressure and final drain) and the busiest receiver's cumulative work time. It accounts for overlapping stages rather than adding parallel worker times. It is not a measured maximum sustainable sample rate; assess actual IQ loss and UHD overflows alongside it.
- The **RX … MS/s** detail is the actual receive rate over the latest display interval. It falls when UHD stops delivering IQ, even if the estimated processing capacity remains high.
- Channelizer timing excludes output staging callbacks. Dispatch includes staging, queueing, and any decoder backpressure. DMR timing is the **sum** of independent worker times and can exceed elapsed wall time. Hover for the busiest worker, peak overlapping workers, decoder queue peak, backpressure wait, recovery/close time, final drain wait, longest dispatch block, process-wide managed allocations and generation-2 collections. Initial planning and asynchronous database writes are outside these counters.
- DMR's per-channel real-time factor uses the sum of RF seconds actually delivered to all receivers divided by their summed decoding time. Receivers retain independent state and process their own samples sequentially, while different receivers run in parallel.
- Queue occupancy and peak occupancy, SQLite backlog, saved signalling-event count, and discarded partial channelizer input (`tail`) are visible. Hover over the queue to see its capacity in milliseconds of RF. Partial input at a discontinuity/EOF is discarded, never padded with invented samples. Tail samples are separate from queue drops; they have already reached the processing worker.

At 10 MS/s, eight 32,768-sample blocks buffer only **26.2 ms**. Average processing throughput can exceed the input rate while short driver/scheduling stalls still overflow this queue. Use 32 blocks (104.9 ms, 8 MiB of queued IQ) for more headroom at this rate. The queue remains bounded and acquisition still drops incoming blocks when full. Saved queue settings are preserved.

Counters restart when a processing run is rebuilt. At the end of each run, its settings, channels, plan description, counters and any error are saved in the `runs` table. Hardware gaps and deliberate stop/rebuild intervals have unknown lengths; timestamps across those gaps are approximate. RF times within each DMR capture remain relative to that capture's input.

**Stalled USRP recovery:** if UHD returns no IQ for one second, acquisition stops/drains/restarts RX and marks the next samples as discontinuous. Normal driver recovery takes precedence; an overflow followed by fresh IQ does not force a restart. The app allows three attempts, renewing the budget after one RF second of clean samples. If input stays stalled, capture stops with an error. Cancellation prevents another restart. This handles the reproduced UHD 4.1/B210 failure where reception never resumed after an overflow; see [recovery evidence](docs/uhd-recovery.md). RX restarts retain the same processing run and counters.

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

The console harness returns a failing exit code on any failed test. Its 24 tests cover loaded DSP optimization flags, fixture decoding and SQLite reopening, two-channel wideband extraction, proven overlap of decoder callbacks on distinct threads, bounded decoder backpressure, borrowed-buffer ownership, ordered gaps/final drain, asynchronous worker failure, exact IQ drop accounting, cancellation, live rebuilds, null metadata identity, database backpressure/failure, and invalid configurations. USRP recovery tests cover transient overflows, stalled RX, retry limits, renewed recovery budgets, cancellation during recovery, and identical DMR payloads before/after a restart on two parallel receivers. The headless Avalonia check uses the real Skia renderer, replays the fixture through the complete app, operates channel controls, checks waterfall history/zoom, and saves desktop/compact screenshots plus a result file. Tests use separate databases under `artifacts`; they do not change normal app data.

### Reproduce a live performance run

```powershell
# Paced deterministic IQ, saved channelizer settings, 10 MS/s; no radio required.
dotnet run --project tests/BadSdr.Tests -c Debug -- --performance --channels 8 --seconds 12 --label noise

# Receive from the USRP using saved device/tuning settings, with a 10 MS/s rate override.
dotnet run --project tests/BadSdr.Tests -c Debug -- --performance --live --channels 16 --seconds 30 --queue-blocks 32 --label live

# Inject one 100 ms processing stall after one RF second to check overload recovery.
dotnet run --project tests/BadSdr.Tests -c Debug -- --performance --live --channels 16 --seconds 12 --stall-ms 100 --label stall

# Pause recv three times while the radio runs, provoking actual hardware overflows.
dotnet run --project tests/BadSdr.Tests -c Debug -- --performance --live --channels 64 --seconds 20 --receive-stall-ms 500 --receive-stalls 3 --label overflow
```

The probe uses saved channels first and adds frequencies inside the sampled band as needed. `--cpu` forces the CPU backend. Reports, one-second metric snapshots, and a separate SQLite database go under `artifacts/performance`. Normal settings and session history are not changed.

For diagnosis, `--rate`, `--wire-format`, `--block-size`, `--device-args`, `--cuda` and `--strategy` override the saved setup. Omitting `--channels` uses the saved channel count; `--channels 0` isolates reception and display FFTs. `--receive-trace` records read timings and gaps between receive calls. The explicit `--live-ui-probe` app mode runs a live radio through the Avalonia dispatcher and real Skia renderer with a temporary database, recording display timing and pipeline counters. Details and commands are in the [40 MS/s report](docs/rate40-investigation.md).

See [parallel DMR validation](docs/parallel-dmr.md) for the current design and results, and the [initial performance investigation](docs/performance-2026-09-26.md) for the build/JIT/reset fixes.

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) before redistributing a published folder.
https://github.com/vadimart92/iq-channelizer
