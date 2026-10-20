# Shapeshift

Программа на C#/WinUI 3 для получения и подготовки установочных образов Windows. Глоссарий — `CONTEXT.md`, решения — `docs/adr/`, спецификация — `.scratch/shapeshift/spec.md`.

## Разработка

- `dotnet build` / `dotnet test` из корня (.NET 10 SDK; если `dotnet` нет в PATH — `C:\Program Files\dotnet\dotnet.exe`).
- Единственный шов тестирования — команды CLI движка: тесты вызывают `Cli.RunAsync` внутри процесса. Новое поведение тестируется через команду, а не через внутренние классы.
- Снимки: `tests/*/Snapshots/*.verified.*`. При расхождении рядом появляется `.received.*` — просмотреть и переименовать, либо прогнать с `SHAPESHIFT_ACCEPT_SNAPSHOTS=1`.
- Портативная сборка: `tools/Publish-Release.ps1` → `artifacts/Shapeshift-<версия>-win-x64.zip` (версия — `Version` в `Directory.Build.props`).
- Окно: `dotnet run --project src/Shapeshift.App [-- <путь к ISO>]`. Автотестами не покрывается — вся логика ниже шва движка.
- Сквозная проверка установки в Hyper-V: `tools/bench/Invoke-Bench.ps1 -Iso <ISO>` (см. `tools/bench/README.md`), не входит в `dotnet test`.
- Тесты на настоящем ISO Windows пропускаются, пока не задан `SHAPESHIFT_TEST_ISO=<путь к ISO>` (около минуты и ~17 ГБ на диске); быстрые тесты используют синтетический образ `FakeWindowsIso`.
- Тесты с DISM (`[Trait("Category", "Admin")]`) запускаются от администратора: `dotnet test -- --filter-trait "Category=Admin"`. Если DISM хоста не открывает смонтированные образы, используйте `tools/bench/Invoke-AdminTestsInVm.ps1`.
- Новый элемент файла ответов нужно объявить в `tests/Shapeshift.Engine.Tests/Schemas/unattend.xsd`, иначе схема его не проверит.

## Agent skills

### Issue tracker

Issues are tracked as local markdown files under `.scratch/<feature>/`. See `docs/agents/issue-tracker.md`.

### Triage labels

Uses the five default triage labels (`needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`). See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: one `CONTEXT.md` and `docs/adr/` at the repo root. See `docs/agents/domain.md`.
