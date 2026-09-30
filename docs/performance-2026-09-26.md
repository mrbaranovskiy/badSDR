# Live SDR performance investigation — 2026-09-26

These measurements preceded parallel DMR processing. See [parallel DMR validation](parallel-dmr.md) for the current architecture and results.

## Result

The final Debug app configuration processed **16 live channels at 10 MS/s for 30 seconds with zero application drops and zero UHD overflows**. Processing capacity was 27.18 MS/s; DMR ran at 48.40× real time per channel. The original eight-block queue was retained.

Three issues contributed to the reported six-channel ceiling:

1. A normal Debug app build also compiled both DSP dependencies without optimization. The app now builds those dependencies in Release and optimizes Core, while keeping the Avalonia UI debuggable.
2. .NET's initial JIT tier ran too slowly during the first second of a larger channel bank. Tiered compilation is now disabled for the app and its performance harness. This uses optimized JIT code from the first invocation and trades some startup compilation work and dynamic PGO for predictable initial throughput. This behavior is described in [Microsoft's runtime compilation documentation](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/compilation).
3. Every dropped interval called `DmrReceiver.Discontinuity`, which rebuilt 1,024 resampler phases and large framing buffers for every channel. Repeated recovery took longer than the queue could buffer, causing further drops. The companion **dmr repository change** retains filter coefficients and buffers, clears signal history, and resets timing/framing state in place. Protocol sessions still close at gaps and missing-sample accounting is preserved.

The IQ channelizer itself was a small part of the measured processing time. These fixes addressed build configuration and gap recovery. DMR scheduling was subsequently moved to parallel workers.

## Measurements

Hardware: B210 over USB 3, Ryzen 9 5900X, RTX 3080 Ti. Runtime: .NET 10. Configuration: 10,000,000 complex samples/s, 32,768-sample acquisition blocks, eight queued blocks, CUDA PFB, 48 kS/s output per DMR receiver. Runs were separate processes without an attached debugger. All builds use the same channel lists for a given channel count.

| Configuration | Channels | Receive interval | IQ drop rate | Processing capacity | Managed allocation |
|---|---:|---:|---:|---:|---:|
| Original Debug | 8 | 12 s | 68.941% | 3.09 MS/s | 5,489.6 MB |
| Original Release | 8 | 12 s | 0% | 42.70 MS/s | 18.7 MB |
| Optimized DSP, original reset and tiered JIT | 16 | 30 s | 86.524% | 1.34 MS/s | 24,477.6 MB |
| Optimized DSP and reusable reset, tiered JIT | 16 | 30 s | 0.873% | 25.82 MS/s | 12.5 MB |
| Final configuration | 16 | 30 s | **0%** | **27.18 MS/s** | **6.6 MB** |

Allocation is process-wide, measured from just before starting capture through storage flush. RF reception can extend slightly beyond the requested interval because stop happens between reads. Processing capacity divides processed samples by processing wall time; it is not a sustained maximum-rate benchmark.

The final run received and processed 302,415,872 sample pairs. It used 9.996 s in DMR, 1.019 s in channelization, and 0.0024 s in final decoder close. Peak queue occupancy was 3/8; the longest processing block took 6.90 ms. The incomplete final channelizer block discarded 20,992 tail samples, reported separately from queue loss.

Increasing the queue to 32 blocks before changing the JIT setting still lost 0.325%, all in the first second. After that first second, its one-second snapshots showed no additional drops. Disabling tiered compilation removed that startup loss with the original eight-block queue.

### Forced overload recovery

With the final configuration, a deliberate 100 ms processing stall after one RF second caused 720,896 dropped sample pairs (0.595% of a 12-second, 16-channel run). There was **one discontinuity**, and every subsequent one-second snapshot showed unchanged loss. Recovery plus final close took 0.0050 s; no UHD overflows occurred. The queue remained bounded and capture resumed after the stall.

The processing consumer also now wakes directly from the bounded channel. Its waiter is synchronous on a dedicated thread, so allowing synchronous channel continuations only wakes that thread; it does not execute DSP on the acquisition thread. This removes an unnecessary thread-pool hop, although that change alone did not remove startup loss.

## Diagnostics and reproduction

The UI reports recovery time separately. Timing hover details include the per-channel DMR factor, unclassified processing time, longest block, process allocations and generation-2 collections. Queue details include peak occupancy and milliseconds of RF buffered. SQLite run records include loaded DSP build configurations and the new counters.

From `badSDR`:

```powershell
dotnet run --project tests/BadSdr.Tests -c Debug -- --performance --live --channels 16 --seconds 30 --label final
dotnet run --project tests/BadSdr.Tests -c Debug -- --performance --live --channels 16 --seconds 12 --stall-ms 100 --label final-stall
```

The probe reads saved tuning/channel settings, overrides the rate to 10 MS/s, and writes its own JSON reports and SQLite database under `artifacts/performance`. It does not change normal settings or session history. Omit `--live` for paced deterministic noise; use `--cpu` for the CPU backend or `--queue-blocks N` to compare buffering.

Local evidence files include `debug-before-8ch-live.json`, `before-8ch-live.json`, `debug-after-16ch-live.json`, `recovery-after-16ch-live.json`, `final-queue32-16ch-live.json`, `final-16ch-live.json` and `final-stall-16ch-live.json` in that artifact folder.

## Validation and limits

- App regression suite: 15 tests, including loaded DSP optimization flags, runtime configuration, exact drop accounting, gap propagation, and DMR/SQLite integration.
- DMR library suite: 37 tests. New checks compare repeated post-gap decoding with a fresh receiver at 48, 54.688 and 192 kS/s, including offset/conjugation, and bound allocations for 100 consecutive resets.
- Existing fixture decoding still recovers 216 voice frames in direct mode and in each of two channelized outputs.
- Headless Avalonia validation replays the fixture, displays sessions, exercises channel interactions, and renders both window sizes.

These are short live transport/DSP tests on the tuned RF at test time, not a maximum-channel or hours-long stability claim. The live runs did not contain confirmed active DMR calls; decoding correctness and active-call behavior are covered separately by recorded and synthetic fixtures. Concurrent busy-channel traffic may have a different cost. Default GPU strategy and signal conditions also affect throughput.

Changes span **badSDR and dmr** and should be retained together. The iq-channelizer repository remains unchanged.
