# Скрипты проекта

Все скрипты определяют пути относительно корня репозитория, поэтому проект
можно держать в любой папке (сейчас — `D:\Opencode\IPTVViewer`).

| Скрипт | Назначение |
|--------|------------|
| `build.ps1` | Сборка (`-Publish` — релизный однофайловый x86 в `publish\x86`) |
| `release.ps1` | Полный релиз: версия → publish → zip → GitHub Release → проверка обновления → commit+push |
| `check-update.ps1` | Проверка встроенного автообновления по `--check-updates` |

## Примеры

```powershell
# обычная сборка
powershell -ExecutionPolicy Bypass -File tools\build.ps1

# релизный exe (self-contained, win-x86, single-file)
powershell -ExecutionPolicy Bypass -File tools\build.ps1 -Publish

# релиз следующей версии (номер патча увеличится автоматически)
powershell -ExecutionPolicy Bypass -File tools\release.ps1 -NotesFile docs\notes-0.0.18.md

# конкретная версия, без публикации и коммита (проверка пайплайна)
powershell -ExecutionPolicy Bypass -File tools\release.ps1 -Version 0.0.18 -DryRun

# создать черновик релиза с файлами, но не публиковать
powershell -ExecutionPolicy Bypass -File tools\release.ps1 -Version 0.0.18 -KeepDraft -NoCommit

# проверить, что обновление предлагается
powershell -ExecutionPolicy Bypass -File tools\check-update.ps1 -Current 0.0.17

# проверить, что обновления нет (после релиза текущей версии)
powershell -ExecutionPolicy Bypass -File tools\check-update.ps1 -ExpectNoUpdate
```

## Что учтено

- **Черновик → загрузка → публикация.** Релиз создаётся черновиком, файлы
  загружаются по одному с повторами (3 попытки), проверяются имена и размеры,
  и только потом черновик публикуется. Так не бывает релиза без вложений.
- **Повторы загрузки.** Обрыв на 150-мегабайтном exe не ломает релиз:
  загрузка повторяется, уже загруженные файлы перезаписываются (`--clobber`).
- **stderr — не ошибка.** PowerShell 5.1 считает вывод `gh`/`git` в stderr
  исключением; обёртка `Invoke-Native` возвращает код и текст, а решение
  принимается по коду возврата.
- **Пути без хардкода.** Всё строится от `$PSScriptRoot`, проект можно переносить.
- **Проверка обновления по факту.** `check-update.ps1` ждёт появления
  `updatecheck.log`, а не фиксированную паузу, и сверяет тег/версию.
- **Остановка плеера.** Перед сборкой закрывается запущенный `IPTVViewer.exe`,
  чтобы файлы не были заблокированы.
