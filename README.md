# AiMOnitoR (AMOR) – AI Usage Monitor

**See how much of your AI subscription limits is left, right in the Windows taskbar.**

A tiny Windows tray app that shows two mini bars per AI subscription (short window, usually 5 hours, and weekly window) for **Claude**, **ChatGPT (Codex)**, **Gemini (Antigravity)** and **GitHub Copilot**. Click an icon to see every limit in detail, including when it resets.

<p align="center">
  <img src="docs/tray.png" alt="Tray icons" width="360"><br>
  <img src="docs/popup.png" alt="Detail view" width="400">
</p>

- No installation and no dependencies. It builds with the C# compiler that ships with every Windows 10/11 (.NET Framework 4.x).
- It reuses the logins of the official CLIs you already have. There are no API keys, and it stores no passwords of its own.
- It refreshes every minute; Gemini refreshes every 5 minutes because each check starts the Antigravity CLI.
- Bar colours follow a traffic-light scale, and a coloured stripe under the bars identifies the provider.
- Autostart, single instance, dark and light taskbar, high-DPI.

> **Note:** The user interface is currently **German**. Pull requests for other languages are welcome.

## What is shown

| Provider | Left bar | Right bar | Also in the detail view |
|---|---|---|---|
| **Claude** (salmon stripe) | 5-hour limit | weekly limit (all models) | weekly limits per model, extra usage |
| **ChatGPT** (teal stripe) | 5-hour limit | weekly limit | extra limits (e.g. Codex-Spark), credits |
| **Gemini** (blue stripe) | 5-hour limit (Gemini models) | weekly limit (Gemini models) | Claude & GPT limits inside Antigravity |
| **Copilot** (purple stripe) | premium requests (monthly) | only on the Free plan: chat | chat / completions, remaining count |

Bar colour shows the **remaining** share:

| remaining | colour |
|---|---|
| 100–75 % | dark green |
| 75–50 % | light green |
| 50–25 % | yellow |
| 25–10 % | orange |
| 10–1 % | red |
| 0 % | black (whole bar) |

A grey corner on an icon means the provider is not set up. A white corner means the last refresh failed; the detail view says why. When a refresh fails, the last known values stay visible but faded.

## Requirements

- Windows 10 or 11
- .NET Framework 4.x (preinstalled on Windows 10/11)
- For each subscription you want to track, the matching official CLI, signed in once with that subscription:

| Provider | Sign in once with | Credentials the monitor reads |
|---|---|---|
| Claude (Pro/Max/Team) | [Claude Code](https://code.claude.com/docs/en/setup): `irm https://claude.ai/install.ps1 \| iex`, then `claude` and log in | `%USERPROFILE%\.claude\.credentials.json` |
| ChatGPT (Plus/Pro/…) | [Codex CLI](https://github.com/openai/codex): `npm i -g @openai/codex`, then `codex login` → *Sign in with ChatGPT* | `%USERPROFILE%\.codex\auth.json` |
| Gemini (Google AI Pro/Ultra/free) | [Antigravity CLI](https://antigravity.google): `irm https://antigravity.google/cli/install.ps1 \| iex`, then `agy` and sign in with Google (run it in your own folder, not in `System32`) | managed by `agy` itself |
| GitHub Copilot | right-click a tray icon → *GitHub Copilot anmelden…* (GitHub device login), **or** an existing `gh auth login` / Copilot editor plugin login | own login (DPAPI-encrypted), `%LOCALAPPDATA%\github-copilot\apps.json` or `gh auth token` |

Step-by-step instructions are in [First-time sign-in](#first-time-sign-in). Providers you don't use can be switched off in the settings, or simply left unconfigured; they then show a grey corner.

## Install

1. Download the repository (*Code → Download ZIP*) and unpack it somewhere permanent, e.g. `C:\Tools\ai-usage-monitor`.
2. Double-click **`install.bat`**. It builds `AIUsageMonitor.exe`, adds it to Windows autostart and starts it.
   (Use `build.bat` instead if you only want to build and start it, without autostart.)
3. Windows 11 hides new tray icons behind the `^` arrow. Drag the four icons onto the taskbar, or enable them under *Settings → Personalization → Taskbar → Other system tray icons*.

To remove it, run **`uninstall.bat`**. It stops the monitor and removes the autostart entry. Settings live in `%APPDATA%\AIUsageMonitor`.

> If you move the folder later, run `install.bat` again. Windows ties tray icon positions to the program's path.

## First-time sign-in

The monitor never asks for your password. It reads the login that each provider's official tool saves on your PC. So you sign in **once per provider** in that tool, and the monitor picks the login up automatically: within a minute, or within 5 minutes for Gemini.

Use a **normal PowerShell window, not one opened "as Administrator"**, so that everything ends up in your own user profile. After installing a tool, **close the window and open a new one**, otherwise the new command isn't found yet.

### Claude

1. Install Claude Code:
   ```powershell
   irm https://claude.ai/install.ps1 | iex
   ```
2. Open a new PowerShell window and run `claude`.
3. When asked how to log in, choose your **Claude account with a subscription** (Pro, Max, Team or Enterprise), not an API key. A browser window opens; sign in and confirm.
4. When the Claude Code prompt appears, the login is saved. You can quit with `/exit`.

If you're already logged in with an API key, run `/login` inside `claude` to switch to your subscription account.

### ChatGPT

1. Install the Codex CLI. It needs [Node.js](https://nodejs.org):
   ```powershell
   npm i -g @openai/codex
   ```
2. Open a new PowerShell window and run `codex login`.
3. Choose **Sign in with ChatGPT** (not API key). A browser window opens; sign in with the account that has your ChatGPT subscription.
4. As soon as the terminal reports a successful login, you're done.

### Gemini

Google no longer serves the old Gemini CLI for personal accounts, so Gemini limits come from **Antigravity**.

1. Install the Antigravity CLI:
   ```powershell
   irm https://antigravity.google/cli/install.ps1 | iex
   ```
2. Open a new PowerShell window. Change to a folder of your own **before** starting it, not `C:\Windows\System32`:
   ```powershell
   mkdir ~\agy -Force; cd ~\agy
   agy
   ```
3. When asked *"Do you trust the contents of this project?"*, choose **Yes, I trust this folder**. That's safe here because the folder is empty.
4. Sign in with your Google account in the browser. When the Antigravity chat screen appears, the login is saved. You can quit with `/quit` or Ctrl+C.

The monitor itself always runs `agy` in its own empty temp folder in report-only mode, so the trust question never comes up there.

### GitHub Copilot

1. Right-click any of the monitor's tray icons and choose **GitHub Copilot anmelden…**.
2. A small window shows a code. Click **Code kopieren & GitHub öffnen**: the code is copied to the clipboard and `github.com/login/device` opens.
3. Paste the code, click **Continue** and then **Authorize**. The window closes by itself.

The monitor signs in the same way VS Code does and only requests read access to your profile (`read:user`). The token is stored encrypted with Windows DPAPI. If you're already signed in with the GitHub CLI (`gh auth login`) or a Copilot editor plugin, the monitor tries that login first, and you may not need this step at all.

### Checking that it worked

- Left-click a tray icon. Each provider should show filled bars with percentages and reset times. Click **Aktualisieren** to refresh at once.
- A **grey corner** on an icon means no login was found for that provider. The detail view says exactly what's missing.
- A **white corner** means the login was found, but the last refresh failed. The reason is in the detail view.

| Message in the detail view | What to do |
|---|---|
| *Kein Claude-Code-Login gefunden* | Do the Claude steps above. |
| *nur API-Key* (Claude or Codex) | Sign in again with your subscription account (`/login` in `claude`, or `codex login`). |
| *Token abgelaufen / konnte nicht erneuert werden* | Start `claude` or `codex` once and use it briefly. If that doesn't help, sign in again. |
| *Gemini läuft für Privatkonten über Antigravity* | `agy` isn't installed or wasn't found. Do the Gemini steps above. |
| *Antigravity ist nicht angemeldet* | Run `agy` in your own folder and sign in again. |
| *Nicht mit GitHub verbunden* / *Token wird abgelehnt* | Use *GitHub Copilot anmelden…* again. Check that your GitHub account actually has Copilot. |
| *zu viele Abfragen (429)* | Nothing to do. The provider is paused for a few minutes automatically. |

## Usage

- **Left-click** an icon to open or close the detail view. `Esc` closes it and `F5` refreshes.
- **Right-click** an icon for the menu: refresh, autostart on/off, Copilot login, settings, log, quit.
- Click a **provider name** in the detail view to open that provider's usage page in your browser.

## Settings

Right-click → *Einstellungen (config.json)* opens `%APPDATA%\AIUsageMonitor\config.json`:

```json
{
  "configVersion": 2,
  "refreshMinutes": 1,
  "providers": { "claude": true, "chatgpt": true, "gemini": true, "copilot": true },
  "claudeAllowTokenRefresh": true,
  "codexAllowTokenRefresh": true,
  "copilotToken": ""
}
```

| Key | Meaning |
|---|---|
| `refreshMinutes` | Refresh interval in minutes (minimum 1). Gemini is refreshed at most every 5 minutes. |
| `providers` | Show or hide individual providers. |
| `claudeAllowTokenRefresh` / `codexAllowTokenRefresh` | When the CLI's access token has expired, the monitor renews it and writes it back to the CLI's credential file, as the CLI itself would. Set to `false` to never touch those files; you then have to start `claude` / `codex` now and then. |
| `copilotToken` | Filled automatically by *GitHub Copilot anmelden…*, stored encrypted with Windows DPAPI. |

Changes are picked up on the next refresh.

## How it works

| Provider | Data source |
|---|---|
| Claude | `GET https://api.anthropic.com/api/oauth/usage` with the Claude Code OAuth token (the same numbers as *claude.ai → Settings → Usage*) |
| ChatGPT | `GET https://chatgpt.com/backend-api/wham/usage` with the Codex CLI ChatGPT token |
| Gemini | `agy -p /usage --output-format json`, the Antigravity CLI's own usage report (no prompt is sent and no quota is used) |
| Copilot | `GET https://api.github.com/copilot_internal/user` |

Since 18 June 2026, Google no longer serves the old Gemini CLI for personal accounts (free, AI Pro and AI Ultra). Gemini limits therefore come from Antigravity.

On HTTP 429 (too many requests), only the affected provider is paused for 2, 4, 8 and up to 15 minutes. If the Antigravity CLI fails (for example because you're not signed in), Gemini is paused for 15 minutes. Each Antigravity check runs in a Windows job object, so no `agy` processes are left behind.

### Privacy and security

- Credentials are read locally and are only ever sent to the provider they belong to (Anthropic, OpenAI, Google or GitHub). There is no telemetry and no other server.
- Tokens are never written to the log (`log.txt` next to the EXE).
- The source is a few hundred lines of plain C#. Read it before you run it, then build it yourself.

> ⚠️ **Disclaimer:** This is an unofficial tool and is not affiliated with Anthropic, OpenAI, Google or GitHub. It uses **undocumented endpoints** that can change or break at any time. All trademarks belong to their respective owners.

## Building manually and tests

```bat
build.bat            :: builds AIUsageMonitor.exe and starts it
build.bat nostart    :: builds only
```

The tests in `tests/Test.cs` check how each provider's responses are parsed, using sample data, and they render preview images. They run with Mono, for example on Linux:

```bash
mcs -langversion:5 -target:exe -out:test.exe -main:AIUsageMonitor.TestMain \
    -r:System.Web.Extensions -r:System.Windows.Forms -r:System.Drawing -r:System.Security \
    src/*.cs tests/Test.cs
xvfb-run -a mono test.exe out/
```

The code deliberately sticks to **C# 5**, so that it compiles with the `csc.exe` built into Windows.

| File | Contents |
|---|---|
| `src/Providers.cs` | Claude, ChatGPT and Copilot: fetching, token refresh and response parsing |
| `src/Antigravity.cs` | Gemini via the Antigravity CLI (job object, report parsing) |
| `src/UI.cs` | tray icon rendering, detail view, Copilot login, app logic |
| `src/Tray.cs` | own tray icons with a stable GUID per provider |
| `src/Util.cs` | HTTP, JSON, config, log, formatting |

## Credits

The undocumented usage endpoints and response formats were figured out by looking at the excellent macOS menu bar app [CodexBar](https://github.com/steipete/CodexBar) by Peter Steinberger.

## Deutsch (Kurzfassung)

Ein kleines Tray-Programm für Windows. Es zeigt für Claude, ChatGPT, Gemini und GitHub Copilot, wie viel vom 5-Stunden- und Wochenlimit noch frei ist. Die Balkenfarben folgen einer Ampel von dunkelgrün bis schwarz, und ein farbiger Streifen unter den Balken zeigt den Anbieter. Ein Klick öffnet die Details mit den Reset-Zeiten.

So richtest du es ein:

1. Repo herunterladen.
2. `install.bat` doppelklicken.
3. Pro Abo einmal im offiziellen Tool anmelden. Am besten in einer normalen PowerShell (nicht als Administrator) und nach jeder Installation in einem neuen Fenster:
   - **Claude:** `irm https://claude.ai/install.ps1 | iex`, dann `claude` starten und „Claude account with subscription“ wählen.
   - **ChatGPT:** `npm i -g @openai/codex`, dann `codex login` und „Sign in with ChatGPT“ wählen.
   - **Gemini:** `irm https://antigravity.google/cli/install.ps1 | iex`, dann in einem eigenen Ordner (`mkdir ~\agy -Force; cd ~\agy`) `agy` starten, den Ordner freigeben und mit Google anmelden.
   - **Copilot:** Rechtsklick auf ein Tray-Symbol → *GitHub Copilot anmelden…*, den Code auf github.com/login/device eingeben und bestätigen.

   Die ausführliche Anleitung mit Fehlerbehebung steht oben unter [First-time sign-in](#first-time-sign-in).

Die Oberfläche ist auf Deutsch.

## License

[MIT](LICENSE)
