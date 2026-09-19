## Codex Meter v0.1.0

A minimal native Windows 11 tray display for your Codex limits.

- Remaining percentage in the tray; click for all reported windows and reset times.
- Refreshes every 15 minutes using read-only account-status requests, without model calls or token consumption.
- Weekly-only and multiple-window accounts, manual refresh, stale-data indicators, and optional startup with Windows.
- Percentage digits fit small tray icons, including 40 and 100.
- CC0 1.0 Universal.

### Download

Download **CodexMeter-v0.1.0-win-x64.zip**, extract it, and run **CodexMeter.exe**.

Requires **Windows x64**, the **[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)**, and a signed-in Codex desktop app or native Codex CLI. The runtime is not bundled. The executable is unsigned.

If Windows hides the icon under the **^** overflow, drag it beside the clock. Right-click to enable Start with Windows.

SHA-256 checksums are attached. The ZIP includes the README, CC0 license, sample images, and executable checksum.

### Validation

Release build and 14 focused checks passed. Usage retrieval was verified against a signed-in account. Synthetic popup and icon renderings were inspected, including 16–32 px percentage icons.
