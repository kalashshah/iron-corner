// Text-only corner team on 0G Private Computer (no image, video or voice models):
//   coach   LLM reviews what the owner demonstrated and coaches the OWNER   POST /train/:id/review
//   tapes   LLM studies the opponent's 0G Storage episode logs → game plan  POST /fights/:id/scout
// Mock mode (no ZG_API_KEY) returns text marked mock:true.
import express from "express";
import OpenAI from "openai";
import { env, modes, state, save, fail, type Fight } from "./state.ts";
import { MOVES } from "./game.ts";
import { loadEpisode } from "./storage.ts";

const BASE = env.ZG_BASE_URL ?? "https://router-api.0g.ai/v1";
const TEXT = env.ZG_CHAT_MODEL ?? "qwen-flash";
const client = modes.zg === "live" ? new OpenAI({ apiKey: env.ZG_API_KEY, baseURL: BASE, maxRetries: 0 }) : null;
const rid = (data: any, headers?: Headers) => data?.x_0g_trace?.request_id ?? headers?.get("x-request-id") ?? null;

// ---- 0G calls ---------------------------------------------------------------------------------
async function textJson(system: string, user: string, maxTokens = 220, timeoutMs = 12000) {
  const { data, response } = await client!.chat.completions
    .create({ model: TEXT, temperature: 0.8, max_tokens: maxTokens,
      messages: [{ role: "system", content: system }, { role: "user", content: user }] } as any,
      { signal: AbortSignal.timeout(timeoutMs) }).withResponse();
  const t: string = (data as any).choices?.[0]?.message?.content ?? "";
  const json = JSON.parse(t.slice(t.indexOf("{"), t.lastIndexOf("}") + 1));
  return { json, requestId: rid(data, response.headers) as string | null };
}

const clamp01 = (x: unknown, d: number) => (typeof x === "number" && isFinite(x) ? Math.min(1, Math.max(0, x)) : d);
const str = (x: unknown, max: number, d = "") => (typeof x === "string" ? x.trim().slice(0, max) : d);
const bot = (id: string) => state.bots[id] ?? fail(404, "unknown bot");
const fightOf = (id: string) => (state.fights[id] as Fight) ?? fail(404, "unknown fight");

// ---- routes -------------------------------------------------------------------------------------
export function mountAi(app: express.Express) {
  // AI coach: reviews what the owner taught and how it went, then coaches the OWNER.
  app.post("/train/:id/review", async (req, res) => {
    const b = bot(req.params.id);
    const demos = ((b as any).demos ?? []) as { a: string; out: string; o1: string | null; src: string }[];
    const recentDemos = demos.filter((d) => d.src === "train").slice(-60);
    const tally: Record<string, { n: number; good: number }> = {};
    for (const d of recentDemos) {
      const k = `${d.o1 ?? "open"}→${d.a}`; tally[k] ??= { n: 0, good: 0 }; tally[k].n++;
      if (["landed", "blocked", "dodged"].includes(d.out)) tally[k].good++;
    }
    const summary = Object.entries(tally).sort((x, y) => y[1].n - x[1].n).slice(0, 12).map(([k, v]) => `${k}: ${v.good}/${v.n} worked`).join("; ");
    const brainBits = `counters: ${b.brain.counters?.map((c) => `${c.when}→${c.moves.join(",")}`).join("; ") || "none"}; lessons: ${b.brain.lessons.slice(-4).join("; ") || "none"}`;
    if (!client) return void res.json({ tips: ["Drill your hook defense: block or slip every left hook."], drill: { against: "hook_l", goal: 5 }, mock: true });
    const { json, requestId } = await textJson(
      `You are a veteran boxing coach reviewing how a human coach is training their robot boxer by demonstration. ` +
      `The robot copies what the human does. Moves: ${MOVES.join(", ")}. Give 2-3 specific, encouraging tips about the HUMAN's habits ` +
      `(max 18 words each) and ONE drill. Reply ONLY JSON {"tips":["..."],"drill":{"against":"<opponent move>","goal":3-8,"why":"max 12 words"}}`,
      `What the human did in recent sparring (situation→their move: success): ${summary || "nothing recorded yet"}. Robot brain: ${brainBits}`, 260);
    const tips = (Array.isArray(json.tips) ? json.tips : []).map((t: unknown) => str(t, 140)).filter(Boolean).slice(0, 3);
    const against = MOVES.includes(json.drill?.against) ? json.drill.against : "hook_l";
    res.json({ tips, drill: { against, goal: Math.min(8, Math.max(3, Number(json.drill?.goal) || 5)), why: str(json.drill?.why, 80) }, requestId });
  });

  // Tape study: read the opponent's last fights back from 0G Storage, then plan.
  app.post("/fights/:id/scout", async (req, res) => {
    const f = fightOf(req.params.id);
    const me = String(req.body?.botId ?? f.a), opp = me === f.a ? f.b : f.a;
    if (me !== f.a && me !== f.b) fail(400, "botId is not in this fight");
    const tapes = Object.values(state.fights)
      .filter((x: any) => x.fightId !== f.fightId && x.episode?.root && (x.a === opp || x.b === opp))
      .sort((x: any, y: any) => (y.createdAt > x.createdAt ? 1 : -1)).slice(0, 2) as any[];
    const studied: { fightId: string; root: string }[] = [];
    const moves: Record<string, number> = {}, landed: Record<string, number> = {};
    for (const t of tapes) {
      const ep: any = await loadEpisode(t.episode.root).catch(() => null);
      if (!ep) continue;
      studied.push({ fightId: t.fightId, root: t.episode.root });
      for (const h of ep.hits ?? []) if (h.attacker === opp) { moves[h.move] = (moves[h.move] ?? 0) + 1; if (h.result === "clean") landed[h.move] = (landed[h.move] ?? 0) + 1; }
    }
    const profile = Object.entries(moves).sort((a, b) => b[1] - a[1]).map(([m, n]) => `${m} x${n} (${landed[m] ?? 0} landed)`).join(", ");
    let report = { summary: "No tapes on this opponent yet. Fight your own fight.", plan: [] as string[] }, requestId: string | null = null;
    if (client && profile) {
      const r = await textJson(`You are a robot boxer's analyst. From the opponent's tape stats write a scouting report. Moves: ${MOVES.join(", ")}. ` +
        `Reply ONLY JSON {"summary":"max 20 words","plan":["2-3 short instructions using move names"]}`, `Opponent ${state.bots[opp].name}: ${profile}`, 200);
      report = { summary: str(r.json.summary, 160), plan: (r.json.plan ?? []).map((x: unknown) => str(x, 90)).filter(Boolean).slice(0, 3) }; requestId = r.requestId;
    } else if (profile) report = { summary: `Throws mostly: ${profile}.`, plan: [] };
    const out = { botId: me, opponent: opp, studied, report, requestId, ...(client ? {} : { mock: true }) };
    ((f as any).scouting ??= {})[me] = out; save();
    res.json(out);
  });

}
