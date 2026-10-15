#!/usr/bin/env bash
# Генерира графиките от data/ и отпечатва docs/documentation.html в docs/Documentation.pdf.
set -euo pipefail
cd "$(dirname "$0")/.."

python3 scripts/make_charts.py data docs/figures

CHROME="/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
if [[ ! -x "$CHROME" ]]; then
  CHROME="$(command -v google-chrome || command -v chromium || true)"
fi
if [[ -z "$CHROME" ]]; then
  echo "Не е намерен Chrome/Chromium — отворете docs/documentation.html и отпечатайте в PDF ръчно." >&2
  exit 1
fi

# Пътят може да съдържа '#' (напр. "C#") — затова URL-ът се кодира.
URL="$(python3 -c 'import pathlib,sys; print(pathlib.Path(sys.argv[1]).resolve().as_uri())' docs/documentation.html)"
PROFILE="$(mktemp -d)"
"$CHROME" --headless=new --disable-gpu --no-pdf-header-footer --user-data-dir="$PROFILE" \
  --print-to-pdf="$PWD/docs/Documentation.pdf" "$URL" 2>/dev/null
rm -rf "$PROFILE"
[[ -f docs/Documentation.pdf ]] || { echo "PDF не беше създаден." >&2; exit 1; }
echo "PDF: docs/Documentation.pdf"
