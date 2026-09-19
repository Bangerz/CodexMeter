# Local validation — 2026-09-19

- Release build passed using .NET SDK 10.0.105, targeting Windows x64.
- 14 focused checks passed: weekly-only accounts, multiple buckets, legacy fallback, unknown and bounded percentages, reset timestamps, exact read-only RPC sequence, authentication errors, and helper cancellation.
- The published executable successfully queried the live signed-in Pro account through the installed Codex app-server. It reported one weekly window with 44% remaining at 07:16 UTC; that is a point-in-time result, not a permanent account value.
- Native popup renders were visually inspected at the current 125% Windows scaling. A tray-number wrapping issue was corrected and the final rendering showed both digits.
- Installed executable: `D:\Tools\CodexMeter\CodexMeter.exe`, 226,483 bytes, using the already-installed .NET 10 Desktop Runtime.
- Launched normally and confirmed the process remained running and responsive. Startup registration was left off.
- The 15-minute interval is fixed in the app; no 15-minute wall-clock soak was performed. Final tray placement, click interaction, and appearance on the user's display remain for the user to confirm.
- Preview PNGs are synthetic UI fixtures and are excluded from Git along with the normalized live check output.
