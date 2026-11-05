// Full loop against a running server. Usage: npx tsx scripts/smoke.ts [baseUrl]
const BASE = process.argv[2] ?? process.env.BASE_URL ?? "http://127.0.0.1:8787";
let failed = 0;
const proofs: string[] = [];

async function call(method: string, path: string, body?: unknown, token?: string, extra: Record<string, string> = {}) {
  const init: RequestInit = { method }, headers: Record<string, string> = { ...extra };
  if (token) headers.authorization = `Bearer ${token}`;
  if (body instanceof FormData) init.body = body;
  else if (body !== undefined) { init.body = JSON.stringify(body); headers["content-type"] = "application/json"; }
  init.headers = headers;
  const r = await fetch(BASE + path, init);
  const ct = r.headers.get("content-type") ?? "";
  return { status: r.status, body: ct.includes("json") ? await r.json() : Buffer.from(await r.arrayBuffer()) } as { status: number; body: any };
}
async function step(name: string, fn: () => Promise<string | void>) {
  const t = Date.now();
  try { const info = await fn(); console.log(`PASS ${name} (${Date.now() - t} ms)${info ? "  " + info : ""}`); }
  catch (e: any) { failed++; console.log(`FAIL ${name}: ${e?.message ?? e}`); }
}
function check(cond: unknown, msg: string): asserts cond { if (!cond) throw new Error(msg); }
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

function wav(seconds = 0.5) { // 16 kHz mono 16-bit silence
  const n = 16000 * seconds * 2, b = Buffer.alloc(44 + n);
  b.write("RIFF", 0); b.writeUInt32LE(36 + n, 4); b.write("WAVEfmt ", 8); b.writeUInt32LE(16, 16); b.writeUInt16LE(1, 20);
  b.writeUInt16LE(1, 22); b.writeUInt32LE(16000, 24); b.writeUInt32LE(32000, 28); b.writeUInt16LE(2, 32); b.writeUInt16LE(16, 34);
  b.write("data", 36); b.writeUInt32LE(n, 40);
  return new Blob([b], { type: "audio/wav" });
}

let health: any, fightId = "", rootA = "", approvalId = "";
const ROUND = `smoke_${Date.now()}`;
let roundStart = 0, energy0 = -1;
// Shadow-sparring demos: the owner's moves on the pad with what happened. hook_r → slip left → cross worked; jab at range whiffed.
const st = (o1: string, self: string[] = [], dist = 1.1) => ({ t: 5, selfBudget: 50, oppBudget: 50, distance: dist, selfGuard: 1, oppGuard: 1, oppLastMoves: ["step_in", o1], selfLastMoves: self, lastHitBy: null, sinceOppAttack: 0.2 });
const DEMOS = [
  ...Array.from({ length: 5 }, () => ({ t: 1, state: st("hook_r"), action: "dodge_l", outcome: "dodged" })),
  ...Array.from({ length: 4 }, () => ({ t: 2, state: st("hook_r", ["dodge_l"]), action: "cross", outcome: "landed" })),
  { t: 3, state: st("hook_r"), action: "block", outcome: "got_hit" },
  ...Array.from({ length: 3 }, () => ({ t: 4, state: st("jab", [], 2.6), action: "jab", outcome: "whiff" })),
];

await step("health", async () => {
  const r = await call("GET", "/health"); check(r.status === 200, `status ${r.status}`); health = r.body;
  return JSON.stringify(r.body.modes) + ` pay=${r.body.payAsset} ledger=${r.body.ledgerTopic}`;
});
await step("start sparring round", async () => {
  const r = await call("POST", "/train/bot_a/demos", { round: ROUND, demos: [] });
  check(r.status === 200 && r.body.energy?.max === 5 && r.body.xp?.level >= 1 && r.body.stats, JSON.stringify(r.body));
  roundStart = Date.now(); energy0 = r.body.energy.left;
  return `energy ${r.body.energy.left}/${r.body.energy.max} level ${r.body.xp.level} stats ${JSON.stringify(r.body.stats)}`;
});
await step("list bots", async () => {
  const r = await call("GET", "/bots");
  check(r.body.length >= 2 && r.body[0].botId === "bot_a" && r.body[1].botId === "bot_b", "expected bot_a and bot_b first");
  return r.body.map((b: any) => `${b.botId}=${b.accountId} hbar=${b.hbar} usdc=${b.usdc} v${b.brainVersion}`).join(", ");
});
await step("coach-text command word", async () => {
  const r = await call("POST", "/train/bot_a/coach-text", { text: "Go body!" });
  check(r.body.command === "body" && r.body.learned === null && r.body.daily?.drills >= 1, JSON.stringify(r.body));
});
const MOVE_LIST = ["jab", "cross", "hook_l", "hook_r", "uppercut", "body", "block", "dodge_l", "dodge_r", "step_in", "step_back"];
const teach = (kind: string | string[], text: string, extra?: (l: any) => boolean) => step(`coach-text teaches a ${kind}`, async () => {
  const r = await call("POST", "/train/bot_a/coach-text", { text });
  const l = r.body.learned;
  check(r.status === 200 && [kind].flat().includes(l?.kind) && l.id && typeof r.body.diff === "string", JSON.stringify(r.body));
  check(l.moves.every((m: string) => MOVE_LIST.includes(m)) && (!extra || extra(l)), JSON.stringify(l));
  return `${r.body.diff}  id=${l.id} req=${r.body.requestId}`;
});
await teach(["phrase", "combo"], "light him up", (l) => l.moves.length > 0);
await teach("counter", "when he throws a left hook, slip right and uppercut", (l) => l.when === "hook_l" && l.moves.length > 0);
await teach("combo", "open the fight with jab, jab, cross", (l) => ["opener", "finisher", "any"].includes(l.use));
await teach("style", "be more aggressive");
await step("daily goal + streak", async () => {
  const r = await call("GET", "/bots");
  const d = r.body[0].daily;
  check(d && d.goal === 3 && d.done === true && d.streak >= 1, JSON.stringify(d));
  return JSON.stringify(d);
});
await step("brain holds counters + combos", async () => {
  const r = await call("GET", "/bots/bot_a");
  check(r.body.brain.counters.length >= 1 && r.body.brain.combos.length >= 1, JSON.stringify(r.body.brain));
});
await step("coach audio (multipart)", async () => {
  const fd = new FormData(); fd.append("audio", wav(), "coach.wav"); fd.append("text", "back off");
  const r = await call("POST", "/train/bot_a/coach", fd);
  check(r.status === 200 && typeof r.body.text === "string", JSON.stringify(r.body));
  return `text="${r.body.text}" command=${r.body.command} req=${r.body.requestId}`;
});
await step("spar uses the taught counter", async () => {
  const r = await call("POST", "/train/bot_a/spar", { state: { t: 9, selfBudget: 50, oppBudget: 50, distance: 1, oppLastMoves: ["hook_l"], selfLastMoves: [], lastHitBy: "hook_l" } });
  check(r.body.stance, JSON.stringify(r.body));
  if (health.modes.zg === "mock") check(/^counter#\d+$/.test(r.body.used) && r.body.usedText, JSON.stringify(r.body));
  else check(r.body.used === undefined || (/^(counter|combo|phrase)#\d+$/.test(r.body.used) && r.body.usedText), JSON.stringify(r.body));
  return `${r.body.stance} ${JSON.stringify(r.body.combo)} used=${r.body.used} (${r.body.usedText}) req=${r.body.requestId}`;
});
await step("spar plan", async () => {
  const r = await call("POST", "/train/bot_a/spar", { state: { t: 3, selfBudget: 50, oppBudget: 50, distance: 1, oppLastMoves: ["jab"], selfLastMoves: [], lastHitBy: null } });
  check(["aggressive", "defensive", "counter", "safe_stop"].includes(r.body.stance), JSON.stringify(r.body));
  return JSON.stringify(r.body);
});
await step("post shadow-sparring demos", async () => {
  const r = await call("POST", "/train/bot_a/demos", { round: ROUND, demos: [...DEMOS, { state: {}, action: "laser", outcome: "landed" }] });
  check(r.status === 200 && r.body.stored === DEMOS.length && r.body.total >= DEMOS.length, JSON.stringify(r.body));
  check(r.body.stats.defense > 0 && r.body.stats.experience > 0, JSON.stringify(r.body.stats));
  return `stored ${r.body.stored} total ${r.body.total} stats ${JSON.stringify(r.body.stats)}`;
});
await step("spar recalls the coach's demos", async () => {
  const r = await call("POST", "/train/bot_a/spar", { state: st("hook_r") });
  check(r.body.stance, JSON.stringify(r.body));
  if (health.modes.zg === "mock") check(r.body.used === "demo" && /coach/i.test(r.body.usedText) && r.body.combo[0] === "dodge_l", JSON.stringify(r.body));
  else check(r.body.stance === "safe_stop" || r.body.used === undefined || r.body.usedText, JSON.stringify(r.body));
  return `${r.body.stance} ${JSON.stringify(r.body.combo)} used=${r.body.used} (${r.body.usedText}) req=${r.body.requestId}`;
});
await step("end round: energy + XP", async () => {
  await sleep(Math.max(0, 15500 - (Date.now() - roundStart))); // a round needs 15 s for XP
  const r = await call("POST", "/train/bot_a/demos", { round: ROUND, roundEnd: true, demos: [] });
  const rd = r.body.round;
  check(rd && rd.moves === DEMOS.length, JSON.stringify(r.body));
  if (energy0 > 0) check(rd.xpGained > 0 && r.body.energy.left === energy0 - 1, JSON.stringify(r.body));
  else check(rd.xpGained === 0 && /energy/.test(rd.reason), JSON.stringify(r.body));
  return `+${rd.xpGained} XP, energy ${r.body.energy.left}/5, level ${r.body.xp.level} (${r.body.xp.into}/${r.body.xp.next}), ${rd.highlight}`;
});
await step("save brain to 0G Storage", async () => {
  const r = await call("POST", "/train/bot_a/save");
  check(r.status === 200 && r.body.root && r.body.level >= 1 && r.body.stats && r.body.demos >= DEMOS.length, JSON.stringify(r.body)); rootA = r.body.root;
  proofs.push(`0G storage bot_a v${r.body.version}: root ${r.body.root} tx ${r.body.txHash}`);
  return `v${r.body.version} root=${r.body.root} tx=${r.body.txHash}`;
});
await step("read brain back (download + decrypt)", async () => {
  const r = await call("GET", `/bots/bot_a/brain?root=${rootA}`);
  check(r.status === 200 && r.body.brain.botId === "bot_a", JSON.stringify(r.body).slice(0, 200));
  check(r.body.brain.demos?.length >= DEMOS.length && r.body.brain.growth?.level >= 1, "brain blob is missing demos/growth");
  return `version ${r.body.brain.version}, ${r.body.brain.phrases.length} phrases, ${r.body.brain.demos.length} demos, level ${r.body.brain.growth.level}`;
});
await step("create fight", async () => {
  const r = await call("POST", "/fights", { a: "bot_a", b: "bot_b", stakes: "normal", budgetUsd: 0.1 });
  check(r.status === 200 && r.body.budgets.bot_a === 10, JSON.stringify(r.body)); fightId = r.body.fightId;
  return `${fightId} ledger=${r.body.ledgerTopic}`;
});
await step("decide x2", async () => {
  const s = { t: 1, selfBudget: 10, oppBudget: 10, distance: 1, selfGuard: 1, oppGuard: 1, oppLastMoves: [], selfLastMoves: [], lastHitBy: null };
  const a = await call("POST", `/fights/${fightId}/decide`, { botId: "bot_a", state: s });
  const b = await call("POST", `/fights/${fightId}/decide`, { botId: "bot_b", state: { ...s, distance: 3 } });
  check(a.body.stance && b.body.stance, "missing stance");
  check(!a.body.used || a.body.usedText, "used without usedText");
  return `a=${a.body.stance}:${a.body.combo ?? ""} used=${a.body.used ?? "-"} b=${b.body.stance}:${b.body.combo ?? ""}`;
});
await step("fights are autonomous (coach → 409)", async () => {
  const fd = new FormData(); fd.append("audio", wav(), "c.wav"); fd.append("botId", "bot_a");
  const c = await call("POST", `/fights/${fightId}/coach`, fd);
  const t = await call("POST", `/fights/${fightId}/coach-text`, { botId: "bot_a", text: "uppercut" });
  check(c.status === 409 && t.status === 409 && /autonomous/.test(t.body.error), `${c.status} ${t.status} ${JSON.stringify(t.body)}`);
});
await step("hits (clean, blocked, dodged, clean)", async () => {
  const hits = [["bot_a", "bot_b", "jab", "clean"], ["bot_b", "bot_a", "cross", "blocked"], ["bot_b", "bot_a", "hook_l", "dodged"], ["bot_b", "bot_a", "cross", "clean"]];
  let last: any;
  for (const [attacker, defender, move, result] of hits) {
    const t = Date.now();
    last = await call("POST", `/fights/${fightId}/hit`, { attacker, defender, move, result });
    check(last.status === 200 && Date.now() - t < 1000, `hit slow or failed: ${JSON.stringify(last.body)}`);
  }
  check(last.body.budgets.bot_b === 9 && last.body.budgets.bot_a === 8, JSON.stringify(last.body));
  return JSON.stringify(last.body.budgets);
});
await step("payments settle", async () => {
  for (let i = 0; i < 60; i++) {
    const r = await call("GET", `/fights/${fightId}`);
    const ps = r.body.payments;
    if (ps.length === 2 && ps.every((p: any) => p.status !== "pending")) {
      check(ps.every((p: any) => p.status === "settled"), JSON.stringify(ps));
      for (const p of ps) proofs.push(`x402 hit ${p.hitNo} ${p.from}->${p.to} ${p.cents}c: ${p.txId} ${p.hashscan ?? ""}`);
      return ps.map((p: any) => `${p.txId}`).join(", ");
    }
    await sleep(2000);
  }
  throw new Error("payments still pending after 120 s");
});
await step("end fight (lessons + brain save queued)", async () => {
  const r = await call("POST", `/fights/${fightId}/end`);
  check(r.status === 200 && r.body.lessons?.bot_a && r.body.xp?.bot_a > 0, JSON.stringify(r.body));
  const f = await call("GET", `/fights/${fightId}`);
  check(f.body.decisions.every((d: any) => d.outcome || d.stance === "safe_stop"), "decisions not settled into fight experience");
  return `winner=${r.body.winner} xp=${JSON.stringify(r.body.xp)} lessons=${JSON.stringify(r.body.lessons)}`;
});
await step("approval create", async () => {
  const r = await call("POST", "/approvals", { botId: "bot_a", action: "high_stakes" });
  check(r.body.approvalId && r.body.qrUri && r.body.userCode, JSON.stringify(r.body)); approvalId = r.body.approvalId;
  return `${approvalId} code=${r.body.userCode} qr=${r.body.qrUri}`;
});
await step("approval qr.png", async () => {
  const r = await call("GET", `/approvals/${approvalId}/qr.png`);
  check(r.status === 200 && Buffer.isBuffer(r.body) && r.body.subarray(1, 4).toString() === "PNG", "not a PNG");
});
await step("high stakes refused before approval", async () => {
  const r = await call("POST", "/fights", { stakes: "high", approvalId });
  check(r.status === 403, `status ${r.status}`);
});
await step("approval poll → approved", async () => {
  for (let i = 0; i < 20; i++) {
    const r = await call("GET", `/approvals/${approvalId}`);
    if (r.body.status !== "pending") { check(r.body.status === "approved", r.body.status); return; }
    await sleep(500);
  }
  throw new Error("still pending (live World mode needs a human)");
});
await step("high stakes fight uses approval once", async () => {
  const r = await call("POST", "/fights", { stakes: "high", approvalId, budgetUsd: 0.05 });
  check(r.status === 200, JSON.stringify(r.body));
  const again = await call("POST", "/fights", { stakes: "high", approvalId });
  check(again.status === 403, "approval reused");
  const s = await call("GET", `/approvals/${approvalId}`);
  check(s.body.status === "used", s.body.status);
});
await step("approval cancel", async () => {
  const c = await call("POST", "/approvals", { botId: "bot_a", action: "revive" });
  const r = await call("POST", `/approvals/${c.body.approvalId}/cancel`);
  check(r.body.status === "cancelled", r.body.status);
});
await step("home shows growth", async () => {
  const r = await call("GET", "/bots");
  const p = r.body[0].progress;
  check(p && p.level >= 1 && p.energy && Object.keys(p.stats).length === 5 && p.demos >= DEMOS.length, JSON.stringify(p));
  return JSON.stringify(p);
});
await step("bad input gives 4xx JSON error", async () => {
  const r = await call("POST", `/fights/${fightId}/hit`, { attacker: "bot_a", defender: "bot_b", move: "laser", result: "clean" });
  check(r.status === 400 && r.body.error, JSON.stringify(r.body));
});

// ---- new player flow (docs/flow-spec.md): two players, each creates, verifies, trains, funds, queues; the server fights ----
type P = { name: string; token: string; userId: string; botId: string };
const players: P[] = [];
const authed = (p: P) => (m: string, path: string, body?: unknown, extra?: Record<string, string>) => call(m, path, body, p.token, extra);
let matchId = "";
for (const [i, name] of ["Smoke Alpha", "Smoke Bravo"].entries()) {
  await step(`create user ${i + 1}`, async () => {
    const r = await call("POST", "/users");
    check(r.status === 200 && r.body.token?.length >= 40 && r.body.userId, JSON.stringify(r.body));
    players.push({ name: `${name} ${Date.now() % 1000}`, token: r.body.token, userId: r.body.userId, botId: "" });
    const m = await call("GET", "/me", undefined, r.body.token);
    check(m.status === 200 && m.body.botId === null && m.body.worldVerified === false && m.body.league === "open", JSON.stringify(m.body));
    return r.body.userId;
  });
}
await step("/me needs a token", async () => {
  const r = await call("GET", "/me"); const b = await call("GET", "/me", undefined, "x".repeat(43));
  check(r.status === 401 && b.status === 401, `${r.status} ${b.status}`);
});
await step("create bot validates name + paint", async () => {
  const p = players[0];
  const bad = [await authed(p)("POST", "/bots", { name: "x", paint: "brass" }), await authed(p)("POST", "/bots", { name: "Robo<script>", paint: "brass" }),
    await authed(p)("POST", "/bots", { name: "Robo", paint: "pink" })];
  check(bad.every((r) => r.status === 400), bad.map((r) => r.status).join());
});
for (const [i, paint] of ["crimson", "cobalt"].entries()) {
  await step(`create bot ${i + 1} (Hedera account + Agentic ID)`, async () => {
    const p = players[i], r = await authed(p)("POST", "/bots", { name: p.name, paint });
    check(r.status === 200 && r.body.botId && r.body.accountId && r.body.paint === paint && r.body.owner === p.userId && r.body.rating === 1000, JSON.stringify(r.body));
    check(r.body.onboarding?.trained === false && r.body.league === "open", JSON.stringify(r.body.onboarding));
    p.botId = r.body.botId;
    let a: any;
    for (let k = 0; k < 40 && !(a = (await call("GET", `/bots/${p.botId}`)).body.agentId)?.tokenId; k++) await sleep(1000);
    check(a?.tokenId && a.tx, `agentId not minted: ${JSON.stringify(a)}`);
    if (a.chainscan) proofs.push(`Agentic ID mint ${p.botId} token ${a.tokenId}: ${a.chainscan}`);
    return `${p.botId} account=${r.body.accountId} evm=${r.body.evmAddress} agentId token=${a.tokenId} tx=${a.tx}`;
  });
}
await step("one bot per user", async () => {
  const r = await authed(players[0])("POST", "/bots", { name: "Second Bot", paint: "jade" });
  check(r.status === 409 && r.body.reason === "one_bot_per_user", JSON.stringify(r.body));
});
await step("owner checks (401 no token, 403 other player)", async () => {
  const [p, q] = players;
  const none = await call("POST", `/train/${p.botId}/coach-text`, { text: "jab" });
  const other = await authed(q)("POST", `/train/${p.botId}/coach-text`, { text: "jab" });
  const queue = await authed(q)("POST", "/queue", { botId: p.botId, stakeCents: 10 });
  check(none.status === 401 && other.status === 403 && queue.status === 403, `${none.status} ${other.status} ${queue.status}`);
});
await step("queue refuses an untrained bot", async () => {
  const r = await authed(players[0])("POST", "/queue", { botId: players[0].botId, stakeCents: 10 });
  check(r.status === 409 && r.body.reason === "needs_training", JSON.stringify(r.body));
});
const signIn = async (p: P, mockSub: string) => {
  const r = await authed(p)("POST", "/auth/world/start", { mockSub });
  check(r.status === 200 && r.body.approvalId && r.body.qrUri && r.body.userCode && r.body.action === "sign_in", JSON.stringify(r.body));
  for (let k = 0; k < 40; k++) {
    const s = await call("GET", `/approvals/${r.body.approvalId}`);
    if (s.body.status !== "pending") return s.body;
    await sleep(500);
  }
  throw new Error("sign-in still pending (live World needs a human)");
};
for (const [i, p] of players.entries()) {
  await step(`World sign-in player ${i + 1}`, async () => {
    const s = await signIn(p, `smoke-human-${p.userId}`);
    check(s.status === "approved", JSON.stringify(s));
    const m = await authed(p)("GET", "/me");
    check(m.body.worldVerified === true && m.body.league === "verified" && m.body.botId === p.botId, JSON.stringify(m.body));
    return `league=${m.body.league}`;
  });
}
await step("one verified fighter per World subject", async () => {
  const u = await call("POST", "/users"), p3: P = { name: "", token: u.body.token, userId: u.body.userId, botId: "" };
  const s = await signIn(p3, `smoke-human-${players[0].userId}`);
  check(s.status === "denied" && s.error === "world_already_used", JSON.stringify(s));
});
await step("first training round + coaching line (both players)", async () => {
  await Promise.all(players.map(async (p) => {
    const round = `onb_${p.botId}`;
    await authed(p)("POST", `/train/${p.botId}/demos`, { round, demos: [] });
    const c = await authed(p)("POST", `/train/${p.botId}/coach-text`, { text: "when he throws a left hook, slip right and uppercut" });
    check(c.status === 200, JSON.stringify(c.body));
    const d = await authed(p)("POST", `/train/${p.botId}/demos`, { round, demos: DEMOS });
    check(d.status === 200 && d.body.stored === DEMOS.length, JSON.stringify(d.body));
    await sleep(15500);
    const e = await authed(p)("POST", `/train/${p.botId}/demos`, { round, roundEnd: true, demos: [] });
    check(e.body.round?.moves === DEMOS.length, JSON.stringify(e.body));
  }));
  const b = await call("GET", `/bots/${players[0].botId}`);
  check(b.body.onboarding.trained === true, JSON.stringify(b.body.onboarding));
});
await step("fund: tops up once a day, idempotent", async () => {
  const p = players[0], key = `smoke-${p.botId}`;
  const r = await authed(p)("POST", `/bots/${p.botId}/fund`, {}, { "idempotency-key": key });
  check(r.status === 200 && r.body.hbar > 0 && r.body.hbar <= 0.5 && r.body.txId, JSON.stringify(r.body));
  const again = await authed(p)("POST", `/bots/${p.botId}/fund`, {}, { "idempotency-key": key });
  check(again.status === 200 && again.body.txId === r.body.txId && again.body.repeat, JSON.stringify(again.body));
  const other = await authed(p)("POST", `/bots/${p.botId}/fund`, {});
  check(other.status === 409 && ["already_funded_today", "balance_high"].includes(other.body.reason), JSON.stringify(other.body));
  const b = await call("GET", `/bots/${p.botId}`);
  check(b.body.onboarding.funded === true, JSON.stringify(b.body.onboarding));
  if (r.body.hashscan) proofs.push(`faucet ${p.botId} ${r.body.hbar} HBAR: ${r.body.hashscan}`);
  return `${r.body.hbar} HBAR tx=${r.body.txId}`;
});
await step("queue two players → matched", async () => {
  const stake = Number(process.env.SMOKE_STAKE ?? 10);
  for (const p of players) {
    const r = await authed(p)("POST", "/queue", { botId: p.botId, stakeCents: stake });
    check(r.status === 200 && ["searching", "matched"].includes(r.body.status), JSON.stringify(r.body));
    const twice = await authed(p)("POST", "/queue", { botId: p.botId, stakeCents: stake });
    check(twice.status === 200, "joining twice must be a no-op");
  }
  for (let k = 0; k < 20; k++) {
    const [a, b] = await Promise.all(players.map((p) => authed(p)("GET", `/queue/${p.botId}`)));
    if (a.body.status === "matched" && b.body.status === "matched") {
      check(a.body.fightId === b.body.fightId && !a.body.house, JSON.stringify([a.body, b.body]));
      matchId = a.body.fightId; return `${matchId} (queue ${a.body.queueSize})`;
    }
    await sleep(1000);
  }
  throw new Error("not matched within 20 s");
});
await step("client can't drive a server fight", async () => {
  const r = await call("POST", `/fights/${matchId}/hit`, { attacker: players[0].botId, defender: players[1].botId, move: "jab", result: "clean" });
  check(r.status === 409, `${r.status}`);
});
let finalEvents: any[] = [], finalSeed = "", finalHash = "";
await step("event stream to the end", async () => {
  let since = 0, ended = false;
  for (let k = 0; k < 400 && !ended; k++) {
    const r = await call("GET", `/fights/${matchId}/events?since=${since}`);
    check(r.status === 200 && Array.isArray(r.body.events) && r.body.seedHash, JSON.stringify(r.body).slice(0, 200));
    for (const e of r.body.events) { check(e.seq === since + 1, `seq gap ${since} → ${e.seq}`); since = e.seq; finalEvents.push(e); }
    ended = r.body.status === "over" && finalEvents.some((e) => e.type === "end");
    if (ended) { finalSeed = r.body.seed; finalHash = r.body.seedHash; }
    else check(!r.body.seed, "seed revealed before the end");
    await sleep(500);
  }
  check(ended, "fight did not end");
  const types = new Set(finalEvents.map((e) => e.type)), end = finalEvents.find((e) => e.type === "end");
  check(finalEvents[0].type === "start" && types.has("plan") && types.has("move"), [...types].join());
  const { createHash } = await import("node:crypto");
  check(createHash("sha256").update(Buffer.from(finalSeed, "hex")).digest("hex") === finalHash, "sha256(seed) != seedHash");
  return `${finalEvents.length} events, ${finalEvents.filter((e) => e.type === "hit").length} hits, end=${JSON.stringify(end)}`;
});
await step("replay from seed + plans gives the same events", async () => {
  process.env.STATE_FILE = "smoke-replay.json"; // the engine module loads state; keep it away from the real file
  const { replay } = await import("../src/engine.ts");
  const strip = ({ seq, ...e }: any) => e;
  const core = finalEvents.filter((e) => ["plan", "move", "hit", "ko"].includes(e.type) && !e.reason).map(strip);
  const start = finalEvents[0];
  const again = replay(finalSeed, start.a, start.b, start.stakeCents, core.filter((e: any) => e.type === "plan"));
  check(JSON.stringify(again) === JSON.stringify(core), "replay differs");
  (await import("node:fs")).rmSync(new URL("../data/smoke-replay.json", import.meta.url), { force: true });
  return `${core.length} events replayed`;
});
await step("ratings, record, payments after the fight", async () => {
  await sleep(1500);
  const f = await call("GET", `/fights/${matchId}`);
  check(f.body.status === "over" && f.body.seed && f.body.ratingDelta, JSON.stringify(f.body).slice(0, 300));
  const bots = await Promise.all(players.map((p) => call("GET", `/bots/${p.botId}`)));
  const [ra, rb] = bots.map((b) => b.body.rating), d = f.body.ratingDelta;
  check(ra - 1000 === d[players[0].botId] && rb - 1000 === d[players[1].botId] && ra + rb === 2000, `${ra} ${rb} ${JSON.stringify(d)}`);
  if (f.body.winner) check(bots.some((b) => b.body.record.w === 1) && bots.some((b) => b.body.record.l === 1), "record not updated");
  for (let k = 0; k < 90; k++) {
    const ps = (await call("GET", `/fights/${matchId}`)).body.payments;
    if (ps.every((p: any) => p.status !== "pending")) {
      for (const p of ps.slice(0, 6)) proofs.push(`x402 ${matchId} hit ${p.hitNo} ${p.from}->${p.to} ${p.cents}c ${p.status}: ${p.txId} ${p.hashscan ?? ""}`);
      check(ps.every((p: any) => p.status === "settled"), JSON.stringify(ps.filter((p: any) => p.status !== "settled")));
      break;
    }
    await sleep(2000);
  }
  proofs.push(`fight ${matchId} seedHash ${f.body.seedHash} seed ${f.body.seed} ledger ${f.body.ledgerTopic} winner ${f.body.winner} (${f.body.endReason})`);
  return `winner=${f.body.winner} reason=${f.body.endReason} ratings ${ra}/${rb} payments ${f.body.payments.length}`;
});
await step("camp needs 40 demos, then runs (mock unless CAMP_MODE=live)", async () => {
  const p = players[0];
  const no = await authed(p)("POST", `/bots/${p.botId}/camp`);
  check(no.status === 409 && no.body.reason === "not_enough_demos", JSON.stringify(no.body));
  if (process.env.SMOKE_CAMP === "skip") return "skipped";
  const more = Array.from({ length: 45 }, (_, k) => ({ t: k, state: st(["hook_l", "hook_r", "jab", "body"][k % 4], [], 1 + (k % 3) * 0.5), action: ["dodge_r", "dodge_l", "block", "block"][k % 4], outcome: k % 5 ? "dodged" : "whiff" }));
  await authed(p)("POST", `/train/${p.botId}/demos`, { demos: more });
  const r = await authed(p)("POST", `/bots/${p.botId}/camp`);
  check(r.status === 200 && r.body.status, JSON.stringify(r.body));
  const again = await authed(p)("POST", `/bots/${p.botId}/camp`);
  check(again.status === 409, JSON.stringify(again.body));
  for (let k = 0; k < 30; k++) {
    const s = await call("GET", `/bots/${p.botId}/camp`);
    if (s.body.status === "done") { check(s.body.version === 1 && s.body.adapterRoot && s.body.taskId, JSON.stringify(s.body)); return JSON.stringify(s.body); }
    check(s.body.status !== "failed", JSON.stringify(s.body));
    await sleep(1000);
  }
  throw new Error("mock camp didn't finish");
});
await step("leave queue, then house fallback after 25 s", async () => {
  if (process.env.SMOKE_HOUSE === "skip") return "skipped";
  const winner = (await call("GET", `/fights/${matchId}`)).body.winner;
  const p = players.find((x) => x.botId === winner) ?? players[0], q = authed(p);
  for (let k = 0; k < 40 && !(await call("GET", `/bots/${p.botId}`)).body.alive; k++) await sleep(1000); // a KO repairs in 30 s
  const j = await q("POST", "/queue", { botId: p.botId, stakeCents: 10 });
  check(j.status === 200 && j.body.status === "searching", JSON.stringify(j.body));
  const l = await q("DELETE", `/queue/${p.botId}`);
  check(l.body.status !== "searching", JSON.stringify(l.body));
  await q("POST", "/queue", { botId: p.botId, stakeCents: 10 });
  for (let k = 0; k < 40; k++) {
    const r = await q("GET", `/queue/${p.botId}`);
    if (r.body.status === "matched" && r.body.house) {
      check(k >= 20, `house came too early (${k} s)`);
      for (let w = 0; w < 200; w++) {
        const f = await call("GET", `/fights/${r.body.fightId}`);
        if (f.body.status === "over") { check(Object.values(f.body.ratingDelta).every((x) => x === 0), "house fights are unrated"); return `${r.body.fightId} vs ${f.body.b} winner=${f.body.winner}`; }
        await sleep(1000);
      }
      throw new Error("house fight did not end");
    }
    await sleep(1000);
  }
  throw new Error("no house match after 40 s");
});

console.log("\n" + (proofs.length ? proofs.join("\n") : "") + `\n${failed ? `${failed} step(s) FAILED` : "ALL PASS"}`);
process.exit(failed ? 1 : 0);
