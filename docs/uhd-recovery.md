# UHD overflow recovery — 2026-09-26

## Reproduced failure

The installed runtime is UHD `4.1.0.0-0-unknown`, with a B210 on USB 3 and app-local libusb 1.0.29. Pausing the acquisition thread's receive calls for 500 ms while the device kept streaming provoked an overflow. Before this change, received and processed IQ stopped at 10,552,920 sample pairs. The next four seconds only increased the timeout count; the application queue had no drops and no backlog.

This matches [Ettus issue #475](https://github.com/EttusResearch/uhd/issues/475). The [UHD 4.1 B200 implementation](https://github.com/EttusResearch/uhd/blob/v4.1.0.0/host/lib/usrp/b200/b200_io_impl.cpp#L451) has its automatic stream restart disabled in the overflow handler. Our source previously counted the metadata error and kept receiving indefinitely, assuming UHD would resume. Restarting RX restored sample delivery on this device. This explains the persistent stall; it does not establish what triggered the user's original overflow.

## App recovery

- After one second without any returned IQ, stop continuous RX, drain old transport buffers, and start continuous RX again. All device access remains on the acquisition thread.
- Allow a normal driver-managed recovery before restarting. An overflow that promptly resumes samples needs no explicit restart.
- Preserve queued pre-gap samples and mark resumed IQ as discontinuous. Channelizer history and each receiver's state reset in their existing ordered processing paths. No decoder threads or channel plans are rebuilt.
- Limit recovery to three restart attempts. Renew the budget after one RF second of clean samples; a few stale samples cannot renew it. If the stream remains stalled, stop capture and report an error. Stop/cancellation during the drain prevents another start.
- Count automatic commands in `RxRestarts`, shown under UHD overflows and included in saved run metrics. Hardware loss remains unknown; restarting does not recover missing samples.

The native transport was separated into `UhdSource`; `UsrpSource` handles recovery. Deterministic tests exercise the same wrapper using scripted receive results and a virtual monotonic clock.

## Live verification

All runs used the saved tuning settings, 10 MS/s, 524,288-sample acquisition blocks, queue capacity 32, and CUDA PFB with parallel DMR receivers. No settings or normal session database were changed.

| Run | Channels | Duration | Forced receive stalls | RX restarts | Received / processed IQ pairs | Application drops | Result |
|---|---:|---:|---:|---:|---:|---:|---|
| Before | 16 | 6 s | 1 | 0 | 10,552,920 | 0 | Stalled after first overflow; 42 timeouts |
| After | 16 | 10 s | 3 | 3 | 52,106,016 | 0 | Resumed after every stall |
| After | 64 | 20 s | 3 | 3 | 152,245,024 | 0 | Resumed after every stall; continued through end |

Each forced stall paused `recv` for 500 ms, spaced by one second of received RF. Both recovery runs recorded six overflow/sequence-error metadata reports and 30 timeouts during the gaps. The 64-channel run had 64 overlapping decoder workers; after its third recovery, input and processing continued at approximately 10 MS/s for the remaining twelve seconds. This verifies recovery from known induced loss, not lossless capture or maximum sustainable channel capacity. Live RF was not confirmed to contain active DMR calls.

The 24-test suite also passes. Six new tests cover automatic driver recovery, idle recovery with gap propagation, exhausted retries/failure drain, renewed retry budgets, cancellation during restart, and two actual parallel DMR decoders replaying the fixture on both sides of a simulated transport stall. Both decoders retain the reference payloads and sample order across the restart.

Reproduce with the USRP available:

```powershell
dotnet run --project tests/BadSdr.Tests -c Debug -- --performance --live --channels 64 --seconds 20 --receive-stall-ms 500 --receive-stalls 3 --label overflow
```

The probe fails if it cannot inject every requested stall, cannot process at least one RF second beyond them, or the live test does not provoke an overflow. Reports retain per-second metrics under `artifacts/performance`. Evidence files for this change are `overflow-before-16ch-live.json`, `overflow-after-16ch-live.json`, and `overflow64-after-64ch-live.json`.
