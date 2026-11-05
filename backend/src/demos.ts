// Shadow sparring: every move the owner makes in training is a demonstration (state → action → outcome).
// The bot recalls the nearest demos in a fight and 0G picks its plan with them in the prompt. Also: growth stats,
// XP/levels, daily energy, rust. Nothing here touches damage or speed: every bot has the same body.
import { state, save, type Bot, type Fight, type Demo } from "./state.ts";
import { DAMAGE, MOVES, addDrill, type FightState, type Plan } from "./game.ts";

export const DEMO_CAP = 2000, ENERGY_MAX = 5, BATCH_MAX = 60;
export const OUTCOMES = ["landed", "blocked", "dodged", "whiff", "got_hit"];
const ATTACKS = Object.keys(DAMAGE);
const isAttack = (m: string) => !!DAMAGE[m];
const DAY = 864e5;

const SAY: Record<string, string> = { jab: "jab", cross: "cross", hook_l: "left hook", hook_r: "right hook", uppercut: "uppercut", body: "body shot",
  block: "block", dodge_l: "slip left", dodge_r: "slip right", step_in: "step in", step_back: "step back" };
export const say = (m: string) => SAY[m] ?? m;

// ---- features ----
const num = (x: unknown, d: number) => (typeof x === "number" && isFinite(x) ? x : d);
const clamp = (x: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, x));
const r2 = (x: number) => Math.round(x * 100) / 100;
// [distance/3, lead (self share of budgets), self guard, opp guard, time since opp attack/5]
export function feats(s: FightState): number[] {
  const sb = num(s.selfBudget, 50), ob = num(s.oppBudget, 50);
  return [clamp(num(s.distance, 1.2) / 3, 0, 1.5), sb + ob > 0 ? sb / (sb + ob) : 0.5, clamp(num(s.selfGuard, 1), 0, 1),
    clamp(num(s.oppGuard, 1), 0, 1), clamp(num(s.sinceOppAttack, 5), 0, 5) / 5].map(r2);
}
const W = [2, 0.5, 0.5, 0.5, 1.5]; // distance and timing matter most
const mv = (x: unknown) => (typeof x === "string" && (MOVES as readonly string[]).includes(x) ? x : null);
const key = (s: FightState) => ({ f: feats(s), o1: mv(s.oppLastMoves?.at(-1)), o2: mv(s.oppLastMoves?.at(-2)), m1: mv(s.selfLastMoves?.at(-1)) });
type Key = ReturnType<typeof key>;
function gap(q: Key, d: Demo) {
  let s = 0;
  for (let i = 0; i < W.length; i++) s += W[i] * (q.f[i] - (d.f[i] ?? 0)) ** 2;
  return Math.sqrt(s) + (q.o1 === d.o1 ? 0 : 0.6) + (q.o2 === d.o2 ? 0 : 0.1) + (q.m1 === d.m1 ? 0 : 0.15);
}

// ---- outcomes ----
// worked: an attack landed, a block blocked, a slip/step made the hit miss.
export const worked = (a: string, out: string) => (isAttack(a) ? out === "landed" : out === "blocked" || out === "dodged");
const score = (a: string, out: string) => (worked(a, out) ? 1 : out === "got_hit" ? -1 : isAttack(a) && out !== "whiff" ? -0.5 : 0);

// Rust: a move not practised in training for > 2 days slowly loses weight (applied on read, floor 50%).
function rustOf(demos: Demo[], now = Date.now()) {
  const last: Record<string, number> = {};
  for (const d of demos) if (d.src === "train" && d.at > (last[d.a] ?? 0)) last[d.a] = d.at;
  const out: Record<string, number> = {};
  for (const m of MOVES) { const days = last[m] ? (now - last[m]) / DAY : 0; out[m] = days <= 2 ? 1 : Math.max(0.5, 0.95 ** (days - 2)); }
  return out;
}

// ---- library ----
function add(b: Bot, d: Demo) {
  const lib = (b.demos ??= []);
  const q = { f: d.f, o1: d.o1, o2: d.o2, m1: d.m1 }, sc = score(d.a, d.out);
  // Reinforce: the same move in a near-identical moment gets stronger when it works again, weaker when it fails.
  if (sc) for (const x of lib) if (x.a === d.a && gap(q, x) < 0.5) x.w = r2(clamp(x.w + (sc > 0 ? 0.1 : -0.1), 0.1, 2));
  lib.push(d);
  while (lib.length > DEMO_CAP) { // ponytail: O(n) min scan per eviction, fine at 2k
    let mi = 0;
    for (let i = 1; i < lib.length; i++) if (lib[i].w < lib[mi].w) mi = i;
    lib.splice(mi, 1);
  }
}
const baseW = (a: string, out: string) => (score(a, out) > 0 ? 1 : score(a, out) === 0 ? 0.7 : 0.5);

// ---- recall: the k nearest demos, summarised for the decision prompt ----
export type Recall = { block: string; top: { moves: string[]; worked: number; n: number; text: string; coach: boolean } | null };
export function recall(b: Bot, s: FightState, k = 6): Recall | null {
  const lib = b.demos ?? [];
  if (!lib.length) return null;
  const q = key(s), rust = rustOf(lib);
  const near = lib.map((d) => [gap(q, d), d] as const).filter(([g]) => g < 1.2).sort((x, y) => x[0] - y[0]).slice(0, k);
  if (!near.length) return null;
  const by = new Map<string, { a: string; n: number; ok: number; hit: number; s: number; train: number }>();
  for (const [g, d] of near) {
    const e = by.get(d.a) ?? { a: d.a, n: 0, ok: 0, hit: 0, s: 0, train: 0 };
    e.n++; if (d.src === "train") e.train++; if (worked(d.a, d.out)) e.ok++; if (d.out === "got_hit") e.hit++;
    e.s += d.w * rust[d.a] * score(d.a, d.out) / (1 + g);
    by.set(d.a, e);
  }
  const acts = [...by.values()].sort((x, y) => y.s - x.s);
  // After a defensive move that works, what did the coach throw next? ("slip right then uppercut")
  const follow = (a: string) => {
    if (isAttack(a)) return null;
    const c: Record<string, number> = {};
    for (const d of lib) if (d.m1 === a && isAttack(d.a) && d.out === "landed") c[d.a] = (c[d.a] ?? 0) + d.w * rust[d.a];
    return Object.entries(c).sort((x, y) => y[1] - x[1])[0]?.[0] ?? null;
  };
  const line = (e: { a: string; n: number; ok: number; hit: number }) => {
    const f = e.ok ? follow(e.a) : null;
    return `- ${e.a}${f ? ` then ${f}` : ""}: worked ${e.ok}/${e.n}${e.hit ? `, got hit ${e.hit}` : ""}`;
  };
  const best = acts[0].s > 0 && acts[0].ok > 0 ? acts[0] : null, f = best ? follow(best.a) : null;
  return {
    block: `Your coach in similar moments (${near.length} demos):\n${acts.slice(0, 3).map(line).join("\n")}`,
    top: best && { moves: f ? [best.a, f] : [best.a], worked: best.ok, n: best.n, coach: best.train * 2 >= best.n,
      text: `${say(best.a)}${f ? ` then ${say(f)}` : ""} (worked ${best.ok}/${best.n})` },
  };
}
// The plan follows the coach when it opens (after any step in) with the coach's top move.
export function demoUsed(rc: Recall | null, combo: string[]): { used: string; usedText: string } | null {
  if (!rc?.top) return null;
  return combo.find((m) => m !== "step_in") === rc.top.moves[0] ? { used: "demo", usedText: `${rc.top.coach ? "Like your coach" : "From its own fights"}: ${rc.top.text}` } : null;
}

// ---- stats (0-100), all computed from data ----
const fightsOf = (botId: string) => Object.values(state.fights).filter((f) => f.a === botId || f.b === botId);
const rate = (demos: Demo[], rust: Record<string, number>, pred: (d: Demo) => boolean) => {
  let ok = 0, n = 0;
  for (const d of demos) if (pred(d)) { n += rust[d.a]; if (worked(d.a, d.out)) ok += rust[d.a]; }
  return { ok, n };
};
export function stats(b: Bot) {
  const lib = b.demos ?? [], rust = rustOf(lib), fights = fightsOf(b.botId);
  const hits = fights.flatMap((f) => f.hits);
  const att = rate(lib, rust, (d) => isAttack(d.a)), def = rate(lib, rust, (d) => !isAttack(d.a) && !!d.o1 && isAttack(d.o1));
  const thrown = hits.filter((h) => h.attacker === b.botId), taken = hits.filter((h) => h.defender === b.botId);
  const covered = ATTACKS.map((x) => b.brain.counters.some((c) => c.when === x) ? 1
    : Math.min(1, rate(lib, rust, (d) => d.o1 === x && !isAttack(d.a)).ok / 3));
  const decs = fights.flatMap((f) => f.decisions.filter((d) => d.botId === b.botId && d.outcome));
  const smart = decs.filter((d) => d.used && d.outcome === "landed").length;
  const pct = (x: number) => Math.round(clamp(x, 0, 1) * 100);
  return {
    reflexes: pct(covered.reduce((s, x) => s + x, 0) / ATTACKS.length),
    accuracy: pct((att.ok + thrown.filter((h) => h.result === "clean").length) / (att.n + thrown.length + 5)),
    defense: pct((def.ok + taken.filter((h) => h.result !== "clean").length) / (def.n + taken.length + 5)),
    ring_iq: pct(smart / (decs.length + 5)),
    experience: pct(Math.log(1 + lib.length) / Math.log(1 + DEMO_CAP)),
  };
}

// ---- XP, level, daily energy (00:00 UTC, same rhythm as 0G credits) ----
const dayOf = (t: number) => new Date(t).toISOString().slice(0, 10);
function prog(b: Bot, now = Date.now()) {
  const p = (b.progress ??= { xp: 0, day: dayOf(now), rounds: 0, fights: 0 });
  if (p.day !== dayOf(now)) Object.assign(p, { day: dayOf(now), rounds: 0, fights: 0 });
  return p;
}
export function levelOf(xp: number) {
  const L = Math.floor((1 + Math.sqrt(1 + (8 * xp) / 100)) / 2), start = (100 * L * (L - 1)) / 2, next = (100 * (L + 1) * L) / 2;
  return { level: L, xp, into: xp - start, next: next - start };
}
const energyOf = (b: Bot, now = Date.now()) => {
  const p = prog(b, now);
  return { left: Math.max(0, ENERGY_MAX - p.rounds), max: ENERGY_MAX, resetsAt: new Date(Date.parse(p.day) + DAY).toISOString() };
};
export const progressView = (b: Bot) => ({ ...levelOf(prog(b).xp), energy: energyOf(b), stats: stats(b), demos: b.demos?.length ?? 0 });

const ROUND_MULT = [1, 0.85, 0.7, 0.6, 0.5]; // diminishing returns inside a day
// POST /train/:botId/demos. A batch with `round` and no demos starts a round; `roundEnd` closes it and spends 1 energy.
export function addDemos(b: Bot, body: any) {
  const p = prog(b), roundId = typeof body?.round === "string" ? body.round.slice(0, 40) : null;
  if (roundId && p.round?.id !== roundId) p.round = { id: roundId, startedAt: Date.now(), demos: 0 };
  const raw: any[] = Array.isArray(body?.demos) ? body.demos.slice(0, BATCH_MAX) : [];
  let stored = 0;
  for (const x of raw) {
    const a = mv(x?.action), out = OUTCOMES.includes(x?.outcome) ? x.outcome : null;
    if (!a || !out || !x?.state || typeof x.state !== "object") continue;
    const k = key(x.state);
    add(b, { at: Date.now(), ...k, a, out, w: baseW(a, out), src: "train", ...(roundId ? { r: roundId } : {}) });
    stored++;
  }
  if (p.round && p.round.id === roundId) p.round.demos += stored;
  let round = null;
  if (body?.roundEnd && p.round && p.round.id === roundId) {
    const r = p.round, secs = (Date.now() - r.startedAt) / 1000;
    let xp = 0, reason: string | null = null;
    if (r.demos < 3 || secs < 15) reason = "round too short for XP (15 s and 3 moves minimum)";
    else if (p.rounds >= ENERGY_MAX) reason = "out of energy: still learning, XP is back at 00:00 UTC";
    else { xp = Math.round((10 + Math.min(20, r.demos)) * ROUND_MULT[p.rounds]); p.rounds++; p.xp += xp; addDrill(b); }
    round = { moves: r.demos, seconds: Math.round(secs), xpGained: xp, reason, highlight: highlight(b, r.id) };
    p.round = undefined;
  }
  save();
  return { stored, total: b.demos?.length ?? 0, energy: energyOf(b), xp: levelOf(p.xp), stats: stats(b), ...(round ? { round } : {}) };
}

// "left hook defense 62% → 71%": the move this round drilled most, before vs after the round.
function highlight(b: Bot, roundId: string): string | null {
  const lib = b.demos ?? [], mine = lib.filter((d) => d.r === roundId), before = lib.filter((d) => d.r !== roundId), rust = rustOf(lib);
  const top = (xs: string[]) => Object.entries(xs.reduce<Record<string, number>>((c, x) => ((c[x] = (c[x] ?? 0) + 1), c), {})).sort((x, y) => y[1] - x[1])[0]?.[0];
  const defX = top(mine.filter((d) => !isAttack(d.a) && d.o1 && isAttack(d.o1)).map((d) => d.o1!));
  const attX = top(mine.filter((d) => isAttack(d.a)).map((d) => d.a));
  const [label, pred] = defX ? [`${say(defX)} defense`, (d: Demo) => d.o1 === defX && !isAttack(d.a)] as const
    : attX ? [`${say(attX)} accuracy`, (d: Demo) => d.a === attX] as const : [null, null];
  if (!label || !pred) return null;
  const pc = (r: { ok: number; n: number }) => Math.round((100 * r.ok) / r.n);
  const was = rate(before, rust, pred), now = rate(lib, rust, pred);
  return was.n ? `${label} ${pc(was)}% → ${pc(now)}%` : `${label} ${pc(now)}%`;
}

// ---- fights: every decision + what happened next is a low-weight self-experience demo ----
const pending = new Map<string, { idx: number; at: number; s: FightState; a: string }>();
function settle(b: Bot, f: Fight) {
  const k = f.fightId + b.botId, p = pending.get(k);
  if (!p) return;
  pending.delete(k);
  const after = f.hits.filter((h) => Date.parse(h.at) >= p.at);
  const landed = after.find((h) => h.attacker === b.botId && h.result === "clean");
  const hitMe = after.some((h) => h.defender === b.botId && h.result === "clean");
  const saved = after.find((h) => h.defender === b.botId && h.result !== "clean");
  const out = landed ? "landed" : hitMe ? "got_hit" : saved ? saved.result : "whiff";
  const a = landed ? landed.move : p.a;
  if (f.decisions[p.idx]) f.decisions[p.idx].outcome = out;
  add(b, { at: Date.now(), ...key(p.s), a, out, w: r2(0.3 * baseW(a, out)), src: "fight" });
}
export function onDecide(b: Bot, f: Fight, s: FightState, plan: Plan) {
  settle(b, f);
  if (plan.combo?.length) pending.set(f.fightId + b.botId, { idx: f.decisions.length - 1, at: Date.now(), s, a: plan.combo[0] });
  save();
}
// Settle the last decisions and pay fight XP (win 25, loss 12, halving-ish with each fight that day).
export function onFightEnd(f: Fight) {
  const out: Record<string, number> = {};
  for (const id of [f.a, f.b]) {
    const b = state.bots[id]; if (!b) continue;
    settle(b, f);
    const p = prog(b), xp = Math.round((f.winner === id ? 25 : 12) / (1 + 0.5 * p.fights));
    p.fights++; p.xp += xp; out[id] = xp;
  }
  save();
  return out;
}

// Self-check: `npx tsx src/demos.ts`
if (process.argv[1]?.endsWith("demos.ts")) {
  const assert = (await import("node:assert")).strict;
  const b = { botId: "t", brain: { counters: [] }, demos: [] } as unknown as Bot;
  const s = (o1: string, dist = 1) => ({ distance: dist, oppLastMoves: [o1], selfLastMoves: [], sinceOppAttack: 0.2 });
  addDemos(b, { round: "r1", demos: [] });
  for (let i = 0; i < 4; i++) addDemos(b, { round: "r1", demos: [
    { state: s("hook_l"), action: "dodge_r", outcome: "dodged" },
    { state: { ...s("hook_l"), selfLastMoves: ["dodge_r"] }, action: "uppercut", outcome: "landed" },
    { state: s("jab", 2.5), action: "step_in", outcome: "whiff" },
  ] });
  addDemos(b, { round: "r1", demos: [{ state: s("hook_l"), action: "block", outcome: "got_hit" }, { state: { foo: 1 }, action: "laser", outcome: "landed" }] });
  assert.equal(b.demos!.length, 13);
  const rc = recall(b, s("hook_l"))!;
  assert.deepEqual(rc.top!.moves, ["dodge_r", "uppercut"]);
  assert.match(rc.block, /dodge_r then uppercut: worked/);
  assert.equal(demoUsed(rc, ["dodge_r", "uppercut"])?.used, "demo");
  assert.equal(demoUsed(rc, ["jab"]), null);
  assert.equal(demoUsed(rc, ["jab", "dodge_r"]), null);
  assert.equal(demoUsed(rc, ["step_in", "dodge_r"])?.used, "demo");
  assert.ok(stats(b).defense > 0 && stats(b).reflexes > 0);
  const end = addDemos(b, { round: "r1", roundEnd: true, demos: [] });
  assert.equal(end.round!.xpGained, 0); // too short: started < 15 s ago
  assert.deepEqual(levelOf(0).level, 1); assert.equal(levelOf(100).level, 2); assert.equal(levelOf(299).level, 2); assert.equal(levelOf(300).level, 3);
  const old = { ...b.demos![0], at: Date.now() - 10 * DAY };
  assert.ok(rustOf([old]).dodge_r < 1 && rustOf([old]).dodge_r >= 0.5);
  console.log("demos.ts self-check ok");
}
