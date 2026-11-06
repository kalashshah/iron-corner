// Server-authoritative fights (docs/flow-spec.md). The server decides, resolves and pays; the app only plays events.
//
// Fairness: a random 32-byte seed is drawn at creation and only sha256(seed) is published; the seed is revealed when
// the fight ends. Every random draw comes from rng(seed), and every plan is in the event log, so anyone can replay
// the fight: replay(seed, a, b, budget, planEvents) gives the same events.
//
// Rules, one exchange every ~1.5 s of fight time, up to 60 exchanges (90 s):
//  - both bots decide in parallel (0G); each plan becomes up to 3 moves (safe_stop → block). A "when_close" plan out
//    of reach opens with step_in.
//  - the moves play in 3 slots; slot i pairs A's i-th move with B's. Steps first: step_in −0.5, step_back +0.5
//    (distance 0.8..3, start 2.0). Then each attack: out of reach (> 1.4) → whiff (no hit event);
//    defender blocks → blocked (guard −0.15); defender slips → dodged with p 0.75, else clean;
//    otherwise clean with p = 0.45 + 0.4 × (1 − guard) + 0.1 if distance ≤ 1, else blocked (guard −0.05).
//  - clean hit = the cents table (capped at what the defender has left); a budget at 0 is a KO. Guard regains 0.1 per
//    exchange. At 90 s the bigger budget wins (draw if equal).
import crypto from "node:crypto";
import { state, save, id, type Bot, type Fight, type FightEvent, type Payment } from "./state.ts";
import { DAMAGE, addLessons, type Plan, type FightState } from "./game.ts";
import * as zg from "./zg.ts";
import * as hedera from "./hedera.ts";
import * as demos from "./demos.ts";
import { saveBrain, saveEpisode } from "./storage.ts";
import { elo } from "./match.ts";

export const REACH = 1.4, START_DIST = 2.0, EXCHANGE_S = 1.5, TIME_LIMIT_S = 90;
const EXCHANGE_MS = Number(process.env.EXCHANGE_MS ?? 1500);
const r2 = (x: number) => Math.round(x * 100) / 100;
const clamp = (x: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, x));

// ---- deterministic core ----
export const seedHash = (seedHex: string) => crypto.createHash("sha256").update(Buffer.from(seedHex, "hex")).digest("hex");
export function makeRng(seedHex: string) {
  let n = 0; // draw k = first 4 bytes of sha256(seed || k)
  return () => crypto.createHash("sha256").update(Buffer.from(seedHex, "hex")).update(String(n++)).digest().readUInt32BE(0) / 2 ** 32;
}
export type Sim = {
  a: string; b: string; t: number; dist: number; budgets: Record<string, number>; guard: Record<string, number>;
  moves: Record<string, string[]>; lastHitBy: Record<string, string | null>; lastAtk: Record<string, number>;
  over: { winner: string | null; reason: string } | null;
};
export function newSim(a: string, b: string, budgets: Record<string, number>): Sim {
  return { a, b, t: 0, dist: START_DIST, budgets, guard: { [a]: 1, [b]: 1 }, moves: { [a]: [], [b]: [] },
    lastHitBy: { [a]: null, [b]: null }, lastAtk: { [a]: -99, [b]: -99 }, over: null };
}
const other = (s: Sim, x: string) => (x === s.a ? s.b : s.a);
export function stateFor(s: Sim, me: string): FightState {
  const o = other(s, me);
  return { t: s.t, selfBudget: s.budgets[me], oppBudget: s.budgets[o], distance: r2(s.dist), selfGuard: r2(s.guard[me]), oppGuard: r2(s.guard[o]),
    oppLastMoves: s.moves[o].slice(-3), selfLastMoves: s.moves[me].slice(-3), lastHitBy: s.lastHitBy[me], sinceOppAttack: r2(Math.min(5, s.t - s.lastAtk[o])) };
}
export function movesOf(p: Plan, dist: number): string[] {
  if (p.stance === "safe_stop" || !p.combo?.length) return ["block"];
  const m = p.trigger === "when_close" && dist > REACH && DAMAGE[p.combo[0]] ? ["step_in", ...p.combo] : p.combo;
  return m.slice(0, 3);
}
type Ev = Omit<FightEvent, "seq">;
// One exchange. Mutates the sim, returns its events. Uses only rng for chance, so it replays exactly.
export function exchange(s: Sim, plans: Record<string, Plan>, rng: () => number): Ev[] {
  const evs: Ev[] = [], t0 = s.t, ms: Record<string, string[]> = {};
  for (const x of [s.a, s.b]) {
    const p = plans[x]; ms[x] = movesOf(p, s.dist);
    evs.push({ t: t0, type: "plan", botId: x, stance: p.stance, combo: p.combo ?? [], trigger: p.trigger ?? null, moves: ms[x], say: p.say ?? "",
      used: p.used ?? null, usedText: p.usedText ?? null, requestId: p.requestId ?? null });
  }
  for (let i = 0; i < 3 && !s.over; i++) {
    const t = r2(t0 + 0.3 + 0.4 * i);
    for (const x of [s.a, s.b]) {
      const m = ms[x][i];
      if (!m) continue;
      evs.push({ t, type: "move", botId: x, move: m });
      s.moves[x] = [...s.moves[x], m].slice(-5);
      if (m === "step_in") s.dist = clamp(s.dist - 0.5, 0.8, 3);
      if (m === "step_back") s.dist = clamp(s.dist + 0.5, 0.8, 3);
    }
    for (const x of [s.a, s.b]) {
      const m = ms[x][i], y = other(s, x), dm = ms[y][i];
      if (!m || !DAMAGE[m]) continue;
      s.lastAtk[x] = t;
      if (s.dist > REACH) continue;
      let result: string;
      if (dm === "block") { result = "blocked"; s.guard[y] = clamp(s.guard[y] - 0.15, 0, 1); }
      else if (dm === "dodge_l" || dm === "dodge_r") result = rng() < 0.75 ? "dodged" : "clean";
      else if (rng() < 0.45 + 0.4 * (1 - s.guard[y]) + (s.dist <= 1 ? 0.1 : 0)) result = "clean";
      else { result = "blocked"; s.guard[y] = clamp(s.guard[y] - 0.05, 0, 1); }
      const cents = result === "clean" ? Math.min(DAMAGE[m], s.budgets[y]) : 0;
      s.budgets[y] -= cents;
      if (result === "clean") s.lastHitBy[y] = m;
      evs.push({ t, type: "hit", attacker: x, defender: y, move: m, result, cents });
      if (s.budgets[y] <= 0) { evs.push({ t, type: "ko", loser: y }); s.over = { winner: x, reason: "ko" }; break; }
    }
  }
  for (const x of [s.a, s.b]) s.guard[x] = r2(clamp(s.guard[x] + 0.1, 0, 1));
  s.t = r2(t0 + EXCHANGE_S);
  if (!s.over && s.t >= TIME_LIMIT_S) {
    const [ba, bb] = [s.budgets[s.a], s.budgets[s.b]];
    s.over = { winner: ba === bb ? null : ba > bb ? s.a : s.b, reason: "time" };
  }
  return evs;
}
// Rebuild a fight's events from its seed and its plan events (what an auditor does with the episode log).
export function replay(seed: string, a: string, b: string, budget: number, planEvents: Ev[]): Ev[] {
  const s = newSim(a, b, { [a]: budget, [b]: budget }), rng = makeRng(seed), out: Ev[] = [];
  for (let i = 0; i + 1 < planEvents.length && !s.over; i += 2) {
    const plans: Record<string, Plan> = {};
    for (const e of [planEvents[i], planEvents[i + 1]]) plans[e.botId as string] = { stance: e.stance as string, combo: e.combo as string[], trigger: (e.trigger as string) ?? undefined, say: e.say as string,
      ...(e.used ? { used: e.used as string, usedText: e.usedText as string } : {}), requestId: e.requestId as string | null };
    out.push(...exchange(s, plans, rng));
  }
  return out;
}

// ---- KO / repair (shared with the client-driven demo fights) ----
const REPAIR_MS = Number(process.env.REPAIR_MS ?? 30_000);
export const opponent = (f: Fight, x: string) => (x === f.a ? f.b : f.a);
export function repairIfDue(b: Bot) {
  if (!b.alive && (b.house || (b.koStakes !== "high" && Date.now() - (b.koAt ?? 0) >= REPAIR_MS))) { b.alive = true; save(); }
}
export const repairIn = (b: Bot) => b.alive || b.koStakes === "high" ? null : Math.max(0, Math.ceil((REPAIR_MS - (Date.now() - (b.koAt ?? 0))) / 1000));
export function knockout(f: Fight, loser: string) {
  if (f.status !== "live") return;
  f.budgets[loser] = 0; f.status = "over"; f.winner = opponent(f, loser);
  Object.assign(state.bots[loser], { alive: false, koStakes: f.stakes, koAt: Date.now() });
  hedera.ledger({ t: "ko", fight: f.fightId, loser, winner: f.winner });
  save();
}

// ---- end of fight: lessons, XP, episode + brain saves (also POST /fights/:id/end for demo fights) ----
const ending = new Map<string, Promise<any>>();
export function finishFight(f: Fight) {
  if (!ending.has(f.fightId)) ending.set(f.fightId, finish(f));
  return ending.get(f.fightId)!;
}
async function finish(f: Fight) {
  if (f.lessons) return { winner: f.winner, lessons: f.lessons };
  if (f.status === "live") {
    f.status = "over";
    f.winner = f.budgets[f.a] === f.budgets[f.b] ? null : f.budgets[f.a] > f.budgets[f.b] ? f.a : f.b;
    hedera.ledger({ t: "end", fight: f.fightId, winner: f.winner, budgets: f.budgets });
  }
  const bots = [state.bots[f.a], state.bots[f.b]];
  const xp = demos.onFightEnd(f);
  const out = await zg.lessons(bots, f.hits, f.decisions);
  for (const b of bots) {
    addLessons(b.brain, out.lessons[b.botId] ?? []);
    const opp = opponent(f, b.botId), notes = out.scouting[b.botId] ?? [];
    if (notes.length) b.brain.scouting[opp] = [...(b.brain.scouting[opp] ?? []), ...notes].slice(-10);
  }
  f.lessons = out.lessons; save();
  // 0G uploads take ~15 s each: save in the background, results land in GET /bots/:id brainHistory.
  (async () => {
    const ep = { fightId: f.fightId, a: f.a, b: f.b, stakes: f.stakes, winner: f.winner, budgets: f.budgets, startBudget: f.startBudget,
      decisions: f.decisions, hits: f.hits, payments: f.payments, ledgerTopic: f.ledgerTopic, lessons: f.lessons,
      ...(f.engine ? { seed: f.seed, seedHash: f.seedHash, events: f.events, endReason: f.endReason, ratingDelta: f.ratingDelta } : {}) };
    await saveEpisode(ep).then((r) => { f.episode = r; save(); hedera.ledger({ t: "episode", fight: f.fightId, root: r.root }); })
      .catch((e) => console.warn(`[0g-storage] episode ${f.fightId} failed:`, e.message));
    for (const b of bots) if (!b.payAs) await saveBrain(b).catch((e) => console.warn(`[0g-storage] ${b.botId} save failed:`, e.message));
  })();
  return { winner: f.winner, lessons: f.lessons, xp, brainSave: "queued", ...("mock" in out ? { mock: true } : {}) };
}

// ---- house sparring bots (bot_b plus two extras that pay from bot_b's Hedera account) ----
const HOUSE = [
  { botId: "house_2", name: "Iron Ma", xp: 300, style: { aggression: 0.5, guard: 0.6 },
    counters: [{ when: "hook_l", moves: ["block", "cross"], said: "house" }], combos: [{ use: "opener" as const, name: "jab jab cross", moves: ["jab", "jab", "cross"], said: "house" }] },
  { botId: "house_3", name: "Old Anvil", xp: 1500, style: { aggression: 0.7, guard: 0.5 },
    counters: [{ when: "hook_l", moves: ["dodge_r", "uppercut"], said: "house" }, { when: "hook_r", moves: ["dodge_l", "hook_l"], said: "house" }, { when: "jab", moves: ["block", "cross"], said: "house" }],
    combos: [{ use: "opener" as const, name: "jab cross hook", moves: ["jab", "cross", "hook_l"], said: "house" }, { use: "finisher" as const, name: "body body hook", moves: ["body", "body", "hook_r"], said: "house" }] },
];
export function houseFor(level: number): Bot {
  const bb = state.bots.bot_b;
  for (const h of HOUSE) {
    const x = (state.bots[h.botId] ??= { botId: h.botId, name: h.name, house: true, accountId: null, evmAddress: null, alive: true, ownerSub: null, payAs: "bot_b",
      brain: { botId: h.botId, version: 0, phrases: [], counters: h.counters, combos: h.combos, lessons: [], scouting: {}, style: h.style }, brainHistory: [],
      progress: { xp: h.xp, day: new Date().toISOString().slice(0, 10), rounds: 0, fights: 0 } });
    x.accountId = bb.accountId; x.evmAddress = bb.evmAddress;
  }
  save();
  const all = [bb, ...HOUSE.map((h) => state.bots[h.botId])];
  return all.sort((x, y) => Math.abs(demos.progressView(x).level - level) - Math.abs(demos.progressView(y).level - level))[0];
}

// ---- driver ----
const sims = new Map<string, Sim>();
const emit = (f: Fight, e: Ev) => { const ev = { seq: f.events!.length + 1, ...e } as FightEvent; f.events!.push(ev); return ev; };
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

export function startFight(a: Bot, b: Bot, stakeCents: number, house: boolean): Fight {
  const seed = crypto.randomBytes(32).toString("hex");
  const f: Fight = { fightId: id("f"), a: a.botId, b: b.botId, stakes: "normal", status: "live", budgets: { [a.botId]: stakeCents, [b.botId]: stakeCents },
    startBudget: stakeCents, winner: null, payments: [], decisions: [], hits: [], ledgerTopic: hedera.ledgerTopic(), createdAt: new Date().toISOString(),
    engine: true, house, stakeCents, seed, seedHash: seedHash(seed), events: [] };
  state.fights[f.fightId] = f;
  for (const x of [a, b]) x.lastMatch = { fightId: f.fightId, at: Date.now() };
  save();
  hedera.ledger({ t: "start", fight: f.fightId, a: f.a, b: f.b, stakes: "normal", budget: stakeCents, seedHash: f.seedHash, house });
  void run(f).catch((e) => console.error(`[engine] ${f.fightId} crashed:`, e));
  return f;
}

async function run(f: Fight) {
  const s = newSim(f.a, f.b, f.budgets), rng = makeRng(f.seed!);
  sims.set(f.fightId, s);
  emit(f, { t: 0, type: "start", a: f.a, b: f.b, budgets: { ...f.budgets }, seedHash: f.seedHash, stakeCents: f.stakeCents, house: !!f.house });
  while (!s.over) {
    const t0 = Date.now();
    const plans = await Promise.all([f.a, f.b].map(async (x) => {
      const st = stateFor(s, x), b = state.bots[x], p = await zg.decide(b, st, other(s, x));
      if (p.stance === "safe_stop") console.warn(`[engine] ${f.fightId} ${x} safe_stop: ${p.reason}`);
      f.decisions.push({ botId: x, requestId: p.requestId ?? null, stance: p.stance, ...(p.used ? { used: p.used } : {}), ...(p.camp ? { camp: p.camp } : {}), at: new Date().toISOString(), ...(p.mock ? { mock: true } : {}) });
      demos.onDecide(b, f, st, p);
      return p;
    }));
    if (s.over) break; // a failed payment knocked someone out while we were deciding
    for (const e of exchange(s, { [f.a]: plans[0], [f.b]: plans[1] }, rng)) {
      emit(f, e);
      if (e.type !== "hit") continue;
      const hitNo = f.hits.length + 1, attacker = e.attacker as string, defender = e.defender as string, cents = e.cents as number;
      f.hits.push({ hitNo, attacker, defender, move: e.move as string, result: e.result as string, cents, at: new Date().toISOString() });
      hedera.ledger({ t: "hit", fight: f.fightId, hit: hitNo, attacker, defender, move: e.move, result: e.result, cents });
      if (cents > 0) {
        const p: Payment = { hitNo, from: defender, to: attacker, cents, status: "pending", txId: null, hashscan: null };
        f.payments.push(p);
        hedera.queuePayment(f, p, () => paymentKo(f, defender),
          (q) => { emit(f, { t: s.t, type: "pay", hitNo: q.hitNo, from: q.from, to: q.to, cents: q.cents, status: q.status, txId: q.txId, hashscan: q.hashscan }); save(); });
      }
    }
    save();
    if (!s.over) await sleep(Math.max(0, EXCHANGE_MS - (Date.now() - t0)));
  }
  await end(f, s);
}

// "A bot whose payment fails is knocked out."
function paymentKo(f: Fight, loser: string) {
  const s = sims.get(f.fightId);
  if (!s || s.over) return;
  s.over = { winner: opponent(f, loser), reason: "payment_failed" };
  emit(f, { t: s.t, type: "ko", loser, reason: "payment_failed" });
}

async function end(f: Fight, s: Sim) {
  const { winner, reason } = s.over!;
  if (reason === "time") { f.status = "over"; f.winner = winner; hedera.ledger({ t: "end", fight: f.fightId, winner, budgets: f.budgets }); save(); }
  else knockout(f, opponent(f, winner!));
  f.endReason = reason;
  const A = state.bots[f.a], B = state.bots[f.b], delta: Record<string, number> = { [f.a]: 0, [f.b]: 0 };
  if (!f.house) {
    const d = elo(A.rating ?? 1000, B.rating ?? 1000, winner === f.a ? 1 : winner === f.b ? 0 : 0.5);
    A.rating = (A.rating ?? 1000) + d; B.rating = (B.rating ?? 1000) - d;
    delta[f.a] = d; delta[f.b] = -d;
  }
  for (const x of [A, B]) {
    x.lastOpp = opponent(f, x.botId);
    if (!x.house && winner) { x.record ??= { w: 0, l: 0 }; winner === x.botId ? x.record.w++ : x.record.l++; }
  }
  f.ratingDelta = delta;
  emit(f, { t: s.t, type: "end", winner, reason, ratingDelta: delta, budgets: { ...f.budgets } });
  sims.delete(f.fightId); save();
  await finishFight(f);
}

// Fights that were live when the process died can't resume (their decide loop is gone): close them as a draw-by-budget.
export function closeOrphans() {
  for (const f of Object.values(state.fights)) {
    if (!f.engine || f.status !== "live" || sims.has(f.fightId)) continue;
    f.status = "over"; f.endReason = "server_restart";
    f.winner = f.budgets[f.a] === f.budgets[f.b] ? null : f.budgets[f.a] > f.budgets[f.b] ? f.a : f.b;
    emit(f, { t: f.events!.at(-1)?.t ?? 0, type: "end", winner: f.winner, reason: "server_restart", ratingDelta: { [f.a]: 0, [f.b]: 0 }, budgets: { ...f.budgets } });
  }
  save();
}

// Self-check: `npx tsx src/engine.ts` (pure core only; no network)
if (process.argv[1]?.endsWith("engine.ts")) {
  const assert = (await import("node:assert")).strict;
  const MV = ["jab", "cross", "hook_l", "hook_r", "uppercut", "body", "block", "dodge_l", "dodge_r", "step_in", "step_back"];
  for (let run = 0; run < 300; run++) {
    const seed = crypto.randomBytes(32).toString("hex"), pick = makeRng(seedHash(seed)); // plans from another stream
    const plan = (): Plan => pick() < 0.05 ? { stance: "safe_stop" }
      : { stance: "aggressive", combo: Array.from({ length: 1 + Math.floor(pick() * 4) }, () => MV[Math.floor(pick() * MV.length)]), trigger: pick() < 0.5 ? "when_close" : "now" };
    const budget = [10, 25, 50][run % 3], s = newSim("a", "b", { a: budget, b: budget }), rng = makeRng(seed), evs: Ev[] = [];
    let n = 0;
    while (!s.over) {
      evs.push(...exchange(s, { a: plan(), b: plan() }, rng)); n++;
      assert.ok(s.budgets.a >= 0 && s.budgets.b >= 0, "budget went negative");
      assert.ok(n <= TIME_LIMIT_S / EXCHANGE_S, "fight did not terminate");
    }
    const paid = (x: string) => evs.filter((e) => e.type === "hit" && e.defender === x).reduce((t, e) => t + (e.cents as number), 0);
    assert.equal(paid("a"), budget - s.budgets.a); assert.equal(paid("b"), budget - s.budgets.b);
    if (s.over.reason === "ko") assert.equal(s.budgets[other(s, s.over.winner!)], 0);
    assert.deepEqual(replay(seed, "a", "b", budget, evs.filter((e) => e.type === "plan")), evs, "replay differs");
  }
  // same plans, different seed → (almost surely) a different fight; the seed matters
  const plansFor = () => ({ a: { stance: "aggressive", combo: ["step_in", "jab", "cross"] }, b: { stance: "aggressive", combo: ["hook_l", "dodge_r"] } });
  const fight = (seed: string) => { const s = newSim("a", "b", { a: 50, b: 50 }), r = makeRng(seed), e: Ev[] = []; while (!s.over) e.push(...exchange(s, plansFor(), r)); return JSON.stringify(e); };
  assert.notEqual(fight("00".repeat(32)), fight("11".repeat(32)));
  assert.equal(fight("22".repeat(32)), fight("22".repeat(32)));
  assert.deepEqual(movesOf({ stance: "safe_stop" }, 2), ["block"]);
  assert.deepEqual(movesOf({ stance: "aggressive", combo: ["jab", "cross", "hook_l"], trigger: "when_close" }, 2), ["step_in", "jab", "cross"]);
  console.log("engine.ts self-check ok (300 fights terminate, budgets ≥ 0, replay identical)");
}
