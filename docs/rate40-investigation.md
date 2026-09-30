# 40 MS/s investigation

Measured on 2026-09-26/27 with the saved 50 listening channels, Ryzen 9 5900X, RTX 3080 Ti, B210 over USB 3, Windows WinUSB, UHD 4.1.0.0 and app-local libusb 1.0.29. Normal receiver settings and session history were not changed.

## Finding

The reproduced stall starts in live UHD reception. It also happens with zero listening channels and in UHD's standalone C++ benchmark. A paced 40 MS/s noise source sustained all 50 channelizer/DMR workers without a single application drop. The app's estimated DSP capacity does not measure incoming USB traffic and previously could remain high while reception was stopped.

At 40 MS/s, `sc16` carries 160 MB/s of sample payload; `sc8` carries 80 MB/s. Protocol overhead is additional. Format and USB buffer changes materially affected the results, but neither CPU decoding nor a larger application IQ queue fixed the transport stall. These tests locate the bottleneck before the processing queue; they do not distinguish every contribution from the USB controller, WinUSB/libusb, UHD or device firmware.

UHD 4.1 additionally has the known [B210 post-overflow restart issue](https://github.com/EttusResearch/uhd/issues/475). The app's bounded RX restart recovers isolated stalls, but stops with an error if every restart immediately overflows again. See [recovery details](uhd-recovery.md).

## Isolation runs

| Input and configuration | Observation |
|---|---|
| Live 40 MS/s, sc16, 50 channels, saved transport defaults | Only 275,400 IQ pairs total across four starts; 7 overflow/sequence reports, 39 timeouts, no application drops; stopped after failed recovery |
| Same input, zero channels | Same 275,400 pairs and failure; processing queue stayed empty |
| UHD C++ benchmark, 40 MS/s sc16, defaults | 69,360 pairs in a six-second test, then persistent timeouts |
| Paced noise, 40 MS/s, same 50 channels / CUDA | 400,490,496 pairs processed in 10.02 s; zero drops and decoder queue wait; channelizer 0.84 s, dispatch 0.25 s |
| Live sc16, larger USB frames / 512 receive buffers | Intermittent improvement; the 50-channel app still stalled. A ten-second native benchmark passed, which was insufficient to establish stability |
| Live sc16, CPU channelizer / larger USB buffers | Ran for about six seconds, then overflowed; switching away from CUDA did not resolve reception |
| Live sc16, 524,288-sample acquisition blocks / larger USB buffers | Sustained reception for stretches but overflowed again; not a reliable remedy |
| UHD C++ benchmark, sc8, default USB buffers | 402,159,905 pairs in ten seconds; zero overflows or timeouts |
| Full app, sc8, default USB buffers | Continued through 20 seconds, but needed three RX restarts; not lossless |
| Full app, sc8, `num_recv_frames=512`, default frame size | 804,126,720 IQ pairs received and processed in 20.108 s; zero drops, overflows, timeouts, RX restarts or decoder queue wait |

Increasing thread priority did not resolve the sc16 failure. Some failing runs had no garbage collection at all. The capture trace measured gaps between calls as well as time inside receive; the observed priority-test maximum caller gap was 2.17 ms, not a decoder-induced queue wait.

## Tested 40 MS/s configuration

In the updated app, select **USRP link format → sc8** and append **`,num_recv_frames=512`** to the existing UHD device arguments. Keep the default USB frame size: the larger sc16 test frame size (`recv_frame_size=16360`) did not work with sc8 on this setup. The successful run used a 65,536-sample application block, queue capacity 128, Auto routing/backend and the 50 saved channels (resolved to CUDA PFB).

The final live UI probe (`artifacts/ui40-final/result.json`) ran the actual MainWindow timer and database refresh under the Avalonia dispatcher, with real Skia drawing. It recorded 99 display ticks and 98 waterfall rows, with RX displayed at 39.98 MS/s. Render p95 was 1.884 ms; the 10 ms heartbeat's p95 interval was 30.405 ms, maximum 39.305 ms. There were no dispatcher errors. The channelizer used 1.810 seconds and dispatch 0.505 seconds over 20.108 seconds of elapsed capture.

This is a tested workaround, not proof of sustained lossless sc16 reception or a maximum channel count. sc8 sacrifices input dynamic range; received live RF was not confirmed to contain active DMR calls. Short tests cannot establish long-run reliability. No saved setting was silently switched to sc8.

## Traces and reproducibility

Managed traces are under `artifacts/performance/rate40.nettrace` and `rate40-dsp.nettrace`, with text stack summaries beside them. They contain sampled thread time, including blocked waits, **not CPU percentages**. DSP workers mostly awaited input. Native USB/driver internals are outside those managed stacks; the native benchmark is the independent control.

```powershell
# Saved 50 channels, independent of the UI. Reports preserve the actual resolved backend.
dotnet run --project tests/BadSdr.Tests -c Release -- --performance --live --rate 40000000 --seconds 12 --receive-trace --label rate40

# Bypass the radio to isolate DSP throughput.
dotnet run --project tests/BadSdr.Tests -c Release -- --performance --rate 40000000 --seconds 10 --label dsp40

# Live receiver with real Skia drawing, dispatcher timers, isolated settings/database.
dotnet run --project src/BadSdr.App -c Release -- --live-ui-probe --rate 40000000 --wire-format sc8 --device-args "type=b200,serial=000000078,num_recv_frames=512" --seconds 20 --output artifacts/ui40
```

The diagnostic flags do not modify saved settings. The live UI probe uses headless window hosting, so its timing does not cover the Windows desktop compositor. Its dispatcher and rendering measurements are stored separately from DSP metrics. Early exploratory `ui40-*` reports used `RunJobs` without a continuous dispatcher loop; their render/heartbeat timings are not evidence of continuous display updates. The final probe uses `Dispatcher.MainLoop` and records display tick/history counts.

## App changes

- Add an explicit USRP `sc16` / `sc8` selector. Existing settings retain sc16; choosing sc8 reduces precision/dynamic range. The libraries still receive fc32 samples.
- Show actual RX MS/s below estimated DSP capacity, making stalled input visible.
- Acquire the first pooled buffer before starting the radio, removing avoidable startup work from the receive deadline.
- Add reproducible rate, transport, read-timing and live-UI probes. All probes use separate databases.

The device format and buffer controls are exposed by UHD; see its [transport documentation](https://files.ettus.com/manual/page_transport.html). No system driver, firmware, or library source was replaced during this investigation.
