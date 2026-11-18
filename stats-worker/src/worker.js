const VALID_ENDPOINT = /^\d{1,3}(\.\d{1,3}){3}:\d{1,5}$/;
const MAX_LEN = 128;
const PAGE_SIZE = 50;

const CORS = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Methods": "POST, GET, OPTIONS",
  "Access-Control-Allow-Headers": "Content-Type",
};

// ---------------------------------------------------------------------------
// Auth: password (PBKDF2) + TOTP (RFC 6238) -> signed HttpOnly session cookie.
// All dashboard/JSON/CSV paths fail closed unless the secrets are configured.
// ---------------------------------------------------------------------------
const SESSION_COOKIE = "warpconnect.sid";
const SESSION_TTL_SECONDS = 12 * 3600;
const MAX_LOGIN_ATTEMPTS = 5;
const LOGIN_LOCKOUT_MS = 10 * 60 * 1000;
const LOGIN_WINDOW_MS = 10 * 60 * 1000;
const MAX_REPORT_PER_MINUTE = 120;
const ONLINE_WINDOW = 90; // seconds

const encoder = new TextEncoder();
const decoder = new TextDecoder();

function hexToBytes(hex) {
  const out = new Uint8Array(hex.length / 2);
  for (let i = 0; i < hex.length; i += 2) out[i / 2] = parseInt(hex.slice(i, i + 2), 16);
  return out;
}

function bytesToHex(bytes) {
  return [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");
}

function constantTimeEqual(a, b) {
  if (typeof a !== "string" || typeof b !== "string") return false;
  const left = new Uint8Array(encoder.encode(a));
  const right = new Uint8Array(encoder.encode(b));
  const len = Math.max(left.length, right.length);
  let diff = left.length ^ right.length;
  for (let i = 0; i < len; i++) diff |= (left[i] ^ right[i]) | 0;
  return diff === 0;
}

async function hmac(keyBytes, data, algo = "SHA-256") {
  const key = await crypto.subtle.importKey("raw", keyBytes, { name: "HMAC", hash: algo }, false, ["sign"]);
  const sig = await crypto.subtle.sign("HMAC", key, encoder.encode(data));
  return new Uint8Array(sig);
}

async function pbkdf2(password, saltHex, iterations) {
  const baseKey = await crypto.subtle.importKey(
    "raw",
    encoder.encode(password),
    "PBKDF2",
    false,
    ["deriveBits"],
  );
  const bits = await crypto.subtle.deriveBits(
    { name: "PBKDF2", hash: "SHA-256", salt: encoder.encode(saltHex), iterations },
    baseKey,
    256,
  );
  return bytesToHex(new Uint8Array(bits));
}

const BASE32_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

function base32Decode(input) {
  const clean = input.toUpperCase().replace(/=+$/, "").replace(/\s+/g, "");
  const bytes = [];
  let bits = 0;
  let value = 0;
  for (const ch of clean) {
    const idx = BASE32_ALPHABET.indexOf(ch);
    if (idx < 0) continue;
    value = (value << 5) | idx;
    bits += 5;
    if (bits >= 8) {
      bytes.push((value >>> (bits - 8)) & 0xff);
      bits -= 8;
    }
  }
  return new Uint8Array(bytes);
}

async function totp(secretBase32, counter) {
  const key = base32Decode(secretBase32);
  const msg = new Uint8Array(8);
  let n = counter;
  for (let i = 7; i >= 0; i--) {
    msg[i] = n & 0xff;
    n = Math.floor(n / 256);
  }
  const sig = await crypto.subtle.sign(
    "HMAC",
    await crypto.subtle.importKey("raw", key, { name: "HMAC", hash: "SHA-1" }, false, ["sign"]),
    msg,
  );
  const digest = new Uint8Array(sig);
  const offset = digest[digest.length - 1] & 0x0f;
  const code =
    ((digest[offset] & 0x7f) << 24) |
    ((digest[offset + 1] & 0xff) << 16) |
    ((digest[offset + 2] & 0xff) << 8) |
    (digest[offset + 3] & 0xff);
  return String(code % 1_000_000).padStart(6, "0");
}

async function verifyTotp(secretBase32, code) {
  const clean = String(code || "").replace(/\s+/g, "");
  if (!/^\d{6}$/.test(clean)) return false;
  const now = Math.floor(Date.now() / 1000);
  const step = Math.floor(now / 30);
  for (const drift of [-1, 0, 1]) {
    if (constantTimeEqual(await totp(secretBase32, step + drift), clean)) return true;
  }
  return false;
}

const LOGIN_SECRET = "warpconnect-license-secret-DO-NOT-CHANGE";

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
const NONCE_RE = /^[a-f0-9]{16}$/;

/** HMAC-SHA256 of the nonce, hex-encoded — the deep-link signature for Telegram login. */
async function loginSignature(nonce) {
  const key = await crypto.subtle.importKey(
    "raw",
    new TextEncoder().encode(LOGIN_SECRET),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"],
  );
  const sig = await crypto.subtle.sign(
    "HMAC",
    key,
    new TextEncoder().encode("telegram-login:" + String(nonce)),
  );
  return [...new Uint8Array(sig)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

/** The deep link the bot hands to the user to sign in to the app. */
async function loginDeepLink(tid, username, nonce) {
  const sig = await loginSignature(nonce);
  const u = username ? `&username=${encodeURIComponent(username)}` : "";
  return `warpconnect://telegram-login?id=${tid}${u}&nonce=${nonce}&sig=${sig}`;
}

/* ---------- Telegram bot helpers ---------- */

async function tgCall(env, method, params) {
  const token = env.TELEGRAM_BOT_TOKEN;
  if (!token) return false;
  try {
    const res = await fetch(`https://api.telegram.org/bot${token}/${method}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(params),
    });
    if (!res.ok) {
      const detail = await res.text().catch(() => "");
      console.error("telegram", method, res.status, detail);
    }
    return res.ok;
  } catch (err) {
    console.error("telegram fetch failed", method, err);
    return false;
  }
}
const tgSend = (env, chatId, text, replyMarkup) =>
  tgCall(env, "sendMessage", { chat_id: chatId, text, parse_mode: "HTML", ...(replyMarkup ? { reply_markup: replyMarkup } : {}) });
const tgAnswerCallback = (env, callbackQueryId, text) =>
  tgCall(env, "answerCallbackQuery", { callback_query_id: callbackQueryId, text: text || "" });
const tgEditMessageText = (env, chatId, messageId, text) =>
  tgCall(env, "editMessageText", { chat_id: chatId, message_id: messageId, text, parse_mode: "HTML", reply_markup: { inline_keyboard: [] } });

async function handleTelegramUpdate(env, update) {
  const msg = update.message;
  const cb = update.callback_query;

  if (msg && typeof msg.text === "string") {
    const text = msg.text.trim();
    const chatId = msg.chat && msg.chat.id;
    if (!chatId) return;

    if (text === "/start" || text === "/help" || text === "/whoami") {
      await tgSend(
        env,
        chatId,
        "👋 <b>WARP Connect</b>\nOpen the app and tap <b>Sign in with Telegram</b>, then press START here.\n\nYour Telegram ID: <code>" + chatId + "</code>",
      );
      return;
    }

    if (text.startsWith("/start")) {
      // Deep link payload from the app: "<nonce>" or "<nonce>_<deviceUuid>"
      // (underscore separator — Telegram start params cannot carry dots).
      const raw = text.slice(6).trim().toLowerCase();
      const sep = raw.indexOf("_");
      const nonce = sep > 0 ? raw.slice(0, sep) : raw;
      const device = sep > 0 ? raw.slice(sep + 1) : null;
      if (!NONCE_RE.test(nonce)) {
        await tgSend(env, chatId, "That doesn't look like a login link. Open the app and tap <b>Sign in with Telegram</b>.");
        return;
      }
      if (device && !UUID_RE.test(device)) {
        await tgSend(env, chatId, "That doesn't look like a login link. Open the app and tap <b>Sign in with Telegram</b>.");
        return;
      }
      const from = msg.from || {};
      const tid = from.id ? String(from.id) : String(chatId);
      const username = from.username ? String(from.username) : null;
      const firstName = from.first_name ? String(from.first_name) : null;
      const now = Math.floor(Date.now() / 1000);
      const existing = await env.DB
        .prepare("SELECT status, pending_nonce FROM users WHERE telegram_id = ?1")
        .bind(tid)
        .first();
      if (existing && existing.status === "active") {
        await env.DB
          .prepare("UPDATE users SET username = ?2, first_name = ?3, chat_id = ?4, pending_nonce = ?5, updated_at = ?6, device = COALESCE(?7, device) WHERE telegram_id = ?1")
          .bind(tid, username, firstName, String(chatId), nonce, now, device)
          .run();
        const link = await loginDeepLink(tid, username, nonce);
        await tgSend(
          env,
          chatId,
          `✅ <b>Approved.</b>${firstName ? " Welcome back, " + esc(firstName) + "." : ""}\n\n<a href="${link}">🚀 Open WARP Connect</a>\n(tap the link above to go back to the app)`,
        );
        return;
      }
      await env.DB
        .prepare(
          `INSERT INTO users (telegram_id, username, first_name, device, status, pending_nonce, chat_id, created_at, updated_at)
           VALUES (?1, ?2, ?3, ?4, 'pending', ?5, ?6, ?7, ?7)
           ON CONFLICT(telegram_id) DO UPDATE SET
             username = excluded.username,
             first_name = excluded.first_name,
             device = COALESCE(excluded.device, users.device),
             chat_id = excluded.chat_id,
             pending_nonce = excluded.pending_nonce,
             updated_at = excluded.updated_at`,
        )
        .bind(tid, username, firstName, device, nonce, String(chatId), now)
        .run();
      await tgSend(env, chatId, `📭 Signed up — an administrator will approve you shortly, then you'll get the sign-in button here. Your Telegram ID: <code>${tid}</code>`);
      if (env.TELEGRAM_ADMIN_ID) {
        await tgSend(
          env,
          env.TELEGRAM_ADMIN_ID,
          `🆕 <b>Sign-in request</b>\n${firstName ? `<b>${esc(firstName)}</b> ` : ""}${username ? `@${esc(username)} ` : ""}(<code>${tid}</code>)`,
          {
            inline_keyboard: [
              [
                { text: "✅ Approve", callback_data: `approve:${tid}` },
                { text: "❌ Reject", callback_data: `reject:${tid}` },
              ],
            ],
          },
        );
      }
      return;
    }

    await tgSend(env, chatId, "Open the app and tap <b>Sign in with Telegram</b>.");
    return;
  }

  if (cb) {
    const data = String(cb.data || "");
    const fromId = cb.from && cb.from.id;
    if (!env.TELEGRAM_ADMIN_ID || String(fromId) !== String(env.TELEGRAM_ADMIN_ID)) {
      await tgAnswerCallback(env, cb.id, "Not authorized.");
      return;
    }
    const idx = data.indexOf(":");
    const action = idx > 0 ? data.slice(0, idx) : "";
    const tid = idx > 0 ? data.slice(idx + 1) : "";
    if ((action !== "approve" && action !== "reject") || !/^\d{5,20}$/.test(tid)) {
      await tgAnswerCallback(env, cb.id, "Bad request.");
      return;
    }
    const now = Math.floor(Date.now() / 1000);
    const status = action === "approve" ? "active" : "inactive";
    await env.DB
      .prepare("UPDATE users SET status = ?2, updated_at = ?3 WHERE telegram_id = ?1")
      .bind(tid, status, now)
      .run();
    const row = await env.DB
      .prepare("SELECT username, first_name, chat_id, pending_nonce FROM users WHERE telegram_id = ?1")
      .bind(tid)
      .first();
    const uname = row && row.username ? `@${esc(row.username)}` : (row && row.first_name ? esc(row.first_name) : `<code>${tid}</code>`);
    await tgAnswerCallback(env, cb.id, action === "approve" ? "Approved ✓" : "Rejected");
    try {
      await tgEditMessageText(
        env,
        cb.message.chat.id,
        cb.message.message_id,
        `${action === "approve" ? "✅ Approved" : "❌ Rejected"} — ${uname} <code>${tid}</code>`,
      );
    } catch (_) {
      /* message may have been deleted; ignore */
    }
    if (row && row.chat_id) {
      if (status === "active") {
        const nonce = row.pending_nonce;
        if (nonce && NONCE_RE.test(nonce)) {
          const link = await loginDeepLink(tid, row.username, nonce);
          await tgSend(
            env,
            row.chat_id,
            `✅ Approved!\n\n<a href="${link}">🚀 Open WARP Connect</a>\n(tap the link above to go back to the app)`,
          );
        } else {
          await tgSend(env, row.chat_id, "✅ Approved! Open the app and tap <b>Sign in with Telegram</b> again to get your sign-in button.");
        }
      } else {
        await tgSend(env, row.chat_id, "❌ Your access was revoked by the administrator.");
      }
    } else {
      await tgSend(env, env.TELEGRAM_ADMIN_ID, `User <code>${tid}</code> ${status === "active" ? "approved" : "rejected"} but never opened the bot (no chat_id stored).`);
    }
    return;
  }
}

function parseCookies(request) {
  const header = request.headers.get("Cookie") || "";
  const map = {};
  for (const part of header.split(";")) {
    const idx = part.indexOf("=");
    if (idx < 0) continue;
    const name = part.slice(0, idx).trim();
    const value = part.slice(idx + 1).trim();
    if (name) map[name] = decodeURIComponent(value);
  }
  return map;
}

function serializeSessionCookie(user) {
  const exp = Math.floor(Date.now() / 1000) + SESSION_TTL_SECONDS;
  const payload = `${user}|${exp}`;
  return { payload, exp };
}

async function makeSessionCookie(user, sessionKeyHex) {
  const { payload, exp } = serializeSessionCookie(user);
  const sig = await hmac(hexToBytes(sessionKeyHex), payload, "SHA-256");
  return { value: `${payload}|${bytesToHex(sig)}`, exp };
}

function sessionCookieHeader(value, exp) {
  const maxAge = Math.max(0, exp * 1000 - Date.now());
  return `${SESSION_COOKIE}=${encodeURIComponent(value)}; Path=/; Max-Age=${Math.floor(maxAge / 1000)}; HttpOnly; Secure; SameSite=Lax`;
}

async function authed(request, env) {
  const user = env.STATS_USER;
  const sessionKey = env.STATS_SESSION_KEY;
  if (!user || !sessionKey) return false;
  const cookies = parseCookies(request);
  const raw = cookies[SESSION_COOKIE];
  if (!raw) return false;
  const lastPipe = raw.lastIndexOf("|");
  if (lastPipe <= 0) return false;
  const payload = raw.slice(0, lastPipe);
  const sig = raw.slice(lastPipe + 1);
  if (!/^[0-9a-f]{64}$/.test(sig)) return false;
  const parts = payload.split("|");
  if (parts.length !== 2) return false;
  const [cookieUser, expStr] = parts;
  if (cookieUser !== user) return false;
  const exp = Number(expStr);
  if (!Number.isFinite(exp) || exp * 1000 < Date.now()) return false;
  const expected = await hmac(hexToBytes(sessionKey), payload, "SHA-256");
  if (bytesToHex(expected) !== sig) return false;
  return true;
}

function sessionConfigured(env) {
  return Boolean(
    env.STATS_USER &&
    env.STATS_SALT &&
    env.STATS_HASH &&
    env.STATS_PBKDF2_ITER &&
    env.STATS_TOTP_SECRET &&
    env.STATS_SESSION_KEY,
  );
}

const attempts = new Map();

function rateLimited(ip, keyPrefix, limit, windowMs) {
  const key = `${keyPrefix}:${ip}`;
  const now = Date.now();
  let rec = attempts.get(key);
  if (!rec || now - rec.start > windowMs) {
    rec = { start: now, count: 0 };
    attempts.set(key, rec);
  }
  rec.count += 1;
  return rec.count > limit;
}

async function verifyPassword(password, saltHex, expectedHash, iterations) {
  const hash = await pbkdf2(password, saltHex, iterations);
  return constantTimeEqual(hash, expectedHash);
}

async function verifyTotpWithLogging(secretBase32, code) {
  return verifyTotp(secretBase32, code);
}

function noCache() {
  return { "Cache-Control": "no-store" };
}

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function redirect(location) {
  return new Response(null, { status: 303, headers: { Location: location, "Cache-Control": "no-store" } });
}

function htmlPage(html, status = 200) {
  return new Response(html, {
    status,
    headers: { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store" },
  });
}

async function clearSession(request) {
  const expires = "Thu, 01 Jan 1970 00:00:00 GMT";
  return new Response(null, {
    status: 303,
    headers: {
      Location: "/login",
      "Set-Cookie": `${SESSION_COOKIE}=; Path=/; Max-Age=0; HttpOnly; Secure; SameSite=Lax; Expires=${expires}`,
      "Cache-Control": "no-store",
    },
  });
}

/** Asia/Yangon time: UTC+6:30, no DST. */
const MMT_OFFSET_MS = (6 * 60 + 30) * 60 * 1000;

function fmtMMT(ms, withSeconds = false) {
  const shifted = new Date(Number(ms) + MMT_OFFSET_MS);
  const iso = shifted.toISOString();
  return withSeconds ? iso.replace("T", " ").replace("Z", "") : iso.slice(0, 16).replace("T", " ");
}

function esc(s) {
  return String(s ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}

function csvCell(s) {
  const v = String(s ?? "");
  return /[",\n]/.test(v) ? `"${v.replace(/"/g, '""')}"` : v;
}

function clamp(str, fallback = "") {
  if (typeof str !== "string") return fallback;
  return str.trim().slice(0, MAX_LEN);
}

function profileKey(r) {
  return r.transport === "cell"
    ? `cell|${r.carrier || "?"}${r.mccmnc ? "|" + r.mccmnc : ""}`
    : `wifi|${r.ssid || "?"}`;
}

function parseRange(url) {
  const now = Math.floor(Date.now() / 1000);
  const r = (url.searchParams.get("range") || "all").toLowerCase();
  switch (r) {
    case "24h": return { range: "24h", minTs: now - 86400, label: "24h" };
    case "7d":  return { range: "7d",  minTs: now - 7 * 86400, label: "7d" };
    case "30d": return { range: "30d", minTs: now - 30 * 86400, label: "30d" };
    default:    return { range: "all", minTs: 0, label: "all" };
  }
}

const TS_WHERE = "WHERE ts >= ?1";
const DEV_WHERE = "WHERE last_seen >= ?1";

function csvSplit(raw) {
  return String(raw || "")
    .split(",")
    .map((x) => x.trim())
    .filter((x) => x.length > 0)
    .slice(0, 50);
}

function inClause(column, values) {
  if (values.length === 0) return { clause: " AND 1=1", binds: [] };
  return {
    clause: ` AND ${column} IN (${values.map(() => "?").join(",")})`,
    binds: values,
  };
}

const PROFILE_EXPR =
  "CASE WHEN transport='cell' THEN 'cell|'||coalesce(carrier,'?')||'|'||coalesce(mccmnc,'?') ELSE 'wifi|'||coalesce(ssid,'?') END";

async function ensureSchema(env) {
  if (globalThis.__d1Ready) return;
  const statements = [
    `CREATE TABLE IF NOT EXISTS reports (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  endpoint TEXT NOT NULL,
  transport TEXT NOT NULL,
  ssid TEXT,
  carrier TEXT,
  mccmnc TEXT,
  isp TEXT,
  asn TEXT,
  asn_org TEXT,
  country TEXT,
  city TEXT,
  ts INTEGER NOT NULL
)`,
    `CREATE INDEX IF NOT EXISTS idx_reports_ts ON reports(ts)`,
    `CREATE INDEX IF NOT EXISTS idx_reports_endpoint ON reports(endpoint)`,
    `CREATE TABLE IF NOT EXISTS devices (
  device TEXT PRIMARY KEY,
  endpoint TEXT,
  transport TEXT,
  ssid TEXT,
  carrier TEXT,
  mccmnc TEXT,
  isp TEXT,
  asn TEXT,
  asn_org TEXT,
  country TEXT,
  city TEXT,
  first_seen INTEGER NOT NULL,
  last_seen INTEGER NOT NULL
)`,
    `CREATE INDEX IF NOT EXISTS idx_devices_last_seen ON devices(last_seen)`,
    `CREATE TABLE IF NOT EXISTS users (
  telegram_id TEXT PRIMARY KEY,
  username TEXT,
  first_name TEXT,
  device TEXT,
  status TEXT NOT NULL DEFAULT 'pending',
  pending_nonce TEXT,
  chat_id TEXT,
  note TEXT,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  notified_at INTEGER
)`,
    `CREATE INDEX IF NOT EXISTS idx_users_status ON users(status)`,
    `CREATE INDEX IF NOT EXISTS idx_users_device ON users(device)`,
  ];
  for (const sql of statements) {
    await env.DB.prepare(sql).run();
  }
  // One-time migration: carry over approved installs (they have a Telegram chat).
  try {
    await env.DB.prepare(
      `INSERT OR IGNORE INTO users (telegram_id, username, first_name, device, status, chat_id, created_at, updated_at, notified_at)
       SELECT chat_id, NULL, 'install ' || substr(uuid, 1, 8), uuid, status, chat_id, created_at, updated_at, notified_at
       FROM licenses
       WHERE chat_id IS NOT NULL AND chat_id != ''`,
    ).run();
    await env.DB.prepare(`DROP TABLE licenses`).run();
  } catch (_) {
    // licenses table already migrated — fine
  }
  globalThis.__d1Ready = true;
}

function timeAgo(sec) {
  if (!sec) return "—";
  const nowSec = Math.floor(Date.now() / 1000);
  const diff = Math.max(0, nowSec - sec);
  if (diff < 60) return `${diff}s ago`;
  if (diff < 3600) return `${Math.floor(diff / 60)}m ago`;
  if (diff < 86400) return `${Math.floor(diff / 3600)}h ago`;
  return `${Math.floor(diff / 86400)}d ago`;
}

function deviceProfile(d) {
  if (d.transport === "cell") {
    return `${d.carrier || "?"}${d.mccmnc ? " (" + d.mccmnc + ")" : ""}`;
  }
  if (d.transport === "wifi") return d.ssid || "?";
  return d.transport || "?";
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const ip = request.headers.get("CF-Connecting-IP") || "unknown";
    await ensureSchema(env);

    const configured = sessionConfigured(env);
    if (!configured && url.pathname !== "/report") {
      return shutPage(
        "Set STATS_USER, STATS_SALT, STATS_HASH, STATS_PBKDF2_ITER, STATS_TOTP_SECRET and STATS_SESSION_KEY via `wrangler secret put`.",
        503,
      );
    }

    if (request.method === "OPTIONS" && url.pathname === "/report") {
      return new Response(null, { status: 204, headers: CORS });
    }

    if (url.pathname === "/logout" && request.method === "POST") {
      return clearSession(request);
    }

    if (url.pathname === "/login" && request.method === "GET") {
      if (await authed(request, env)) return redirect("/");
      return htmlPage(loginPage(null));
    }

    if (url.pathname === "/login" && request.method === "POST") {
      if (rateLimited(ip, "login", MAX_LOGIN_ATTEMPTS, LOGIN_WINDOW_MS)) {
        return htmlPage(loginPage("Too many attempts. Try again later."), 429);
      }
      const form = await request.formData();
      const username = String(form.get("username") || "");
      const password = String(form.get("password") || "");
      const otp = String(form.get("otp") || "");
      const user = env.STATS_USER;
      const iterations = Number(env.STATS_PBKDF2_ITER);
      const passwordOk =
        constantTimeEqual(username, user) &&
        (await verifyPassword(password, env.STATS_SALT, env.STATS_HASH, iterations));
      const otpOk = passwordOk && (await verifyTotp(env.STATS_TOTP_SECRET, otp));
      if (!passwordOk || !otpOk) {
        await delay(300 + Math.random() * 500);
        return htmlPage(loginPage("Wrong user, password or authenticator code."), 401);
      }
      attempts.delete(`login:${ip}`);
      const { value, exp } = await makeSessionCookie(user, env.STATS_SESSION_KEY);
      const headers = new Headers();
      headers.set("Set-Cookie", sessionCookieHeader(value, exp));
      headers.set("Location", "/");
      headers.set("Cache-Control", "no-store");
      return new Response(null, { status: 303, headers });
    }

    // Unauthenticated endpoints the app POSTs to; everything else requires auth.
    const openPaths = new Set(["/report", "/heartbeat", "/bye", "/recommended", "/telegram/webhook", "/telegram/login/status"]);
    const authenticated = openPaths.has(url.pathname) || (await authed(request, env));
    if (!authenticated) {
      if (url.pathname === "/stats.json" || url.pathname === "/stats.csv") {
        return new Response(JSON.stringify({ error: "auth required" }), {
          status: 401,
          headers: { "Content-Type": "application/json", "Cache-Control": "no-store" },
        });
      }
      return redirect("/login");
    }

    if (url.pathname === "/telegram/login/status" && request.method === "GET") {
      if (rateLimited(ip, "license", MAX_REPORT_PER_MINUTE, 60_000)) {
        return badJson({ error: "slow down" }, 429);
      }
      const nonce = String(url.searchParams.get("nonce") || "").trim().toLowerCase();
      if (!NONCE_RE.test(nonce)) {
        return badJson({ error: "invalid nonce" }, 400);
      }
      const row = await env.DB
        .prepare("SELECT telegram_id, username, first_name, status FROM users WHERE pending_nonce = ?1 ORDER BY updated_at DESC LIMIT 1")
        .bind(nonce)
        .first();
      if (!row) {
        return new Response(JSON.stringify({ status: "not_found" }), {
          headers: { ...CORS, "Content-Type": "application/json", "Cache-Control": "no-store" },
        });
      }
      const body = JSON.stringify({
        status: row.status,
        telegram_id: row.telegram_id,
        username: row.username || null,
        first_name: row.first_name || null,
      });
      return new Response(body, {
        headers: { ...CORS, "Content-Type": "application/json", "Cache-Control": "no-store" },
      });
    }

    if (url.pathname === "/telegram/webhook" && request.method === "POST") {
      const secret = request.headers.get("X-Telegram-Bot-Api-Secret-Token") || "";
      if (!env.TELEGRAM_WEBHOOK_SECRET || secret !== env.TELEGRAM_WEBHOOK_SECRET) {
        return badJson({ error: "unauthorized" }, 401);
      }
      let update;
      try {
        update = await request.json();
      } catch {
        return badJson({ error: "invalid JSON" }, 400);
      }
      try {
        await handleTelegramUpdate(env, update);
      } catch (err) {
        // never bubble a 5xx to Telegram — it would keep retrying the same update
        console.error("telegram update failed", err);
      }
      return new Response(JSON.stringify({ ok: true }), {
        headers: { "Content-Type": "application/json" },
      });
    }

    if (url.pathname === "/report" && request.method === "POST") {
      if (rateLimited(ip, "report", MAX_REPORT_PER_MINUTE, 60_000)) {
        return badJson({ error: "slow down" }, 429);
      }
      let body;
      try {
        body = await request.json();
      } catch {
        return badJson({ error: "invalid JSON" }, 400);
      }
      const endpoint = clamp(body.endpoint);
      if (!VALID_ENDPOINT.test(endpoint)) return badJson({ error: "invalid endpoint" }, 400);
      const transport = clamp(body.transport, 16);
      if (!transport) return badJson({ error: "transport required" }, 400);

      const ts = Math.floor(Date.now() / 1000);
      await env.DB.prepare(
        `INSERT INTO reports(endpoint, transport, ssid, carrier, mccmnc, isp, asn, asn_org, country, city, ts)
         VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11)`,
      ).bind(
        endpoint,
        transport,
        clamp(body.ssid, 64),
        clamp(body.carrier),
        clamp(body.mccmnc, 16),
        clamp(body.isp),
        clamp(body.asn, 32),
        clamp(body.asn_org),
        clamp(body.country, 64),
        clamp(body.city, 64),
        ts,
      ).run();
      return new Response(JSON.stringify({ ok: true }), {
        status: 201,
        headers: { ...CORS, "Content-Type": "application/json" },
      });
    }

    if (url.pathname === "/recommended" && request.method === "GET") {
      if (rateLimited(ip, "recommended", MAX_REPORT_PER_MINUTE, 60_000)) {
        return badJson({ error: "slow down" }, 429);
      }
      // Public, stateless lookup: which endpoint works on this transport?
      // Matches by ASN when available, falls back to ISP substring.
      const transport = (url.searchParams.get("transport") || "").toLowerCase();
      if (transport !== "wifi" && transport !== "cell" && transport !== "eth") {
        return badJson({ error: "invalid transport" }, 400);
      }
      const asn = clamp(url.searchParams.get("asn") || "", 32).toUpperCase();
      const isp = clamp(url.searchParams.get("isp") || "", 64);
      const range = (url.searchParams.get("range") || "all").toLowerCase();
      const now = Math.floor(Date.now() / 1000);
      const minTs = range === "24h" ? now - 86400
        : range === "7d" ? now - 7 * 86400
        : range === "30d" ? now - 30 * 86400
        : 0;

      let where = "transport = ?";
      const binds = [transport];
      if (asn) {
        where += " AND asn = ?";
        binds.push(asn);
      } else if (isp) {
        where += " AND isp LIKE ?";
        binds.push(`%${isp}%`);
      } else {
        return badJson({ error: "asn or isp required" }, 400);
      }
      where += " AND ts >= ?";
      binds.push(minTs);

      const row = (await env.DB.prepare(
        `SELECT endpoint, COUNT(*) AS support, MAX(ts) AS lastTs
         FROM reports WHERE ${where}
         GROUP BY endpoint ORDER BY support DESC, lastTs DESC LIMIT 1`,
      ).bind(...binds).first());

      if (!row) return badJson({ endpoint: null, note: "no matching reports" }, 200);

      const picked = String(row.endpoint || "");
      return new Response(JSON.stringify({
        endpoint: picked,
        support: Number(row.support || 0),
        lastReportAt: Number(row.lastTs || 0),
        transport,
        asn: asn || null,
        isp: isp || null,
        range,
      }), {
        headers: { ...CORS, "Content-Type": "application/json", "Cache-Control": "no-store" },
      });
    }

    if (url.pathname === "/heartbeat" && request.method === "POST") {
      if (rateLimited(ip, "heartbeat", MAX_REPORT_PER_MINUTE, 60_000)) {
        return badJson({ error: "slow down" }, 429);
      }
      let body;
      try {
        body = await request.json();
      } catch {
        return badJson({ error: "invalid JSON" }, 400);
      }
      const device = clamp(body.device, 64);
      if (!/^[a-zA-Z0-9-]{8,64}$/.test(device)) return badJson({ error: "invalid device id" }, 400);
      const endpoint = body.endpoint ? clamp(body.endpoint, 32) : "";
      if (endpoint && !VALID_ENDPOINT.test(endpoint)) return badJson({ error: "invalid endpoint" }, 400);

      const nowSec = Math.floor(Date.now() / 1000);
      // Keep the original first_seen when the row exists (UPSERT).
      await env.DB.prepare(
        `INSERT INTO devices(device, endpoint, transport, ssid, carrier, mccmnc, isp, asn, asn_org, country, city, first_seen, last_seen)
         VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?12)
         ON CONFLICT(device) DO UPDATE SET
           endpoint=excluded.endpoint,
           transport=excluded.transport,
           ssid=excluded.ssid,
           carrier=excluded.carrier,
           mccmnc=excluded.mccmnc,
           isp=excluded.isp,
           asn=excluded.asn,
           asn_org=excluded.asn_org,
           country=excluded.country,
           city=excluded.city,
           last_seen=excluded.last_seen`,
      ).bind(
        device,
        endpoint || null,
        clamp(body.transport, 16) || null,
        clamp(body.ssid, 64) || null,
        clamp(body.carrier) || null,
        clamp(body.mccmnc, 16) || null,
        clamp(body.isp) || null,
        clamp(body.asn, 32) || null,
        clamp(body.asn_org) || null,
        clamp(body.country, 64) || null,
        clamp(body.city, 64) || null,
        nowSec,
      ).run();

      // First-ever heartbeat from this device => first connect (this path runs
      // exactly once per devices row, when first_seen is created now).
      try {
        const seen = await env.DB.prepare(`SELECT first_seen FROM devices WHERE device = ?1`).bind(device).first();
        if (seen && Number(seen.first_seen) === nowSec) {
          const transport = clamp(body.transport, 16) || "unknown";
          if (env.TELEGRAM_ADMIN_ID) {
            const user = await env.DB
              .prepare(`SELECT telegram_id, username, first_name, status, notified_at FROM users WHERE device = ?1`)
              .bind(device)
              .first();
            if (!user || Number(user.notified_at || 0) === 0) {
              const who = user
                ? `${user.first_name ? `<b>${esc(user.first_name)}</b> ` : ""}${user.username ? `@${esc(user.username)}` : `<code>${esc(String(user.telegram_id))}</code>`}`
                : `<b>unregistered</b> install`;
              const lines = [
                `🟢 <b>First connect</b>`,
                `User: ${who}`,
                `Endpoint: <code>${esc(endpoint || "unknown")}</code> · ${esc(transport)}`,
              ];
              const isp = clamp(body.isp);
              const asn = clamp(body.asn, 32);
              if (isp) lines.push(`ISP: ${esc(isp)}`);
              if (asn) lines.push(`ASN: ${esc(asn)}`);
              lines.push(user ? `License: ${esc(user.status)}` : "License: —");
              lines.push(`Seen: ${fmtMMT(nowSec * 1000)} MMT`);
              await tgSend(env, env.TELEGRAM_ADMIN_ID, lines.join("\n"));
              if (user) {
                await env.DB.prepare(`UPDATE users SET notified_at = ?2 WHERE device = ?1`).bind(device, nowSec).run();
              }
            }
          }
        }
      } catch (err) {
        // never break the heartbeat over a notification failure
        console.error("first-connect notify failed", err);
      }
      return new Response(JSON.stringify({ ok: true }), {
        status: 201,
        headers: { ...CORS, "Content-Type": "application/json" },
      });
    }

    if (url.pathname === "/bye" && request.method === "POST") {
      if (rateLimited(ip, "bye", MAX_REPORT_PER_MINUTE, 60_000)) {
        return badJson({ error: "slow down" }, 429);
      }
      let body;
      try {
        body = await request.json();
      } catch {
        return badJson({ error: "invalid JSON" }, 400);
      }
      const device = clamp(body.device, 64);
      if (!/^[a-zA-Z0-9-]{8,64}$/.test(device)) return badJson({ error: "invalid device id" }, 400);
      await env.DB.prepare(`DELETE FROM devices WHERE device = ?1`).bind(device).run();
      return new Response(JSON.stringify({ ok: true }), {
        status: 200,
        headers: { ...CORS, "Content-Type": "application/json" },
      });
    }

    if (url.pathname === "/stats.json") {
      const { range, minTs, label } = parseRange(url);
      const bind = [minTs];
      const total = (await env.DB.prepare(`SELECT count(*) AS n FROM reports ${TS_WHERE}`).bind(...bind).first()).n;
      const earliest = (await env.DB.prepare(`SELECT MIN(ts) AS m FROM reports ${TS_WHERE}`).bind(...bind).first()).m;
      const endpoints = (await env.DB.prepare(`SELECT endpoint, count(*) AS total FROM reports ${TS_WHERE} GROUP BY endpoint ORDER BY total DESC`).bind(...bind).all()).results;
      const profiles = (await env.DB.prepare(`SELECT ${PROFILE_EXPR} AS profile, count(*) AS n FROM reports ${TS_WHERE} GROUP BY profile`).bind(...bind).all()).results;
      const aggregates = (await env.DB.prepare(`SELECT endpoint, ${PROFILE_EXPR} AS profile, COUNT(*) AS count, MAX(ts) AS lastTs FROM reports ${TS_WHERE} GROUP BY endpoint, profile ORDER BY count DESC`).bind(...bind).all()).results;
      const ispTags = (await env.DB.prepare(`SELECT endpoint, ${PROFILE_EXPR} AS profile, coalesce(isp,'') AS isp, COUNT(*) AS n FROM reports ${TS_WHERE} AND isp IS NOT NULL AND isp != '' GROUP BY endpoint, profile, isp`).bind(...bind).all()).results;
      const asnTags = (await env.DB.prepare(`SELECT endpoint, ${PROFILE_EXPR} AS profile, coalesce(asn,'') AS asn, COUNT(*) AS n FROM reports ${TS_WHERE} AND asn IS NOT NULL AND asn != '' GROUP BY endpoint, profile, asn`).bind(...bind).all()).results;
      const devices = (await env.DB.prepare(`SELECT * FROM devices ${DEV_WHERE} ORDER BY last_seen DESC`).bind(...bind).all()).results;
      const recent = (await env.DB.prepare(`SELECT * FROM reports ${TS_WHERE} ORDER BY ts DESC LIMIT 50`).bind(...bind).all()).results;
      const transports = (await env.DB.prepare(`SELECT transport, count(*) AS n FROM reports ${TS_WHERE} GROUP BY transport`).bind(...bind).all()).results;
      return new Response(
        JSON.stringify({ range: label, total, earliest, endpoints, profiles, aggregates, ispTags, asnTags, devices, recent, transports }, null, 2),
        { headers: { ...CORS, "Content-Type": "application/json", "Cache-Control": "no-store" } },
      );
    }

    if (url.pathname === "/admin/users" && request.method === "GET") {
      const authedAdmin = await authed(request, env);
      if (!authedAdmin) return redirect("/login");
      const flash = url.searchParams.get("msg");
      const all = (await env.DB.prepare(`SELECT telegram_id, username, first_name, device, status, created_at, updated_at FROM users ORDER BY CASE WHEN status='pending' THEN 0 ELSE 1 END, updated_at DESC`).all()).results;
      const counts = (await env.DB.prepare(`SELECT status, count(*) AS n FROM users GROUP BY status`).all()).results;
      const byStatus = Object.fromEntries(counts.map((r) => [r.status, Number(r.n)]));

      const rows = all.map((u) => {
        const display = u.username ? `@${esc(String(u.username))}` : u.first_name ? esc(String(u.first_name)) : `<code>${esc(String(u.telegram_id))}</code>`;
        const tid = esc(String(u.telegram_id));
        const deviceCell = u.device ? esc(String(u.device).slice(0, 8)) : "—";
        const seen = fmtMMT(Number(u.created_at) * 1000);
        const updated = fmtMMT(Number(u.updated_at) * 1000);
        const statusTag = u.status === "active" ? `<span class="tag cell">active</span>` : u.status === "pending" ? `<span class="tag off">pending</span>` : `<span class="tag">inactive</span>`;
        const activateForm = `<form class="inline-form" method="post" action="/admin/users"><input type="hidden" name="action" value="activate"/><input type="hidden" name="tid" value="${tid}"/><button class="btn primary" type="submit">Activate</button></form>`;
        const deactivateForm = `<form class="inline-form" method="post" action="/admin/users"><input type="hidden" name="action" value="deactivate"/><input type="hidden" name="tid" value="${tid}"/><button class="btn danger" type="submit">Deactivate</button></form>`;
        const deleteForm = `<form class="inline-form" method="post" action="/admin/users"><input type="hidden" name="action" value="delete"/><input type="hidden" name="tid" value="${tid}"/><button class="btn" type="submit">Delete</button></form>`;
        return `<tr><td>${display}</td><td><code>${tid}</code></td><td>${statusTag}</td><td><code>${deviceCell}</code></td><td>${esc(seen)}</td><td>${esc(updated)}</td><td class="actions">${u.status === "active" ? deactivateForm : activateForm}${deleteForm}</td></tr>`;
      }).join("\n");

      const html = `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>WARP Connect · Users</title>
  <style>
:root {
  --bg:#05070F;
  --panel:#0B1220;
  --panel-2:#0E1E36;
  --panel-3:#13203A;
  --line:#1A2A44;
  --line-2:#243352;
  --line-soft:rgba(148,184,220,.08);
  --text:#F2F7FF;
  --sub:#A9B8CF;
  --dim:#7A889F;
  --faint:#5E6B85;
  --cyan:#38BDF8;
  --cyan-2:#2DD4BF;
  --green:#34D399;
  --amber:#FBBF24;
  --red:#F87171;
  --mono:"Cascadia Mono",ui-monospace,SFMono-Regular,Menlo,Consolas,"Liberation Mono",monospace;
  --sans:"Segoe UI",Inter,system-ui,-apple-system,"Roboto","Helvetica Neue",sans-serif;
}
* { box-sizing: border-box; }
html, body { min-height: 100vh; }
body {
  margin: 0;
  padding: 26px 20px 18px;
  color: var(--text);
  font-family: var(--sans);
  background:
    radial-gradient(1100px 620px at 78% -8%, rgba(56,189,248,.16), transparent 58%),
    radial-gradient(900px 620px at -12% 112%, rgba(45,212,191,.12), transparent 55%),
    linear-gradient(180deg,#05070F 0%,#0B1220 55%,#05070F 100%);
  -webkit-font-smoothing: antialiased;
}
::-webkit-scrollbar { width: 10px; height: 10px; }
::-webkit-scrollbar-thumb { background: #1e2c47; border-radius: 8px; }
::-webkit-scrollbar-thumb:hover { background: #2b3d5e; }
code, .mono { font-family: var(--mono); color: #C7D8EF; background: transparent; padding: 0; border: 0; }
a { color: var(--cyan); text-decoration: none; }
.wrap { max-width: 1220px; margin: 0 auto; }
.label { font-size: 10px; font-weight: 700; letter-spacing: .18em; text-transform: uppercase; color: var(--dim); }
.top { display: flex; align-items: center; justify-content: space-between; gap: 16px; padding: 2px 0 18px; border-bottom: 1px solid var(--line-soft); margin-bottom: 18px; }
.brand { display: flex; align-items: center; gap: 14px; }
.mark {
  width: 46px; height: 46px; border-radius: 50%; position: relative; flex: 0 0 auto;
  background: radial-gradient(circle at 38% 32%, #8fdcff 0%, #38bdf8 42%, #0b4d6d 100%);
  box-shadow: 0 0 26px rgba(56,189,248,.45), inset 0 0 10px rgba(255,255,255,.18);
}
.mark::after {
  content: ""; position: absolute; inset: 13px; border-radius: 50%; background: var(--panel);
  box-shadow: inset 0 0 8px rgba(0,0,0,.65);
}
.brand h1 { font-size: 15px; font-weight: 800; letter-spacing: .32em; margin: 0; color: var(--text); text-transform: uppercase; }
.brand span { display: block; font-family: var(--mono); font-size: 9.5px; color: var(--cyan-2); margin-top: 4px; letter-spacing: .3em; text-transform: uppercase; }
.links { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
.links a, .logout { color: var(--cyan); text-decoration: none; font-size: 9.5px; font-weight: 700; padding: 8px 15px; border: 1px solid var(--line); border-radius: 999px; letter-spacing: .18em; text-transform: uppercase; transition: all .16s ease; background: transparent; font-family: inherit; cursor: pointer; }
.links a:hover, .logout:hover { border-color: var(--cyan); color: var(--text); background: var(--panel-2); box-shadow: 0 0 18px rgba(56,189,248,.15); }
.inline-form { display: inline-flex; }
.kpis { display: grid; grid-template-columns: repeat(auto-fit, minmax(170px, 1fr)); gap: 12px; margin-bottom: 16px; }
.kpi { position: relative; overflow: hidden; background: var(--panel); border: 1px solid var(--line); border-radius: 16px; padding: 16px 18px; transition: transform .18s ease, border-color .18s ease; }
.kpi::before { content: ""; position: absolute; left: 0; right: 0; top: 0; height: 1px; background: linear-gradient(90deg, transparent, rgba(56,189,248,.55), transparent); }
.kpi:hover { transform: translateY(-2px); border-color: var(--line-2); }
.kpi .num { font-family: var(--mono); font-size: 31px; font-weight: 700; color: var(--text); line-height: 1; letter-spacing: -.02em; }
.kpi .label { font-size: 9.5px; font-weight: 700; letter-spacing: .22em; text-transform: uppercase; color: var(--dim); margin-top: 9px; }
.kpi.live-now { border-color: rgba(52,211,153,.4); box-shadow: inset 0 0 28px rgba(52,211,153,.07), 0 0 24px rgba(52,211,153,.08); }
.kpi.live-now .num { color: var(--green); }
.grid { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; margin-bottom: 16px; }
@media (max-width: 820px) { .grid { grid-template-columns: 1fr; } }
.panel { background: var(--panel); border: 1px solid var(--line); border-radius: 16px; padding: 18px 18px 16px; position: relative; overflow: hidden; box-shadow: 0 12px 34px rgba(0,0,0,.35); }
.panel::before { content: ""; position: absolute; left: 0; right: 0; top: 0; height: 1px; background: linear-gradient(90deg, transparent, rgba(125,211,252,.4), transparent); }
.panel h2 { font-family: var(--mono); font-size: 10px; font-weight: 700; letter-spacing: .24em; text-transform: uppercase; color: var(--cyan-2); margin: 0 0 14px; opacity: .9; }
.panel.live { background: linear-gradient(180deg, rgba(52,211,153,.075) 0%, var(--panel) 38%); border-color: rgba(52,211,153,.32); }
.panel.live h2 { color: var(--green); opacity: 1; }
.panel.live .pulsedot { margin-left: 8px; }
table { border-collapse: collapse; width: 100%; font-size: 12px; }
th { font-family: var(--mono); text-align: left; font-size: 9px; font-weight: 700; letter-spacing: .2em; text-transform: uppercase; color: var(--faint); padding: 2px 12px 12px; border-bottom: 1px solid var(--line); }
td { padding: 11px 12px; border-bottom: 1px solid var(--line-soft); vertical-align: middle; color: var(--text); }
tbody tr { transition: background .15s ease; }
tbody tr:nth-child(odd) td { background: rgba(255,255,255,.018); }
tbody tr:hover td { background: rgba(56,189,248,.06); }
tr:last-child td { border-bottom: 0; }
td code { color: #9ADCFF; }
.tag { display: inline-block; padding: 2px 10px; border-radius: 999px; font-family: var(--mono); font-size: 9px; font-weight: 700; letter-spacing: .16em; text-transform: uppercase; }
.tag.cell { color: #34D399; background: rgba(52,211,153,.1); border: 1px solid rgba(52,211,153,.35); }
.tag.wifi { color: #7DD3FC; background: rgba(56,189,248,.1); border: 1px solid rgba(56,189,248,.35); }
.tag.live { color: #34D399; background: rgba(52,211,153,.12); border: 1px solid rgba(52,211,153,.45); box-shadow: 0 0 12px rgba(52,211,153,.25); }
.tag.off { color: #FBBF24; background: rgba(251,191,36,.1); border: 1px solid rgba(251,191,36,.35); }
.live-row td { background: rgba(52,211,153,.055) !important; }
.dead-row { opacity: .5; }
.btn { background: transparent; border: 1px solid var(--line); color: var(--sub); padding: 5px 14px; border-radius: 999px; font-size: 9.5px; font-weight: 700; letter-spacing: .18em; text-transform: uppercase; font-family: inherit; cursor: pointer; transition: all .15s ease; }
.btn.primary { border-color: var(--cyan); color: var(--cyan); }
.btn.primary:hover { background: var(--cyan); color: #04121F; border-color: var(--cyan); }
.btn.danger { border-color: var(--red); color: #FCA5A5; }
.btn.danger:hover { background: var(--red); color: #04121F; border-color: var(--red); }
.btn:hover { border-color: var(--cyan); color: var(--text); }
.csv-link { font-family: var(--mono); color: var(--cyan); text-decoration: none; font-size: 9px; font-weight: 700; letter-spacing: .18em; text-transform: uppercase; padding: 5px 12px; border: 1px solid var(--line); border-radius: 999px; transition: all .15s ease; }
.csv-link:hover { background: var(--panel-2); border-color: var(--cyan); }
.sub { color: var(--sub); font-size: 11px; }
.pills { display: flex; gap: 8px; margin: 0 0 16px; flex-wrap: wrap; }
.pill { font-size: 9.5px; font-weight: 700; letter-spacing: .18em; padding: 9px 18px; border: 1px solid var(--line); border-radius: 999px; color: var(--sub); text-decoration: none; text-transform: uppercase; transition: all .16s ease; background: transparent; }
.pill:hover { color: var(--text); border-color: var(--line-2); background: var(--panel-2); }
.pill.active { color: #04121F; background: var(--cyan); border-color: var(--cyan); box-shadow: 0 8px 28px rgba(56,189,248,.35); }
.live-meta { display:flex; gap:16px; align-items:center; font-family: var(--mono); font-size: 10px; color: var(--sub); margin-bottom: 12px; letter-spacing: .16em; text-transform: uppercase; }
.live-meta b { color: var(--green); font-size: 17px; letter-spacing: 0; }
.pulsedot { display:inline-block; width: 9px; height: 9px; border-radius: 50%; background: var(--green); box-shadow: 0 0 12px #34d399; animation: pulse 1.6s infinite ease-in-out; vertical-align: middle; }
@keyframes pulse { 0%,100% { opacity: 1; transform: scale(1); } 50% { opacity: .3; transform: scale(.75); } }
.donut-wrap { display: flex; align-items: center; gap: 22px; }
.donut { width: 128px; height: 128px; border-radius: 50%; position: relative; flex: 0 0 auto; box-shadow: 0 0 30px rgba(56,189,248,.22), inset 0 0 0 1px var(--line); }
.donut::after { content: attr(data-total); position: absolute; inset: 17px; border-radius: 50%; background: #0C1425; display: flex; align-items: center; justify-content: center; flex-direction: column; text-align: center; color: var(--text); font-family: var(--mono); font-size: 13px; line-height: 1.3; }
.donut small { display: block; font-size: 8px; color: var(--dim); letter-spacing: .22em; text-transform: uppercase; font-weight: 700; }
.legend { display: flex; flex-direction: column; gap: 9px; font-size: 12px; color: var(--sub); }
.legend div { display: flex; align-items: center; gap: 9px; }
.dot { width: 10px; height: 10px; border-radius: 50%; }
.bar-row { display: flex; align-items: center; gap: 12px; margin: 9px 0; font-size: 12px; }
.bar-label { width: 150px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: #C7D8EF; font-family: var(--mono); font-size: 11px; }
.bar { flex: 1; height: 11px; border-radius: 7px; background: #0E1E36; overflow: hidden; border: 1px solid var(--line-soft); }
.bar i { display: block; height: 100%; background: linear-gradient(90deg, var(--cyan), var(--cyan-2)); border-radius: 7px; box-shadow: 0 0 12px rgba(45,212,191,.35); }
.bar-count { width: 38px; text-align: right; color: var(--dim); font-variant-numeric: tabular-nums; font-family: var(--mono); font-size: 11px; }
.foot { font-family: var(--mono); color: var(--faint); font-size: 9px; padding: 20px 4px 8px; display: flex; align-items: center; justify-content: center; gap: 12px; letter-spacing: .22em; text-transform: uppercase; }
.foot a { color: var(--cyan); text-decoration: none; }
.logout { background: transparent; border: 1px solid var(--line); color: var(--dim); border-radius: 999px; padding: 8px 15px; font: inherit; font-size: 9.5px; font-weight: 700; letter-spacing: .18em; text-transform: uppercase; cursor: pointer; transition: all .16s ease; }
.msg { background: rgba(52,211,153,.09); border: 1px solid rgba(52,211,153,.3); color: #86EFAC; border-radius: 14px; padding: 12px 16px; margin-bottom: 14px; font-size: 12px; font-weight: 500; }
@media (max-width: 720px) {
  body { padding: 16px 12px; }
  .top { flex-direction: column; align-items: flex-start; gap: 12px; }
  .kpis { grid-template-columns: repeat(2, 1fr); }
  .brand h1 { font-size: 13px; }
}
</style>

</head>
<body>
  <div class="wrap">
    <header class="top">
      <div class="brand">
        <div class="mark"></div>
        <div>
          <h1>WARP Connect</h1>
          <span>stats console · users</span>
        </div>
      </div>
      <div class="links"><a href="/">← dashboard</a><form class="inline-form" method="post" action="/logout"><button class="logout" type="submit">Sign out</button></form></div>
    </header>
    ${flash ? `<div class="msg">${esc(String(flash))}</div>` : ""}
    <div class="kpis">
      <div class="kpi"><div class="num">${Number(byStatus.active || 0)}</div><div class="label">active</div></div>
      <div class="kpi"><div class="num">${Number(byStatus.pending || 0)}</div><div class="label">pending</div></div>
      <div class="kpi"><div class="num">${Number(byStatus.inactive || 0)}</div><div class="label">inactive</div></div>
    </div>
    <table>
      <thead><tr><th>user</th><th>id</th><th>status</th><th>install</th><th>seen</th><th>updated</th><th>actions</th></tr></thead>
      <tbody>${rows || "<tr><td colspan='7'>No users yet.</td></tr>"}</tbody>
    </table>
    <div class="foot" style="justify-content:center">Developed by Sithu</div>
  </div>
</body>
</html>`;
      return new Response(html, { headers: { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store" } });
    }

    if (url.pathname === "/admin/users" && request.method === "POST") {
      const authedAdmin = await authed(request, env);
      if (!authedAdmin) return redirect("/login");
      const form = await request.formData();
      const action = String(form.get("action") || "");
      const tid = String(form.get("tid") || "").trim();
      if (!/^\d{5,20}$/.test(tid)) {
        return redirect("/admin/users?msg=Invalid+ID");
      }
      const now = Math.floor(Date.now() / 1000);
      if (action === "activate") {
        await env.DB.prepare(`UPDATE users SET status='active', updated_at=?2 WHERE telegram_id=?1`).bind(tid, now).run();
        return redirect("/admin/users?msg=Activated");
      }
      if (action === "deactivate") {
        await env.DB.prepare(`UPDATE users SET status='inactive', updated_at=?2 WHERE telegram_id=?1`).bind(tid, now).run();
        return redirect("/admin/users?msg=Deactivated");
      }
      if (action === "delete") {
        await env.DB.prepare(`DELETE FROM users WHERE telegram_id=?1`).bind(tid).run();
        return redirect("/admin/users?msg=Deleted");
      }
      return redirect("/admin/users?msg=Unknown+action");
    }

    if (url.pathname === "/stats.csv") {
      const { range, minTs, label } = parseRange(url);
      const isps = csvSplit(url.searchParams.get("isp"));
      const asns = csvSplit(url.searchParams.get("asn"));
      const ispClause = isps.length ? ` AND isp IN (${isps.map(() => "?").join(",")})` : "";
      const asnClause = asns.length ? ` AND asn IN (${asns.map(() => "?").join(",")})` : "";
      const bind = [minTs, ...isps, ...asns];
      const { results } = await env.DB.prepare(
        `SELECT * FROM reports WHERE ts >= ?1${ispClause}${asnClause} ORDER BY ts DESC LIMIT 1000`,
      ).bind(...bind).all();
      const rows = [["endpoint", "transport", "ssid", "carrier", "mccmnc", "isp", "asn", "asn_org", "country", "city", "ts"]];
      for (const r of results) {
        rows.push([r.endpoint, r.transport, r.ssid || "", r.carrier || "", r.mccmnc || "", r.isp || "", r.asn || "", r.asn_org || "", r.country || "", r.city || "", new Date(Number(r.ts) * 1000).toISOString()]);
      }
      const csv = rows.map((r) => r.map(csvCell).join(",")).join("\n");
      return new Response(csv, {
        headers: {
          ...CORS,
          "Content-Type": "text/csv",
          "Cache-Control": "no-store",
          "Content-Disposition": `attachment; filename="warpconnect-${label}${isps.length ? "-isp" : ""}${asns.length ? "-asn" : ""}.csv"`,
        },
      });
    }

    // HTML dashboard (range-aware via ?range=24h|7d|30d|all)
    const nowSec = Math.floor(Date.now() / 1000);
    const { range, minTs, label: rangeLabel } = parseRange(url);
    const bindTs = [minTs];
    const totalReports = (await env.DB.prepare(`SELECT count(*) AS n FROM reports ${TS_WHERE}`).bind(...bindTs).first()).n;
    const uniqueEndpoints = (await env.DB.prepare(`SELECT count(distinct endpoint) AS n FROM reports ${TS_WHERE}`).bind(...bindTs).first()).n;
    const uniqueProfiles = (await env.DB.prepare(`SELECT count(distinct ${PROFILE_EXPR}) AS n FROM reports ${TS_WHERE}`).bind(...bindTs).first()).n;
    const earliest = (await env.DB.prepare(`SELECT MIN(ts) AS m FROM reports ${TS_WHERE}`).bind(...bindTs).first()).m;
    const transportRows = (await env.DB.prepare(`SELECT transport, count(*) AS n FROM reports ${TS_WHERE} GROUP BY transport`).bind(...bindTs).all()).results;
    const transportCounts = { wifi: 0, cell: 0, other: 0 };
    for (const r of transportRows) {
      const k = r.transport === "wifi" ? "wifi" : r.transport === "cell" ? "cell" : "other";
      transportCounts[k] += Number(r.n);
    }
    const transportTotal = Math.max(1, totalReports);
    const transportShare = {
      wifi: (transportCounts.wifi / transportTotal) * 100,
      cell: (transportCounts.cell / transportTotal) * 100,
      other: (transportCounts.other / transportTotal) * 100,
    };
    const donutStops = [
      `#38BDF8 0 ${transportShare.wifi}%`,
      `#34D399 ${transportShare.wifi}% ${transportShare.wifi + transportShare.cell}%`,
      `#FBBF24 ${transportShare.wifi + transportShare.cell}% 100%`,
    ].join(", ");

    const agg = (await env.DB.prepare(`SELECT endpoint, ${PROFILE_EXPR} AS profile, COUNT(*) AS count, MAX(ts) AS lastTs FROM reports ${TS_WHERE} GROUP BY endpoint, profile ORDER BY count DESC`).bind(...bindTs).all()).results;
    const ispTagRows = (await env.DB.prepare(`SELECT endpoint, ${PROFILE_EXPR} AS profile, coalesce(isp,'') AS isp, COUNT(*) AS n FROM reports ${TS_WHERE} AND isp IS NOT NULL AND isp != '' GROUP BY endpoint, profile, isp`).bind(...bindTs).all()).results;
    const asnTagRows = (await env.DB.prepare(`SELECT endpoint, ${PROFILE_EXPR} AS profile, coalesce(asn,'') AS asn, COUNT(*) AS n FROM reports ${TS_WHERE} AND asn IS NOT NULL AND asn != '' GROUP BY endpoint, profile, asn`).bind(...bindTs).all()).results;

    const ispsByKey = new Map();
    for (const row of ispTagRows) {
      const key = `${row.endpoint}|${row.profile}`;
      const arr = ispsByKey.get(key) || [];
      arr.push([row.isp, Number(row.n)]);
      ispsByKey.set(key, arr);
    }
    const asnByKey = new Map();
    for (const row of asnTagRows) {
      const key = `${row.endpoint}|${row.profile}`;
      const arr = asnByKey.get(key) || [];
      arr.push([row.asn, Number(row.n)]);
      asnByKey.set(key, arr);
    }

    const aggregateRows = agg.map((a) => {
      const time = fmtMMT(Number(a.lastTs) * 1000, true) + " MMT";
      const isCell = a.profile.startsWith("cell");
      const tag = isCell ? `<span class="tag cell">cell</span>` :
        a.profile.startsWith("wifi") ? `<span class="tag wifi">wifi</span>` : `<span class="tag">?</span>`;
      const key = `${a.endpoint}|${a.profile}`;
      const isps = (ispsByKey.get(key) || [])
        .sort((x, y) => y[1] - x[1])
        .slice(0, 5)
        .map(([k, v]) => (v > 1 ? `${esc(k)} ×${v}` : esc(k)))
        .join(" · ");
      const asns = (asnByKey.get(key) || [])
        .sort((x, y) => y[1] - x[1])
        .slice(0, 5)
        .map(([k, v]) => (v > 1 ? `${esc(k)} ×${v}` : esc(k)))
        .join(" · ");
      // Per-row CSV download link filtered to this cell's top ISPs/ASNs + the
      // current time range.
      const enc = (v) => encodeURIComponent(v);
      const csvIsns = (ispsByKey.get(key) || []).slice(0, 3).map(([k]) => enc(k)).join(",");
      const csvAsns = (asnByKey.get(key) || []).slice(0, 3).map(([k]) => enc(k)).join(",");
      const csvQuery = [
        `range=${encodeURIComponent(range)}`,
        csvIsns ? `isp=${csvIsns}` : "",
        csvAsns ? `asn=${csvAsns}` : "",
      ].filter(Boolean).join("&");
      const csvLink = `<a href="/stats.csv?${csvQuery}" class="csv-link" title="Download this cohort as CSV (filtered to this endpoint's ISPs/ASNs)">CSV</a>`;
      return `<tr><td>${tag}</td><td>${esc(a.endpoint)}</td><td>${esc(String(a.profile).replace(/^(cell|wifi)\|/, ""))}</td><td>${a.count}</td><td>${esc(isps || "—")}</td><td>${esc(asns || "—")}</td><td>${esc(time)}</td><td>${csvLink}</td></tr>`;
    }).join("\n");

    const endpointCounts = new Map();
    for (const r of agg) {
      endpointCounts.set(r.endpoint, (endpointCounts.get(r.endpoint) || 0) + Number(r.count));
    }
    const topEndpoints = [...endpointCounts.entries()].sort((a, b) => b[1] - a[1]).slice(0, 8);
    const maxEndpointCount = topEndpoints.length ? topEndpoints[0][1] : 1;
    const endpointBars = topEndpoints.map(([endpoint, count]) => {
      const pct = Math.round((count / maxEndpointCount) * 100);
      return `<div class="bar-row"><span class="bar-label">${esc(endpoint)}</span><div class="bar"><i style="width:${pct}%"></i></div><span class="bar-count">${count}</span></div>`;
    }).join("\n");

    const deviceRows = (await env.DB.prepare(`SELECT * FROM devices ${DEV_WHERE} ORDER BY last_seen DESC`).bind(...bindTs).all()).results;
    const onlineDevices = deviceRows.filter((d) => nowSec - Number(d.last_seen) <= ONLINE_WINDOW);
    const offlineDevices = deviceRows.filter((d) => nowSec - Number(d.last_seen) > ONLINE_WINDOW).slice(0, 10);
    const onlineCount = onlineDevices.length;
    const trackedCount = deviceRows.length;

    const liveRow = (d, live) => {
      const tag = live ? `<span class="tag live">online</span>` : `<span class="tag off">offline</span>`;
      const asnText = d.asn ? `<code>${esc(d.asn)}</code>${d.asn_org ? ` <span class="sub">${esc(d.asn_org)}</span>` : ""}` : "—";
      const ispText = d.isp ? esc(d.isp) : "—";
      const countryText = d.country ? esc(d.country) + (d.city ? ` · ${esc(d.city)}` : "") : "—";
      const device = String(d.device).slice(0, 8);
      return `<tr class="${live ? "live-row" : "dead-row"}"><td>${tag}</td><td><code>${esc(device)}</code></td><td>${esc(deviceProfile(d))}</td><td>${asnText}</td><td>${ispText}</td><td>${countryText}</td><td>${esc(timeAgo(Number(d.last_seen)))}</td></tr>`;
    };
    const liveOnlineRows = onlineDevices.map((d) => liveRow(d, true)).join("\n");
    const liveOfflineRows = offlineDevices.map((d) => liveRow(d, false)).join("\n");

    const recent = (await env.DB.prepare(`SELECT * FROM reports ${TS_WHERE} ORDER BY ts DESC LIMIT ${PAGE_SIZE}`).bind(...bindTs).all()).results;
    const recentRows = recent.map((r) => {
      const profile = r.transport === "cell"
        ? `${r.carrier || "?"}${r.mccmnc ? " (" + r.mccmnc + ")" : ""}`
        : (r.ssid || "?");
      const time = fmtMMT(Number(r.ts) * 1000, true) + " MMT";
      const isCell = r.transport === "cell";
      const tag = isCell ? `<span class="tag cell">cell</span>` : `<span class="tag wifi">${esc(String(r.transport))}</span>`;
      return `<tr><td>${tag}</td><td>${esc(time)}</td><td>${esc(profile)}</td><td>${esc(r.endpoint)}</td><td>${esc(r.isp || "—")}</td><td><code>${esc(r.asn || "—")}</code></td><td>${esc(r.country || "")}</td></tr>`;
    }).join("\n");

    const firstSeen = earliest ? fmtMMT(Number(earliest) * 1000).slice(0, 10) : "—";

    const html = `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>WARP Connect · Endpoint Reports</title>
  <style>
:root {
  --bg:#05070F;
  --panel:#0B1220;
  --panel-2:#0E1E36;
  --panel-3:#13203A;
  --line:#1A2A44;
  --line-2:#243352;
  --line-soft:rgba(148,184,220,.08);
  --text:#F2F7FF;
  --sub:#A9B8CF;
  --dim:#7A889F;
  --faint:#5E6B85;
  --cyan:#38BDF8;
  --cyan-2:#2DD4BF;
  --green:#34D399;
  --amber:#FBBF24;
  --red:#F87171;
  --mono:"Cascadia Mono",ui-monospace,SFMono-Regular,Menlo,Consolas,"Liberation Mono",monospace;
  --sans:"Segoe UI",Inter,system-ui,-apple-system,"Roboto","Helvetica Neue",sans-serif;
}
* { box-sizing: border-box; }
html, body { min-height: 100vh; }
body {
  margin: 0;
  padding: 26px 20px 18px;
  color: var(--text);
  font-family: var(--sans);
  background:
    radial-gradient(1100px 620px at 78% -8%, rgba(56,189,248,.16), transparent 58%),
    radial-gradient(900px 620px at -12% 112%, rgba(45,212,191,.12), transparent 55%),
    linear-gradient(180deg,#05070F 0%,#0B1220 55%,#05070F 100%);
  -webkit-font-smoothing: antialiased;
}
::-webkit-scrollbar { width: 10px; height: 10px; }
::-webkit-scrollbar-thumb { background: #1e2c47; border-radius: 8px; }
::-webkit-scrollbar-thumb:hover { background: #2b3d5e; }
code, .mono { font-family: var(--mono); color: #C7D8EF; background: transparent; padding: 0; border: 0; }
a { color: var(--cyan); text-decoration: none; }
.wrap { max-width: 1220px; margin: 0 auto; }
.label { font-size: 10px; font-weight: 700; letter-spacing: .18em; text-transform: uppercase; color: var(--dim); }
.top { display: flex; align-items: center; justify-content: space-between; gap: 16px; padding: 2px 0 18px; border-bottom: 1px solid var(--line-soft); margin-bottom: 18px; }
.brand { display: flex; align-items: center; gap: 14px; }
.mark {
  width: 46px; height: 46px; border-radius: 50%; position: relative; flex: 0 0 auto;
  background: radial-gradient(circle at 38% 32%, #8fdcff 0%, #38bdf8 42%, #0b4d6d 100%);
  box-shadow: 0 0 26px rgba(56,189,248,.45), inset 0 0 10px rgba(255,255,255,.18);
}
.mark::after {
  content: ""; position: absolute; inset: 13px; border-radius: 50%; background: var(--panel);
  box-shadow: inset 0 0 8px rgba(0,0,0,.65);
}
.brand h1 { font-size: 15px; font-weight: 800; letter-spacing: .32em; margin: 0; color: var(--text); text-transform: uppercase; }
.brand span { display: block; font-family: var(--mono); font-size: 9.5px; color: var(--cyan-2); margin-top: 4px; letter-spacing: .3em; text-transform: uppercase; }
.links { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
.links a, .logout { color: var(--cyan); text-decoration: none; font-size: 9.5px; font-weight: 700; padding: 8px 15px; border: 1px solid var(--line); border-radius: 999px; letter-spacing: .18em; text-transform: uppercase; transition: all .16s ease; background: transparent; font-family: inherit; cursor: pointer; }
.links a:hover, .logout:hover { border-color: var(--cyan); color: var(--text); background: var(--panel-2); box-shadow: 0 0 18px rgba(56,189,248,.15); }
.inline-form { display: inline-flex; }
.kpis { display: grid; grid-template-columns: repeat(auto-fit, minmax(170px, 1fr)); gap: 12px; margin-bottom: 16px; }
.kpi { position: relative; overflow: hidden; background: var(--panel); border: 1px solid var(--line); border-radius: 16px; padding: 16px 18px; transition: transform .18s ease, border-color .18s ease; }
.kpi::before { content: ""; position: absolute; left: 0; right: 0; top: 0; height: 1px; background: linear-gradient(90deg, transparent, rgba(56,189,248,.55), transparent); }
.kpi:hover { transform: translateY(-2px); border-color: var(--line-2); }
.kpi .num { font-family: var(--mono); font-size: 31px; font-weight: 700; color: var(--text); line-height: 1; letter-spacing: -.02em; }
.kpi .label { font-size: 9.5px; font-weight: 700; letter-spacing: .22em; text-transform: uppercase; color: var(--dim); margin-top: 9px; }
.kpi.live-now { border-color: rgba(52,211,153,.4); box-shadow: inset 0 0 28px rgba(52,211,153,.07), 0 0 24px rgba(52,211,153,.08); }
.kpi.live-now .num { color: var(--green); }
.grid { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; margin-bottom: 16px; }
@media (max-width: 820px) { .grid { grid-template-columns: 1fr; } }
.panel { background: var(--panel); border: 1px solid var(--line); border-radius: 16px; padding: 18px 18px 16px; position: relative; overflow: hidden; box-shadow: 0 12px 34px rgba(0,0,0,.35); }
.panel::before { content: ""; position: absolute; left: 0; right: 0; top: 0; height: 1px; background: linear-gradient(90deg, transparent, rgba(125,211,252,.4), transparent); }
.panel h2 { font-family: var(--mono); font-size: 10px; font-weight: 700; letter-spacing: .24em; text-transform: uppercase; color: var(--cyan-2); margin: 0 0 14px; opacity: .9; }
.panel.live { background: linear-gradient(180deg, rgba(52,211,153,.075) 0%, var(--panel) 38%); border-color: rgba(52,211,153,.32); }
.panel.live h2 { color: var(--green); opacity: 1; }
.panel.live .pulsedot { margin-left: 8px; }
table { border-collapse: collapse; width: 100%; font-size: 12px; }
th { font-family: var(--mono); text-align: left; font-size: 9px; font-weight: 700; letter-spacing: .2em; text-transform: uppercase; color: var(--faint); padding: 2px 12px 12px; border-bottom: 1px solid var(--line); }
td { padding: 11px 12px; border-bottom: 1px solid var(--line-soft); vertical-align: middle; color: var(--text); }
tbody tr { transition: background .15s ease; }
tbody tr:nth-child(odd) td { background: rgba(255,255,255,.018); }
tbody tr:hover td { background: rgba(56,189,248,.06); }
tr:last-child td { border-bottom: 0; }
td code { color: #9ADCFF; }
.tag { display: inline-block; padding: 2px 10px; border-radius: 999px; font-family: var(--mono); font-size: 9px; font-weight: 700; letter-spacing: .16em; text-transform: uppercase; }
.tag.cell { color: #34D399; background: rgba(52,211,153,.1); border: 1px solid rgba(52,211,153,.35); }
.tag.wifi { color: #7DD3FC; background: rgba(56,189,248,.1); border: 1px solid rgba(56,189,248,.35); }
.tag.live { color: #34D399; background: rgba(52,211,153,.12); border: 1px solid rgba(52,211,153,.45); box-shadow: 0 0 12px rgba(52,211,153,.25); }
.tag.off { color: #FBBF24; background: rgba(251,191,36,.1); border: 1px solid rgba(251,191,36,.35); }
.live-row td { background: rgba(52,211,153,.055) !important; }
.dead-row { opacity: .5; }
.btn { background: transparent; border: 1px solid var(--line); color: var(--sub); padding: 5px 14px; border-radius: 999px; font-size: 9.5px; font-weight: 700; letter-spacing: .18em; text-transform: uppercase; font-family: inherit; cursor: pointer; transition: all .15s ease; }
.btn.primary { border-color: var(--cyan); color: var(--cyan); }
.btn.primary:hover { background: var(--cyan); color: #04121F; border-color: var(--cyan); }
.btn.danger { border-color: var(--red); color: #FCA5A5; }
.btn.danger:hover { background: var(--red); color: #04121F; border-color: var(--red); }
.btn:hover { border-color: var(--cyan); color: var(--text); }
.csv-link { font-family: var(--mono); color: var(--cyan); text-decoration: none; font-size: 9px; font-weight: 700; letter-spacing: .18em; text-transform: uppercase; padding: 5px 12px; border: 1px solid var(--line); border-radius: 999px; transition: all .15s ease; }
.csv-link:hover { background: var(--panel-2); border-color: var(--cyan); }
.sub { color: var(--sub); font-size: 11px; }
.pills { display: flex; gap: 8px; margin: 0 0 16px; flex-wrap: wrap; }
.pill { font-size: 9.5px; font-weight: 700; letter-spacing: .18em; padding: 9px 18px; border: 1px solid var(--line); border-radius: 999px; color: var(--sub); text-decoration: none; text-transform: uppercase; transition: all .16s ease; background: transparent; }
.pill:hover { color: var(--text); border-color: var(--line-2); background: var(--panel-2); }
.pill.active { color: #04121F; background: var(--cyan); border-color: var(--cyan); box-shadow: 0 8px 28px rgba(56,189,248,.35); }
.live-meta { display:flex; gap:16px; align-items:center; font-family: var(--mono); font-size: 10px; color: var(--sub); margin-bottom: 12px; letter-spacing: .16em; text-transform: uppercase; }
.live-meta b { color: var(--green); font-size: 17px; letter-spacing: 0; }
.pulsedot { display:inline-block; width: 9px; height: 9px; border-radius: 50%; background: var(--green); box-shadow: 0 0 12px #34d399; animation: pulse 1.6s infinite ease-in-out; vertical-align: middle; }
@keyframes pulse { 0%,100% { opacity: 1; transform: scale(1); } 50% { opacity: .3; transform: scale(.75); } }
.donut-wrap { display: flex; align-items: center; gap: 22px; }
.donut { width: 128px; height: 128px; border-radius: 50%; position: relative; flex: 0 0 auto; box-shadow: 0 0 30px rgba(56,189,248,.22), inset 0 0 0 1px var(--line); }
.donut::after { content: attr(data-total); position: absolute; inset: 17px; border-radius: 50%; background: #0C1425; display: flex; align-items: center; justify-content: center; flex-direction: column; text-align: center; color: var(--text); font-family: var(--mono); font-size: 13px; line-height: 1.3; }
.donut small { display: block; font-size: 8px; color: var(--dim); letter-spacing: .22em; text-transform: uppercase; font-weight: 700; }
.legend { display: flex; flex-direction: column; gap: 9px; font-size: 12px; color: var(--sub); }
.legend div { display: flex; align-items: center; gap: 9px; }
.dot { width: 10px; height: 10px; border-radius: 50%; }
.bar-row { display: flex; align-items: center; gap: 12px; margin: 9px 0; font-size: 12px; }
.bar-label { width: 150px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: #C7D8EF; font-family: var(--mono); font-size: 11px; }
.bar { flex: 1; height: 11px; border-radius: 7px; background: #0E1E36; overflow: hidden; border: 1px solid var(--line-soft); }
.bar i { display: block; height: 100%; background: linear-gradient(90deg, var(--cyan), var(--cyan-2)); border-radius: 7px; box-shadow: 0 0 12px rgba(45,212,191,.35); }
.bar-count { width: 38px; text-align: right; color: var(--dim); font-variant-numeric: tabular-nums; font-family: var(--mono); font-size: 11px; }
.foot { font-family: var(--mono); color: var(--faint); font-size: 9px; padding: 20px 4px 8px; display: flex; align-items: center; justify-content: center; gap: 12px; letter-spacing: .22em; text-transform: uppercase; }
.foot a { color: var(--cyan); text-decoration: none; }
.logout { background: transparent; border: 1px solid var(--line); color: var(--dim); border-radius: 999px; padding: 8px 15px; font: inherit; font-size: 9.5px; font-weight: 700; letter-spacing: .18em; text-transform: uppercase; cursor: pointer; transition: all .16s ease; }
.msg { background: rgba(52,211,153,.09); border: 1px solid rgba(52,211,153,.3); color: #86EFAC; border-radius: 14px; padding: 12px 16px; margin-bottom: 14px; font-size: 12px; font-weight: 500; }
@media (max-width: 720px) {
  body { padding: 16px 12px; }
  .top { flex-direction: column; align-items: flex-start; gap: 12px; }
  .kpis { grid-template-columns: repeat(2, 1fr); }
  .brand h1 { font-size: 13px; }
}
</style>

</head>
<body>
  <div class="wrap">
    <header class="top">
      <div class="brand">
        <div class="mark"></div>
        <div>
          <h1>WARP Connect</h1>
          <span>stats console · field reports</span>
        </div>
      </div>
      <div class="links">
        <a href="/stats.json?range=${esc(rangeLabel)}">JSON</a><a href="/stats.csv?range=${esc(rangeLabel)}">CSV</a><a href="/admin/users">Users</a><form class="inline-form" method="post" action="/logout"><button class="logout" type="submit">Sign out</button></form>
      </div>
    </header>

    <div class="pills" role="tablist" aria-label="Time range">
      <a href="?range=24h" class="pill${rangeLabel === "24h" ? " active" : ""}">24h</a>
      <a href="?range=7d"  class="pill${rangeLabel === "7d"  ? " active" : ""}">7d</a>
      <a href="?range=30d" class="pill${rangeLabel === "30d" ? " active" : ""}">30d</a>
      <a href="?range=all" class="pill${rangeLabel === "all" ? " active" : ""}">all time</a>
    </div>

    <section class="kpis">
      <div class="kpi"><div class="num">${totalReports}</div><div class="label">verified connects</div></div>
      <div class="kpi live-now"><div class="num">${onlineCount}</div><div class="label">online now</div></div>
      <div class="kpi"><div class="num">${uniqueEndpoints}</div><div class="label">unique endpoints</div></div>
      <div class="kpi"><div class="num">${uniqueProfiles}</div><div class="label">network profiles</div></div>
      <div class="kpi"><div class="num">${esc(firstSeen)}</div><div class="label">first report</div></div>
    </section>

    <section class="panel live">
      <h2>Online now · live <span class="pulsedot"></span></h2>
      <div class="live-meta"><div><b>${onlineCount}</b> online · ${Math.max(0, trackedCount - onlineCount)} offline (${esc(rangeLabel)} window)</div></div>
      <table>
        <thead><tr><th>status</th><th>device</th><th>profile</th><th>ASN</th><th>ISP</th><th>country</th><th>last seen</th></tr></thead>
        <tbody>${liveOnlineRows || '<tr><td colspan="7" style="text-align:center;color:var(--sub);padding:14px">No devices online right now — open the tunnel on a phone.</td></tr>'}
          ${liveOfflineRows ? liveOfflineRows : ""}
        </tbody>
      </table>
    </section>

    <section class="grid">
      <div class="panel">
        <h2>Transport mix</h2>
        <div class="donut-wrap">
          <div class="donut" data-total="${totalReports}&#10;reports" style="background: conic-gradient(${donutStops})"></div>
          <div class="legend">
            <div><span class="dot" style="background: var(--blue)"></span>Wi-Fi — ${transportCounts.wifi} (${transportShare.wifi.toFixed(0)}%)</div>
            <div><span class="dot" style="background: var(--green)"></span>Cell — ${transportCounts.cell} (${transportShare.cell.toFixed(0)}%)</div>
            <div><span class="dot" style="background: var(--amber)"></span>Other — ${transportCounts.other} (${transportShare.other.toFixed(0)}%)</div>
          </div>
        </div>
      </div>
      <div class="panel">
        <h2>Top endpoints</h2>
        ${endpointBars || '<div style="color: var(--sub); font-size: 12px">No reports yet.</div>'}
      </div>
    </section>

    <section class="panel">
      <h2>Aggregate — which ISPs work on which endpoint</h2>
      <table>
        <thead><tr><th>net</th><th>endpoint</th><th>profile</th><th>reports</th><th>ISPs seen</th><th>ASNs</th><th>last seen (MMT)</th><th>export</th></tr></thead>
        <tbody>${aggregateRows || "<tr><td colspan=8>No reports yet.</td></tr>"}</tbody>
      </table>
    </section>

    <section class="panel">
      <h2>Recent reports</h2>
      <table>
        <thead><tr><th>net</th><th>when (MMT)</th><th>profile</th><th>endpoint</th><th>ISP</th><th>ASN</th><th>country</th></tr></thead>
        <tbody>${recentRows || "<tr><td colspan=7>No reports yet.</td></tr>"}</tbody>
      </table>
    </section>

    <div class="foot" style="justify-content:center">Developed by Sithu</div>
  </div>
</body>
</html>`;
    return htmlPage(html, 200);
  },
};

function shutPage(message, status) {
  return new Response(
    `<!doctype html><html><head><meta charset="utf-8"><title>Stats offline</title></head>
<body style="background:#0b1020;color:#7b88a8;font-family:monospace;display:flex;align-items:center;justify-content:center;min-height:100vh;margin:0;text-align:center">
<div><h1 style="color:#38bdf8">Stats is not configured</h1><p>${esc(message)}</p></div>
</body></html>`,
    { status, headers: { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store" } },
  );
}

function loginPage(error) {
  const errorHtml = error ? `<div class="err">${esc(error)}</div>` : "";
  return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>WARP Connect · Sign in</title>
  <style>
:root {
  --bg:#05070F;
  --panel:#0B1220;
  --panel-2:#0E1E36;
  --line:#1A2A44;
  --line-soft:rgba(148,184,220,.08);
  --text:#F2F7FF;
  --sub:#A9B8CF;
  --dim:#7A889F;
  --faint:#5E6B85;
  --cyan:#38BDF8;
  --cyan-2:#2DD4BF;
  --mono:"Cascadia Mono",ui-monospace,SFMono-Regular,Menlo,Consolas,"Liberation Mono",monospace;
  --sans:"Segoe UI",Inter,system-ui,-apple-system,"Roboto","Helvetica Neue",sans-serif;
}
* { box-sizing: border-box; }
body {
  margin: 0; padding: 24px 16px; min-height: 100vh; display: flex; align-items: center; justify-content: center;
  font-family: var(--sans); color: var(--text); -webkit-font-smoothing: antialiased;
  background:
    radial-gradient(1100px 600px at 80% -10%, rgba(56,189,248,.18), transparent 58%),
    radial-gradient(900px 600px at -12% 112%, rgba(45,212,191,.14), transparent 55%),
    linear-gradient(180deg,#05070F 0%,#0B1220 55%,#05070F 100%);
}
.card { position: relative; overflow: hidden; background: linear-gradient(165deg,#0D1626 0%,#0B1220 60%); border: 1px solid var(--line); border-radius: 20px; padding: 34px 30px 26px; width: 100%; max-width: 390px; box-shadow: 0 26px 70px rgba(0,0,0,.55), 0 0 60px rgba(56,189,248,.08); }
.card::before { content: ""; position: absolute; left: 0; right: 0; top: 0; height: 1px; background: linear-gradient(90deg, transparent, rgba(125,211,252,.55), transparent); }
.brand { text-align: center; margin-bottom: 26px; }
.mark { width: 72px; height: 72px; border-radius: 50%; margin: 0 auto 16px; position: relative; background: radial-gradient(circle at 38% 32%, #8fdcff 0%, #38bdf8 42%, #0b4d6d 100%); box-shadow: 0 0 34px rgba(56,189,248,.45), inset 0 0 14px rgba(255,255,255,.18); }
.mark::after { content: ""; position: absolute; inset: 20px; border-radius: 50%; background: var(--panel); box-shadow: inset 0 0 12px rgba(0,0,0,.7); }
.brand h1 { font-size: 15px; font-weight: 800; letter-spacing: .34em; margin: 0; color: var(--text); text-transform: uppercase; }
.brand span { display: block; font-family: var(--mono); color: var(--cyan-2); margin-top: 6px; letter-spacing: .3em; text-transform: uppercase; font-size: 9px; font-weight: 700; }
label { display: block; font-family: var(--mono); font-size: 9px; font-weight: 700; letter-spacing: .26em; text-transform: uppercase; color: var(--dim); margin: 18px 0 6px; }
input { width: 100%; background: var(--panel-2); border: 1px solid var(--line); color: var(--text); border-radius: 12px; padding: 13px 14px; font-family: var(--mono); font-size: 13px; outline: none; transition: border-color .16s ease, box-shadow .16s ease; }
input:focus { border-color: var(--cyan); box-shadow: 0 0 0 3px rgba(56,189,248,.16), 0 0 22px rgba(56,189,248,.18); }
button { width: 100%; margin-top: 22px; padding: 15px; border-radius: 999px; border: 0; background: linear-gradient(180deg,#5fd1ff 0%,#38bdf8 100%); color: #04121F; font: inherit; font-family: var(--sans); font-weight: 800; letter-spacing: .22em; text-transform: uppercase; font-size: 11px; cursor: pointer; transition: all .16s ease; box-shadow: 0 12px 34px rgba(56,189,248,.35); }
button:hover { filter: brightness(1.07); transform: translateY(-1px); box-shadow: 0 14px 40px rgba(56,189,248,.42); }
.err { background: rgba(248,113,113,.1); border: 1px solid rgba(248,113,113,.35); color: #FCA5A5; padding: 11px 14px; border-radius: 12px; font-size: 12px; margin-bottom: 10px; }
.foot { margin-top: 22px; font-family: var(--mono); font-size: 9px; font-weight: 700; letter-spacing: .3em; text-transform: uppercase; color: var(--faint); text-align: center; }
</style>

</head>
<body>
  <form class="card" method="post" action="/login" autocomplete="off">
    <div class="brand"><div class="mark"></div><h1>WARP Connect</h1><span>stats console</span></div>
    ${errorHtml}
    <label for="u">User</label>
    <input id="u" name="username" autocomplete="username" required autofocus />
    <label for="p">Password</label>
    <input id="p" name="password" type="password" autocomplete="current-password" required />
    <label for="o">Authenticator code</label>
    <input id="o" name="otp" inputmode="numeric" pattern="[0-9]{6}" placeholder="6 digits" autocomplete="one-time-code" required />
    <button type="submit">Sign in</button>
    <div class="foot">Developed by Sithu</div>
  </form>
</body>
</html>`;
}

function badJson(obj, status = 400) {
  return new Response(JSON.stringify(obj), {
    status,
    headers: { "Content-Type": "application/json", "Cache-Control": "no-store" },
  });
}
