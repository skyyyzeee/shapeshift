# Shapeshift

Программа на C#/WinUI 3 для получения и подготовки установочных образов Windows. Глоссарий — `CONTEXT.md`, решения — `docs/adr/`, спецификация — `.scratch/shapeshift/spec.md`.

## Разработка

- `dotnet build` / `dotnet test` из корня (.NET 10 SDK; если `dotnet` нет в PATH — `C:\Program Files\dotnet\dotnet.exe`).
- Единственный шов тестирования — команды CLI движка: тесты вызывают `Cli.RunAsync` внутри процесса. Новое поведение тестируется через команду, а не через внутренние классы.
- Снимки: `tests/*/Snapshots/*.verified.*`. При расхождении рядом появляется `.received.*` — просмотреть и переименовать, либо прогнать с `SHAPESHIFT_ACCEPT_SNAPSHOTS=1`.
- Новый элемент файла ответов нужно объявить в `tests/Shapeshift.Engine.Tests/Schemas/unattend.xsd`, иначе схема его не проверит.

## Agent skills

### Issue tracker

Issues are tracked as local markdown files under `.scratch/<feature>/`. See `docs/agents/issue-tracker.md`.

### Triage labels

Uses the five default triage labels (`needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`). See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: one `CONTEXT.md` and `docs/adr/` at the repo root. See `docs/agents/domain.md`.
