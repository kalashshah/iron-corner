// "Use your own 0G credits" (the iAI bonus): a player signs in with their wallet through our Privy app and links an
// API key from their own pc.0g.ai account (where iAI holders get daily credits). From then on their bot's 0G calls
// (fight decisions, coaching, lessons) are billed to that account, and we count what each call cost from 0G's own
// billing trace. 0G's router only takes Privy logins from 0G's own Privy app, so linking a key is the one manual step.
//
// Flow: app → POST /bots/:id/zg/link (owner) → {url} → in-app sheet → GET /connect?c=<code> → Privy login + key →
// POST /connect {code, privyToken, apiKey} → key checked against 0G, sealed with AES-256-GCM, never sent out again.
import crypto from "node:crypto";
import OpenAI from "openai";
import { createRemoteJWKSet, jwtVerify } from "jose";
import { env, state, save, fail, SELF_URL, type Bot } from "./state.ts";

const BASE = env.ZG_BASE_URL ?? "https://router-api.0g.ai/v1";
export const PRIVY_APP_ID = env.PRIVY_APP_ID ?? "";
const jwks = PRIVY_APP_ID ? createRemoteJWKSet(new URL(`https://auth.privy.io/api/v1/apps/${PRIVY_APP_ID}/jwks.json`)) : null;

export type ZgLink = { key: string; last4: string; wallet: string | null; privyUser: string; at: string; spentMicro: number; calls: number };

const sealKey = (botId: string) => Buffer.from(crypto.hkdfSync("sha256", env.BRAIN_SECRET ?? state.brainSecret, "iron-corner-zg-key", botId, 32));
function seal(botId: string, text: string) {
  const iv = crypto.randomBytes(12), c = crypto.createCipheriv("aes-256-gcm", sealKey(botId), iv);
  return Buffer.concat([iv, c.update(text, "utf8"), c.final(), c.getAuthTag()]).toString("base64");
}
function unseal(botId: string, blob64: string) {
  const blob = Buffer.from(blob64, "base64"), d = crypto.createDecipheriv("aes-256-gcm", sealKey(botId), blob.subarray(0, 12));
  d.setAuthTag(blob.subarray(-16));
  return Buffer.concat([d.update(blob.subarray(12, -16)), d.final()]).toString("utf8");
}

// One OpenAI client per linked bot; null = use the house key.
const clients = new Map<string, OpenAI>();
export function clientFor(b?: Bot | null): OpenAI | null {
  if (!b?.zg) return null;
  let c = clients.get(b.botId);
  if (!c) clients.set(b.botId, (c = new OpenAI({ apiKey: unseal(b.botId, b.zg.key), baseURL: BASE, maxRetries: 0, defaultHeaders: { "X-0G-Provider-Sort": "latency" } })));
  return c;
}
// Count what a call cost the player, from 0G's billing trace on the response.
export function charge(b: Bot | null | undefined, body: any) {
  if (!b?.zg) return;
  const micro = Number(body?.x_0g_trace?.billing?.total_cost_usd_micro ?? 0);
  b.zg.spentMicro += Number.isFinite(micro) ? micro : 0; b.zg.calls++;
}
export const view = (b: Bot) => b.zg
  ? { linked: true, last4: b.zg.last4, wallet: b.zg.wallet, at: b.zg.at, spentUsd: b.zg.spentMicro / 1e6, calls: b.zg.calls }
  : { linked: false };

// ---- link codes: short-lived, single use, tied to the bot whose owner asked ----
const codes = new Map<string, { botId: string; exp: number }>();
export function linkUrl(b: Bot) {
  if (!PRIVY_APP_ID) fail(503, "0G account linking isn't configured (PRIVY_APP_ID)");
  const code = crypto.randomBytes(16).toString("hex");
  codes.set(code, { botId: b.botId, exp: Date.now() + 10 * 60_000 });
  return { url: `${SELF_URL}/connect?c=${code}`, expiresIn: 600 };
}

export async function link(code: string, privyToken: string, apiKey: string, wallet: string | null) {
  const c = codes.get(code);
  if (!c || c.exp < Date.now()) fail(410, "this link expired, open it again from the game");
  if (!jwks) fail(503, "0G account linking isn't configured");
  const { payload } = await jwtVerify(privyToken, jwks, { issuer: "privy.io", audience: PRIVY_APP_ID }).catch(() => fail(401, "Privy sign-in didn't verify, sign in again"));
  if (!/^sk-[A-Za-z0-9_-]{16,}$/.test(apiKey)) fail(400, "that doesn't look like a 0G API key (starts with sk-)");
  // A real key on a funded account answers a 1-token request; a bad one gets 401 and an empty one 402.
  const probe = await fetch(`${BASE}/chat/completions`, { method: "POST", signal: AbortSignal.timeout(15000),
    headers: { authorization: `Bearer ${apiKey}`, "content-type": "application/json" },
    body: JSON.stringify({ model: env.ZG_CHAT_MODEL ?? "qwen-flash", messages: [{ role: "user", content: "hi" }], max_tokens: 1 }) });
  if (probe.status === 401) fail(400, "0G didn't accept that key: check it on pc.0g.ai → API Keys");
  if (probe.status === 402) fail(400, "that 0G account has no credits left today");
  if (!probe.ok) fail(502, `0G answered ${probe.status}, try again`);
  const b = state.bots[c.botId] ?? fail(404, "unknown bot");
  codes.delete(code);
  b.zg = { key: seal(b.botId, apiKey), last4: apiKey.slice(-4), wallet: wallet && /^0x[0-9a-fA-F]{40}$/.test(wallet) ? wallet : null,
    privyUser: String(payload.sub), at: new Date().toISOString(), spentMicro: 0, calls: 0 };
  clients.delete(b.botId); save();
  return { botId: b.botId, name: b.name, ...view(b) };
}
export function unlink(b: Bot) { delete b.zg; clients.delete(b.botId); save(); return view(b); }

// The sign-in page: Privy (email, Google or wallet) → paste the pc.0g.ai key → done. The React + Privy code is one
// bundled file (connect/main.jsx → public/connect.js, built in the Dockerfile), served gzipped.
export const page = () => `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Iron Corner: use your 0G credits</title>
<style>:root{color-scheme:dark}body{margin:0;font:16px/1.45 system-ui,sans-serif;background:#0d0f14;color:#e9ecf2;display:grid;place-items:center;min-height:100vh}
main{width:min(420px,100% - 32px);padding:24px;border:1px solid #2a2f3a;border-radius:14px;background:#151922}h1{font-size:20px;margin:0 0 6px}
p{color:#a9b0bf;margin:8px 0}button{width:100%;padding:12px;border:0;border-radius:10px;background:#f2b134;color:#111;font-weight:700;font-size:16px;margin-top:12px}
input{width:100%;box-sizing:border-box;padding:12px;border-radius:10px;border:1px solid #2a2f3a;background:#0d0f14;color:#e9ecf2;font:14px ui-monospace,monospace}
a{color:#f2b134}.ok{color:#5fd38d}.err{color:#ff7a7a}code{font-size:13px}</style></head><body><main id="root"><p>Loading…</p></main>
<script>window.PRIVY_APP_ID=${JSON.stringify(PRIVY_APP_ID)}</script><script type="module" src="/connect.js"></script></body></html>`;

// Self-check: `npx tsx src/zgaccount.ts` (no network)
if (process.argv[1]?.endsWith("zgaccount.ts")) {
  const assert = (await import("node:assert")).strict;
  const sealed = seal("bot_x", "sk-test-1234567890abcdef");
  assert.equal(unseal("bot_x", sealed), "sk-test-1234567890abcdef");
  assert.throws(() => unseal("bot_y", sealed)); // another bot's key can't open it
  const b = { botId: "bot_x", zg: { key: sealed, last4: "cdef", wallet: null, privyUser: "did:privy:x", at: "", spentMicro: 0, calls: 0 } } as any;
  charge(b, { x_0g_trace: { billing: { total_cost_usd_micro: "15" } } }); charge(b, {});
  assert.deepEqual([b.zg.spentMicro, b.zg.calls], [15, 2]);
  assert.equal(view(b).spentUsd, 0.000015);
  console.log("zgaccount.ts self-check ok");
}
