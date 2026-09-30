# Parallel DMR processing

Each listening channel owns a dedicated DMR worker thread and a four-batch FIFO. The acquisition thread and channelizer/dispatch thread run independently of these workers. There is no per-input-block barrier across receivers.

## Ordering, ownership and overload

- Channelizer callbacks borrow library memory. The dispatcher copies it into reusable staging buffers and distributes output batches across receivers in turns. Direct mode distributes input in the same order.
- A worker receives roughly 10 ms of IQ per batch. Pooled sample arrays belong to the worker after enqueue and return to the pool after processing. Each receiver reuses its own complex-double conversion array.
- Each FIFO holds at most four batches/control markers. There may also be one active batch and one partial batch per receiver, plus one channelizer output round in staging.
- A full decoder FIFO backpressures dispatch. Acquisition continues into the existing bounded IQ queue, which drops and counts incoming blocks when full. Decoder queues do not silently discard data.
- A gap marker is queued after all accepted pre-gap samples. Only the owning worker resets its receiver. This preserves sample order, call boundaries and missing-sample counts.
- Stop, EOF and channel rebuild flush partial decoder batches, complete all FIFOs, join all workers, and then flush storage. Fatal worker errors cancel acquisition and pending enqueue operations, return owned buffers, and surface the original exception.
- SQLite's existing multi-producer channel accepts events from every worker. Event order is preserved per receiver; cross-channel callbacks are concurrent. Custom sinks must be thread-safe.

## Metrics

The UI and SQLite run records show worker count, maximum overlapping decoder operations, busiest receiver work time, maximum per-receiver queue depth, dispatch/backpressure time, decoder reset time, and final drain wait.

`DecoderSeconds` is the sum of receiver decoding times; overlapping times must not be subtracted from channelizer time or treated as pipeline wall time. `ProcessingSeconds` measures channelizer/dispatch work, including queue waits and final drain. The displayed capacity estimate divides processed IQ by the larger of that time and the busiest receiver's cumulative work time. Actual drops, overflows and timeouts remain the evidence of sustained live reception.

## Correctness validation

The 18-test app suite includes:

- Two actual DMR receivers blocked simultaneously inside decoder callbacks, proving overlap on distinct threads separate from dispatch.
- Full decoder queues applying backpressure while the receiver callbacks are blocked.
- Poisoning the caller's input buffer immediately after dispatch to detect invalid ownership.
- Decoded voice payloads and sample positions matching a sequential reference receiver.
- Four concurrent receivers decoding both sides of a gap without reordered events, mixed sessions, lost gap markers or truncated final batches.
- An asynchronous receiver error cancelling a source blocked waiting for more IQ, while unblocking dispatch and joining workers.
- Existing wideband channelization, SQLite, acquisition-loss, cancellation and live add/retune/remove/restart tests.

## Measured runs — 2026-09-26

All runs used 10 MS/s input, 32,768-sample acquisition blocks, the original eight-block IQ queue, and the Debug UI configuration with optimized DSP and tiered compilation disabled. Hardware was the same B210, Ryzen 9 5900X and RTX 3080 Ti as the initial investigation.

| Input | Channels / workers | Backend | Duration | Received IQ pairs | Application drops | UHD overflows / timeouts | Peak overlapping workers |
|---|---:|---|---:|---:|---:|---|---:|
| Live B210 | 16 | CUDA PFB | 30 s | 302,317,568 | 0 | 0 / 0 | 16 |
| Live B210 | 32 | CUDA PFB | 20 s | 201,719,808 | 0 | 0 / 0 | 32 |
| Paced deterministic noise | 64 | CPU FDC | 30 s | 302,514,176 | 0 | N/A | 64 |

All three runs had a peak per-receiver queue depth of 1/4 and zero decoder backpressure wait. The 32-channel live run spent 1.23 s in channelization and 0.35 s in dispatch. Its decoder times summed to 23.83 s, while the busiest individual receiver accumulated 0.84 s. In the 64-channel noise run, receiver times summed to 72.60 s over 30.27 s elapsed, with 1.25 s in the busiest receiver. These sums include worker scheduling delays; they are not CPU-time measurements.

A separate 64-channel live CUDA run received normally for about 6.6 seconds, then UHD reported one overflow and subsequently returned timeouts without more samples. Its 65,830,800 received pairs had no application queue drops, but it is **not a successful sustained live result**. Reports preserve the one-second timeline and all 215 timeouts. A subsequent 32-channel live run completed normally. Follow-up work reproduced the post-overflow stall with the installed UHD 4.1/B210 driver and verified recovery by restarting RX; see [UHD recovery](uhd-recovery.md).

These short runs validate parallel transport/DSP operation. They do not establish a maximum channel count or the cost of every channel carrying active calls; live tests had no confirmed active calls. Recorded DMR tests separately verify payload correctness, active-session gap closure and concurrency.

Reproduce from `badSDR`:

```powershell
dotnet run --project tests/BadSdr.Tests -c Debug -- --performance --live --channels 32 --seconds 20 --label parallel32
dotnet run --project tests/BadSdr.Tests -c Debug -- --performance --cpu --channels 64 --seconds 30 --label parallel64
```

Reports are under `artifacts/performance`: `parallel16-16ch-live.json`, `parallel32-32ch-live.json`, `parallel64-64ch-noise.json` and `parallel64-64ch-live.json`. Normal user settings and session history are unchanged. The earlier build/JIT/reset investigation is preserved in [performance-2026-09-26.md](performance-2026-09-26.md).
