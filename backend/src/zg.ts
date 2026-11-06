// 0G Compute router (OpenAI-compatible): qwen omni speech-to-text + qwen-flash decisions.
import OpenAI from "openai";
import { env, modes, type Bot } from "./state.ts";
import { MOVES, DAMAGE, SAFE_STOP, validatePlan, validateLearn, mockPlan, mockLearn, mockLessons, ruleSheet, ruleHints, resolveUsed,
  type Plan, type FightState, type Learned } from "./game.ts";
import { recall, demoUsed } from "./demos.ts";
import { adapterMoves, ADAPTER_SERVING } from "./camp.ts";
import { clientFor, charge } from "./zgaccount.ts";

const BASE = env.ZG_BASE_URL ?? "https://router-api.0g.ai/v1";
const CHAT_MODEL = env.ZG_CHAT_MODEL ?? "qwen-flash";
const DECIDE_MS = Number(env.DECIDE_TIMEOUT_MS ?? 4000); // Unity keeps playing the current combo while this is in flight
const ai = modes.zg === "live" ? new OpenAI({ apiKey: env.ZG_API_KEY, baseURL: BASE, maxRetries: 0, defaultHeaders: { "X-0G-Provider-Sort": "latency" } }) : null;

type Trace = { requestId: string | null; teeVerified: boolean | null };
function trace(body: any, headers: Headers): Trace {
  const t = body?.x_0g_trace ?? {};
  return { requestId: t.request_id ?? headers.get("x-request-id"), teeVerified: t.tee_verified ?? body?.tee_verified ?? null };
}

// b: the bot whose owner linked their own 0G account pays for the call (zgaccount.ts); otherwise the house key does.
async function chatJson(system: string, user: string, timeoutMs: number, maxTokens = 160, temperature = 0.4, b?: Bot): Promise<{ json: unknown; trace: Trace }> {
  const { data, response } = await (clientFor(b) ?? ai!).chat.completions
    .create(
      // qwen-flash has no provider for response_format (503 "No available providers"), so ask for JSON in the
      // prompt and pull the object out of any ``` fences. Strict validation happens in validatePlan/validateLearn.
      { model: CHAT_MODEL, messages: [{ role: "system", content: system }, { role: "user", content: user }],
        temperature, max_tokens: maxTokens, verify_tee: true } as any,
      { signal: AbortSignal.timeout(timeoutMs) },
    )
    .withResponse();
  charge(b, data);
  const text: string = (data as any).choices?.[0]?.message?.content ?? "";
  const obj = text.slice(text.indexOf("{"), text.lastIndexOf("}") + 1);
  return { json: JSON.parse(obj), trace: trace(data, response.headers) };
}

const moveSheet = () =>
  `Move names: ${MOVES.join(", ")}. Damage per clean hit: ${Object.entries(DAMAGE).map(([m, d]) => `${m}=${d}c`).join(", ")}. ` +
  `block chips guard and stops damage; dodge_l/dodge_r make the hit miss; step_in/step_back change distance (1 = punching range).`;

// Compact on purpose: the decision runs every 1.5 s per bot and has to come back in well under 2 s.
const brainSheet = (bot: Bot, opp?: string) => {
  const b = bot.brain, rules = ruleSheet(b), scout = opp ? b.scouting[opp] ?? [] : [];
  return `You are ${bot.name}, a robot boxer. Style: aggression ${b.style.aggression}, guard ${b.style.guard}.\n` +
    (rules ? `Rules your coach taught you in training:\n${rules}\n` : "") +
    (b.lessons.length ? `Lessons: ${b.lessons.slice(-5).join("; ")}\n` : "") +
    (scout.length ? `Scouting: ${scout.slice(-3).join("; ")}\n` : "");
};

export async function decide(bot: Bot, s: FightState, opp?: string): Promise<Plan> {
  // The bot's own camp model (LoRA trained on 0G from its owner's sparring) suggests; qwen-flash on 0G decides.
  const camp = ADAPTER_SERVING && bot.adapter ? await adapterMoves(bot, s).catch((e) => (console.warn(`[adapter] ${bot.botId}: ${e.message}`), null)) : null;
  const rc = recall(bot, s); // the k nearest shadow-sparring demos for this moment
  if (!ai) {
    const p = mockPlan(bot.brain, s);
    if (!rc?.top || /^(counter|combo)/.test(p.used ?? "")) return p; // a rule that fits the moment beats a demo
    return { ...p, stance: rc.top.moves.some((m) => DAMAGE[m]) ? "counter" : "defensive", combo: rc.top.moves, trigger: "now", say: "Doing what coach showed me.", ...demoUsed(rc, rc.top.moves)! };
  }
  const system = `${brainSheet(bot, opp)}${moveSheet()}\nBudgets are health in cents. Follow a taught rule when one fits; otherwise copy what worked for your coach.\n` +
    `Reply with ONLY JSON like {"stance":"aggressive","combo":["jab","cross"],"trigger":"when_close","used":"<rule id, demo or none>","say":"Testing his guard"}\n` +
    `stance: aggressive|defensive|counter. combo: 1-4 move NAMES. trigger: now|after_block|when_close. ` +
    `used: the rule id you follow (e.g. counter#1), "demo" if you copy your coach's moves, or "none". say: your own thought, max 8 words.`;
  const hints = ruleHints(bot.brain, s);
  const user = `Fight state: ${JSON.stringify(s)}${hints.length ? `\nNote: ${hints.join("; ")}` : ""}${rc ? `\n${rc.block}` : ""}` +
    (camp ? `\nYour fight-camp instincts (your own model, fine-tuned on your coach's sparring) say: ${camp.moves.join(" ")}. Use them unless a taught rule fits better.` : "");
  if (env.LOG_PROMPTS) console.log(`[decide prompt] ${bot.botId}\n${user}`);
  try {
    const { json, trace: tr } = await chatJson(system, user, DECIDE_MS, 110, 0.4, bot);
    const plan = validatePlan(json);
    if (!plan) return { ...SAFE_STOP(`invalid plan from model: ${JSON.stringify(json).slice(0, 160)}`), requestId: tr.requestId };
    // A taught rule wins attribution; else the coach's demo if the combo plays it.
    const fromCamp = camp && plan.combo!.slice(0, camp.moves.length).join(" ") === camp.moves.join(" ");
    return { ...plan, ...(resolveUsed(bot.brain, (json as any).used, plan.combo!) ?? demoUsed(rc, plan.combo!)),
      ...(fromCamp ? { used: "adapter", usedText: `Fight camp v${bot.adapter!.version}: ${camp!.moves.join(" ")} (my own model)` } : {}),
      ...(camp ? { camp: camp.moves, campMs: camp.ms } : {}), ...tr };
  } catch (e: any) {
    return SAFE_STOP(e?.name === "AbortError" || /timed? ?out|abort/i.test(String(e?.message)) ? "timeout" : `0G error: ${e?.message ?? e}`);
  }
}

export async function transcribe(audio: Blob, mockText?: string, b?: Bot): Promise<{ text: string; requestId: string | null; mock?: boolean }> {
  if (!ai) return { text: mockText || "block", requestId: null, mock: true };
  // 0G's router dropped whisper (Nov 6); qwen omni models take audio as a chat input. The provider wants a data URL.
  const b64 = Buffer.from(await audio.arrayBuffer()).toString("base64");
  const { data, response } = await (clientFor(b) ?? ai).chat.completions
    .create({
      model: env.ZG_STT_MODEL ?? "qwen3-omni-flash", max_tokens: 80, temperature: 0,
      messages: [{ role: "user", content: [
        { type: "input_audio", input_audio: { data: `data:audio/wav;base64,${b64}`, format: "wav" } },
        { type: "text", text: "Transcribe this boxing coach's words exactly. Reply with the transcript only." },
      ] }],
    } as any, { signal: AbortSignal.timeout(15000) })
    .withResponse();
  charge(b, data);
  const text = String((data as any).choices?.[0]?.message?.content ?? "").trim().replace(/^["']|["']$/g, "");
  return { text, requestId: trace(data, response.headers).requestId };
}

// Training: classify one coach line into a structured brain update (validated strictly in game.ts).
const LEARN_SYSTEM = `A robot boxer's coach said one line in training. ${moveSheet()}\n` +
  `Turn it into ONE brain update. Reply with ONLY JSON, one of:\n` +
  `{"kind":"counter","when":"<opponent attack move>","moves":[1-4 moves]}  // what to do right after the opponent throws that move\n` +
  `{"kind":"style","aggression":-0.2..0.2,"guard":-0.2..0.2}  // be more/less aggressive or careful\n` +
  `{"kind":"phrase","means":"max 8 words","moves":[1-4 moves]}  // a catchphrase that means a combo\n` +
  `{"kind":"combo","use":"opener|finisher|any","name":"2-4 words","moves":[2-4 moves]}  // use=opener only if told to start/open with it, finisher only if told to finish with it, else any\n` +
  `{"kind":"none"}  // not a boxing instruction\n` +
  `A line about reacting to the opponent ("when he throws/does X, ...", "if he jabs, ...", "after his hook ...") is ALWAYS a counter: ` +
  `"when" = the opponent's attack move (jab|cross|hook_l|hook_r|uppercut|body; "left hook"=hook_l, "right hook"=hook_r), "moves" = our response ("slip right"=dodge_r, "slip left"=dodge_l, "duck"=dodge_l).\n` +
  `Slang or a nickname for attacking ("light him up") is a phrase.`;
export async function learn(text: string, b?: Bot): Promise<{ learned: Learned | null; requestId: string | null; mock?: boolean; error?: string }> {
  if (!ai) return { learned: validateLearn(mockLearn(text)), requestId: null, mock: true };
  try {
    const { json, trace: tr } = await chatJson(LEARN_SYSTEM, `Coach said: "${text}"`, 6000, 80, 0, b);
    return { learned: validateLearn(json), requestId: tr.requestId };
  } catch (e: any) {
    return { learned: null, requestId: null, error: `0G error: ${e?.message ?? e}` };
  }
}

type HitLog = { attacker: string; defender: string; move: string; result: string }[];
export async function lessons(bots: Bot[], hits: HitLog, decisions: unknown[]) {
  const fallback = () => ({ lessons: Object.fromEntries(bots.map((b) => [b.botId, mockLessons(b.botId, hits)])), scouting: {} as Record<string, string[]>, requestId: null, mock: true });
  if (!ai) return fallback();
  const system = `You are a boxing coach for robot boxers ${bots.map((b) => `${b.botId} (${b.name})`).join(" and ")}. ${moveSheet()}\n` +
    `From the fight log give each bot 1-3 short lessons (max 10 words each), and 1-2 scouting notes about its opponent. ` +
    `Reply with ONLY JSON: {"lessons":{"<botId>":["..."]},"scouting":{"<botId>":["note about the opponent"]}}`;
  try {
    const { json, trace: tr } = await chatJson(system, JSON.stringify({ hits: hits.slice(-80), decisions: decisions.slice(-40) }), 10000);
    const j = json as any, out: Record<string, string[]> = {}, sc: Record<string, string[]> = {};
    const strs = (x: unknown, n: number) => (Array.isArray(x) ? x.filter((s) => typeof s === "string").map((s) => s.slice(0, 100)).slice(0, n) : []);
    for (const b of bots) { out[b.botId] = strs(j?.lessons?.[b.botId], 3); sc[b.botId] = strs(j?.scouting?.[b.botId], 2); }
    if (!Object.values(out).some((l) => l.length)) return fallback();
    return { lessons: out, scouting: sc, requestId: tr.requestId };
  } catch {
    return fallback();
  }
}
