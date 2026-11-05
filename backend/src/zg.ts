// 0G Compute router (OpenAI-compatible): whisper transcription + qwen-flash decisions.
import OpenAI, { toFile } from "openai";
import { env, modes, type Bot, type Phrase } from "./state.ts";
import { MOVES, DAMAGE, SAFE_STOP, validatePlan, validatePhrase, mockPlan, mockPhrase, mockLessons, type Plan, type FightState } from "./game.ts";

const BASE = env.ZG_BASE_URL ?? "https://router-api.0g.ai/v1";
const CHAT_MODEL = env.ZG_CHAT_MODEL ?? "qwen-flash";
const DECIDE_MS = Number(env.DECIDE_TIMEOUT_MS ?? 4000); // Unity keeps playing the current combo while this is in flight
const ai = modes.zg === "live" ? new OpenAI({ apiKey: env.ZG_API_KEY, baseURL: BASE, maxRetries: 0, defaultHeaders: { "X-0G-Provider-Sort": "latency" } }) : null;

type Trace = { requestId: string | null; teeVerified: boolean | null };
function trace(body: any, headers: Headers): Trace {
  const t = body?.x_0g_trace ?? {};
  return { requestId: t.request_id ?? headers.get("x-request-id"), teeVerified: t.tee_verified ?? body?.tee_verified ?? null };
}

async function chatJson(system: string, user: string, timeoutMs: number): Promise<{ json: unknown; trace: Trace }> {
  const { data, response } = await ai!.chat.completions
    .create(
      // qwen-flash has no provider for response_format (503 "No available providers"), so ask for JSON in the
      // prompt and pull the object out of any ``` fences. Strict validation happens in validatePlan/validatePhrase.
      { model: CHAT_MODEL, messages: [{ role: "system", content: system }, { role: "user", content: user }],
        temperature: 0.4, max_tokens: 160, verify_tee: true } as any,
      { signal: AbortSignal.timeout(timeoutMs) },
    )
    .withResponse();
  const text: string = (data as any).choices?.[0]?.message?.content ?? "";
  const obj = text.slice(text.indexOf("{"), text.lastIndexOf("}") + 1);
  return { json: JSON.parse(obj), trace: trace(data, response.headers) };
}

const moveSheet = () =>
  `Moves: ${MOVES.join(", ")}. Damage in cents per clean hit: ${Object.entries(DAMAGE).map(([m, d]) => `${m} ${d}`).join(", ")}. ` +
  `block chips guard and stops damage; dodge_l/dodge_r make the hit miss; step_in/step_back change distance (1 = punching range).`;

const brainSheet = (bot: Bot, opp?: string) => {
  const b = bot.brain;
  return `You are ${bot.name}, a robot boxer. Style: aggression ${b.style.aggression}, guard ${b.style.guard}.\n` +
    `Coach phrases: ${JSON.stringify(b.phrases)}\nLessons: ${JSON.stringify(b.lessons)}\n` +
    (opp ? `Scouting on opponent: ${JSON.stringify(b.scouting[opp] ?? [])}\n` : "");
};

export async function decide(bot: Bot, s: FightState, opp?: string): Promise<Plan> {
  if (!ai) return mockPlan(bot.brain, s);
  const system = `${brainSheet(bot, opp)}${moveSheet()}\n` +
    `Budgets are health in cents. Reply with ONLY a JSON object: {"stance":"aggressive|defensive|counter","combo":[1-4 moves],"trigger":"now|after_block|when_close","say":"short thought, max 8 words"}`;
  try {
    const { json, trace: tr } = await chatJson(system, `Fight state: ${JSON.stringify(s)}`, DECIDE_MS);
    const plan = validatePlan(json);
    if (!plan) return { ...SAFE_STOP("invalid plan from model"), requestId: tr.requestId };
    return { ...plan, ...tr };
  } catch (e: any) {
    return SAFE_STOP(e?.name === "AbortError" || /timed? ?out|abort/i.test(String(e?.message)) ? "timeout" : `0G error: ${e?.message ?? e}`);
  }
}

export async function transcribe(audio: Blob, mockText?: string): Promise<{ text: string; requestId: string | null; mock?: boolean }> {
  if (!ai) return { text: mockText || "block", requestId: null, mock: true };
  const file = await toFile(Buffer.from(await audio.arrayBuffer()), "coach.wav", { type: "audio/wav" });
  let lastErr: unknown;
  for (const model of [env.ZG_STT_MODEL ?? "whisper-large-v3", "openai/whisper-large-v3"]) {
    try {
      const { data, response } = await ai.audio.transcriptions
        .create({ file, model }, { query: { verify_tee: true }, signal: AbortSignal.timeout(15000) })
        .withResponse();
      return { text: String((data as any).text ?? "").trim(), requestId: trace(data, response.headers).requestId };
    } catch (e: any) {
      lastErr = e;
      if (e?.status !== 400 && e?.status !== 404) break; // only retry the alternate id on a model-not-found style error
    }
  }
  throw lastErr;
}

export async function mapPhrase(bot: Bot, text: string): Promise<{ learned: Phrase | null; requestId: string | null; mock?: boolean }> {
  if (!ai) return { learned: mockPhrase(text), requestId: null, mock: true };
  const system = `${brainSheet(bot)}${moveSheet()}\nYour coach just said a phrase. Map it to a short combo. ` +
    `Reply with ONLY JSON: {"means":"what the coach wants, max 8 words","moves":[1-4 moves]} or {"moves":[]} if it is not a fighting instruction.`;
  try {
    const { json, trace: tr } = await chatJson(system, `Coach said: "${text}"`, 6000);
    return { learned: validatePhrase(json, text), requestId: tr.requestId };
  } catch {
    return { learned: null, requestId: null };
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
