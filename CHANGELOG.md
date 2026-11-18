# Changelog

All notable changes to WARP Connect are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.0.3] - 2026-11-18

### Fixed
- **Version metadata**: v1.0.2's APK shipped with `versionName "1.0.1"` /
  `versionCode 2` — the bump was missed. This release labels itself 1.0.3 /
  code 3 correctly.
- Windows: the connect ladder now queries the stats board for an
  ASN-matched endpoint **before** falling back to seeds or a full scan
  (`/recommended`, ASN preferred over ISP substring), mirroring the Android
  fast path; the recommendation is validated and tried only after the same
  handshake check as any other candidate.
- Stats worker source (`stats-worker/`) is now versioned in the repo.

### Changed
- Stats dashboard redesigned to the app's visual language (radial-glow navy,
  glowing app-disc brand mark, teal eyebrow captions, mono readouts,
  green-tinted online-now panel, sign-out added to both pages). Deploy is
  separate from this release (the board is a Cloudflare Worker); current
  build: `stats-worker/src/worker.js`.

## [1.0.2] - 2026-11-16

Two delivery targets in one release: the Android APK and the Windows desktop build (new).

### Added
- Windows desktop build ships in this release: WPF port (warpscout engine + WinTUN), single-file self-contained `WARP Connect.exe` with the engine and driver bundled. Endpoint ladder: cached winner → ATOM-verified seeds → full scan. HTTPS-first exit-info chain (ipwho.is → ifconfig.co → ip-api.com). App icon; WinTUN bundled (no first-run download in the release zip).
- Windows (WPF + legacy WinForms): stats sharing is now always on and cannot be disabled — the SHARE STATS toggle is removed; reporting, heartbeat and `bye` run on every connect.

### Changed
- Android stats uplink mirrors the Windows fix: report posts 2.5 s after "tunnel up" (DNS on the VPN interface needs a beat), retries once after 4 s on failure, logs the outcome in the activity card.
- Windows stats uplink: 2.5 s settle after tunnel-up, one retry, post/heartbeat status logged; report payload now carries the pre-tunnel line identity (ISP/ASN/company/country/city) built before any routes flip — never the WARP exit.

### Fixed
- Windows stats never reached the dashboard: reports went out empty-endpoint (rejected 400 by the worker) and later before adapter DNS was routable (`No such host is known`); the result was discarded as "Stats reported". Both fixed; reports verified on-line end-to-end (rows visible on the stats board).
- NETWORK readout showed the WARP exit or nothing; it now shows the raw line (wrapped onto two lines when the org is long).
- Ready-state CONNECT disc no longer draws the white specular cap.

## [1.0.1] - 2026-11-13

First public release (APK via the Releases page).

### Added
- One-button connect/disconnect with automatic WARP account registration
  through direct Cloudflare API, BaseRelay proxies, or the WARP-tunnel
  fallback (AmneziaWG bootstrap keys).
- Per-network caching: endpoints that carried traffic on this network are
  tried first (per-network TTL), keyed to Wi-Fi SSID or carrier + MCC/MNC.
- Bundled list of Cloudflare WARP endpoints verified on ATOM Myanmar, tried
  before a full scan on fresh installs.
- Self-healing tunnel: liveness probes, two consecutive failures trigger
  reconnect with backoff; per-network silence list mutes endpoints that fail
  repeatedly; healing prefers previously successful endpoints.
- Stats uplink to a Cloudflare Worker + D1 backend (reports, heartbeat,
  `bye` on clean disconnect) with a password + TOTP gated dashboard and
  per-network recommendation API used as the fast path before scanning.
- Firebase Crashlytics and Analytics (connect attempts, successes/failures,
  speed tests, disconnects).
- Speed/latency readout in the app (`Run speed test`).
- Public license-free release: no sign-in, no activation code.

### Fixed
- Fresh-install cold start now lands directly on the Ready state; CONNECT
  works without any gating step.

### Changed
- Endpoint ladder: stats-recommendation → cached winner → bundled seeds →
  full scan → deep scan (junk-parameter search).

## [1.0.1-rc] - 2026-11 (internal)

- Windows desktop port started: Go engine cross-compiles to `windows/amd64`;
  WinTUN adapter bring-up (routes, DNS, endpoint host route) implemented;
  `warpscout tunnel` CLI added. Not shipped in any release.
