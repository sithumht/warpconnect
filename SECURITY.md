# Security Policy

## Supported versions

| Version | Supported |
|---------|-----------|
| 1.0.x   | Yes       |

WARP Connect is distributed as a sideloaded APK outside Google Play; fixes
ship in new releases, so staying current matters.

## Reporting a vulnerability

Please report security issues **privately** via GitHub Security Advisories:

<https://github.com/sithumht/warpconnect/security/advisories/new>

Include:

- the WARP Connect version (Settings is not shown in-app; it is the APK
  version — 1.0.1 for current releases),
- the Android version and device,
- a description of the issue and, if possible, reproduction steps.

Please do not open public issues for security bugs until a fix is available.

## Scope notes

- The app is a VPN client; bugs that leak traffic outside the tunnel, or that
  accept spoofed WARP endpoints silently, are treated as security issues.
- Bugs in the engine code under `engine/` (a vendored fork of
  [warpscout](https://github.com/openwarpkit/warpscout-android)) should be
  reported here too — a fix in the fork alone keeps you safe.
- The stats Worker (`stats.vanzconnect.online`) serves aggregate endpoint
  telemetry; issues there are in scope.
- Your WARP account is a throwaway device identity stored in app-private
  storage; losing or compromising it does not affect any other identity.
