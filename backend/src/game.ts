// Pure game rules: moves, damage, command words, plan validation, mock planner.
import type { Brain, Phrase } from "./state.ts";

export const MOVES = ["jab", "cross", "hook_l", "hook_r", "uppercut", "body", "block", "dodge_l", "dodge_r", "step_in", "step_back"] as const;
export const DAMAGE: Record<string, number> = { jab: 1, cross: 2, hook_l: 3, hook_r: 3, uppercut: 4, body: 2 };
const STANCES = ["aggressive", "defensive", "counter"];
const TRIGGERS = ["now", "after_block", "when_close"];

export type Plan = {
  stance: string; combo?: string[]; trigger?: string; say?: string;
  requestId?: string | null; teeVerified?: boolean | null; mock?: boolean; override?: boolean; reason?: string;
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

// Phrase → tactic from a model answer, or null.
export function validatePhrase(raw: unknown, said: string): Phrase | null {
  if (!raw || typeof raw !== "object") return null;
  const r = raw as Record<string, unknown>;
  const moves = cleanMoves(r.moves).slice(0, 4);
  if (!moves.length) return null;
  return { say: said.slice(0, 60), means: typeof r.means === "string" ? r.means.slice(0, 80) : moves.join(" "), moves };
}

export function learnPhrase(brain: Brain, p: Phrase) {
  const i = brain.phrases.findIndex((x) => norm(x.say) === norm(p.say));
  if (i >= 0) brain.phrases[i] = p; else brain.phrases.push(p);
  if (brain.phrases.length > 30) brain.phrases.shift();
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
  if (dist > 2.2) p = { stance: "aggressive", combo: ["step_in", "jab"], trigger: "now", say: "Closing in." };
  else if (s.lastHitBy && DAMAGE[s.lastHitBy] >= 3) p = { stance: "counter", combo: [s.lastHitBy === "hook_l" ? "dodge_r" : "dodge_l", "cross"], trigger: "now", say: `Saw that ${s.lastHitBy} coming.` };
  else if (self < opp * 0.6 && brain.style.guard >= 0.5) p = { stance: "defensive", combo: ["block", "step_back"], trigger: "now", say: "Cover up, reset." };
  else if (fav) p = { stance: "aggressive", combo: fav.moves.slice(0, 4), trigger: "when_close", say: `"${fav.say}"` };
  else if (brain.style.aggression >= 0.5) p = { stance: "aggressive", combo: t % 2 ? ["jab", "cross", "hook_l"] : ["jab", "body"], trigger: "when_close", say: "Work the jab." };
  else p = { stance: "counter", combo: ["block", "cross"], trigger: "after_block", say: "Let him come." };
  return { ...p, requestId: null, teeVerified: null, mock: true };
}

// Mock phrase mapping: pick out move words inside a longer sentence.
export function mockPhrase(text: string): Phrase | null {
  const words = norm(text);
  const moves: string[] = [];
  for (const [k, v] of Object.entries(COMMANDS)) if (k.length > 3 && words.includes(k) && !moves.includes(v)) moves.push(v);
  if (/fast|quick|light/.test(words) && !moves.length) moves.push("jab", "jab", "cross");
  if (/heavy|big|knock/.test(words) && !moves.length) moves.push("hook_r", "uppercut");
  return moves.length ? { say: text.slice(0, 60), means: `mock: ${moves.join(", ")}`, moves: moves.slice(0, 4) } : null;
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
  const b: Brain = { botId: "x", version: 0, phrases: [], lessons: [], scouting: {}, style: { aggression: 0.6, guard: 0.5 } };
  assert.deepEqual(mockPlan(b, { t: 1 }), mockPlan(b, { t: 1 }));
  assert.ok(mockPhrase("light him up")?.moves.length);
  learnPhrase(b, { say: "Light him up", means: "", moves: ["jab"] });
  learnPhrase(b, { say: "light him up!", means: "", moves: ["cross"] });
  assert.equal(b.phrases.length, 1);
  console.log("game.ts self-check ok");
}
