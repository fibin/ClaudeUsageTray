# Claude Usage Tray

A Windows tray icon that shows how much of your Claude subscription (Pro/Max) usage limit is left.

- **Icon**: a vertical battery whose solid fill shows what's left of the 5-hour session window.
  Green above 50%, yellow at 21–50%, red at 20% or below, gray when there's no data or it's stale.
- **Hover**: a popup with two bars, one for the session (5 h) and one for the week, plus reset times.
- **Left click**: refresh now (at most once every 30 seconds).
- **Right click**: menu with refresh, open claude.ai/settings/usage, settings, start with Windows, exit.

![Icon at different levels](icon-preview.png)

## Download

Grab the latest build from the [Releases page](https://github.com/fibin/ClaudeUsageTray/releases/latest):

| File | Size | Requirements |
|---|---|---|
| `ClaudeUsageTray-win-x64.exe` | ~70 MB | None, runs on any Windows 10/11 x64 |
| `ClaudeUsageTray-win-x64-small.exe` | < 1 MB | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |

Just run the exe: there's no installer and no window, the app goes straight to the tray.
To start it with Windows, right-click the icon and enable **Start with Windows**
(put the exe in its permanent location first, since autostart remembers the path).

The exe is not code-signed, so SmartScreen may warn on first launch:
click **More info → Run anyway**.

If the icon stays gray (hover over it to see why), the app can't find a usable token:
follow [Set up a long-lived token](#set-up-a-long-lived-token-recommended).

## Keep the icon always visible

By default Windows hides new tray icons in the overflow menu (or, if the hidden icon menu is
turned off, doesn't show them at all). To pin the indicator next to the clock:

**Windows 11**

1. Make sure the app is running.
2. Right-click an empty spot on the taskbar and choose **Taskbar settings**.
3. Expand **Other system tray icons**.
4. Find **ClaudeUsageTray** and switch it **On**.

**Windows 10**

1. Right-click the taskbar and choose **Taskbar settings**.
2. Under **Notification area**, click **Select which icons appear on the taskbar**.
3. Switch **ClaudeUsageTray** **On**.

If the hidden icon menu (the `^` arrow) is enabled, you can also simply drag the icon from it
onto the taskbar.

Windows remembers this setting per exe path. If you move the exe or switch from `dotnet run`
to a downloaded build, turn the switch on again for the new location.

## Build from source

Requires the .NET 8 SDK (or newer).

```powershell
git clone https://github.com/fibin/ClaudeUsageTray.git
cd ClaudeUsageTray
dotnet run                     # run from source

# single exe (requires the .NET 8 Desktop Runtime):
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
# output: bin\Release\net8.0-windows\win-x64\publish\ClaudeUsageTray.exe
```

Releases are built by GitHub Actions (`.github/workflows/release.yml`). To publish a new
version, bump `<Version>` in `ClaudeUsageTray.csproj` and push to `main`: the workflow builds
both exe variants, creates the `v<Version>` tag and attaches the files to a new release.

## Where the data comes from

The app sends a GET request to `https://api.anthropic.com/api/oauth/usage`, the same endpoint
Claude Code uses for `/usage`. **This endpoint is undocumented**: it may change or disappear
without notice. If that happens, the popup will say the API format has changed.

The token is looked up in this order:

1. `oauthToken` in `%APPDATA%\ClaudeUsageTray\settings.json`
2. the `CLAUDE_CODE_OAUTH_TOKEN` environment variable
3. Claude Code's login file: `%USERPROFILE%\.claude\.credentials.json`
   (or `%CLAUDE_CONFIG_DIR%\.credentials.json`)

The app **never refreshes the token itself**. Claude Code rotates its refresh tokens, so a
refresh from another program would log Claude Code out. As a result, if Claude Code hasn't
been used for a while, its token expires and the icon turns gray. As soon as you use Claude
Code again it renews the token, and the app picks that up within a few seconds.

A more reliable option is a long-lived token: see
[Set up a long-lived token](#set-up-a-long-lived-token-recommended) below.

## Set up a long-lived token (recommended)

If the icon is gray after Windows starts, or the popup says the token has expired, the app is
relying on a short-lived token from Claude Code or the Claude desktop app. A long-lived token
removes that dependency. You only need to do this once per computer.

### 1. Install the Claude Code CLI

Skip this step if `claude --version` already prints a version number.

Open **PowerShell** (no administrator rights needed) and run the official installer:

```powershell
irm https://claude.ai/install.ps1 | iex
```

Close the terminal and open a new one, then check the installation:

```powershell
claude --version
```

### 2. If `claude` is "not recognized"

```
claude : The term 'claude' is not recognized as the name of a cmdlet, function, script file, or operable program.
```

The installer puts `claude.exe` in `%USERPROFILE%\.local\bin`, but your terminal doesn't see that
folder yet. You can either run it by its full path:

```powershell
& "$env:USERPROFILE\.local\bin\claude.exe" --version
```

or add the folder to your user `PATH` once:

```powershell
[Environment]::SetEnvironmentVariable("Path", [Environment]::GetEnvironmentVariable("Path", "User") + ";$env:USERPROFILE\.local\bin", "User")
```

After changing `PATH`, restart the **whole app** that hosts your terminal (Windows Terminal,
VS Code and so on), not just the tab: new tabs inherit the `PATH` the app started with.

If even the full path fails, check that the file exists:

```powershell
Get-ChildItem "$env:USERPROFILE\.local\bin"
```

### 3. Generate the token

```powershell
claude setup-token
```

(or `& "$env:USERPROFILE\.local\bin\claude.exe" setup-token` if `claude` isn't on your `PATH`).

1. The command prints a sign-in link and usually opens it in your browser.
   If the browser doesn't open, copy the link into the address bar yourself.
2. Sign in with your Claude account and click **Authorize**.
3. The browser shows a code. The terminal is waiting at `Paste code if prompted >`:
   paste the code there (right-click or `Ctrl+V`) and press **Enter**.
   If the browser hands the sign-in back to the terminal on its own, there's nothing to paste.
4. The terminal prints a token that starts with `sk-ant-oat01-`. Copy all of it.
   It's shown only once.

### 4. Give the token to the app

Right-click the tray icon, choose **Settings (settings.json)** and fill in `oauthToken`:

```json
{
  "pollIntervalMinutes": 3,
  "warnBelowPercent": 50,
  "criticalBelowPercent": 20,
  "usageUrl": "https://api.anthropic.com/api/oauth/usage",
  "oauthToken": "sk-ant-oat01-..."
}
```

Save the file (`Ctrl+S`). The app notices the change and turns the icon colored within a few
seconds; no restart is needed.

Alternatively, set the `CLAUDE_CODE_OAUTH_TOKEN` environment variable instead of editing the file.

> **Keep the token secret.** It gives access to your Claude subscription. `settings.json` stores
> it in plain text in `%APPDATA%\ClaudeUsageTray`, outside any repository. Don't paste it into
> chats, issues or screenshots. If it leaks, generate a new one with `claude setup-token`.

## Settings

`%APPDATA%\ClaudeUsageTray\settings.json` is created on first run and re-read before every
refresh, so changes apply without a restart.

| Field | Default | What it does |
|---|---|---|
| `pollIntervalMinutes` | `3` | How often to poll the server. The endpoint returns 429 on frequent requests, so don't go below 2–3 minutes. |
| `warnBelowPercent` | `50` | Remaining percentage at which the color turns yellow. |
| `criticalBelowPercent` | `20` | Remaining percentage at which the color turns red. |
| `usageUrl` | `https://api.anthropic.com/api/oauth/usage` | Endpoint address. |
| `oauthToken` | `null` | Manually supplied token (see above). |

## Automatic refresh

Besides polling every `pollIntervalMinutes`, the app refreshes on its own when:

- Claude Code's login file changes (Claude Code renewed its token);
- `settings.json` changes (for example, you pasted a token);
- the computer wakes from sleep, the session is unlocked, or the network comes back.

Failed attempts are retried sooner than the regular interval: a missing or expired token is
re-checked every 30 seconds (a local file read, no network request), and network errors such as
the ones right after Windows starts are retried after 15 s, 30 s, 60 s and so on.
On a 429 response the polling interval backs off exponentially (up to 30 minutes), and the last
known data stays on screen.

## Troubleshooting

Hover over the icon: the bottom of the popup says why the data is missing. For more detail,
right-click the icon and choose **Open log**
(`%APPDATA%\ClaudeUsageTray\log.txt`, errors and state changes only, never tokens).

| Message | What to do |
|---|---|
| Claude Code login not found | [Set up a long-lived token](#set-up-a-long-lived-token-recommended), or sign in to Claude Code on this computer. |
| Claude Code token has expired | [Set up a long-lived token](#set-up-a-long-lived-token-recommended), or use Claude Code once (it renews the token). |
| The server rejected the token | The token was revoked or mistyped: generate a new one with `claude setup-token` and replace `oauthToken`. |
| Cannot reach the server | Check the connection; the app keeps retrying on its own. |
| Gray icon right after Windows starts | Usually the token expired overnight: [set up a long-lived token](#set-up-a-long-lived-token-recommended). |
| `claude` is not recognized | See [If `claude` is "not recognized"](#2-if-claude-is-not-recognized). |

## Project layout

| File | Contents |
|---|---|
| `TrayController.cs` | Tray icon, polling, hover popup, menu |
| `Usage.cs` | HTTP client, response parsing, token lookup |
| `IconRenderer.cs` | Drawing the battery icon, colors |
| `UsagePopup.xaml(.cs)` | Popup with the two bars |
| `AppSettings.cs` | settings.json |
| `Native.cs` | WinAPI and autostart (HKCU\…\Run) |
| `Log.cs` | log.txt in %APPDATA%\ClaudeUsageTray |

## License

[MIT](LICENSE) © 2026 Dmytro Viienko
