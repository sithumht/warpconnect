# WARP Connect — website

Static site for the WARP Connect Android app, served by a Cloudflare Worker
with static assets. Lives in this repo on purpose: the page and the app
release it links to always stay in sync.

## Contents

- `public/index.html` — the page. Bilingual (English / မြန်မာ), auto-toggles
  from the header, persisted in `localStorage`.
- `public/style.css` — the app's visual language: navy gradient, the glowing
  CONNECT disc, letter-spaced eyebrows, JetBrains Mono data readouts.
- `public/app.js` — language toggle only.
- `public/screenshots/` — the same cropped captures as `docs/screenshots/`.

## Develop

Open `public/index.html` in a browser and edit. No build step, no bundler.

## Deploy

```powershell
npx wrangler deploy
```

requires a Cloudflare API token (`CLOUDFLARE_API_TOKEN`) with **Workers Scripts: Edit**
or a logged-in `wrangler login`. The site is live at **https://site.vanzconnect.online/**
(Cloudflare Workers Custom Domain, zone `vanzconnect.online`), with
`https://warpconnect-site.<your-subdomain>.workers.dev` kept as a fallback via
`workers_dev = true` in `wrangler.jsonc`.

## Custom domain (already configured)

The custom domain is declared in `wrangler.jsonc` (`routes` with
`custom_domain: true`). Cloudflare auto-creates the proxied DNS record on
deploy. To change it, edit the route there and redeploy — the dashboard
route page shows the same binding.

## Editing rules

- Keep both languages in sync — if you touch the English copy, touch the
  Burmese one (or remove the pair), never just one.
- The APK link must point at a real, current release asset. Update both
  places (`href` in two spots) and the SHA-256 caption when a new version
  ships.
- Do not hot-link screenshots from `docs/` — copy them here so the site
  works even if the repo moves.
