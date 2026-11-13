# Changelog

All notable changes to WARP Connect are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

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
