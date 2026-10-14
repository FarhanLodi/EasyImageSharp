# Concurrent core measurements, 2026-10-14

## Measurement without concurrent tasks

The final report is [core-throughput-quiet-2026-10-14.json](core-throughput-quiet-2026-10-14.json). OCR inference, builds, and WSL workloads were paused while it ran. Each of the 18 cases used a fresh process, at least two seconds of warmup and four operations per destination buffer, then 100 requests per worker: 4,200 measured requests in total. The report records actual warmup counts/durations, core and runner hashes, OS, processor identifier, architecture, and .NET version. This is one controlled workload run, not a statistical study across deployments.

The input is a deterministic synthetic 1200×800 image. Tensor cases pad to 960×960; JPEG cases decode a JPEG encoded once from that input. Results below use decimal MB. Throughput columns correspond to one, two, and four simultaneous requests, each configured for one library worker.

| Workload | Requests/s, concurrency 1 | Requests/s, concurrency 2 | Requests/s, concurrency 4 | Managed MB/request, concurrency 1 | Sampled peak MB, concurrency 1 |
|---|---:|---:|---:|---:|---:|
| Materialized resize + tensor | 74.3 | 154.5 | 225.1 | 4.689 | 143.4 |
| Fused resize + tensor | 72.3 | 126.8 | 241.8 | 0.057 | 65.8 |
| Full JPEG | 69.8 | 134.4 | 249.7 | 5.778 | 100.1 |
| Half JPEG | 73.6 | 140.8 | 249.8 | 1.457 | 116.0 |
| Quarter JPEG | 81.7 | 166.6 | 320.0 | 0.376 | 70.6 |
| Eighth JPEG | 137.9 | 267.8 | 490.1 | 0.105 | 54.0 |

Fused preprocessing reduced measured managed allocations by about 98.8% at concurrency 1, with similar single-request throughput. It was slower at concurrency 2 and faster at concurrency 4 in this run, so it is a memory-saving choice with workload-dependent speed, not a universal speedup. Reduced JPEG lowered allocations and improved throughput on this input, most strongly at eighth size. Progressive JPEG still retains source-sized coefficient storage and needs separate workload measurements.

Lower per-request allocation is not a proportional reduction in process working set: half JPEG's sampled peak exceeded full JPEG's peak in this run. GC, runtime reservations, retained pools, and warmup history matter. Sampling can miss short peaks, and these measurements do not enforce a cap. See [the isolated worker](../tools/IsolatedImageWorker/README.md) for the separately tested OS limits.

## Measurement limits

Earlier short runs overlapped builds and other host work, so they are excluded from the committed performance evidence. The final report above supersedes those exploratory reports. Local copies remain under ignored `artifacts/commit-cleanup`.

These inputs measure image operations, not OCR accuracy. They do not justify claims about arbitrary documents, production server traffic, native allocation totals, hard memory caps, or ARM64 performance.
