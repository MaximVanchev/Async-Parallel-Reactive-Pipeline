#!/usr/bin/env bash
# Възпроизвежда всички експерименти от документацията.
# Употреба: scripts/run-experiments.sh [изходна-папка]   (по подразбиране: data)
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

OUT="${1:-data}"
dotnet build src/StreamPipeline -c Release -v quiet
APP=(dotnet src/StreamPipeline/bin/Release/net8.0/StreamPipeline.dll)

echo "== Експеримент 1: входна честота × степен на паралелизъм (политика Wait) =="
"${APP[@]}" experiment --rates 250,500,1000,2000,3000,5000,7000 --dops 1,2,4,8,12,15 \
  --policies wait --duration 6 --warmup 1 --out "$OUT/exp1-rate-x-dop"

echo "== Експеримент 2: поведение при претоварване — Wait срещу DropOldest (DOP 2) =="
"${APP[@]}" experiment --rates 500,1000,2000,3000,5000 --dops 2 \
  --policies wait,drop --duration 6 --warmup 1 --out "$OUT/exp2-wait-vs-drop"

echo "== Контролно измерване + Експеримент 3 (равномерни срещу поасонови пристигания, 3 повторения) =="
scripts/run-exp3-and-bench.sh "$OUT"

echo "== Сценарии: нормален, грешки, фатална грешка, прекъсване, претоварване =="
for sc in normal faults fatal cancel overload; do
  mkdir -p "$OUT/scenarios/$sc"
  # fatal завършва с код 2 по замисъл
  "${APP[@]}" scenario "$sc" --out "$OUT/scenarios/$sc" | tee "$OUT/scenarios/$sc/console.log" || true
done

echo "Готово: $OUT"
