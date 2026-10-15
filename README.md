# Async-Parallel-Reactive-Pipeline — Интегриране на асинхронно, паралелно и реактивно програмиране

Поточна обработка на вибрационни данни от индустриални машини (.NET 8, C#):

| Етап | Модел | Технология | Роля |
|---|---|---|---|
| Прием | **асинхронност** (I/O) | `async/await`, `IAsyncEnumerable`-подобно четене, `Task.Delay` само за мрежово закъснение | 16 сензора изпращат кадри по „мрежата“, повторни опити с експоненциално отлагане |
| Буфер | координация | `System.Threading.Channels` (ограничен канал) | обратен натиск (`Wait`) или изхвърляне (`DropOldest`) |
| Обработка | **паралелизъм** (CPU) | `Parallel.ForEachAsync`, `MaxDegreeOfParallelism` | FFT, Welch PSD, обвиващ анализ (Хилберт) → индекс на дефект на лагер |
| Представяне | **реактивност** | `System.Reactive` | аларми (`GroupBy`→`Scan`→`DistinctUntilChanged`), табло (`Buffer(1 s)`), метрики, грешки |

Подробностите, архитектурната схема, експериментите и анализът са в
[`docs/Documentation.pdf`](docs/Documentation.pdf) (изходен текст: [`docs/documentation.html`](docs/documentation.html)).

## Изисквания

* .NET SDK 8.0 (`global.json` позволява всяка 8.0.x ≥ 8.0.426 с roll-forward)
* по желание: Visual Studio Code + C# Dev Kit; Python 3 (графики); Google Chrome (PDF)

Ако SDK-то не е инсталирано:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0
```

и добавете в `~/.zshrc`:

```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"
```

## Компилиране и тестове

```bash
dotnet build -c Release
```

```bash
dotnet test
```

## Стартиране

```bash
dotnet run -c Release --project src/StreamPipeline -- scenario normal
```

Сценарии: `normal`, `faults` (мрежови грешки, повредени кадри, трайно отпаднал сензор),
`fatal` (неустранима грешка → `OnError`), `cancel` (претоварване + твърдо прекратяване),
`overload` (политика `DropOldest`). Свободно изпълнение:

```bash
dotnet run -c Release --project src/StreamPipeline -- run --rate 2000 --dop 4 --duration 10 --policy wait
```

`Ctrl+C` веднъж — плавно спиране (източниците спират, опашката се изпразва);
два пъти — твърдо прекратяване. Всички опции: `... -- --help`.

Всяко изпълнение записва в `results/<сценарий>-<време>/` (или `--out`):
`summary.json`, `timeline.csv` (по секунди), `latencies.csv` (по кадър), `events.csv`, `threads.csv`.

## Visual Studio Code

1. `File → Open Folder…` → тази папка; инсталирайте препоръчаните разширения.
2. **Run and Debug (F5)** — готови конфигурации за всеки сценарий, свободно изпълнение (пита за честота/DOP) и bench.
   Точки на прекъсване работят навсякъде, вкл. в паралелното тяло и в Rx абонатите.
3. **Testing** панел — 13 теста (FFT, анализатор, плавно/твърдо спиране, грешки, Rx оператори).
4. **Terminal → Run Task…** — `build`, `test`, `експерименти`, `графики + PDF документация`.

## Възпроизвеждане на експериментите

```bash
scripts/run-experiments.sh data
```

(~9 минути) пресъздава всичко в `data/`; входните данни се генерират детерминирано (`--seed`, по подразбиране 42).
След това:

```bash
scripts/build-docs.sh
```

генерира графиките (`docs/figures/*.svg`) и PDF-а.

## Структура

```
src/StreamPipeline/
  Ingestion/    SimulatedSensorClient (async I/O), SignalGenerator
  Processing/   FftPlan, SpectralAnalyzer (CPU)
  Reactive/     EventHub, ReactiveMonitor, HealthTracker, MetricsRecorder
  Pipeline/     PipelineRunner (координация), ExperimentRunner, ResultWriter, RunReport
  Program.cs    CLI, сценарии, Ctrl+C
tests/StreamPipeline.Tests/   xUnit
scripts/      run-experiments.sh, make_charts.py, build-docs.sh
data/         сурови експериментални данни (CSV/JSON) + machine.json
docs/         документация (HTML → PDF) и графики
```

## Лиценз на използваните ресурси

Логото на Технически университет – София (`docs/figures/tu-sofia-logo.svg`) е от
[Wikimedia Commons](https://commons.wikimedia.org/wiki/File:Tu-sofia-logo.svg), лиценз CC BY-SA 3.0.
