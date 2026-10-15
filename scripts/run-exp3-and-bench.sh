#!/usr/bin/env bash
# Контролно паралелно измерване + експеримент 3 с три повторения (различен seed).
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
OUT="${1:-data}"
dotnet build src/StreamPipeline -c Release -v quiet
APP=(dotnet src/StreamPipeline/bin/Release/net8.0/StreamPipeline.dll)

echo "== Контролно измерване: само изчисления (Parallel.For), без канал/сензори/Rx =="
"${APP[@]}" bench --dops 1,2,4,8,12,15 --out "$OUT/bench-parallel.csv"

echo "== Експеримент 3: равномерни срещу поасонови пристигания, 3 повторения =="
for seed in 1 2 3; do
  for arr in uniform poisson; do
    "${APP[@]}" experiment --rates 800,1100,1250 --dops 2 --arrival "$arr" --seed "$seed" \
      --duration 6 --warmup 1 --out "$OUT/exp3-arrival/$arr-seed$seed"
  done
done
