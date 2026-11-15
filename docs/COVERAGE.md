# Code coverage

Every test project references the [coverlet](https://github.com/coverlet-coverage/coverlet) data collector
(`coverlet.collector`, MIT). Coverage is collected on request only; CI does not collect or gate on it yet.

## Collect

```powershell
dotnet test EasyImageSharp.slnx -c Release --collect:"XPlat Code Coverage" --results-directory artifacts/coverage
```

Each test assembly and target framework writes one `coverage.cobertura.xml` under
`artifacts/coverage/<run-id>/`. Add `-f net10.0` to collect for a single framework.

## Report

Merge the Cobertura files into an HTML report with
[ReportGenerator](https://github.com/danielpalme/ReportGenerator):

```powershell
dotnet tool install --global dotnet-reportgenerator-globaltool
reportgenerator -reports:"artifacts/coverage/**/coverage.cobertura.xml" -targetdir:artifacts/coverage/report -reporttypes:Html
```

## Reading the numbers

- One test project's file counts lines in every assembly it loads, so a single project's file shows low
  totals for the assemblies it does not exercise. Read merged reports, or filter by assembly.
- Tests that depend on native runtimes (libjxl, libavif, LittleCMS, SkiaSharp) or on downloaded models
  skip when those are missing, which lowers coverage of the matching packages.
- The git-ignored libwebp conformance vectors only count when built locally
  (`python -I tests/EasyImageSharp.Tests/Fixtures/conformance/build_conformance.py --libwebp`).
- Line coverage measures which code ran, not whether its output was checked. Correctness evidence comes
  from the reference and conformance comparisons.
