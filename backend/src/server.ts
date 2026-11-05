// Iron Corner backend: one Express process, contract in docs/spec.md.
import express from "express";
import { Readable } from "node:stream";
import QRCode from "qrcode";
import { modes, state, save, id, fail, HttpError, PORT, type Bot, type Fight, type Payment } from "./state.ts";
import { MOVES, DAMAGE, commandFor, phraseFor, applyLearn, addLessons, addDrill, dailyView, type FightState } from "./game.ts";
import * as zg from "./zg.ts";
import { saveBrain, loadBrain, saveEpisode, loadEpisode } from "./storage.ts";
import * as hedera from "./hedera.ts";
import * as world from "./world.ts";

const app = express();
app.use((req, res, next) => {
  res.set({ "Access-Control-Allow-Origin": "*", "Access-Control-Allow-Headers": "*", "Access-Control-Allow-Methods": "GET,POST,OPTIONS" });
  if (req.method === "OPTIONS") return void res.sendStatus(204);
  next();
});
app.use(hedera.arenaMiddleware());
app.use(express.json({ limit: "200kb" }));

const bot = (botId: string): Bot => state.bots[botId] ?? fail(404, `unknown bot ${botId}`);
const fightOf = (fid: string): Fight => state.fights[fid] ?? fail(404, "unknown fight");
const owned = (f: { a: string; b: string }) => (state.bots[f.a].house ? f.b : f.a);

// multipart without a dependency: hand the stream to the web Request parser.
async function form(req: express.Request) {
  if (Number(req.headers["content-length"] ?? 0) > 3_000_000) fail(413, "audio too large (max ~10 s wav)");
  if (!String(req.headers["content-type"]).startsWith("multipart/form-data")) fail(400, "expected multipart/form-data");
  return new Request("http://x", { method: "POST", headers: req.headers as any, body: Readable.toWeb(req) as any, duplex: "half" } as any).formData();
}
async function heard(fd: FormData) {
  const audio = fd.get("audio");
  if (!(audio instanceof Blob) || !audio.size) fail(400, "missing audio field");
  // mock STT has no ears: `text` field (or "block") stands in for the transcript
  return zg.transcribe(audio as Blob, (fd.get("text") as string) || undefined);
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
    const r = await zg.learn(text);
    out = { command: null, learned: null, diff: null, requestId: r.requestId, ...(r.mock ? { mock: true } : {}), ...(r.error ? { error: r.error } : {}) };
    if (r.learned) {
      const { id, diff } = applyLearn(b.brain, r.learned, text);
      out.learned = { ...r.learned, id, say: text.slice(0, 60), means: diff, moves: "moves" in r.learned ? r.learned.moves : [] };
      out.diff = diff;
    }
  }
  const goalHit = out.command || out.learned ? addDrill(b) : false;
  save();
  return { ...out, daily: { ...dailyView(b), justDone: goalHit } };
}

// A normal-fight KO repairs itself after REPAIR_MS; a high-stakes KO needs a World ID revive.
const REPAIR_MS = Number(process.env.REPAIR_MS ?? 30_000);
function repairIfDue(b: Bot) {
  if (!b.alive && (b.house || (b.koStakes !== "high" && Date.now() - (b.koAt ?? 0) >= REPAIR_MS))) { b.alive = true; save(); }
}
const repairIn = (b: Bot) => b.alive || b.koStakes === "high" ? null : Math.max(0, Math.ceil((REPAIR_MS - (Date.now() - (b.koAt ?? 0))) / 1000));

async function botView(b: Bot) {
  repairIfDue(b);
  const bal = b.accountId ? await hedera.balance(b.accountId).catch(() => ({ hbar: null, usdc: null })) : { hbar: null, usdc: null };
  const last = b.brainHistory.at(-1);
  return { botId: b.botId, name: b.name, house: b.house, accountId: b.accountId, evmAddress: b.evmAddress, ...bal,
    payAsset: hedera.PAY_ASSET, brainVersion: b.brain.version, daily: dailyView(b), brainRoot: last?.root ?? null, alive: b.alive,
    koStakes: b.alive ? null : b.koStakes ?? null, repairIn: repairIn(b), needsRevive: !b.alive && b.koStakes === "high" };
}

// ---- health / bots ----
app.get("/health", (_req, res) => void res.json({ ok: true, modes, payAsset: hedera.PAY_ASSET, ledgerTopic: hedera.ledgerTopic() }));
app.get("/bots", async (_req, res) => void res.json(await Promise.all(Object.values(state.bots).map(botView))));
app.get("/bots/:id", async (req, res) => {
  const b = bot(req.params.id);
  res.json({ ...(await botView(b)), brain: b.brain, brainHistory: b.brainHistory });
});
// Download + decrypt a saved version from 0G Storage (defaults to the latest).
app.get("/bots/:id/brain", async (req, res) => {
  const b = bot(req.params.id);
  const root = (req.query.root as string) ?? b.brainHistory.at(-1)?.root ?? fail(404, "no saved brain yet");
  res.json({ root, brain: await loadBrain(b.botId, root) });
});
app.post("/bots/:id/revive", (req, res) => {
  const b = bot(req.params.id);
  world.consume(req.body?.approvalId, b.botId, "revive");
  b.alive = true; save();
  res.json({ botId: b.botId, alive: true });
});

// ---- training ----
app.post("/train/:botId/coach", async (req, res) => {
  const b = bot(req.params.botId);
  const t = await heard(await form(req));
  const r = await teach(b, t.text);
  res.json({ text: t.text, ...r, requestId: r.requestId ?? t.requestId, sttRequestId: t.requestId, ...(t.mock ? { mock: true } : {}) });
});
app.post("/train/:botId/coach-text", async (req, res) => {
  const b = bot(req.params.botId);
  const text = String(req.body?.text ?? "").trim().slice(0, 200) || fail(400, "text required");
  res.json({ text, ...(await teach(b, text)) });
});
app.post("/train/:botId/spar", async (req, res) => void res.json(await zg.decide(bot(req.params.botId), (req.body?.state ?? {}) as FightState, "dummy")));
app.post("/train/:botId/save", async (req, res) => void res.json(await saveBrain(bot(req.params.botId))));

// ---- fights ----
app.post("/fights", async (req, res) => {
  const { a = "bot_a", b = "bot_b", stakes = "normal", budgetUsd = 0.5, approvalId } = req.body ?? {};
  bot(a); bot(b);
  if (a === b) fail(400, "a bot cannot fight itself");
  if (!["normal", "high"].includes(stakes)) fail(400, "stakes must be normal or high");
  const cents = Math.round(Number(budgetUsd) * 100);
  if (!(cents >= 5 && cents <= 500)) fail(400, "budgetUsd must be 0.05..5");
  for (const x of [a, b]) {
    const kb = state.bots[x]; repairIfDue(kb);
    if (!kb.alive) fail(409, kb.koStakes === "high" ? `${x} was knocked out in a high-stakes fight; revive needs a World ID approval` : `${x} is repairing (${repairIn(kb)} s)`);
  }
  if (stakes === "high") world.consume(approvalId, owned({ a, b }), "high_stakes"); // marks it used in this same step
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
const opponent = (f: Fight, x: string) => (x === f.a ? f.b : f.a);

function knockout(f: Fight, loser: string) {
  if (f.status !== "live") return;
  f.budgets[loser] = 0; f.status = "over"; f.winner = opponent(f, loser);
  Object.assign(state.bots[loser], { alive: false, koStakes: f.stakes, koAt: Date.now() });
  hedera.ledger({ t: "ko", fight: f.fightId, loser, winner: f.winner });
  save();
}

app.post("/fights/:id/decide", async (req, res) => {
  const f = fightOf(req.params.id), botId = inFight(f, req.body?.botId);
  if (f.status !== "live") return void res.json({ stance: "safe_stop", reason: "fight over" });
  const plan = await zg.decide(bot(botId), (req.body?.state ?? {}) as FightState, opponent(f, botId));
  if (plan.stance === "safe_stop") console.warn(`[decide] ${f.fightId} ${botId} safe_stop: ${plan.reason}`);
  f.decisions.push({ botId, requestId: plan.requestId ?? null, stance: plan.stance, ...(plan.used ? { used: plan.used } : {}), at: new Date().toISOString(), ...(plan.mock ? { mock: true } : {}) });
  save();
  res.json(plan);
});

app.post("/fights/:id/hit", (req, res) => {
  const f = fightOf(req.params.id);
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
    episode: f.episode ?? null });
});

// Replay source: the episode log read back from 0G Storage (not from server memory).
app.get("/fights/:id/episode", async (req, res) => {
  const f = fightOf(req.params.id);
  if (!f.episode) fail(404, "episode not saved yet (it uploads right after /end)");
  res.json({ root: f.episode.root, txHash: f.episode.txHash, episode: await loadEpisode(f.episode.root) });
});
app.post("/fights/:id/end", async (req, res) => {
  const f = fightOf(req.params.id);
  if (f.lessons) return void res.json({ winner: f.winner, lessons: f.lessons });
  if (f.status === "live") {
    f.status = "over";
    f.winner = f.budgets[f.a] === f.budgets[f.b] ? null : f.budgets[f.a] > f.budgets[f.b] ? f.a : f.b;
    hedera.ledger({ t: "end", fight: f.fightId, winner: f.winner, budgets: f.budgets });
  }
  const bots = [bot(f.a), bot(f.b)];
  const out = await zg.lessons(bots, f.hits, f.decisions);
  for (const b of bots) {
    addLessons(b.brain, out.lessons[b.botId] ?? []);
    const opp = opponent(f, b.botId), notes = out.scouting[b.botId] ?? [];
    if (notes.length) b.brain.scouting[opp] = [...(b.brain.scouting[opp] ?? []), ...notes].slice(-10);
  }
  f.lessons = out.lessons; save();
  // 0G uploads take ~15 s each: save in the background, results land in GET /bots/:id brainHistory.
  (async () => {
    // Episode log first (it is what the replay and the judges read), then each brain.
    const ep = { fightId: f.fightId, a: f.a, b: f.b, stakes: f.stakes, winner: f.winner, budgets: f.budgets, startBudget: f.startBudget,
      decisions: f.decisions, hits: f.hits, payments: f.payments, ledgerTopic: f.ledgerTopic, lessons: f.lessons };
    await saveEpisode(ep).then((r) => { f.episode = r; save(); hedera.ledger({ t: "episode", fight: f.fightId, root: r.root }); })
      .catch((e) => console.warn(`[0g-storage] episode ${f.fightId} failed:`, e.message));
    for (const b of bots) await saveBrain(b).catch((e) => console.warn(`[0g-storage] ${b.botId} save failed:`, e.message));
  })();
  res.json({ winner: f.winner, lessons: f.lessons, brainSave: "queued", ...(out.mock ? { mock: true } : {}) });
});

// ---- approvals ----
const approvalView = (a: ReturnType<typeof world.get>) =>
  ({ approvalId: a.approvalId, botId: a.botId, action: a.action, status: a.status, qrUri: a.qrUri, userCode: a.userCode, expiresAt: a.expiresAt, ...(a.mock ? { mock: true } : {}) });
app.post("/approvals", async (req, res) => void res.json(approvalView(await world.create(String(req.body?.botId), req.body?.action))));
app.get("/approvals/:id", (req, res) => void res.json(approvalView(world.get(req.params.id))));
app.get("/approvals/:id/qr.png", async (req, res) => {
  res.type("png").send(await QRCode.toBuffer(world.get(req.params.id).qrUri, { width: 512, margin: 2 }));
});
app.post("/approvals/:id/cancel", (req, res) => void res.json(approvalView(world.cancel(req.params.id))));

// ---- arena (x402 paid route; the middleware above settles payment) ----
app.post("/arena/hit", (_req, res) => void res.json({ ok: true }));

app.use((_req, res) => void res.status(404).json({ error: "not found" }));
app.use((err: any, _req: express.Request, res: express.Response, _next: express.NextFunction) => {
  const status = err instanceof HttpError ? err.status : err?.status ?? 500;
  if (status >= 500) console.error(err);
  res.status(status).json({ error: err?.message ?? String(err) });
});

// ---- start ----
for (const a of Object.values(state.approvals)) if (a.status === "pending") a.status = "cancelled"; // their pollers died with the old process
console.log(`[modes] 0G inference=${modes.zg} 0G storage=${modes.storage} hedera=${modes.hedera} (pay in ${hedera.PAY_ASSET}) world=${modes.world}`);
await hedera.init();
console.log(`[hedera] bots ${Object.values(state.bots).map((b) => `${b.botId}=${b.accountId}`).join(" ")} ledger=${hedera.ledgerTopic()}`);
app.listen(PORT, () => console.log(`[iron-corner] listening on :${PORT}`));
