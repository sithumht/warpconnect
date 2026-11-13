// WARP Connect Telegram bot.
//
// Serves the latest release APK to anyone who messages the bot — built for
// Myanmar users who get the app via a shared t.me link. The worker fetches
// the APK from GitHub once per release, streams it to Telegram, and caches
// the returned file_id in KV; every later send is an instant file_id send.
//
// Commands (case-insensitive; also work as /start@BotName in groups):
//   /start     welcome message + the APK
//   /apk       the APK again
//   /download  same as /apk
//   /sha       SHA-256 of the current release asset
//   /help      command list
//
// Security: the webhook path carries a shared secret checked against the
// X-Telegram-Bot-Api-Secret-Token header Telegram attaches when the webhook
// is registered with secret_token — everyone else gets 403.
//
// The GitHub releases API is unauthenticated (60 req/h per IP); the latest
// release object is cached in KV for 10 minutes to survive bursts.

const REPO = "sithumht/warpconnect";
const USER_AGENT = "warpconnect-bot (Cloudflare Workers)";
const RELEASES_PAGE = `https://github.com/${REPO}/releases/latest`;
const APK_RE = /^warpconnect-v\d+(\.\d+)*\.apk$/;

async function fetchLatestRelease(env) {
  const cached = await env.BOT_KV.get("gh:latest", "json");
  if (cached) return cached;
  const res = await fetch(`https://api.github.com/repos/${REPO}/releases/latest`, {
    headers: { "User-Agent": USER_AGENT, Accept: "application/vnd.github+json" },
  });
  if (!res.ok) {
    throw new Error(`github releases/latest: HTTP ${res.status}`);
  }
  const data = await res.json();
  await env.BOT_KV.put("gh:latest", JSON.stringify(data), { expirationTtl: 600 });
  return data;
}

function findApkAsset(release) {
  return (release.assets || []).find((a) => APK_RE.test(a.name));
}

function releaseSha256(release) {
  const m = /SHA-?256[^0-9a-f]*([0-9a-f]{64})/i.exec(release.body || "");
  return m ? m[1].toLowerCase() : null;
}

function captionFor(release, asset) {
  const sha = releaseSha256(release);
  const lines = [
    `<b>WARP Connect ${release.tag_name || ""}</b>`,
    `တစ်ချက်နှိပ်ရုံဖြင့် WARP VPN — ATOM / MPT / Ooredoo / Mytel`,
    `<b>${asset.name}</b> · ~${Math.round(asset.size / 1024 / 1024)} MB`,
  ];
  if (sha) lines.push(`SHA-256 <code>${sha}</code>`);
  lines.push(`<i>Install: download → tap APK → Allow "Install unknown apps" → CONNECT`);
  lines.push(`Play Protect သတိပေးလျှင်: <b>More details → Install anyway</b>`);
  lines.push(`မြန်မာဘာသာ လမ်းညွှန်: <a href="https://github.com/${REPO}/blob/main/README.my.md">README.my.md</a></i>`);
  return lines.join("\n");
}

function helpText() {
  return [
    `<b>WARP Connect bot</b> — one-button WARP VPN for Myanmar.`,
    ``,
    `/start — welcome + the APK`,
    `/apk — the latest APK (တစ်ခုတည်း download လုပ်ပါ)`,
    `/sha — SHA-256 checksum of the current APK`,
    `/help — this message`,
    ``,
    `Install guide (မြန်မာဘာသာ): https://github.com/${REPO}/blob/main/README.my.md`,
  ].join("\n");
}

async function tg(env, method, payload) {
  const res = await fetch(`https://api.telegram.org/bot${env.TELEGRAM_BOT_TOKEN}/${method}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload),
  });
  const data = await res.json().catch(() => ({}));
  if (!data.ok) console.log(`telegram ${method} failed`, JSON.stringify(data));
  return data;
}

async function sendText(env, chatId, text) {
  return tg(env, "sendMessage", { chat_id: chatId, text, parse_mode: "HTML", disable_web_page_preview: true });
}

async function sendApk(env, chatId) {
  // 3s per-chat cooldown against accidental double-taps and doc-spam.
  if (await env.BOT_KV.get(`cooldown:${chatId}`)) return ok();
  await env.BOT_KV.put(`cooldown:${chatId}`, "1", { expirationTtl: 3 });

  let release;
  try {
    release = await fetchLatestRelease(env);
  } catch (e) {
    console.log("release lookup failed", String(e));
    await sendText(env, chatId, `GitHub ကို ချိတ်မရသေး (HTTP error) — အနည်းငယ်ကြာမှ ထပ်ကြိုးစားပါ။\nManual: ${RELEASES_PAGE}`);
    return ok();
  }

  const asset = findApkAsset(release);
  if (!asset) {
    await sendText(env, chatId, `😕 Latest release has no APK yet.\nGet it manually: ${RELEASES_PAGE}`);
    return ok();
  }

  const tag = String(release.tag_name || "latest");
  const caption = captionFor(release, asset);

  const cachedId = await env.BOT_KV.get(`apk:${tag}`);
  if (cachedId) {
    const sent = await tg(env, "sendDocument", { chat_id: chatId, document: cachedId, caption, parse_mode: "HTML" });
    if (sent.ok) return ok();
    // Cached file_id can go stale (release asset replaced); fall through and re-upload.
    console.log(`cached file_id for ${tag} rejected, re-uploading`);
  }

  // First send of this version: server-side fetch from GitHub, then upload to Telegram.
  const apkRes = await fetch(asset.browser_download_url, { headers: { "User-Agent": USER_AGENT } });
  if (!apkRes.ok) {
    await sendText(env, chatId, `APK download from GitHub failed (HTTP ${apkRes.status}).\nManual: ${asset.browser_download_url}`);
    return ok();
  }
  const bytes = await apkRes.arrayBuffer();
  const form = new FormData();
  form.append("chat_id", String(chatId));
  form.append("caption", caption);
  form.append("parse_mode", "HTML");
  form.append("document", new File([bytes], asset.name, { type: "application/vnd.android.package-archive" }), asset.name);
  const upRes = await fetch(`https://api.telegram.org/bot${env.TELEGRAM_BOT_TOKEN}/sendDocument`, { method: "POST", body: form });
  const up = await upRes.json().catch(() => ({}));
  if (up.ok && up.result && up.result.document && up.result.document.file_id) {
    // Telegram keeps the file for bots forever; 180-day TTL is just hygiene.
    await env.BOT_KV.put(`apk:${tag}`, up.result.document.file_id, { expirationTtl: 180 * 86400 });
  } else {
    console.log("upload failed", JSON.stringify(up));
    await sendText(env, chatId, `APK ပေးပို့မှု မအောင်မြင်ပါ (${(up && up.description) || "Telegram error"}).\nManual: ${RELEASES_PAGE}`);
  }
  return ok();
}

async function sendSha(env, chatId) {
  try {
    const release = await fetchLatestRelease(env);
    const asset = findApkAsset(release);
    if (!asset) return sendText(env, chatId, `Latest release has no APK yet.\nManual: ${RELEASES_PAGE}`);
    const sha = releaseSha256(release) || "(SHA-256 not found in release notes)";
    const size = asset.size ? `~${Math.round(asset.size / 1024 / 1024)} MB` : "";
    await sendText(env, chatId, `<b>${asset.name}</b> ${size}\nSHA-256 <code>${sha}</code>\nVerify: <code>Get-FileHash</code> သို့ sha256sum`);
  } catch (e) {
    await sendText(env, chatId, `GitHub ကို ချိတ်မရသေး — အနည်းငယ်ကြာမှ ထပ်ကြိုးစားပါ။\nManual: ${RELEASES_PAGE}`);
  }
  return ok();
}

function ok() {
  return new Response("ok", { status: 200 });
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (request.method === "GET" && url.pathname === "/health") {
      return Response.json({ ok: true, service: "warpconnect-bot" });
    }
    if (request.method !== "POST" || url.pathname !== "/webhook") {
      return new Response("not found", { status: 404 });
    }
    if (env.WEBHOOK_SECRET) {
      const got = request.headers.get("X-Telegram-Bot-Api-Secret-Token") || "";
      if (got !== env.WEBHOOK_SECRET) return new Response("forbidden", { status: 403 });
    }

    let update;
    try {
      update = await request.json();
    } catch {
      return ok();
    }

    const msg = update && update.message;
    if (!msg || !msg.chat || typeof msg.text !== "string") return ok();

    const chatId = msg.chat.id;
    const raw = msg.text.trim().split(/\s+/)[0].toLowerCase();
    const command = raw.split("@")[0];

    switch (command) {
      case "/start":
        await sendText(env, chatId, `${helpText()}\n\nမင်္ဂလာပါ — APK ကို အောက်မှာ ပေးထားပါတယ် 👇`);
        return sendApk(env, chatId);
      case "/apk":
      case "/download":
        return sendApk(env, chatId);
      case "/sha":
      case "/checksum":
        return sendSha(env, chatId);
      case "/help":
        return sendText(env, chatId, helpText());
      default:
        return sendText(env, chatId, `Commands: /start · /apk · /sha · /help\nမြန်မာဘာသာ လမ်းညွှန်: https://github.com/${REPO}/blob/main/README.my.md`);
    }
  },
};
