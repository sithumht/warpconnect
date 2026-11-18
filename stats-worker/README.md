# warpconnect-stats

Cloudflare Worker + D1 backing the WARP Connect stats board — the anonymous
endpoint/carrier/ASN telemetry both apps report on verified connections, and
the `/recommended` fast path the apps use before falling back to a full scan.

**Live at** `https://stats.vanzconnect.online` (custom domain) · also
`https://warpconnect-stats.sithu-mhtvps.workers.dev`

## Endpoints

| Route | Auth | Purpose |
|---|---|---|
| `POST /report` | public (rate-limited 120/min/IP) | app reports one verified connect (endpoint, transport, ssid/carrier/MCC-MNC, ISP, ASN, org, country, city) |
| `POST /heartbeat` | public | ~60 s "device is in the tunnel" ping |
| `POST /bye` | public | clean disconnect — drops the device from "online now" |
| `GET /recommended?transport=&asn=\|isp=` | public | best known-good endpoint for this line (used by the apps before seeds/scan) |
| `GET /` | password + TOTP | the dashboard (aggregate, online-now, aggregate table, recent reports, CSV export) |
| `GET /stats.json` · `/stats.csv` | password + TOTP | machine-readable stats export |
| `/admin/users` (GET/POST) | password + TOTP | manage the (legacy) admin user list |
| `POST /telegram/webhook` | HMAC secret | Telegram bot webhook (approval flow) |

## Layout

- `src/worker.js` — everything: routing, auth, D1 access, HTML/CSS.
- `wrangler.jsonc` — worker config (D1 binding `DB`, `compatibility_date`).

The D1 schema bootstraps itself on first request (`CREATE TABLE IF NOT EXISTS`).

## Deploy

```powershell
npx wrangler deploy
```

Requires `CLOUDFLARE_API_TOKEN` (Workers Scripts:Edit) or `wrangler login`.

## Secrets (set once, via `wrangler secret put`)

| Secret | Purpose |
|---|---|
| `STATS_USER` | dashboard username |
| `STATS_SALT` / `STATS_HASH` / `STATS_PBKDF2_ITER` | password hash for the dashboard login |
| `STATS_TOTP_SECRET` | TOTP seed for the dashboard 2FA (RFC 6238) |
| `STATS_SESSION_KEY` | signs the session cookie |
| `TELEGRAM_BOT_TOKEN` | bot token for the Telegram webhook (sign-in notifications) |
| `TELEGRAM_WEBHOOK_SECRET` | HMAC for the webhook endpoint |

`LOGIN_SECRET` in `src/worker.js` is a legacy product constant (the deep-link
sign-in signature from the license era, retained verbatim for compatibility).
It is not a deployment credential.

## What the apps report

Only things needed to answer "does this endpoint work on this line":
the endpoint's IP:port, the carrier (ATOM/…), MCC/MNC, ASN, org, country/city,
transport (wifi/cell/eth). No browsing history, no identity, no device
fingerprint beyond a random `device` UUID for the online/offline view.
