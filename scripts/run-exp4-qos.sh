#!/usr/bin/env bash
# Експеримент 4: влияние на QoS класа на работниците (macOS, хетерогенен процесор).
# 8 повторения × {QoS по подразбиране, USER_INTERACTIVE} при 1100 кадъра/s и DOP 2, редувани.
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
OUT="${1:-data}/exp4-qos"
dotnet build src/StreamPipeline -c Release -v quiet
APP=(dotnet src/StreamPipeline/bin/Release/net8.0/StreamPipeline.dll)

for i in 1 2 3 4 5 6 7 8; do
  "${APP[@]}" experiment --rates 1100 --dops 2 --seed "$i" --duration 6 --warmup 1 --out "$OUT/default-run$i"
  "${APP[@]}" experiment --rates 1100 --dops 2 --seed "$i" --duration 6 --warmup 1 --qos --out "$OUT/interactive-run$i"
done
