// Pure game rules: moves, damage, command words, plan validation, mock planner.
import type { Bot, Brain, Phrase, Counter, Combo } from "./state.ts";

export const MOVES = ["jab", "cross", "hook_l", "hook_r", "uppercut", "body", "block", "dodge_l", "dodge_r", "step_in", "step_back"] as const;
export const DAMAGE: Record<string, number> = { jab: 1, cross: 2, hook_l: 3, hook_r: 3, uppercut: 4, body: 2 };
const STANCES = ["aggressive", "defensive", "counter"];
const TRIGGERS = ["now", "after_block", "when_close"];

export type Plan = {
  stance: string; combo?: string[]; trigger?: string; say?: string;
  used?: string; usedText?: string; // the taught rule this plan follows ("counter#1") and its readable form
  requestId?: string | null; teeVerified?: boolean | null; mock?: boolean; reason?: string;
};
export const SAFE_STOP = (reason: string): Plan => ({ stance: "safe_stop", reason });

export type FightState = {
  t?: number; selfBudget?: number; oppBudget?: number; distance?: number; selfGuard?: number; oppGuard?: number;
  oppLastMoves?: string[]; selfLastMoves?: string[]; lastHitBy?: string | null;
};

const norm = (s: string) => s.toLowerCase().replace(/[^a-z0-9 ]+/g, " ").replace(/\s+/g, " ").trim();

// Single command words/phrases the coach can shout; mapped without any model call.
const COMMANDS: Record<string, string> = {
  block: "block", "guard up": "block", "hands up": "block", cover: "block", "cover up": "block", defend: "block",
  jab: "jab", "jab jab": "jab", cross: "cross", "straight right": "cross",
  hook: "hook_l", "left hook": "hook_l", "right hook": "hook_r",
  uppercut: "uppercut", upper: "uppercut",
  body: "body", "go body": "body", "body shot": "body", "go to the body": "body", "hit the body": "body",
  dodge: "dodge_l", "dodge left": "dodge_l", "slip left": "dodge_l", "dodge right": "dodge_r", "slip right": "dodge_r", slip: "dodge_r",
  "step in": "step_in", "get in": "step_in", "go in": "step_in", press: "step_in", forward: "step_in", "close the distance": "step_in",
  "back off": "step_back", "step back": "step_back", "back up": "step_back", "get out": "step_back", back: "step_back",
};
export function commandFor(text: string): string | null {
  return COMMANDS[norm(text)] ?? null;
}
export function phraseFor(brain: Brain, text: string): Phrase | null {
  const t = norm(text);
  return brain.phrases.find((p) => norm(p.say) === t) ?? null;
}

export function cleanMoves(x: unknown): string[] {
  return Array.isArray(x) ? x.filter((m): m is string => typeof m === "string" && (MOVES as readonly string[]).includes(m)) : [];
}

// Strict validation of a model plan. Returns null when it is not usable (caller turns that into safe_stop).
export function validatePlan(raw: unknown): Plan | null {
  if (!raw || typeof raw !== "object") return null;
  const r = raw as Record<string, unknown>;
  if (typeof r.stance !== "string" || !STANCES.includes(r.stance)) return null;
  const combo = cleanMoves(r.combo);
  if (combo.length < 1) return null;
  return {
    stance: r.stance,
    combo: combo.slice(0, 4),
    trigger: typeof r.trigger === "string" && TRIGGERS.includes(r.trigger) ? r.trigger : "now",
    say: typeof r.say === "string" ? r.say.slice(0, 80) : "",
  };
}

export function learnPhrase(brain: Brain, p: Phrase) {
  const i = brain.phrases.findIndex((x) => norm(x.say) === norm(p.say));
  if (i >= 0) brain.phrases[i] = p; else brain.phrases.push(p);
  if (brain.phrases.length > 30) brain.phrases.shift();
}

// ---- training: one coach line → one structured brain update ----
export type Learned =
  | { kind: "phrase"; means: string; moves: string[] }
  | { kind: "counter"; when: string; moves: string[] }
  | { kind: "combo"; use: Combo["use"]; name: string; moves: string[] }
  | { kind: "style"; aggression: number; guard: number }; // deltas
const USES = ["opener", "finisher", "any"];
const clampD = (x: unknown) => (typeof x === "number" && isFinite(x) ? Math.max(-0.2, Math.min(0.2, x)) : 0);
const str = (x: unknown, n: number) => (typeof x === "string" ? x.trim().slice(0, n) : "");

// Strict: moves from the fixed list only; anything off-shape → null (nothing is learned).
export function validateLearn(raw: unknown): Learned | null {
  if (!raw || typeof raw !== "object") return null;
  const r = raw as Record<string, unknown>;
  const moves = cleanMoves(r.moves).slice(0, 4);
  switch (r.kind) {
    case "counter":
      return typeof r.when === "string" && DAMAGE[r.when] && moves.length ? { kind: "counter", when: r.when, moves } : null;
    case "combo":
      return moves.length ? { kind: "combo", use: (USES.includes(r.use as string) ? r.use : "any") as Combo["use"], name: str(r.name, 30) || moves.join(" "), moves } : null;
    case "style": {
      const aggression = clampD(r.aggression), guard = clampD(r.guard);
      return aggression || guard ? { kind: "style", aggression, guard } : null;
    }
    case "phrase":
      return moves.length ? { kind: "phrase", means: str(r.means, 80) || moves.join(" "), moves } : null;
  }
  return null;
}

const list = (m: string[]) => m.join(", ");
const r2 = (x: number) => Math.round(Math.max(0, Math.min(1, x)) * 100) / 100;
// Apply to the brain (dedupe + caps) and say what changed in one line.
export function applyLearn(brain: Brain, l: Learned, said: string): { id: string; diff: string } {
  said = said.slice(0, 60);
  if (l.kind === "phrase") {
    learnPhrase(brain, { say: said, means: l.means, moves: l.moves });
    return { id: `phrase#${brain.phrases.findIndex((p) => norm(p.say) === norm(said)) + 1}`, diff: `Learned phrase: “${said}” = ${list(l.moves)}` };
  }
  if (l.kind === "counter") {
    const c: Counter = { when: l.when, moves: l.moves, said };
    const i = brain.counters.findIndex((x) => x.when === l.when); // one answer per attack; newest coaching wins
    if (i >= 0) brain.counters[i] = c; else brain.counters.push(c);
    if (brain.counters.length > 12) brain.counters.shift();
    return { id: `counter#${brain.counters.indexOf(c) + 1}`, diff: `Learned counter: ${l.when} → ${list(l.moves)}` };
  }
  if (l.kind === "combo") {
    const c: Combo = { use: l.use, name: l.name, moves: l.moves, said };
    const i = brain.combos.findIndex((x) => x.use === l.use && x.moves.join() === l.moves.join());
    if (i >= 0) brain.combos[i] = c; else brain.combos.push(c);
    if (brain.combos.length > 10) brain.combos.shift();
    return { id: `combo#${brain.combos.indexOf(c) + 1}`, diff: `Learned ${l.use === "any" ? "combo" : l.use}: ${list(l.moves)}` };
  }
  const s = brain.style, before = { ...s };
  s.aggression = r2(s.aggression + l.aggression); s.guard = r2(s.guard + l.guard);
  const parts = (["aggression", "guard"] as const).filter((k) => s[k] !== before[k]).map((k) => `${k} ${before[k].toFixed(2)} → ${s[k].toFixed(2)}`);
  return { id: "style", diff: `Style: ${parts.join(", ") || "already at the limit"}` };
}

// Readable form of a rule id, or null if it doesn't exist.
export function ruleText(brain: Brain, id: string): string | null {
  const m = /^(counter|combo|phrase)#(\d+)$/.exec(id);
  if (!m) return null;
  const i = Number(m[2]) - 1;
  if (m[1] === "counter") { const c = brain.counters[i]; return c ? `counter: ${c.when} → ${list(c.moves)}` : null; }
  if (m[1] === "combo") { const c = brain.combos[i]; return c ? `${c.use === "any" ? "combo" : c.use}: ${list(c.moves)}` : null; }
  const p = brain.phrases[i]; return p ? `“${p.say}”: ${list(p.moves)}` : null;
}
const ruleMoves = (brain: Brain, id: string) => {
  const [k, n] = id.split("#"), i = Number(n) - 1;
  return (k === "counter" ? brain.counters[i] : k === "combo" ? brain.combos[i] : brain.phrases[i])?.moves ?? [];
};

// Compact rule list for the decision prompt (one line each, ids the model cites back).
export function ruleSheet(brain: Brain): string {
  return [
    ...brain.counters.map((c, i) => `counter#${i + 1}: opp throws ${c.when} -> ${c.moves.join(" ")}`),
    ...brain.combos.map((c, i) => `combo#${i + 1} (${c.use}): ${c.moves.join(" ")}`),
    ...brain.phrases.slice(-8).map((p) => `phrase#${brain.phrases.indexOf(p) + 1} "${p.say}": ${p.moves.join(" ")}`),
  ].join("\n");
}
// Rules that fit the moment, so a small model doesn't have to work it out.
export function ruleHints(brain: Brain, s: FightState): string[] {
  const out: string[] = [];
  const last = s.oppLastMoves?.at(-1), hitBy = s.lastHitBy;
  brain.counters.forEach((c, i) => { if (c.when === last || c.when === hitBy) out.push(`opponent just threw ${c.when}: counter#${i + 1} applies`); });
  brain.combos.forEach((c, i) => {
    if (c.use === "opener" && (s.t ?? 99) < 4) out.push(`fight just started: combo#${i + 1} (opener) applies`);
    if (c.use === "finisher" && (s.oppBudget ?? 99) <= 12) out.push(`opponent is nearly out: combo#${i + 1} (finisher) applies`);
  });
  return out;
}
// Model's "used" → a rule id that exists and whose moves the combo actually plays; anything else is dropped.
// If the model says "none" but its combo is exactly a taught sequence, it is that rule.
export function resolveUsed(brain: Brain, used: unknown, combo: string[]): { used: string; usedText: string } | null {
  const same = (m: string[]) => m.join() === combo.join();
  const exact = [["counter", brain.counters], ["combo", brain.combos], ["phrase", brain.phrases]]
    .map(([k, xs]) => [k, (xs as { moves: string[] }[]).findIndex((x) => same(x.moves))] as const).find(([, i]) => i >= 0);
  let id = (typeof used === "string" ? /(counter|combo|phrase)#\d+/.exec(used)?.[0] ?? used.trim() : "") // models add "(opener)" etc.
    || "none";
  if (!ruleText(brain, id) && brain.phrases.every((x) => norm(x.say) !== norm(id)) && exact) id = `${exact[0]}#${exact[1] + 1}`;
  const p = brain.phrases.findIndex((x) => norm(x.say) === norm(id));
  if (p >= 0) id = `phrase#${p + 1}`;
  const text = ruleText(brain, id);
  let k = 0; // the rule's moves must appear in order inside the combo (extra moves like step_in are fine)
  for (const m of combo) if (m === ruleMoves(brain, id)[k]) k++;
  if (!text || k < ruleMoves(brain, id).length) return null;
  return { used: id, usedText: text };
}

// ---- daily training goal ----
export const DAILY_GOAL = 3;
const dayOf = (d: Date) => d.toISOString().slice(0, 10);
export function dailyView(bot: Bot, now = new Date()) {
  const d = bot.daily, today = dayOf(now), yest = dayOf(new Date(now.getTime() - 864e5));
  const drills = d?.day === today ? d.drills : 0;
  return { goal: DAILY_GOAL, drills: Math.min(drills, DAILY_GOAL), done: drills >= DAILY_GOAL,
    streak: d && (d.goalDay === today || d.goalDay === yest) ? d.streak : 0 };
}
// One coaching call that taught or drilled something. Returns true the moment today's goal is hit.
export function addDrill(bot: Bot, now = new Date()): boolean {
  const today = dayOf(now), yest = dayOf(new Date(now.getTime() - 864e5));
  const d = bot.daily ?? { day: today, drills: 0, streak: 0, goalDay: null };
  if (d.day !== today) { d.day = today; d.drills = 0; }
  d.drills++;
  bot.daily = d;
  if (d.drills !== DAILY_GOAL) return false;
  d.streak = d.goalDay === yest ? d.streak + 1 : 1; d.goalDay = today;
  return true;
}
export function addLessons(brain: Brain, lessons: string[]) {
  for (const l of lessons) if (!brain.lessons.some((x) => norm(x) === norm(l))) brain.lessons.push(l);
  brain.lessons = brain.lessons.slice(-20);
}

// ---- mock (deterministic) brains ----
export function mockPlan(brain: Brain, s: FightState): Plan {
  const self = s.selfBudget ?? 50, opp = s.oppBudget ?? 50, dist = s.distance ?? 1.5;
  const t = Math.floor(s.t ?? 0);
  const fav = brain.phrases.length ? brain.phrases[t % brain.phrases.length] : null;
  let p: Plan;
  const ci = brain.counters.findIndex((c) => c.when === s.oppLastMoves?.at(-1) || c.when === s.lastHitBy);
  const oi = brain.combos.findIndex((c) => c.use === "opener"), fi = brain.combos.findIndex((c) => c.use === "finisher");
  const rule = (id: string, stance: string, combo: string[], say: string): Plan => ({ stance, combo, trigger: "now", say, used: id, usedText: ruleText(brain, id)! });
  if (ci >= 0) p = rule(`counter#${ci + 1}`, "counter", brain.counters[ci].moves, `Saw the ${brain.counters[ci].when}. Drilled this.`);
  else if (fi >= 0 && opp <= 12) p = rule(`combo#${fi + 1}`, "aggressive", brain.combos[fi].moves, "Finish it like we practised.");
  else if (oi >= 0 && t < 4) p = rule(`combo#${oi + 1}`, "aggressive", brain.combos[oi].moves, "Opener from training.");
  else if (dist > 2.2) p = { stance: "aggressive", combo: ["step_in", "jab"], trigger: "now", say: "Closing in." };
  else if (s.lastHitBy && DAMAGE[s.lastHitBy] >= 3) p = { stance: "counter", combo: [s.lastHitBy === "hook_l" ? "dodge_r" : "dodge_l", "cross"], trigger: "now", say: `Saw that ${s.lastHitBy} coming.` };
  else if (self < opp * 0.6 && brain.style.guard >= 0.5) p = { stance: "defensive", combo: ["block", "step_back"], trigger: "now", say: "Cover up, reset." };
  else if (fav) p = { stance: "aggressive", combo: fav.moves.slice(0, 4), trigger: "when_close", say: `"${fav.say}"`, used: `phrase#${brain.phrases.indexOf(fav) + 1}`, usedText: ruleText(brain, `phrase#${brain.phrases.indexOf(fav) + 1}`)! };
  else if (brain.style.aggression >= 0.5) p = { stance: "aggressive", combo: t % 2 ? ["jab", "cross", "hook_l"] : ["jab", "body"], trigger: "when_close", say: "Work the jab." };
  else p = { stance: "counter", combo: ["block", "cross"], trigger: "after_block", say: "Let him come." };
  return { ...p, requestId: null, teeVerified: null, mock: true };
}

// Move words in the order they're said ("left hook" before "hook", each word used once).
const MOVE_WORDS = [...Object.keys(COMMANDS).filter((k) => k !== "jab jab"), ...MOVES.map((m) => m.replace("_", " "))].sort((a, b) => b.length - a.length);
export function movesIn(text: string): string[] {
  let s = ` ${norm(text)} `;
  const found: [number, string][] = [];
  for (const k of MOVE_WORDS) for (let i; (i = s.indexOf(` ${k} `)) >= 0;) {
    found.push([i, COMMANDS[k] ?? k.replace(" ", "_")]);
    s = s.slice(0, i + 1) + "#".repeat(k.length) + s.slice(i + 1 + k.length);
  }
  return found.sort((a, b) => a[0] - b[0]).map((f) => f[1]);
}

// Mock phrase mapping: pick out move words inside a longer sentence.
export function mockPhrase(text: string): Phrase | null {
  const words = norm(text);
  const moves = movesIn(text);
  if (/fast|quick|light/.test(words) && !moves.length) moves.push("jab", "jab", "cross");
  if (/heavy|big|knock/.test(words) && !moves.length) moves.push("hook_r", "uppercut");
  return moves.length ? { say: text.slice(0, 60), means: `mock: ${moves.join(", ")}`, moves: moves.slice(0, 4) } : null;
}
// Mock classifier with the same output shape as the 0G one (run through validateLearn by the caller).
export function mockLearn(text: string): unknown {
  const w = norm(text), ms = movesIn(text);
  if (/\b(when|if|whenever|every time)\b/.test(w) && ms.length >= 2 && DAMAGE[ms[0]]) return { kind: "counter", when: ms[0], moves: ms.slice(1) };
  if (/\b(open|opener|start)/.test(w) && ms.length) return { kind: "combo", use: "opener", moves: ms };
  if (/\b(finish|finisher|put him away|end it)/.test(w) && ms.length) return { kind: "combo", use: "finisher", moves: ms };
  if (/aggress|be brave|attack more|more pressure/.test(w)) return { kind: "style", aggression: 0.15, guard: -0.05 };
  if (/careful|patient|defen|protect/.test(w)) return { kind: "style", aggression: -0.1, guard: 0.15 };
  const p = mockPhrase(text);
  return p ? { kind: "phrase", means: p.means, moves: p.moves } : { kind: "none" };
}

export function mockLessons(botId: string, hits: { attacker: string; defender: string; move: string; result: string }[]): string[] {
  const out: string[] = [];
  const landed = hits.filter((h) => h.attacker === botId && h.result === "clean");
  const taken = hits.filter((h) => h.defender === botId && h.result === "clean");
  const top = (xs: { move: string }[]) => Object.entries(xs.reduce<Record<string, number>>((a, h) => ((a[h.move] = (a[h.move] ?? 0) + 1), a), {})).sort((a, b) => b[1] - a[1])[0]?.[0];
  if (top(landed)) out.push(`${top(landed)} landed most`);
  if (top(taken)) out.push(`watch for the ${top(taken)}`);
  if (!out.length) out.push("nothing landed; get closer");
  return out;
}

// Self-check: `npx tsx src/game.ts`
if (process.argv[1]?.endsWith("game.ts")) {
  const assert = (await import("node:assert")).strict;
  assert.equal(commandFor("Go body!"), "body");
  assert.equal(commandFor("Back off."), "step_back");
  assert.equal(commandFor("light him up"), null);
  assert.deepEqual(validatePlan({ stance: "aggressive", combo: ["jab", "laser", "cross", "jab", "jab", "jab"], trigger: "x", say: "hi" }),
    { stance: "aggressive", combo: ["jab", "cross", "jab", "jab"], trigger: "now", say: "hi" });
  assert.equal(validatePlan({ stance: "aggressive", combo: ["laser"] }), null);
  assert.equal(validatePlan({ stance: "yolo", combo: ["jab"] }), null);
  assert.equal(validatePlan("nope"), null);
  const b: Brain = { botId: "x", version: 0, phrases: [], counters: [], combos: [], lessons: [], scouting: {}, style: { aggression: 0.6, guard: 0.5 } };
  assert.deepEqual(mockPlan(b, { t: 1 }), mockPlan(b, { t: 1 }));
  assert.ok(mockPhrase("light him up")?.moves.length);
  learnPhrase(b, { say: "Light him up", means: "", moves: ["jab"] });
  learnPhrase(b, { say: "light him up!", means: "", moves: ["cross"] });
  assert.equal(b.phrases.length, 1);
  assert.deepEqual(movesIn("when he throws a left hook, slip right and uppercut"), ["hook_l", "dodge_r", "uppercut"]);
  assert.deepEqual(movesIn("jab, jab, cross"), ["jab", "jab", "cross"]);
  assert.equal(validateLearn({ kind: "counter", when: "block", moves: ["jab"] }), null); // must counter an attack
  assert.equal(validateLearn({ kind: "counter", when: "hook_l", moves: ["laser"] }), null);
  assert.equal(validateLearn({ kind: "none" }), null);
  const c = validateLearn(mockLearn("when he throws a left hook, slip right and uppercut"))!;
  assert.deepEqual(applyLearn(b, c, "x"), { id: "counter#1", diff: "Learned counter: hook_l → dodge_r, uppercut" });
  applyLearn(b, validateLearn({ kind: "counter", when: "hook_l", moves: ["block", "cross"] })!, "y"); // same attack → replaced
  assert.equal(b.counters.length, 1);
  assert.equal(validateLearn({ kind: "style", aggression: 5, guard: 0 })!.kind, "style");
  assert.match(applyLearn(b, validateLearn({ kind: "style", aggression: 5 })!, "z").diff, /aggression 0.60 → 0.80/);
  assert.deepEqual(resolveUsed(b, "counter#1", ["block", "cross"]), { used: "counter#1", usedText: "counter: hook_l → block, cross" });
  assert.equal(resolveUsed(b, "counter#2", ["jab"]), null);
  assert.equal(resolveUsed(b, "counter#1 (hook)", ["step_in", "block", "cross"])?.used, "counter#1");
  assert.equal(resolveUsed(b, "counter#1", ["cross", "block"]), null); // wrong order
  assert.equal(resolveUsed(b, "counter#1", ["jab"]), null); // cites a rule it doesn't follow
  assert.equal(resolveUsed(b, "light him up", ["jab", "cross"])?.used, "phrase#1");
  assert.equal(resolveUsed(b, "none", ["block", "cross"])?.used, "counter#1"); // plays the taught sequence exactly
  assert.equal(mockPlan(b, { oppLastMoves: ["hook_l"] }).used, "counter#1");
  const bot = { daily: undefined } as unknown as Bot, d0 = new Date("2026-11-05T10:00:00Z"), d1 = new Date("2026-11-06T10:00:00Z");
  assert.deepEqual([addDrill(bot, d0), addDrill(bot, d0), addDrill(bot, d0), addDrill(bot, d0)], [false, false, true, false]);
  addDrill(bot, d1); addDrill(bot, d1);
  assert.equal(addDrill(bot, d1), true);
  assert.deepEqual(dailyView(bot, d1), { goal: 3, drills: 3, done: true, streak: 2 });
  assert.equal(dailyView(bot, new Date("2026-11-09T10:00:00Z")).streak, 0);
  console.log("game.ts self-check ok");
}
