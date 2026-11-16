# WARP Connect — Windows (WPF)

Native .NET 8 WPF port of WARP Connect with the same single-button UX as the
Android app. This is the preferred Windows build; `windows\` (WinForms) is kept
as a fallback reference that already shipped.

## Highlights

- One-button CONNECT/DISCONNECT — no screens, no settings
- Same architecture as Android: warpscout engine (`windows/amd64`, CGO-free),
  WinTUN adapter, account IPv4 /32, split routes, endpoint host route,
  AmneziaWG obfuscation with rotating I1 profiles
- Same stats reporting as the Android app: on every verified connect, endpoint
  + network profile (Wi-Fi SSID via `netsh wlan show interfaces` or `eth`) +
  ASN/ISP/city/country via ip-api shipped to `warpconnect-stats` (/report)
  with heartbeat every 60s and `/bye` on clean disconnect
- Share stats toggle is a per-install last word: it POSTs only when ON the
  first connect after enabling; nothing is sent if the user turns it OFF
- Background engine never runs as a service; the tunnel lives only while the
  app has it up — kill it and all routes + the adapter are torn down

## Build

Requirements: Go 1.24+ and .NET 8 SDK (or portable install). Then:

```powershell
.\windows\wpf\build.ps1
```

Output (self-contained): `windows\wpf\bin\Release\` with `WARP Connect.exe`,
plus `warpscout.exe` (the engine).

## First run

1. Double-click `WARP Connect.exe`, approve the UAC prompt.
2. If no WARP account exists it registers one (2–3 min on a filtered line).
3. On first connect, the app downloads WinTUN 0.14.1 (~600 KB, GPLv2,
   WireGuard LLC) from wintun.net and places `wintun.dll` next to `warpscout.exe`.
4. Tap CONNECT. The app inherits the Android app's endpoint ladder:
   stats-recommendation → cached winner → bundled seeds → full scan.

## Safety

Reliable for both phone and PC users on restricted networks. Same posture as
the Android app: CSP-compliant first connect, stats opt-out, open source.

## File overview

| File | Purpose |
|---|---|
| `app.manifest` | `requireAdministrator` (WinTUN needs it) + DPI awareness |
| `App.xaml` / `App.xaml.cs` | resources (Android palette) + startup, with crash-log fallback |
| `MainWindow.xaml` | the entire UI — gradient background, disc with glow, activity log |
| `MainWindow.xaml.cs` | state machine, connect/wire-up, speed test, stats toggle, heartbeats |
| `Engine.cs` | process runner + `warpscout tunnel` stdin-quit for clean teardown |
| `Net.cs` | Cloudflare speed-test endpoint through tunnel, ping via ICMP, ip-api lookup |
| `Report.cs` | telemetry endpoint POSTs (matches Android payload keys exactly) |
| `Settings.cs` | `%APPDATA%\WarpConnect\settings.json` — account, winner endpoint, device id, toggle |
| `Wintun.cs` | first-run WinTUN download/extract |
| `build.ps1` | one-shot engine + publish build |

Developed by **Sithu**. Open source under the same license as the parent repo.
