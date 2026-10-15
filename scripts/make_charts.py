#!/usr/bin/env python3
"""Генерира SVG графики от суровите CSV данни (само стандартната библиотека).

Употреба: python3 scripts/make_charts.py [data-папка] [изходна-папка]
"""
import csv
import math
import os
import sys
from html import escape

DATA = sys.argv[1] if len(sys.argv) > 1 else "data"
OUT = sys.argv[2] if len(sys.argv) > 2 else "docs/figures"

# Валидирана категорийна палитра (светъл режим), фиксиран ред на слотовете.
SERIES = ["#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#4a3aa7", "#e34948"]
SURFACE = "#fcfcfb"
TEXT = "#0b0b0b"
TEXT2 = "#52514e"
MUTED = "#898781"
GRID = "#e1e0d9"
FONT = "font-family='Helvetica, Arial, sans-serif'"


def read_csv(path):
    with open(path, newline="", encoding="utf-8") as f:
        return list(csv.DictReader(f))


def nice_ticks(lo, hi, n=5):
    if hi <= lo:
        hi = lo + 1
    raw = (hi - lo) / n
    mag = 10 ** math.floor(math.log10(raw))
    step = min((m * mag for m in (1, 2, 2.5, 5, 10) if m * mag >= raw), default=10 * mag)
    start = math.floor(lo / step) * step
    ticks, v = [], start
    while v <= hi + step * 1e-9:
        ticks.append(round(v, 10))
        v += step
    return ticks


def log_ticks(lo, hi):
    ticks = []
    e = math.floor(math.log10(lo))
    while 10 ** e <= hi * 1.0001:
        for m in (1, 2, 5):
            v = m * 10 ** e
            if lo * 0.999 <= v <= hi * 1.001:
                ticks.append(v)
        e += 1
    return ticks


def fmt(v):
    if v == 0:
        return "0"
    if abs(v) >= 1000:
        return f"{v/1000:g}k" if v % 1000 == 0 else f"{v:,.0f}".replace(",", " ")
    if abs(v) >= 10:
        return f"{v:g}"
    return f"{v:g}"


class Chart:
    def __init__(self, title, xlabel, ylabel, width=680, height=380, logx=False, logy=False,
                 xdomain=None, ydomain=None, legend=True):
        self.title, self.xlabel, self.ylabel = title, xlabel, ylabel
        self.w, self.h = width, height
        self.logx, self.logy = logx, logy
        self.xdomain, self.ydomain = xdomain, ydomain
        self.legend = legend
        self.m = dict(l=64, r=140, t=72, b=52)
        self.series = []
        self.bars = None

    def line(self, name, xs, ys, color, dashed=False, label=True, markers=True):
        self.series.append(dict(name=name, xs=xs, ys=ys, color=color, dashed=dashed, label=label, markers=markers))

    def grouped_bars(self, categories, groups):
        """groups: [(name, color, [values per category])]"""
        self.bars = (categories, groups)

    # --- скали ---
    def _domain(self):
        xs = [x for s in self.series for x in s["xs"]]
        ys = [y for s in self.series for y in s["ys"] if y is not None]
        if self.bars:
            ys += [v for _, _, vals in self.bars[1] for v in vals]
        xd = self.xdomain or (min(xs), max(xs)) if xs else (0, 1)
        if self.ydomain:
            yd = self.ydomain
        elif self.logy:
            yd = (min(y for y in ys if y > 0), max(ys))
        else:
            yd = (0, max(ys) * 1.08)
        return xd, yd

    def svg(self):
        m, W, H = self.m, self.w, self.h
        pw, ph = W - m["l"] - m["r"], H - m["t"] - m["b"]
        (x0, x1), (y0, y1) = self._domain()

        if self.logy:
            yt = log_ticks(y0, y1)
            y0, y1 = min(yt[0], y0), max(yt[-1], y1)
            ly0, ly1 = math.log10(y0), math.log10(y1)
            Y = lambda v: m["t"] + ph - (math.log10(max(v, y0)) - ly0) / (ly1 - ly0) * ph
        else:
            yt = nice_ticks(y0, y1)
            y1 = max(y1, yt[-1])
            Y = lambda v: m["t"] + ph - (v - y0) / (y1 - y0) * ph

        out = [f"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {W} {H}' width='{W}' height='{H}' {FONT} font-size='12'>",
               f"<rect width='{W}' height='{H}' fill='{SURFACE}'/>",
               f"<text x='{m['l']}' y='22' font-size='14' font-weight='600' fill='{TEXT}'>{escape(self.title)}</text>"]

        for v in yt:
            y = Y(v)
            out.append(f"<line x1='{m['l']}' x2='{m['l']+pw}' y1='{y:.1f}' y2='{y:.1f}' stroke='{GRID}' stroke-width='1'/>")
            out.append(f"<text x='{m['l']-8}' y='{y+4:.1f}' text-anchor='end' fill='{MUTED}'>{fmt(v)}</text>")
        out.append(f"<text transform='translate(16 {m['t']+ph/2}) rotate(-90)' text-anchor='middle' fill='{TEXT2}'>{escape(self.ylabel)}</text>")
        out.append(f"<text x='{m['l']+pw/2}' y='{H-12}' text-anchor='middle' fill='{TEXT2}'>{escape(self.xlabel)}</text>")
        out.append(f"<line x1='{m['l']}' x2='{m['l']+pw}' y1='{m['t']+ph}' y2='{m['t']+ph}' stroke='{MUTED}' stroke-width='1'/>")

        if self.bars:
            cats, groups = self.bars
            band = pw / len(cats)
            gap = 2
            bw = min(28, (band * 0.7 - gap * (len(groups) - 1)) / len(groups))
            for ci, c in enumerate(cats):
                cx = m["l"] + band * ci + band / 2
                out.append(f"<text x='{cx:.1f}' y='{m['t']+ph+18}' text-anchor='middle' fill='{MUTED}'>{escape(str(c))}</text>")
                total = len(groups) * bw + (len(groups) - 1) * gap
                for gi, (name, color, vals) in enumerate(groups):
                    v = vals[ci]
                    bx = cx - total / 2 + gi * (bw + gap)
                    by = Y(v)
                    hgt = m["t"] + ph - by
                    r = min(4, bw / 2, hgt)
                    # Заоблен само горният край (данни), долният стъпва на базовата линия.
                    out.append(f"<path d='M{bx:.1f},{m['t']+ph:.1f} V{by+r:.1f} Q{bx:.1f},{by:.1f} {bx+r:.1f},{by:.1f} "
                               f"H{bx+bw-r:.1f} Q{bx+bw:.1f},{by:.1f} {bx+bw:.1f},{by+r:.1f} V{m['t']+ph:.1f} Z' fill='{color}'/>")
                    if len(cats) * len(groups) <= 12:  # при много стълбове етикетите се застъпват
                        out.append(f"<text x='{bx+bw/2:.1f}' y='{by-4:.1f}' text-anchor='middle' font-size='10' fill='{TEXT2}'>{fmt(round(v, 1))}</text>")
        else:
            if self.logx:
                xt = log_ticks(x0, x1)
                lx0, lx1 = math.log10(x0), math.log10(x1)
                X = lambda v: m["l"] + (math.log10(v) - lx0) / (lx1 - lx0) * pw
            else:
                xt = nice_ticks(x0, x1)
                x1 = max(x1, xt[-1])
                X = lambda v: m["l"] + (v - x0) / (x1 - x0) * pw
            for v in xt:
                if x0 <= v <= x1:
                    out.append(f"<text x='{X(v):.1f}' y='{m['t']+ph+18}' text-anchor='middle' fill='{MUTED}'>{fmt(v)}</text>")

            labels = []
            for s in self.series:
                pts = [(X(x), Y(y)) for x, y in zip(s["xs"], s["ys"]) if y is not None]
                if not pts:
                    continue
                d = " ".join(f"{'M' if i == 0 else 'L'}{px:.1f},{py:.1f}" for i, (px, py) in enumerate(pts))
                dash = " stroke-dasharray='5 4'" if s["dashed"] else ""
                out.append(f"<path d='{d}' fill='none' stroke='{s['color']}' stroke-width='2' stroke-linejoin='round'{dash}/>")
                if s["markers"]:
                    for px, py in pts:
                        out.append(f"<circle cx='{px:.1f}' cy='{py:.1f}' r='4' fill='{s['color']}' stroke='{SURFACE}' stroke-width='2'/>")
                if s["label"]:
                    labels.append([pts[-1][1], pts[-1][0], s])
            # Директни етикети в края на линиите, разместени да не се застъпват.
            labels.sort(key=lambda t: t[0])
            for i in range(1, len(labels)):
                if labels[i][0] - labels[i - 1][0] < 14:
                    labels[i][0] = labels[i - 1][0] + 14
            for ly, lx, s in labels:
                out.append(f"<line x1='{lx+6:.1f}' x2='{lx+14:.1f}' y1='{ly:.1f}' y2='{ly:.1f}' stroke='{s['color']}' stroke-width='2'/>")
                out.append(f"<text x='{lx+18:.1f}' y='{ly+4:.1f}' fill='{TEXT}'>{escape(s['name'])}</text>")

        # Легенда (винаги при ≥ 2 серии): собствен ред под заглавието, с пренасяне.
        names = [(s["name"], s["color"], s["dashed"]) for s in self.series] if not self.bars else \
                [(n, c, False) for n, c, _ in self.bars[1]]
        if self.legend and len(names) >= 2:
            x, y = m["l"], 40
            for n, c, dashed in names:
                w = 30 + 6.6 * len(n)
                if x + w > W - 10:
                    x, y = m["l"], y + 16
                dash = " stroke-dasharray='5 4'" if dashed else ""
                out.append(f"<line x1='{x}' x2='{x+16}' y1='{y}' y2='{y}' stroke='{c}' stroke-width='3'{dash}/>")
                out.append(f"<text x='{x+20}' y='{y+4}' font-size='11' fill='{TEXT2}'>{escape(n)}</text>")
                x += w
        out.append("</svg>")
        return "\n".join(out)

    def save(self, name):
        os.makedirs(OUT, exist_ok=True)
        with open(os.path.join(OUT, name), "w", encoding="utf-8") as f:
            f.write(self.svg())
        print("  ", os.path.join(OUT, name))


def num(r, k):
    return float(r[k])


def exp1():
    path = os.path.join(DATA, "exp1-rate-x-dop", "summary.csv")
    if not os.path.exists(path):
        return
    rows = read_csv(path)
    dops = sorted({int(r["dop"]) for r in rows})
    rates = sorted({num(r, "target_rate") for r in rows})

    c = Chart("Пропускателност спрямо целева входна честота", "целева входна честота (кадъра/s)",
              "обработени кадъра/s", legend=True)
    c.line("идеал (y = x)", rates, rates, MUTED, dashed=True, label=False, markers=False)
    for i, d in enumerate(dops):
        rs = sorted((r for r in rows if int(r["dop"]) == d), key=lambda r: num(r, "target_rate"))
        c.line(f"DOP {d}", [num(r, "target_rate") for r in rs], [num(r, "throughput") for r in rs], SERIES[i])
    c.save("exp1_throughput.svg")

    c = Chart("Латентност p95 (приемане → резултат), логаритмична скала", "целева входна честота (кадъра/s)",
              "e2e p95 (ms)", logy=True)
    for i, d in enumerate(dops):
        rs = sorted((r for r in rows if int(r["dop"]) == d), key=lambda r: num(r, "target_rate"))
        c.line(f"DOP {d}", [num(r, "target_rate") for r in rs], [max(0.5, num(r, "e2e_p95_ms")) for r in rs], SERIES[i])
    c.save("exp1_latency_p95.svg")

    maxtp = [max(num(r, "throughput") for r in rows if int(r["dop"]) == d) for d in dops]
    c = Chart("Максимална пропускателност и ускорение спрямо степента на паралелизъм",
              "степен на паралелизъм (MaxDegreeOfParallelism)", "кадъра/s (най-високата измерена)")
    c.line("линейно ускорение (идеал)", dops, [maxtp[0] * d for d in dops], MUTED, dashed=True, label=False, markers=False)
    c.line("целият поток", dops, maxtp, SERIES[0])
    bench = os.path.join(DATA, "bench-parallel.csv")
    if os.path.exists(bench):
        b = read_csv(bench)
        c.line("само изчисления", [int(r["dop"]) for r in b], [num(r, "frames_per_s") for r in b], SERIES[1])
    c.save("exp1_scaling.svg")

    cpu = [max(num(r, "cpu_cores") for r in rows if int(r["dop"]) == d) for d in dops]
    c = Chart("Натоварване на процесора при най-висока входна честота", "степен на паралелизъм",
              "използвани ядра (CPU time / wall time)")
    c.line("идеал (= DOP)", dops, dops, MUTED, dashed=True, label=False, markers=False)
    c.line("измерено", dops, cpu, SERIES[0])
    c.save("exp1_cpu.svg")


def exp2():
    path = os.path.join(DATA, "exp2-wait-vs-drop", "summary.csv")
    if not os.path.exists(path):
        return
    rows = read_csv(path)
    rates = sorted({num(r, "target_rate") for r in rows})
    by = lambda pol: sorted((r for r in rows if r["policy"] == pol), key=lambda r: num(r, "target_rate"))
    wait, drop = by("Wait"), by("DropOldest")
    cats = [fmt(r) for r in rates]

    def loss_pct(r):
        # dropped ⊂ received (изхвърлени след приемане); overruns никога не са приети.
        lost = num(r, "dropped") + num(r, "overruns")
        return 100 * lost / max(1, num(r, "received") + num(r, "overruns"))

    c = Chart("Загубени кадри при претоварване (DOP 2)", "целева входна честота (кадъра/s)", "загубени кадри (%)")
    c.grouped_bars(cats, [("Wait (overrun в сензора)", SERIES[0], [loss_pct(r) for r in wait]),
                          ("DropOldest (изхвърлени от опашката)", SERIES[1], [loss_pct(r) for r in drop])])
    c.save("exp2_loss.svg")

    c = Chart("Латентност p95 от приемане до резултат (DOP 2)", "целева входна честота (кадъра/s)", "e2e p95 (ms)")
    c.grouped_bars(cats, [("Wait", SERIES[0], [num(r, "e2e_p95_ms") for r in wait]),
                          ("DropOldest", SERIES[1], [num(r, "e2e_p95_ms") for r in drop])])
    c.save("exp2_latency.svg")

    c = Chart("Възраст на данните p95 (от заснемане в сензора до резултат)", "целева входна честота (кадъра/s)", "възраст p95 (ms)")
    c.grouped_bars(cats, [("Wait", SERIES[0], [num(r, "age_p95_ms") for r in wait]),
                          ("DropOldest", SERIES[1], [num(r, "age_p95_ms") for r in drop])])
    c.save("exp2_age.svg")


def exp4():
    base = os.path.join(DATA, "exp4-qos")
    if not os.path.isdir(base):
        return
    runs = sorted({int(d.split("run")[1]) for d in os.listdir(base) if "run" in d})
    def tp(kind, i):
        path = os.path.join(base, f"{kind}-run{i}", "summary.csv")
        return num(read_csv(path)[0], "e2e_p95_ms") if os.path.exists(path) else 0
    runs = [i for i in runs if tp("default", i) and tp("interactive", i)]
    if not runs:
        return
    c = Chart("Латентност e2e p95 при вход 1100/s и DOP 2 — 8 повторения", "повторение", "e2e p95 (ms)")
    c.grouped_bars([str(i) for i in runs],
                   [("QoS по подразбиране", SERIES[0], [tp("default", i) for i in runs]),
                    ("QoS USER_INTERACTIVE", SERIES[1], [tp("interactive", i) for i in runs])])
    c.save("exp4_qos.svg")


def scenarios():
    base = os.path.join(DATA, "scenarios")
    for sc in ("normal", "faults", "fatal", "cancel", "overload"):
        path = os.path.join(base, sc, "timeline.csv")
        if not os.path.exists(path):
            continue
        rows = [r for r in read_csv(path) if num(r, "processed") + num(r, "rejected") + num(r, "retries") > 0]
        if not rows:
            continue
        xs = [num(r, "second") for r in rows]
        c = Chart(f"Сценарий „{sc}“: пропускателност по секунди", "време от старта (s)", "кадъра/s",
                  xdomain=(0, max(xs)))
        c.line("обработени/s", xs, [num(r, "throughput_per_s") for r in rows], SERIES[0])
        c.save(f"scenario_{sc}_throughput.svg")

        if sc == "faults":
            c = Chart("Сценарий „faults“: грешки по секунди", "време от старта (s)", "брой събития",
                      xdomain=(0, max(xs)))
            c.line("повторни опити", xs, [num(r, "retries") for r in rows], SERIES[0])
            c.line("отхвърлени", xs, [num(r, "rejected") for r in rows], SERIES[1])
            c.save("scenario_faults_errors.svg")
        if sc in ("cancel", "overload"):
            c = Chart(f"Сценарий „{sc}“: максимална дълбочина на опашката", "време от старта (s)", "кадъра в опашката",
                      xdomain=(0, max(xs)))
            c.line("max дълбочина", xs, [num(r, "max_queue_depth") for r in rows], SERIES[0])
            c.save(f"scenario_{sc}_queue.svg")


if __name__ == "__main__":
    print("Графики →", OUT)
    exp1()
    exp2()
    exp4()
    scenarios()
