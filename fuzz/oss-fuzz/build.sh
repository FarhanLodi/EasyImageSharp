#!/bin/bash -eu
# OSS-Fuzz build script for EasyImageSharp. Prepared, not submitted; see fuzz/README.md ("OSS-Fuzz").
#
# Produces, in $OUT:
#   easyimagesharp-fuzz/                     the self-contained harness (the runner image has no .NET), with the
#                                            SharpFuzz-instrumented EasyImageSharp.dll and the libfuzzer-dotnet driver
#   easyimagesharp_<target>_fuzzer           one wrapper script per harness target (png, jpeg, ..., roundtrip)
#   easyimagesharp_<target>_fuzzer_seed_corpus.zip, .dict, .options
#
# The driver lives inside easyimagesharp-fuzz/ rather than at the top of $OUT on purpose: it is itself a libFuzzer
# binary, and anything executable at the top level that contains LLVMFuzzerTestOneInput is taken for a fuzz target.

cd "$SRC/easyimagesharp"
harness_dir="$OUT/easyimagesharp-fuzz"

# 1. The managed harness, self-contained for linux-x64.
dotnet publish fuzz/EasyImageSharp.Fuzz/EasyImageSharp.Fuzz.csproj -c Release -r linux-x64 --self-contained true \
  -p:UseAppHost=true -o "$harness_dir" --nologo

# 2. Coverage probes in the library only; the harness, SharpFuzz and the runtime are left alone.
sharpfuzz "$harness_dir/EasyImageSharp.dll"

# 3. The libFuzzer driver, built with this build's compiler, sanitizer flags and fuzzing engine.
$CXX $CXXFLAGS $LIB_FUZZING_ENGINE "$SRC/libfuzzer-dotnet/libfuzzer-dotnet.cc" -o "$harness_dir/libfuzzer-dotnet"

# 4. Seed corpora and merged dictionaries from the test fixtures (Python standard library only).
corpus_work="$WORK/easyimagesharp-corpus"
python3 fuzz/scripts/build_corpus.py --out "$corpus_work" --zip "$corpus_work/zip"

# 5. One wrapper per target. The comment line is what OSS-Fuzz's fuzz-target discovery looks for.
for target in $("$harness_dir/EasyImageSharp.Fuzz" list | awk '{print $1}'); do
  fuzzer="easyimagesharp_${target//-/_}_fuzzer"
  cat > "$OUT/$fuzzer" <<EOF
#!/bin/bash
# LLVMFuzzerTestOneInput for fuzzer detection.
this_dir=\$(dirname "\$0")
exec "\$this_dir/easyimagesharp-fuzz/libfuzzer-dotnet" \\
  --target_path="\$this_dir/easyimagesharp-fuzz/EasyImageSharp.Fuzz" --target_arg=$target "\$@"
EOF
  chmod +x "$OUT/$fuzzer"

  cp "$corpus_work/zip/${target}_seed_corpus.zip" "$OUT/${fuzzer}_seed_corpus.zip"
  cp "$corpus_work/zip/${target}.dict" "$OUT/${fuzzer}.dict"
  # libfuzzer-dotnet refuses inputs over 1 MiB; the seeds are capped at 256 KiB, and so are generated inputs.
  printf '[libfuzzer]\nmax_len = 262144\ntimeout = 25\n' > "$OUT/${fuzzer}.options"
done
