# WARP Connect — Telegram bot

Sends the latest release APK to anyone who messages the bot. Built for
Myanmar users who receive the app through a shared `t.me/<bot>` link —
Facebook/Telegram is how this distribution channel actually works there.

**Bot username (after @BotFather setup):** see the root README once the bot
is live. The worker serves it at `https://warpconnect-bot.<subdomain>.workers.dev`.

## What it does

| Command | Behavior |
|---|---|
| `/start` | Welcome + Burmese/English intro, then the APK |
| `/apk`, `/download` | The latest release APK |
| `/sha`, `/checksum` | SHA-256 of the current release asset |
| `/help` | Command list |
| anything else | Brief hint |

## How it works

1. Telegram POSTs every message update to `/webhook` (registered with
   `secret_token`; the worker rejects anything without it).
2. The worker asks GitHub for the latest release (`releases/latest`),
   finds the `warpconnect-v*.apk` asset, and looks up its `file_id` in KV.
3. If cached: instant `sendDocument` by `file_id`.
   If not (first send of a new version): downloads the APK from the
   GitHub release URL into the worker, uploads it to Telegram, and caches
   the resulting `file_id` per tag. Later sends for that same version are
   instant. A new release therefore self-provisions on its first `/apk`.
4. Per-chat 3 s cooldown against double-taps.

KV namespace: `BOT_KV` (id `fc42c9dd1072409e998088f13e7529ec`).

## Profile & UX (applied, reproducible)

**UX (worker):** `/start` greets by first name (`မင်္ဂလာပါ <name>`) and attaches a
persistent reply keyboard — `⬇️ Download APK · 🔑 SHA-256 · 📖 Install Guide (မြန်မာ) ·
🌐 Website` — no commands to memorize; unknown messages re-attach it. Keyboard
buttons arrive as plain text and are matched verbatim in the handler.

**Profile (Bot API, `language_code` `en` and `my`):**
- Name (my): `WARP Connect မြန်မာ` — default name left as `Warp Connect Bot - Official` (anti-impersonation)
- Description + short description (en/my) — full install flow, APK privacy lines, site + source links
- Command menu (en/my): `/start`, `/apk`, `/sha`, `/help`

**Avatar:** the app launcher icon, rasterized to JPG — `docs/brand/warpconnect-icon-512.jpg`
(see repo root `docs/brand/`). To re-apply everything, use the JSON payloads from
git history or recreate them following the same shapes and `language_code`.

## Deploy

```powershell
npx wrangler deploy
```

then register the webhook once (needs the bot token):

```powershell
$token = '<bot token from @BotFather>'
$secret = '<random 32+ char secret>'          # e.g. openssl rand -hex 32
npx wrangler secret put TELEGRAM_BOT_TOKEN   # paste the token when prompted
npx wrangler secret put WEBHOOK_SECRET       # paste the secret
curl.exe -s "https://api.telegram.org/bot$token/setWebhook?url=https://warpconnect-bot.<subdomain>.workers.dev/webhook&secret_token=$secret"
curl.exe -s "https://api.telegram.org/bot$token/getWebhookInfo"
```

`getWebhookInfo` should report `pending_update_count: 0` and your URL.

## Testing

Once the webhook is set: message the bot `/start` from any Telegram account —
you should receive the APK with the install caption (~48 MB).

## Notes / limits

- Telegram `sendDocument` caps bot uploads at **50 MB**; the APK is ~48 MB.
  If the app ever grows past 50 MB this design breaks — split per-ABI APKs
  or switch to `t.me/<bot>?start=` deep-link downloads.
- Server-side rendering of the ~48 MB upload happens inside a Worker; it
  fits the 128 MB memory limit, but the first send of a fresh release is
  the slowest path (~5–20 s). Subsequent sends are instant.
- R2 would serve this even better (no 50 MB bot cap for the direct-file
  route, zero egress) but R2 is **not enabled** on the Cloudflare account
  yet (dashboard → R2 → enable → no credit card needed for free tier).
  Enabling it later lets the bot serve the APK via a `https://` link as a
  fallback when Telegram uploads fail.
- The APK URL comes from GitHub; in Myanmar GitHub availability fluctuates.
  The bot works around it for users (worker fetches once, Telegram caches),
  but a total GitHub block breaks fresh-release propagation until it lifts.
