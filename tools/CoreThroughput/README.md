# Warmed concurrent core workload measurements

Run from the repository root:

```powershell
dotnet run -c Release --project tools/CoreThroughput -- output.json 40
# Optional: use an existing local image instead of the deterministic synthetic input.
dotnet run -c Release --project tools/CoreThroughput -- output-local.json 40 D:/images/page.jpg
```

The runner measures materialized and fused resize-to-tensor (960×960 pad), and full/half/quarter/eighth JPEG decoding at request concurrency 1, 2, and 4. Each request allows one internal library worker. Each case uses a fresh process, warms at least four operations per worker and two seconds per case, preallocates tensor destinations, then records throughput, per-request p50/p95/p99 latency, process-wide managed allocated bytes, GC collections, and working set. Input, core assembly, and runner assembly SHA-256 fingerprints accompany each result, along with actual warmup duration and operation count. Existing JPEG input bytes are preserved; other inputs are JPEG encoded once before measurement for the decoder cases.

Managed allocation measurements include small harness and sampling overhead and exclude native allocations. Working set is sampled every 10 ms and can miss short peaks; the separate lifetime peak includes startup and warmup. Neither is a hard cap or exact per-request memory usage. Run enough iterations for stable percentiles, repeat on an otherwise idle machine, and retain environment details with reports. Synthetic results measure engineering workloads, not OCR accuracy or representative corpus quality.

Cases run sequentially, so elapsed time for the entire runner is deliberately longer than one scenario. The `worker` subcommand exists for isolated investigations; use the normal command for comparable reports.
