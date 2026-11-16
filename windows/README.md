# WARP Connect for Windows

The same one-button WARP VPN, native on the desktop — matching the Android app's UI.

![shield](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6) ![license](https://img.shields.io/badge/license-MIT-blue)

- Native .NET Framework app with the app's layout (navy gradient, the glowing CONNECT disc, mono readouts, activity log)
- Engine is the same Go warpscout engine cross-compiled for `windows/amd64` (CGO off)
- Tunnel: WinTUN adapter, account IPv4 `/32`, split `0.0.0.0/1` + `128.0.0.0/1` routes via the adapter, `/32` host route for the endpoint pinned to the physical gateway, 1.1.1.1 DNS on the adapter
- Real handshake + data verification — the same honest gates as the Android app (handshake + 1.1.1.1:443 TCP cross-check via rx_bytes delta)
- Auto-heal: if the tunnel drops, health checks (1.1.1.1:443 every few seconds) and tap the disc to reconnect

## Build

Requires Go on PATH and Windows 10/11 with .NET Framework 4.x (present by default).
From the repo root:

```powershell
.\windows\build.ps1
```

Output (recreated by the script): `windows\bin\WARP Connect\`

- `WARP Connect.exe` — the app (requires Administrator — WinTUN creates a virtual adapter)
- `warpscout.exe` — the engine
- `LICENSE.txt`, `README.md`

## First run

1. Double-click `WARP Connect.exe` and approve the UAC prompt.
2. Tap **CONNECT**. If no WARP account exists yet the app registers one (direct Cloudflare API → relay proxies → tunnel bootstrap; a few minutes on the first run).
3. On networks with no winner cached, the app runs `warpscout scan -best` (1–3 minutes on a heavily filtered line) and remembers the endpoint for this network.
4. If the endpoint is torn down by your network, the app notices and offers a retry.

The app downloads the official **WinTUN 0.14.1** driver (GPLv2, WireGuard LLC) from wintun.net on first run and places `wintun.dll` next to `warpscout.exe`. Note: the WinTUN binary is fetched separately from this repo (which ships no driver).

## Honest limits

- **Requires Administrator** — WinTUN + route changes are not optional on Windows.
- **IPv4 only** for now (matching the Android app's default experience).
- **Unsigned** — SmartScreen will warn "unknown publisher" until a code-signing cert is purchased; that is the same situation as the Android APK sideload.
- The app's engine and network verification are identical code paths to the Android version, but the desktop flow has not been proven across every Windows network configuration (routers, filters, VPNs of their own). If the tunnel comes up with wrong routes, `warpscout trust` has `Ctrl+C` teardown; the app removes routes and the adapter on exit.
- WinTUN is the only supported adapter technology; you cannot use this with a third-party VPN already holding the adapter (shut that off first).

## File overview

| File | Purpose |
|---|---|
| `app.manifest` | requireAdministrator + DPI awareness |
| `src/Program.cs` | entry, single-instance, dark title bar via DWM |
| `src/MainForm.cs` | layout, state machine, connect/disconnect/speed-test flow |
| `src/DiscButton.cs` | the glowing CONNECT/DISCONNECT disc (custom paint + pulse) |
| `src/Engine.cs` | process wrapper — acquires account/endpoint, starts/stops tunnel, streams logs |
| `src/Settings.cs` | per-machine JSON under `%APPDATA%\WarpConnect\settings.json` |
| `src/Net.cs` | Cloudflare speed test through the tunnel, latency via ICMP ping, exit-info via `ip-api.com` |
| `src/Wintun.cs` | WinTUN download + install on first run (GPL, WireGuard LLC) |
| `build.ps1` | one-shot build: engine → icon → app → dist folder |

Developed by **Sithu**.
