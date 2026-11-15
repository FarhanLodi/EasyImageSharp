#!/usr/bin/env bash
# Coverage-guided fuzzing of the EasyImageSharp decoders on Linux: publish the harness, instrument the library with
# SharpFuzz, build the seed corpora, then run each target under libFuzzer (through libfuzzer-dotnet) for a fixed time.
#
#   fuzz/scripts/fuzz.sh                                   # every target, 60 s each
#   fuzz/scripts/fuzz.sh -t 600 png tiff                   # two targets, ten minutes each
#   fuzz/scripts/fuzz.sh -t 300 jpeg -- -jobs=4 -workers=4 # anything after -- goes straight to libFuzzer
#
# Options:
#   -t, --time SECONDS   per-target time box (libFuzzer -max_total_time); default 60
#   --timeout SECONDS    one input running longer than this is reported as a hang (libFuzzer -timeout); default 25
#   --max-len BYTES      the largest input libFuzzer generates; default 262144 (libfuzzer-dotnet's ceiling is 1 MiB)
#   --driver PATH        the libfuzzer-dotnet binary; default $LIBFUZZER_DOTNET, else downloaded and SHA-256-checked
#   --no-build           reuse fuzz/out/bin from the previous run (it is already instrumented)
#   --out DIR            output root; default fuzz/out
#
# Layout under the output root:
#   bin/              the published, instrumented harness
#   corpus/<target>/  seeds, rebuilt from the test fixtures on every run
#   work/<target>/    the corpus libFuzzer grows; kept between runs, so every run starts where the last one stopped
#   artifacts/<target>/crash-*, timeout-*   reproducers; replay one with: fuzz/out/bin/EasyImageSharp.Fuzz <target> <file>
#   logs/<target>.log
#
# Exit status: 0 when every target ran clean, 1 when any target produced a crash or timeout, 2 on a setup error.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FUZZ_DIR="$(dirname "$SCRIPT_DIR")"
PROJECT="$FUZZ_DIR/EasyImageSharp.Fuzz/EasyImageSharp.Fuzz.csproj"

# Pinned libfuzzer-dotnet release (MIT, https://github.com/Metalnem/libfuzzer-dotnet). The digest was computed from
# the release asset; update both together.
DRIVER_URL="https://github.com/Metalnem/libfuzzer-dotnet/releases/download/v2025.05.02.0904/libfuzzer-dotnet-ubuntu"
DRIVER_SHA256="c2c2a90d94c409a4af339a0d4f244e0442c5a5d249be0f1252ba07871f285958"
SHARPFUZZ_VERSION="2.3.0"

seconds=60
timeout=25
max_len=262144
driver="${LIBFUZZER_DOTNET:-}"
build=1
out="$FUZZ_DIR/out"
targets=()
extra=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    -t|--time) seconds="$2"; shift 2 ;;
    --timeout) timeout="$2"; shift 2 ;;
    --max-len) max_len="$2"; shift 2 ;;
    --driver) driver="$2"; shift 2 ;;
    --no-build) build=0; shift ;;
    --out) out="$2"; shift 2 ;;
    --) shift; extra=("$@"); break ;;
    -h|--help) sed -n '2,27p' "$0"; exit 0 ;;
    -*) echo "unknown option: $1" >&2; exit 2 ;;
    *) targets+=("$1"); shift ;;
  esac
done

if [[ "$(uname -s)" != "Linux" ]]; then
  echo "libfuzzer-dotnet runs on Linux and Windows only; on Windows use fuzz/scripts/fuzz.ps1." >&2
  exit 2
fi

mkdir -p "$out/tools"
out="$(cd "$out" && pwd)"
bin="$out/bin"

# ----- Driver -----
if [[ -z "$driver" ]]; then
  driver="$out/tools/libfuzzer-dotnet"
  if [[ ! -x "$driver" ]] || ! echo "$DRIVER_SHA256  $driver" | sha256sum --check --status; then
    echo "Downloading libfuzzer-dotnet..."
    curl -sSfL "$DRIVER_URL" -o "$driver.tmp"
    echo "$DRIVER_SHA256  $driver.tmp" | sha256sum --check --quiet
    mv "$driver.tmp" "$driver"
    chmod +x "$driver"
  fi
fi

# ----- Build and instrument -----
if [[ $build -eq 1 ]]; then
  if command -v sharpfuzz >/dev/null 2>&1; then
    sharpfuzz_cmd="sharpfuzz"
  else
    sharpfuzz_cmd="$out/tools/sharpfuzz"
    if [[ ! -x "$sharpfuzz_cmd" ]]; then
      dotnet tool install SharpFuzz.CommandLine --version "$SHARPFUZZ_VERSION" --tool-path "$out/tools" >/dev/null
    fi
  fi

  # A fresh directory every time: sharpfuzz rewrites EasyImageSharp.dll in place, and an incremental publish that
  # left the previous (already instrumented) copy behind would be instrumented twice.
  rm -rf "$bin"
  dotnet publish "$PROJECT" -c Release -o "$bin" -p:UseAppHost=true --nologo -v quiet
  # Only the library is instrumented. The harness, SharpFuzz itself and the framework stay as they are.
  "$sharpfuzz_cmd" "$bin/EasyImageSharp.dll"
elif [[ ! -x "$bin/EasyImageSharp.Fuzz" ]]; then
  echo "--no-build: $bin/EasyImageSharp.Fuzz does not exist; run once without --no-build." >&2
  exit 2
fi

harness="$bin/EasyImageSharp.Fuzz"
if [[ ${#targets[@]} -eq 0 ]]; then
  mapfile -t targets < <("$harness" list | awk '{print $1}')
fi

# ----- Seeds and dictionaries -----
python3 "$SCRIPT_DIR/build_corpus.py" --out "$out" --max-bytes "$max_len" --targets "${targets[@]}"

# ----- Fuzz -----
status=0
summary=()
for target in "${targets[@]}"; do
  mkdir -p "$out/work/$target" "$out/artifacts/$target" "$out/logs"
  log="$out/logs/$target.log"
  echo "=== $target: ${seconds}s"
  set +e
  # The first corpus directory is the one libFuzzer writes new inputs to; the seeds are read-only input.
  "$driver" \
    --target_path="$harness" --target_arg="$target" \
    -dict="$out/dict/$target.dict" \
    -max_total_time="$seconds" -timeout="$timeout" -max_len="$max_len" \
    -artifact_prefix="$out/artifacts/$target/" \
    -print_final_stats=1 \
    "${extra[@]}" \
    "$out/work/$target" "$out/corpus/$target" >"$log" 2>&1
  code=$?
  set -e
  tail -n 12 "$log"
  found=$(find "$out/artifacts/$target" -type f \( -name 'crash-*' -o -name 'timeout-*' -o -name 'oom-*' \) | wc -l)
  if [[ $code -ne 0 || $found -gt 0 ]]; then
    status=1
    summary+=("$target: FOUND $found reproducer(s), libFuzzer exit $code (see $log)")
  else
    summary+=("$target: clean, corpus $(find "$out/work/$target" -type f | wc -l) file(s)")
  fi
done

echo
printf '%s\n' "${summary[@]}"
exit $status
