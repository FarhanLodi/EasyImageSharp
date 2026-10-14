# Offline document model smoke

Run the published orientation and UVDoc image-output models against a local document:

```powershell
dotnet run -c Release --project tools/DocumentModelSmoke -- input.png orientation.onnx UVDoc.onnx artifacts/document-smoke
```

Supply existing local model files. The tool uses offline CPU inference and verifies both files against
the registry SHA-256 values before loading them. It checks that preprocessing preserves source pixels
and that image-only dewarping correctly reports unavailable coordinate mappings. It writes
`processed.png` and `report.json` to the chosen output directory.

The report records model/image hashes, runtime, dimensions, stage confidence and elapsed times.
Inspect the output image and evaluate recognized text separately: this is an integration smoke,
not a text-accuracy test. Cold model initialization is included in elapsed time, so these numbers
are not steady-state benchmarks. Run on non-sensitive local documents; nothing is uploaded.

See [recorded evidence](../../benchmarks/ocr/results/document-model-smoke-2026-10-14.json) and
the [AI package contract documentation](../../src/EasyImageSharp.AI/README.md).
