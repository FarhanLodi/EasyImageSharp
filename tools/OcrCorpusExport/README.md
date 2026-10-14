# Local OCR corpus export

This evaluation executable runs labeled or unlabeled images through the sibling EasyOcrSharp and PaddleOcrNet projects. It is offline by default; an explicit manifest setting can seed missing models. It records recognized text, Unicode CER, whitespace WER, latency, managed allocations, image hashes, assembly hashes, and a snapshot of cached model asset hashes. Unlabeled images have null accuracy metrics. CPU execution and one intra-op/inter-op inference thread are explicit; image identifiers determine stable processing order. Timing includes first-use model initialization and is not a warmed throughput benchmark.

The sibling repositories must be present. Override `EasyOcrSharpProject` and `PaddleOcrNetProject` for other checkout locations. By default the siblings use their released imaging package. Pass `EasyImageSharpSourceProject` to evaluate the source implementation and opt-in tensor integrations.

```powershell
dotnet run --project tools/OcrCorpusExport -c Release -p:EasyImageSharpSourceProject=D:/Projects/EasyImageSharp/src/EasyImageSharp/EasyImageSharp.csproj -- manifest.json easyocr easyocr-results.json
dotnet run --project tools/OcrCorpusExport -c Release -p:EasyImageSharpSourceProject=D:/Projects/EasyImageSharp/src/EasyImageSharp/EasyImageSharp.csproj -- manifest.json paddleocr paddleocr-results.json
```

Example manifest (paths are relative to the manifest; cache paths can be absolute):

```json
{
  "easyOcrCache": "local-models/easyocr",
  "paddleOcrCache": "local-models/paddleocr",
  "allowModelDownload": false,
  "preprocessingProfile": null,
  "images": [
    {
      "id": "receipt-001",
      "path": "images/receipt-001.png",
      "groundTruthFile": "labels/receipt-001.txt",
      "category": "receipt",
      "split": "held-out",
      "easyOcrLanguages": ["en"],
      "paddleOcrLanguages": ["English"]
    }
  ]
}
```

Use the same inputs and seeded caches for before/after runs. `allowModelDownload` defaults to `false`; set it explicitly to `true` only for a cache-seeding run, then use offline comparisons. Reports record this choice. The model snapshot lists all cached assets after inference, not just models loaded for a particular image. Keep training/tuning and held-out sets distinct. Existing synthetic pages and a single labeled sample are smoke coverage; they cannot establish multilingual or real-document accuracy. Detection precision/recall additionally requires labeled boxes and matching criteria, which this text exporter does not supply.

`preprocessingProfile` defaults to `null` (the original image). Source-imaging builds optionally accept `NeuralOcr`, `ScannedDocument`, `PhoneDocument`, or `Screenshot`; the resulting image is written to a temporary PNG for OCR and deleted afterward. Released-imaging builds explicitly reject profile requests. Profile time/allocations include preprocessing. These experiments use unchanged profile defaults and do not imply that preprocessing improves every document.

Per-image exceptions become explicit `Error` rows and contribute empty predictions to labeled CER/WER; subsequent images continue. `FailedImages` makes these failures visible. Configuration/model-construction failures can abort the process. Use the [resumable, memory-guarded corpus runner](../../benchmarks/ocr/README.md) for larger evaluations. It preserves failed-image denominators, separates family-disjoint splits, and compares successful source/release text and model fingerprints.

`--self-test` runs seven metric checks, including insertion/deletion/substitution, empty inputs, and supplementary Unicode characters. Environment variables in manifest paths are expanded (for example `%LOCALAPPDATA%` on Windows). `benchmarks/ocr/local-sample-manifest.json` uses the existing labeled sample in the sibling EasyOcrSharp checkout.

The initial 2026-10-14 source and release exports in `benchmarks/ocr/results` use identical manifest, image, and model hashes. Both engines produced identical text and CER/WER of zero on that one sample. These cold, single-image runs establish smoke parity only; their latency and allocation fields include model initialization and must not be used to claim a throughput improvement.
