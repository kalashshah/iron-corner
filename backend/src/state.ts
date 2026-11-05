// Env, modes and the one JSON state file. Everything else imports from here.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import crypto from "node:crypto";

const ROOT = path.resolve(import.meta.dirname, "..");
for (const f of [path.join(ROOT, ".env"), path.join(os.homedir(), ".iron-corner/wallets.env")]) {
  if (fs.existsSync(f)) process.loadEnvFile(f); // never overrides vars already set
}
export const env = process.env;

type Mode = "live" | "mock";
function mode(name: string, hasCreds: boolean): Mode {
  const want = env[name];
  if (want === "mock") return "mock";
  if (want === "live" && !hasCreds) throw new Error(`${name}=live but credentials are missing`);
  return hasCreds ? "live" : "mock";
}
export const modes = {
  zg: mode("ZG_MODE", !!env.ZG_API_KEY),
  storage: mode("ZG_STORAGE_MODE", !!env.OG_OPS_PRIVATE_KEY),
  hedera: mode("HEDERA_MODE", !!(env.HEDERA_OPERATOR_PRIVATE_KEY && env.BOT_A_PRIVATE_KEY && env.BOT_B_PRIVATE_KEY)),
  world: mode("WORLD_MODE", !!env.WORLD_CLIENT_ID),
};
export const PORT = Number(env.PORT ?? 8787);
export const SELF_URL = env.SELF_URL ?? `http://127.0.0.1:${PORT}`;

// ---- types ----
export type Phrase = { say: string; means: string; moves: string[] };
export type Brain = {
  botId: string; version: number; savedAt?: string;
  phrases: Phrase[]; lessons: string[]; scouting: Record<string, string[]>;
  style: { aggression: number; guard: number };
};
export type Bot = {
  botId: string; name: string; house: boolean;
  accountId: string | null; evmAddress: string | null;
  alive: boolean; ownerSub: string | null;
  koStakes?: "normal" | "high"; koAt?: number;
  brain: Brain;
  brainHistory: { version: number; root: string; txHash: string; at: string; mock?: boolean }[];
};
export type Payment = {
  hitNo: number; from: string; to: string; cents: number;
  status: "pending" | "settled" | "failed"; txId: string | null; hashscan: string | null;
  error?: string; mock?: boolean;
};
export type Fight = {
  fightId: string; a: string; b: string; stakes: "normal" | "high";
  status: "live" | "over"; budgets: Record<string, number>; startBudget: number;
  winner: string | null; payments: Payment[];
  decisions: { botId: string; requestId: string | null; stance: string; at: string; mock?: boolean }[];
  hits: { hitNo: number; attacker: string; defender: string; move: string; result: string; cents: number; at: string }[];
  overrides: Record<string, string[]>;
  ledgerTopic: string | null; lessons?: Record<string, string[]>; createdAt: string;
  episode?: { root: string; txHash: string; mock?: boolean };
};
export type Approval = {
  approvalId: string; botId: string; action: "high_stakes" | "revive" | "cash_out";
  status: "pending" | "approved" | "denied" | "expired" | "cancelled" | "used";
  qrUri: string; userCode: string; expiresAt: string; startedAt: number; sub?: string; error?: string; mock?: boolean;
};
type State = {
  bots: Record<string, Bot>; fights: Record<string, Fight>; approvals: Record<string, Approval>;
  ledgerTopic: string | null; brainSecret: string; mockBlobs: Record<string, string>;
};

// ---- persistence ----
const DATA = path.join(ROOT, "data");
const FILE = path.join(DATA, env.STATE_FILE ?? "state.json");
fs.mkdirSync(DATA, { recursive: true });

function newBrain(botId: string, aggression: number, guard: number): Brain {
  return { botId, version: 0, phrases: [], lessons: [], scouting: {}, style: { aggression, guard } };
}
function seed(): State {
  const bot = (botId: string, name: string, house: boolean, ag: number, g: number): Bot => ({
    botId, name, house, accountId: null, evmAddress: null, alive: true, ownerSub: null,
    brain: newBrain(botId, ag, g), brainHistory: [],
  });
  return {
    bots: { bot_a: bot("bot_a", "Brass Knuckle", false, 0.6, 0.5), bot_b: bot("bot_b", "Rustbucket", true, 0.4, 0.6) },
    fights: {}, approvals: {}, ledgerTopic: null,
    brainSecret: crypto.randomBytes(32).toString("hex"), mockBlobs: {},
  };
}
// STATE_SEED (base64 state.json) lets a fresh deploy reuse existing bot accounts + brain key.
if (!fs.existsSync(FILE) && env.STATE_SEED) fs.writeFileSync(FILE, Buffer.from(env.STATE_SEED, "base64"));
export const state: State = fs.existsSync(FILE) ? JSON.parse(fs.readFileSync(FILE, "utf8")) : seed();
// Phrases learned while 0G ran in mock mode are placeholders; drop them once inference is live.
if (modes.zg === "live") for (const b of Object.values(state.bots)) b.brain.phrases = b.brain.phrases.filter((p) => !p.means.startsWith("mock:"));

let timer: NodeJS.Timeout | null = null;
export function save() {
  if (timer) return;
  timer = setTimeout(() => {
    timer = null;
    fs.writeFileSync(FILE + ".tmp", JSON.stringify(state, null, 1));
    fs.renameSync(FILE + ".tmp", FILE);
  }, 200);
}
save();

export const id = (p: string) => `${p}_${crypto.randomBytes(5).toString("hex")}`;
export class HttpError extends Error { constructor(public status: number, msg: string) { super(msg); } }
export const fail = (status: number, msg: string): never => { throw new HttpError(status, msg); };
