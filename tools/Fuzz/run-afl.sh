#!/usr/bin/env bash
# Short SharpFuzz smoke run of the handshake, session and pose-cache parsers under AFL++.
# usage: [TARGETS="handshake session posecache"] tools/Fuzz/run-afl.sh <work-dir> <seconds-per-target>
# Needs: the .NET SDK on PATH, curl, make, gcc. Builds AFL++ and the SharpFuzz tool into <work-dir> on first use.
set -euo pipefail
WORK=$(realpath -m "${1:?work dir}")
SECS=${2:-120}
REPO=$(cd "$(dirname "$0")/../.." && pwd)
mkdir -p "$WORK"
cd "$WORK"
if [ ! -x AFLplusplus/afl-fuzz ]; then
  curl -sL https://github.com/AFLplusplus/AFLplusplus/archive/refs/tags/v4.21c.tar.gz | tar xz
  mv AFLplusplus-4.21c AFLplusplus
  make -C AFLplusplus source-only NO_PYTHON=1 -j4 >/dev/null 2>&1
fi
if [ ! -x sf/sharpfuzz ]; then
  dotnet tool install SharpFuzz.CommandLine --tool-path sf >/dev/null
fi
rm -rf app seeds
dotnet publish "$REPO/tools/Fuzz" -c Release -o app -v quiet -clp:NoSummary >/dev/null
# Seeds first: instrumented assemblies only run under afl-fuzz.
dotnet app/Fuzz.dll seeds seeds
cp "$REPO"/testdata/*.ospc seeds/posecache/
sf/sharpfuzz app/RecursiveAI.OneScope.Probe.dll >/dev/null
sf/sharpfuzz app/RecursiveAI.OneScope.Sdk.dll >/dev/null
sf/sharpfuzz app/RecursiveAI.OneScope.Attach.dll >/dev/null
export AFL_SKIP_BIN_CHECK=1 AFL_I_DONT_CARE_ABOUT_MISSING_CRASHES=1 AFL_SKIP_CPUFREQ=1 AFL_NO_AFFINITY=1 AFL_NO_UI=1
for target in ${TARGETS:-handshake session posecache}; do
  rm -rf "out-$target"
  AFLplusplus/afl-fuzz -i "seeds/$target" -o "out-$target" -t 10000 -m none -V "$SECS" -- dotnet app/Fuzz.dll "$target" >"afl-$target.log" 2>&1 || true
  echo "== $target"
  grep -E 'execs_done|bitmap_cvg|saved_crashes|saved_hangs|corpus_count|run_time' "out-$target/default/fuzzer_stats" || tail -5 "afl-$target.log"
  ls "out-$target/default/crashes" 2>/dev/null | grep -v README || true
done
