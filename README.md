# Claude Usage Tray

Иконка в трее Windows, показывающая остаток лимита подписки Claude (Pro/Max).

- **Иконка** — стек из сегментов, заполнен по остатку 5-часового окна.
  Зелёный > 50%, жёлтый 21–50%, красный ≤ 20%, серый — нет данных или они устарели.
- **Наведение** — всплывающая панель с двумя полосками: сессия (5 ч) и неделя, с временем сброса.
- **Левый клик** — обновить сейчас (не чаще раза в 30 секунд).
- **Правый клик** — меню: обновить, открыть claude.ai/settings/usage, настройки, автозапуск, выход.

![Иконка на разных уровнях](icon-preview.png)

## Сборка

Нужен .NET 8 SDK (или новее).

```powershell
cd ClaudeUsageTray
dotnet run                     # запустить из исходников

# один exe-файл (нужен установленный .NET 8 Desktop Runtime):
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
# результат: bin\Release\net8.0-windows\win-x64\publish\ClaudeUsageTray.exe
```

## Откуда берутся данные

Приложение делает GET к `https://api.anthropic.com/api/oauth/usage` — тому же эндпоинту,
которым пользуется Claude Code для `/usage`. **Он не документирован**: может измениться или
пропасть без предупреждения. Если это случится, в панели будет «формат API изменился».

Токен ищется по порядку:

1. `oauthToken` в `%APPDATA%\ClaudeUsageTray\settings.json`
2. переменная окружения `CLAUDE_CODE_OAUTH_TOKEN`
3. файл входа Claude Code: `%USERPROFILE%\.claude\.credentials.json`
   (или `%CLAUDE_CONFIG_DIR%\.credentials.json`)

Приложение **никогда не обновляет токен само** — Claude Code ротирует refresh-токены,
и чужой refresh разлогинил бы его. Поэтому если Claude Code долго не запускался,
его токен истекает и иконка становится серой.

Надёжнее выпустить долгоживущий токен один раз:

```powershell
claude setup-token
```

и вписать его в `oauthToken` в settings.json (или в переменную `CLAUDE_CODE_OAUTH_TOKEN`).
Файл настроек хранит токен открытым текстом — не выкладывайте его никуда.

## Настройки

`%APPDATA%\ClaudeUsageTray\settings.json` создаётся при первом запуске и перечитывается
перед каждым обновлением, перезапуск не нужен.

| Поле | По умолчанию | Что делает |
|---|---|---|
| `pollIntervalMinutes` | `3` | Как часто опрашивать сервер. Эндпоинт отвечает 429 на частые запросы — не ставьте меньше 2–3 минут. |
| `warnBelowPercent` | `50` | Остаток, при котором цвет становится жёлтым. |
| `criticalBelowPercent` | `20` | Остаток, при котором цвет становится красным. |
| `usageUrl` | `https://api.anthropic.com/api/oauth/usage` | Адрес эндпоинта. |
| `oauthToken` | `null` | Токен вручную (см. выше). |

При ответе 429 интервал растёт экспоненциально (до 30 минут), последние данные остаются на экране.

## Структура

| Файл | Что внутри |
|---|---|
| `TrayController.cs` | Иконка в трее, опрос, всплывающая панель, меню |
| `Usage.cs` | HTTP-клиент, разбор ответа, поиск токена |
| `IconRenderer.cs` | Рисование иконки-стека, цвета |
| `UsagePopup.xaml(.cs)` | Панель с двумя полосками |
| `AppSettings.cs` | settings.json |
| `Native.cs` | WinAPI и автозапуск (HKCU\…\Run) |
