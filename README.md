# Codex Meter

A small native Windows 11 tray app for the limits on your signed-in Codex account. Inspired by [CodexReserve](https://github.com/yourarnav/CodexReserve); this is an independent Windows implementation, with no code or artwork copied from that project.

- The tray number is the **lowest percentage remaining** across your reported windows. A small colored bar makes low quota easy to spot.
- Click for every window, its remaining percentage, reset countdown, and local reset date.
- Reads usage at launch and **every 15 minutes**, plus manual refresh. Resuming Windows refreshes if a poll is due.
- Supports weekly-only accounts, five-hour plus weekly accounts, and multiple usage buckets. Unknown values stay unknown.
- Failed checks show an amber `!`; last known figures are explicitly marked. Reset dates never manufacture fresh quota.
- Right-click for **Refresh now**, **Start with Windows**, or **Quit**. Startup is off until you select it.

## Run

Run `CodexMeter.exe`. Have the Codex desktop app or Codex CLI installed and signed in with your ChatGPT account. Windows may initially put the icon under the tray's **^** overflow: drag it beside the clock to keep it visible.

The published build uses the **.NET 10 Desktop Runtime (x64)**. It is a portable executable; no installer, service, browser, or administrator privileges are required. Keep the executable at a stable path before enabling Start with Windows. Disable that option before deleting or moving it.

## No tokens used for polling

Each poll starts a hidden `codex app-server --listen stdio://`, sends only:

1. `initialize`
2. `initialized`
3. `account/rateLimits/read`

Then it closes its private helper. It never starts a thread or model turn, consumes a reset, purchases credits, or calls a model API. These are account-status requests, not inference. The app does not itself read, copy, or store OAuth tokens; Codex manages its existing sign-in and any required authentication refresh. It stores no usage history, account identifiers, or raw server logs.

Official protocol: [Codex App Server, rate limits](https://learn.chatgpt.com/docs/app-server#6-rate-limits-chatgpt). The local CLI calls its app-server command experimental, so future Codex protocol changes may require an update.

The helper is discovered from the running Codex desktop process, the desktop app's versioned binary directory, or a native Codex CLI installation on PATH. To select an executable explicitly, set `CODEX_METER_CODEX_PATH` to the full path of `codex.exe`. It inherits the normal `CODEX_HOME`, if you use one. Limits belong to that signed-in account, not an individual task.

## Build and focused checks

With the .NET 10 SDK installed:

```powershell
.\build.ps1
dotnet run --project .\tests\CodexMeter.Tests.csproj -c Release
```

The build script keeps temporary files, CLI setup data, and NuGet packages within this repository. No third-party library packages are needed. The checks cover response parsing, unknown values, multiple buckets, the no-model-call message sequence, sign-in errors, and helper cancellation.

For a single real usage check (writes only normalized quota data):

```powershell
Start-Process .\artifacts\publish\CodexMeter.exe -ArgumentList '--check', 'D:\usage-check.json' -WindowStyle Hidden -Wait
```

`--preview <directory>` renders synthetic sample panels for layout review. These images are not live account data.

## Boundaries

This is an unofficial local utility. It has no analytics, notifications, sounds, or model dependencies. Rounded percentages are conservative; the popup shows the last successful fetch time. Automatic updates are not included. The code and executable are unsigned. Native tray interaction and appearance should also be checked on your own display and Windows scaling setting.
