// Iron Corner backend: one Express process, contract in docs/spec.md.
import express from "express";
import crypto from "node:crypto";
import { Readable } from "node:stream";
import { env, modes, state, save, id, fail, refuse, newBrain, HttpError, PORT, SELF_URL, type Bot, type Fight, type Payment, type User } from "./state.ts";
import { MOVES, DAMAGE, commandFor, phraseFor, applyLearn, addLessons, addDrill, dailyView, type FightState } from "./game.ts";
import * as zg from "./zg.ts";
import { saveBrain, loadBrain, saveEpisode, loadEpisode } from "./storage.ts";
import * as hedera from "./hedera.ts";
import * as demos from "./demos.ts";
import * as agentid from "./agentid.ts";
import * as match from "./match.ts";
import * as engine from "./engine.ts";
import * as camp from "./camp.ts";
import * as zgacct from "./zgaccount.ts";
import { knockout, opponent, repairIfDue, repairIn, finishFight } from "./engine.ts";

const app = express();
app.use((req, res, next) => {
  res.set({ "Access-Control-Allow-Origin": "*", "Access-Control-Allow-Headers": "*", "Access-Control-Allow-Methods": "GET,POST,DELETE,OPTIONS" });
  if (req.method === "OPTIONS") return void res.sendStatus(204);
  next();
});
app.use(hedera.arenaMiddleware());
app.use(express.json({ limit: "200kb" }));

// Light rate limits: a sliding minute per IP (all writes) plus tighter ones on the routes that spend testnet funds.
// ponytail: in-memory, single process; fine for one Fly machine.
const seen = new Map<string, number[]>();
function limit(n: number, key = "") {
  return (req: express.Request, _res: express.Response, next: express.NextFunction) => {
    const k = `${req.ip}|${key}`, now = Date.now(), xs = (seen.get(k) ?? []).filter((t) => now - t < 60_000);
    if (xs.length >= n) return next(new HttpError(429, "too many requests, slow down"));
    xs.push(now); seen.set(k, xs); next();
  };
}
app.use((req, res, next) => (req.method === "GET" ? next() : limit(Number(env.WRITE_LIMIT_PER_MIN ?? 240), "w")(req, res, next)));

const bot = (botId: string): Bot => state.bots[botId] ?? fail(404, `unknown bot ${botId}`);

// ---- identity: bearer tokens, only their SHA-256 is stored ----
const sha256 = (x: string) => crypto.createHash("sha256").update(x).digest("hex");
function userOf(req: express.Request): User | null {
  const m = /^Bearer\s+(\S{20,200})$/.exec(req.headers.authorization ?? "");
  if (!m) return null;
  const h = sha256(m[1]);
  return Object.values(state.users).find((u) => u.tokenHash === h) ?? null;
}
const me = (req: express.Request) => userOf(req) ?? fail(401, "sign in first: Authorization: Bearer <token>");
// Bot writes: a player's bot only by its owner; demo/house bots (no owner) stay open so the demo keeps working.
function ownBot(req: express.Request, botId: string): Bot {
  const b = bot(botId);
  if (b.owner) { const u = me(req); if (u.userId !== b.owner) fail(403, "not your bot"); }
  return b;
}
const leagueOf = (_b: Bot) => "open"; // one league since World ID was removed
const trained = (b: Bot) => !b.owner || !!(b.onboarding?.round && b.onboarding.coached);
const onboard = (b: Bot) => (b.onboarding ??= { round: false, coached: false, funded: false });
const fightOf = (fid: string): Fight => state.fights[fid] ?? fail(404, "unknown fight");

// multipart without a dependency: hand the stream to the web Request parser.
async function form(req: express.Request) {
  if (Number(req.headers["content-length"] ?? 0) > 3_000_000) fail(413, "audio too large (max ~10 s wav)");
  if (!String(req.headers["content-type"]).startsWith("multipart/form-data")) fail(400, "expected multipart/form-data");
  return new Request("http://x", { method: "POST", headers: req.headers as any, body: Readable.toWeb(req) as any, duplex: "half" } as any).formData();
}
async function heard(fd: FormData, b?: Bot) {
  const audio = fd.get("audio");
  if (!(audio instanceof Blob) || !audio.size) fail(400, "missing audio field");
  // mock STT has no ears: `text` field (or "block") stands in for the transcript
  return zg.transcribe(audio as Blob, (fd.get("text") as string) || undefined, b);
}

// Training coach line → instant command word (sparring drill), a known phrase, or (via 0G) a structured brain update:
// counter / combo / style / phrase. Response carries a one-line diff and the 0G request id.
async function teach(b: Bot, text: string) {
  let out: { command: string | null; learned: Record<string, unknown> | null; diff: string | null; requestId: string | null; mock?: boolean; error?: string };
  const command = commandFor(text), known = command ? null : phraseFor(b.brain, text);
  if (command) out = { command, learned: null, diff: null, requestId: null };
  else if (known) out = { command: known.moves[0], learned: { kind: "phrase", id: `phrase#${b.brain.phrases.indexOf(known) + 1}`, ...known },
    diff: `Drilled phrase: “${known.say}” = ${known.moves.join(", ")}`, requestId: null };
  else {
    const r = await zg.learn(text, b);
    out = { command: null, learned: null, diff: null, requestId: r.requestId, ...(r.mock ? { mock: true } : {}), ...(r.error ? { error: r.error } : {}) };
    if (r.learned) {
      const { id, diff } = applyLearn(b.brain, r.learned, text);
      out.learned = { ...r.learned, id, say: text.slice(0, 60), means: diff, moves: "moves" in r.learned ? r.learned.moves : [] };
      out.diff = diff;
    }
  }
  const goalHit = out.command || out.learned ? addDrill(b) : false;
  if (out.command || out.learned) onboard(b).coached = true;
  save();
  return { ...out, daily: { ...dailyView(b), justDone: goalHit } };
}

// A normal-fight KO repairs itself after REPAIR_MS (engine.ts); a high-stakes KO waits for its owner to revive it.

async function botView(b: Bot) {
  repairIfDue(b);
  const bal = b.accountId ? await hedera.balance(b.accountId).catch(() => ({ hbar: null, usdc: null })) : { hbar: null, usdc: null };
  const last = b.brainHistory.at(-1);
  return { botId: b.botId, name: b.name, house: b.house, accountId: b.accountId, evmAddress: b.evmAddress, ...bal,
    payAsset: hedera.PAY_ASSET, brainVersion: b.brain.version, daily: dailyView(b), brainRoot: last?.root ?? null, alive: b.alive,
    koStakes: b.alive ? null : b.koStakes ?? null, repairIn: repairIn(b), needsRevive: !b.alive && b.koStakes === "high",
    progress: demos.progressView(b),
    paint: b.paint ?? null, owner: b.owner ?? null, league: leagueOf(b), rating: b.rating ?? 1000, record: b.record ?? { w: 0, l: 0 },
    onboarding: { trained: trained(b), funded: !!b.onboarding?.funded, round: !!b.onboarding?.round, coached: !!b.onboarding?.coached },
    agentId: agentid.view(b), adapter: b.adapter ?? null, zgAccount: zgacct.view(b) };
}

// ---- health / bots ----
app.get("/health", (_req, res) => void res.json({ ok: true, modes, payAsset: hedera.PAY_ASSET, ledgerTopic: hedera.ledgerTopic() }));
app.get("/bots", async (_req, res) => void res.json(await Promise.all(Object.values(state.bots).map(botView))));
app.get("/bots/:id", async (req, res) => {
  const b = bot(req.params.id), mine = !b.owner || userOf(req)?.userId === b.owner; // a player's brain is for its owner
  res.json({ ...(await botView(b)), ...(mine ? { brain: b.brain } : {}), brainHistory: b.brainHistory });
});
// Download + decrypt a saved version from 0G Storage (defaults to the latest).
app.get("/bots/:id/brain", async (req, res) => {
  const b = ownBot(req, req.params.id);
  const root = (req.query.root as string) ?? b.brainHistory.at(-1)?.root ?? fail(404, "no saved brain yet");
  res.json({ root, brain: await loadBrain(b.botId, root) });
});
app.post("/bots/:id/revive", (req, res) => {
  const b = ownBot(req, req.params.id);
  b.alive = true; save();
  res.json({ botId: b.botId, alive: true });
});

// ---- training ----
app.post("/train/:botId/coach", async (req, res) => {
  const b = ownBot(req, req.params.botId);
  const t = await heard(await form(req), b);
  const r = await teach(b, t.text);
  res.json({ text: t.text, ...r, requestId: r.requestId ?? t.requestId, sttRequestId: t.requestId, ...(t.mock ? { mock: true } : {}) });
});
app.post("/train/:botId/coach-text", async (req, res) => {
  const b = ownBot(req, req.params.botId);
  const text = String(req.body?.text ?? "").trim().slice(0, 200) || fail(400, "text required");
  res.json({ text, ...(await teach(b, text)) });
});
app.post("/train/:botId/spar", async (req, res) => void res.json(await zg.decide(ownBot(req, req.params.botId), (req.body?.state ?? {}) as FightState, "dummy")));
// Shadow sparring: batches of demonstrations from the move pad; `roundEnd` closes a round (energy + XP).
// A round that ran long enough (15 s, 3 moves) counts as the onboarding training round.
app.post("/train/:botId/demos", (req, res) => {
  const b = ownBot(req, req.params.botId), out = demos.addDemos(b, req.body);
  if (out.round && !/too short/.test(out.round.reason ?? "")) { onboard(b).round = true; save(); }
  res.json(out);
});
app.post("/train/:botId/save", limit(20, "save"), async (req, res) => void res.json(await saveBrain(ownBot(req, req.params.botId))));

// ---- fights ----
app.post("/fights", async (req, res) => {
  const { a = "bot_a", b = "bot_b", stakes = "normal", budgetUsd = 0.5 } = req.body ?? {};
  ownBot(req, String(a)); ownBot(req, String(b));
  if (a === b) fail(400, "a bot cannot fight itself");
  if (!["normal", "high"].includes(stakes)) fail(400, "stakes must be normal or high");
  const cents = Math.round(Number(budgetUsd) * 100);
  if (!(cents >= 5 && cents <= 500)) fail(400, "budgetUsd must be 0.05..5");
  for (const x of [a, b]) {
    const kb = state.bots[x]; repairIfDue(kb);
    if (!kb.alive) fail(409, kb.koStakes === "high" ? `${x} was knocked out in a high-stakes fight; its owner can revive it` : `${x} is repairing (${repairIn(kb)} s)`);
  }
  const f: Fight = { fightId: id("f"), a, b, stakes, status: "live", budgets: { [a]: cents, [b]: cents }, startBudget: cents,
    winner: null, payments: [], decisions: [], hits: [], ledgerTopic: hedera.ledgerTopic(), createdAt: new Date().toISOString() };
  state.fights[f.fightId] = f; save();
  hedera.ledger({ t: "start", fight: f.fightId, a, b, stakes, budget: cents });
  res.json({ fightId: f.fightId, a, b, budgets: f.budgets, ledgerTopic: f.ledgerTopic });
});

function inFight(f: Fight, botId: unknown): string {
  if (botId !== f.a && botId !== f.b) fail(400, "botId is not in this fight");
  return botId as string;
}

// Matched fights run on the server; the client-driven routes below are for the demo fight only.
const clientDriven = (f: Fight) => { if (f.engine) fail(409, "the server runs this fight; play GET /fights/:id/events"); };

app.post("/fights/:id/decide", async (req, res) => {
  const f = fightOf(req.params.id), botId = inFight(f, req.body?.botId);
  clientDriven(f);
  if (f.status !== "live") return void res.json({ stance: "safe_stop", reason: "fight over" });
  const plan = await zg.decide(bot(botId), (req.body?.state ?? {}) as FightState, opponent(f, botId));
  if (plan.stance === "safe_stop") console.warn(`[decide] ${f.fightId} ${botId} safe_stop: ${plan.reason}`);
  f.decisions.push({ botId, requestId: plan.requestId ?? null, stance: plan.stance, ...(plan.used ? { used: plan.used } : {}), at: new Date().toISOString(), ...(plan.mock ? { mock: true } : {}) });
  demos.onDecide(bot(botId), f, (req.body?.state ?? {}) as FightState, plan); // self-experience from its own fights
  save();
  res.json(plan);
});

app.post("/fights/:id/hit", (req, res) => {
  const f = fightOf(req.params.id);
  clientDriven(f);
  const { attacker, defender, move, result } = req.body ?? {};
  inFight(f, attacker); inFight(f, defender);
  if (attacker === defender) fail(400, "attacker and defender must differ");
  if (!MOVES.includes(move)) fail(400, "unknown move");
  if (!["clean", "blocked", "dodged"].includes(result)) fail(400, "result must be clean|blocked|dodged");
  if (f.status !== "live") fail(409, "fight is over");
  const dmg = result === "clean" ? DAMAGE[move] ?? 0 : 0;
  const hitNo = f.hits.length + 1;
  const cents = Math.min(dmg, f.budgets[defender]);
  f.hits.push({ hitNo, attacker, defender, move, result, cents, at: new Date().toISOString() });
  hedera.ledger({ t: "hit", fight: f.fightId, hit: hitNo, attacker, defender, move, result, cents });
  if (cents > 0) {
    f.budgets[defender] -= cents;
    const p: Payment = { hitNo, from: defender, to: attacker, cents, status: "pending", txId: null, hashscan: null };
    f.payments.push(p);
    hedera.queuePayment(f, p, () => knockout(f, defender));
  }
  if (dmg > 0 && f.budgets[defender] <= 0) knockout(f, defender); // can't cover the hit → KO
  save();
  res.json({ budgets: f.budgets, ko: f.status === "over" ? opponent(f, f.winner!) : null });
});

// Fights are autonomous: the bot fights on what it learned in training. No mid-fight coaching.
app.post(["/fights/:id/coach", "/fights/:id/coach-text"], (req) => { fightOf(req.params.id as string); fail(409, "fights are autonomous; coach in training"); });

app.get("/fights/:id", (req, res) => {
  const f = fightOf(req.params.id);
  res.json({ fightId: f.fightId, a: f.a, b: f.b, stakes: f.stakes, status: f.status, budgets: f.budgets, winner: f.winner,
    payments: f.payments, ledgerTopic: f.ledgerTopic, decisions: f.decisions, hits: f.hits.length, lessons: f.lessons ?? null,
    episode: f.episode ?? null,
    ...(f.engine ? { engine: true, house: !!f.house, stakeCents: f.stakeCents, seedHash: f.seedHash, seed: f.status === "over" ? f.seed : null,
      endReason: f.endReason ?? null, ratingDelta: f.ratingDelta ?? null } : {}) });
});
// The event stream the app plays (poll with since = last seq seen). The seed is revealed once the fight is over.
app.get("/fights/:id/events", (req, res) => {
  const f = fightOf(req.params.id);
  if (!f.engine) fail(404, "only server-run fights have an event stream");
  const since = Math.max(0, Number(req.query.since) || 0);
  res.json({ events: f.events!.filter((e) => e.seq > since), status: f.status, budgets: f.budgets, winner: f.winner,
    seedHash: f.seedHash, ...(f.status === "over" ? { seed: f.seed } : {}) });
});

// Replay source: the episode log read back from 0G Storage (not from server memory).
app.get("/fights/:id/episode", async (req, res) => {
  const f = fightOf(req.params.id);
  if (!f.episode) fail(404, "episode not saved yet (it uploads right after /end)");
  res.json({ root: f.episode.root, txHash: f.episode.txHash, episode: await loadEpisode(f.episode.root) });
});
app.post("/fights/:id/end", async (req, res) => {
  const f = fightOf(req.params.id);
  if (f.engine && f.status === "live") fail(409, "the server ends this fight (KO or 90 s)");
  res.json(await finishFight(f));
});

// ---- players (docs/flow-spec.md) ----
app.post("/users", limit(5, "users"), (_req, res) => {
  const token = crypto.randomBytes(32).toString("base64url"), userId = id("u");
  state.users[userId] = { userId, tokenHash: sha256(token), createdAt: new Date().toISOString(), botId: null };
  save();
  res.json({ userId, token });
});
app.get("/me", (req, res) => {
  const u = me(req), b = u.botId ? state.bots[u.botId] : null;
  res.json({ userId: u.userId, league: "open", botId: b?.botId ?? null });
});
export const PAINTS = ["brass", "gunmetal", "crimson", "cobalt", "jade", "bone"];
const MAX_PLAYER_BOTS = Number(env.MAX_PLAYER_BOTS ?? 8); // each costs the operator 0.3 testnet HBAR
app.post("/bots", limit(10, "bots"), async (req, res) => {
  const u = me(req);
  if (u.botId) refuse("one_bot_per_user", { botId: u.botId });
  const name = String(req.body?.name ?? "").trim().replace(/\s+/g, " ");
  if (!/^[A-Za-z0-9 ]{2,20}$/.test(name)) fail(400, "name: 2-20 letters, digits or spaces");
  const paint = String(req.body?.paint ?? "");
  if (!PAINTS.includes(paint)) fail(400, `paint: one of ${PAINTS.join(", ")}`);
  if (Object.values(state.bots).filter((b) => b.owner).length >= MAX_PLAYER_BOTS) refuse("arena_full", { error: "The arena is full for this testnet build. Try the demo bots." });
  const b: Bot = { botId: id("bot"), name, house: false, accountId: null, evmAddress: null, alive: true, ownerSub: null,
    brain: newBrain("", 0.5, 0.5), brainHistory: [], owner: u.userId, paint, rating: 1000, record: { w: 0, l: 0 },
    onboarding: { round: false, coached: false, funded: false } };
  b.brain.botId = b.botId;
  state.bots[b.botId] = b; u.botId = b.botId; save(); // slot taken before the slow part
  try { await hedera.createBotAccount(b); }
  catch (e: any) {
    delete state.bots[b.botId]; u.botId = null; save();
    console.warn(`[bots] account create failed: ${e.message}`);
    fail(502, "could not create the bot's Hedera account, try again");
  }
  save();
  void agentid.sync(b); // mint the Agentic ID in the background; failures land in agentId.retry
  res.json(await botView(b));
});

// Test funds from the house faucet: once per bot per UTC day, tops up to 0.5 HBAR, only while below 0.5.
// Idempotency-Key: a repeat with the same key returns the first result.
const funding = new Set<string>();
app.post("/bots/:id/fund", limit(6, "fund"), async (req, res) => {
  const b = ownBot(req, req.params.id), key = String(req.get("idempotency-key") ?? "").slice(0, 80) || undefined;
  if (!b.owner || !b.hkey) refuse("demo_bot");
  const today = new Date().toISOString().slice(0, 10);
  if (b.fund?.day === today) {
    if (key && b.fund.key === key) return void res.json({ hbar: b.fund.hbar, txId: b.fund.txId, hashscan: modes.hedera === "live" ? hedera.hashscan(b.fund.txId) : null, repeat: true });
    refuse("already_funded_today");
  }
  if (funding.has(b.botId)) refuse("in_progress");
  funding.add(b.botId);
  try {
    const bal = await hedera.balance(b.accountId!, true);
    const hbar = Math.floor((0.5 - (bal.hbar ?? 0)) * 100) / 100;
    if (hbar < 0.01) refuse("balance_high", { hbarNow: bal.hbar });
    if ((await hedera.operatorBalance()).hbar < Number(env.FAUCET_FLOOR_HBAR ?? 1)) refuse("faucet_empty");
    const txId = await hedera.sendHbar(b, hbar);
    b.fund = { day: today, hbar, txId, ...(key ? { key } : {}) }; onboard(b).funded = true; save();
    res.json({ hbar, txId, hashscan: modes.hedera === "live" ? hedera.hashscan(txId) : null });
  } finally { funding.delete(b.botId); }
});

// ---- matchmaking ----
const inLiveFight = (botId: string) => Object.values(state.fights).some((f) => f.engine && f.status === "live" && (f.a === botId || f.b === botId));
function queueView(b: Bot) {
  const e = state.queue.find((x) => x.botId === b.botId), lm = b.lastMatch, f = lm ? state.fights[lm.fightId] : null;
  if (e) return { status: "searching", waitedSec: Math.floor((Date.now() - e.joinedAt) / 1000), queueSize: state.queue.length, stakeCents: e.stakeCents };
  if (f && (f.status === "live" || Date.now() - lm!.at < 120_000)) return { status: "matched", waitedSec: 0, queueSize: state.queue.length, fightId: f.fightId, house: !!f.house };
  return { status: "idle", waitedSec: 0, queueSize: state.queue.length };
}
app.post("/queue", limit(20, "queue"), async (req, res) => {
  const b = ownBot(req, String(req.body?.botId)), stakeCents = Number(req.body?.stakeCents);
  if (b.house) fail(400, "house bots don't queue");
  if (!match.STAKES.includes(stakeCents)) fail(400, `stakeCents: one of ${match.STAKES.join(", ")}`);
  if (state.queue.some((x) => x.botId === b.botId)) return void res.json(queueView(b)); // joining twice is a no-op
  repairIfDue(b);
  if (!b.alive) refuse(b.koStakes === "high" ? "needs_revive" : "repairing", { repairIn: repairIn(b) });
  if (!trained(b)) refuse("needs_training");
  if (inLiveFight(b.botId)) refuse("in_fight");
  // The stake is reserved while queued and fighting: one entry or fight per bot, and the wallet must cover it (+5 c fees).
  const bal = await hedera.balance(b.accountId!, true);
  const have = hedera.PAY_ASSET === "usdc" ? (bal.usdc ?? 0) * 100 : (bal.hbar ?? 0) / hedera.HBAR_PER_CENT;
  if (have < stakeCents + 5) refuse("insufficient_funds", { haveCents: Math.floor(have), needCents: stakeCents + 5 });
  if (!state.queue.some((x) => x.botId === b.botId) && !inLiveFight(b.botId)) {
    state.queue.push({ botId: b.botId, ownerId: b.owner ?? `demo:${b.botId}`, league: leagueOf(b), rating: b.rating ?? 1000,
      level: demos.progressView(b).level, stakeCents, joinedAt: Date.now() });
    save();
  }
  res.json(queueView(b));
});
app.delete("/queue/:botId", (req, res) => {
  const b = ownBot(req, req.params.botId);
  state.queue = state.queue.filter((x) => x.botId !== b.botId); save();
  res.json(queueView(b));
});
app.get("/queue/:botId", (req, res) => void res.json(queueView(ownBot(req, req.params.botId))));

// One matcher pass every 2 s; synchronous, so a pass is atomic in this single process.
function matchTick() {
  state.queue = state.queue.filter((e) => state.bots[e.botId]?.alive);
  if (!state.queue.length) return;
  const { pairs, house, rest } = match.pass(state.queue, Date.now(), (x) => state.bots[x]?.lastOpp);
  state.queue = rest;
  for (const [x, y] of pairs) engine.startFight(state.bots[x.botId], state.bots[y.botId], x.stakeCents, false);
  for (const x of house) engine.startFight(state.bots[x.botId], engine.houseFor(x.level), x.stakeCents, true);
  if (pairs.length || house.length) { save(); console.log(`[match] ${pairs.length} pair(s), ${house.length} house fight(s), ${rest.length} waiting`); }
}

// ---- the owner's own 0G account (iAI credits): see zgaccount.ts ----
app.post("/bots/:id/zg/link", limit(6, "zglink"), (req, res) => void res.json(zgacct.linkUrl(ownBot(req, req.params.id))));
app.delete("/bots/:id/zg", (req, res) => void res.json(zgacct.unlink(ownBot(req, req.params.id))));
app.get("/connect", (_req, res) => void res.type("html").send(zgacct.page()));
const CONNECT_JS = new URL("../public/connect.js.gz", import.meta.url);
app.get("/connect.js", (_req, res) => void res.set({ "content-type": "text/javascript", "content-encoding": "gzip", "cache-control": "public, max-age=3600" }).sendFile(CONNECT_JS.pathname));
app.post("/connect", limit(10, "connect"), async (req, res) => {
  const { code, privyToken, apiKey, wallet } = req.body ?? {};
  res.json(await zgacct.link(String(code ?? ""), String(privyToken ?? ""), String(apiKey ?? ""), wallet ? String(wallet) : null));
});

// ---- fight camp (LoRA on 0G Compute) ----
app.post("/bots/:id/camp", limit(3, "camp"), async (req, res) => void res.json(camp.view(await camp.start(ownBot(req, req.params.id)))));
app.get("/bots/:id/camp", (req, res) => { const b = bot(req.params.id); res.json(camp.view(b.campId ? state.camps[b.campId] : undefined)); });

// ---- arena (x402 paid route; the middleware above settles payment) ----
app.post("/arena/hit", (_req, res) => void res.json({ ok: true }));
app.post("/arena/scout", (_req, res) => void res.json({ ok: true })); // the report itself is built after payment (engine.ts scout)

app.use((_req, res) => void res.status(404).json({ error: "not found" }));
app.use((err: any, _req: express.Request, res: express.Response, _next: express.NextFunction) => {
  const status = err instanceof HttpError ? err.status : err?.status ?? 500;
  if (status >= 500) console.error(err);
  res.status(status).json({ error: err?.message ?? String(err), ...(err instanceof HttpError ? err.extra : {}) });
});

// ---- start ----
console.log(`[modes] 0G inference=${modes.zg} 0G storage=${modes.storage} hedera=${modes.hedera} (pay in ${hedera.PAY_ASSET})`);
await hedera.init();
console.log(`[hedera] bots ${Object.values(state.bots).map((b) => `${b.botId}=${b.accountId}`).join(" ")} ledger=${hedera.ledgerTopic()}`);
engine.closeOrphans();
camp.resumeAll();
setInterval(matchTick, Number(env.MATCH_TICK_MS ?? 2000)).unref?.();
app.listen(PORT, () => console.log(`[iron-corner] listening on :${PORT}`));
