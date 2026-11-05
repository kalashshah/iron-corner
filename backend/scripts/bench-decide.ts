// Live latency check for zg.decide/learn with a trained brain. Usage: ZG_MODE=live ZG_STORAGE_MODE=mock HEDERA_MODE=mock WORLD_MODE=mock STATE_FILE=bench.json npx tsx scripts/bench-decide.ts
import * as zg from "../src/zg.ts";
import type { Bot } from "../src/state.ts";
const bot = { botId: "bot_a", name: "Brass Knuckle", house: false, brain: { botId: "bot_a", version: 1, lessons: ["uppercuts keep missing"], scouting: { bot_b: ["drops guard after a hook"] },
  style: { aggression: 0.7, guard: 0.5 }, phrases: [{ say: "light him up", means: "fast jabs", moves: ["jab", "jab", "cross"] }],
  counters: [{ when: "hook_l", moves: ["dodge_r", "uppercut"], said: "" }], combos: [{ use: "opener", name: "", moves: ["jab", "jab", "cross"], said: "" }, { use: "finisher", name: "", moves: ["hook_r", "uppercut"], said: "" }] } } as unknown as Bot;
const states = [
  { t: 1, selfBudget: 50, oppBudget: 50, distance: 1.2, oppLastMoves: [], selfLastMoves: [], lastHitBy: null },
  { t: 8, selfBudget: 44, oppBudget: 40, distance: 1.1, oppLastMoves: ["jab", "hook_l"], selfLastMoves: ["jab"], lastHitBy: "hook_l" },
  { t: 20, selfBudget: 30, oppBudget: 9, distance: 1.0, oppLastMoves: ["block"], selfLastMoves: ["cross"], lastHitBy: null },
  { t: 12, selfBudget: 35, oppBudget: 30, distance: 2.6, oppLastMoves: ["step_back"], selfLastMoves: [], lastHitBy: null },
];
for (const text of process.argv.slice(2)) {
  const t0 = Date.now(); const r = await zg.learn(text);
  console.log(`learn ${Date.now() - t0} ms  "${text}" → ${JSON.stringify(r.learned)} req=${r.requestId}${r.error ? " " + r.error : ""}`);
}
const ms: number[] = [];
for (let i = 0; i < 8; i++) {
  const s = states[i % states.length], t0 = Date.now();
  const p = await zg.decide(bot, s, "bot_b");
  ms.push(Date.now() - t0);
  console.log(`decide ${ms.at(-1)} ms  t=${s.t} → ${p.stance} ${JSON.stringify(p.combo ?? [])} used=${p.used ?? "-"} ${p.reason ?? ""} req=${p.requestId}`);
}
ms.sort((a, b) => a - b);
console.log(`decide median ${ms[4]} ms, max ${ms.at(-1)} ms`);
process.exit(0);
