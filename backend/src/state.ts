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
};
export const PORT = Number(env.PORT ?? 8787);
export const SELF_URL = env.SELF_URL ?? `http://127.0.0.1:${PORT}`;

// ---- types ----
export type Phrase = { say: string; means: string; moves: string[] };
// Tactical rules taught in training. `when` is an opponent attack; `use` says when a combo fits.
export type Counter = { when: string; moves: string[]; said: string };
export type Combo = { use: "opener" | "finisher" | "any"; name: string; moves: string[]; said: string };
export type Brain = {
  botId: string; version: number; savedAt?: string;
  phrases: Phrase[]; counters: Counter[]; combos: Combo[]; lessons: string[]; scouting: Record<string, string[]>;
  style: { aggression: number; guard: number };
};
// Daily training goal: `drills` coaching calls on `day` (UTC); `goalDay` = last day the goal was hit.
export type Daily = { day: string; drills: number; streak: number; goalDay: string | null };
// One shadow-sparring demonstration (or a fight decision, src "fight"): features of the moment, the move, how it went.
// f = [distance/3, lead, selfGuard, oppGuard, sinceOppAttack/5]; o1/o2 = opponent's last moves; m1 = own last move.
// mo = [self, opp] budgets in cents (camp datasets put money in words); older demos lack it.
export type Demo = { at: number; f: number[]; o1: string | null; o2: string | null; m1: string | null; mo?: number[]; a: string; out: string; w: number; src: "train" | "fight"; r?: string };
// XP total + today's counters (energy = 5 - rounds); `round` = the sparring round in progress.
export type Progress = { xp: number; day: string; rounds: number; fights: number; round?: { id: string; startedAt: number; demos: number } };
export type Bot = {
  botId: string; name: string; house: boolean;
  accountId: string | null; evmAddress: string | null;
  alive: boolean; ownerSub: string | null; nft?: { serial: number; tx: string };
  ens?: { label: string; name: string; resolver: string | null; registeredTx: string | null; rolesTx: string | null; synced: Record<string, string>; lastSyncTx?: string };
  koStakes?: "normal" | "high"; koAt?: number;
  brain: Brain; daily?: Daily; demos?: Demo[]; progress?: Progress;
  brainHistory: { version: number; root: string; txHash: string; at: string; mock?: boolean; level?: number; stats?: Record<string, number>; demos?: number }[];
  // ---- player-owned bots (docs/flow-spec.md); demo/house bots have no owner ----
  owner?: string | null; paint?: string; rating?: number; record?: { w: number; l: number };
  onboarding?: { round: boolean; coached: boolean; funded: boolean };
  hkey?: string;          // sealed Hedera private key (AES-256-GCM, server secret); never sent out
  payAs?: string;         // house extras pay from this bot's Hedera account
  agentId?: AgentId;
  fund?: { day: string; hbar: number; txId: string; key?: string };
  lastOpp?: string | null; lastMatch?: { fightId: string; at: number };
  adapter?: { version: number; taskId: string; root: string | null; loss: number | null; at: string; mock?: boolean };
  campId?: string;
  // the owner's own 0G account (zgaccount.ts): sealed sk- key, what its calls cost
  zg?: { key: string; last4: string; wallet: string | null; privyUser: string; at: string; spentMicro: number; calls: number };
};
export type AgentId = {
  contract: string; tokenId: string | null; tx: string | null; version: number; // version = last brain version recorded on chain
  records: { version: number; tx: string; at: string }[]; retry: "mint" | "record" | null; error?: string; mock?: boolean;
};
export type User = { userId: string; tokenHash: string; createdAt: string; botId: string | null };
export type QEntry = { botId: string; ownerId: string; league: string; rating: number; level: number; stakeCents: number; joinedAt: number };
export type FightEvent = { seq: number; t: number; type: string; [k: string]: unknown };
export type Camp = {
  campId: string; botId: string; day: string; status: "queued" | "uploading" | "training" | "delivered" | "downloading" | "done" | "failed";
  taskId: string | null; progress: string | null; version: number | null; adapterRoot: string | null; loss: number | null;
  examples: number; startedAt: string; error?: string; mock?: boolean;
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
  decisions: { botId: string; requestId: string | null; stance: string; used?: string; at: string; mock?: boolean; outcome?: string }[];
  hits: { hitNo: number; attacker: string; defender: string; move: string; result: string; cents: number; at: string }[];
  ledgerTopic: string | null; lessons?: Record<string, string[]>; createdAt: string;
  episode?: { root: string; txHash: string; mock?: boolean };
  scouts?: { by: string; of: string; cents: number; tapes: string[]; notes: string[]; txId: string; hashscan: string | null; mock?: boolean }[];
  // server-authoritative (engine) fights only
  engine?: boolean; house?: boolean; stakeCents?: number; seed?: string; seedHash?: string; events?: FightEvent[];
  endReason?: string; ratingDelta?: Record<string, number>;
};
type State = {
  bots: Record<string, Bot>; fights: Record<string, Fight>; hts?: { tokenId: string; belt: { serial: number; holder: string | null; tx: string | null } };
  ens?: { parent: string; registry: string | null; resolver: string | null; registered: { tx: string; at: string } | null; commit: { secret: string; at: number } | null };
  ledgerTopic: string | null; brainSecret: string; mockBlobs: Record<string, string>;
  users: Record<string, User>; queue: QEntry[]; camps: Record<string, Camp>; mockHbar: Record<string, number>;
};

// ---- persistence ----
const DATA = path.join(ROOT, "data");
const FILE = path.join(DATA, env.STATE_FILE ?? "state.json");
fs.mkdirSync(DATA, { recursive: true });

export function newBrain(botId: string, aggression: number, guard: number): Brain {
  return { botId, version: 0, phrases: [], counters: [], combos: [], lessons: [], scouting: {}, style: { aggression, guard } };
}
function seed(): State {
  const bot = (botId: string, name: string, house: boolean, ag: number, g: number): Bot => ({
    botId, name, house, accountId: null, evmAddress: null, alive: true, ownerSub: null,
    brain: newBrain(botId, ag, g), brainHistory: [],
  });
  return {
    bots: { bot_a: bot("bot_a", "Brass Knuckle", false, 0.6, 0.5), bot_b: bot("bot_b", "Rustbucket", true, 0.4, 0.6) },
    fights: {}, ledgerTopic: null, users: {}, queue: [], camps: {}, mockHbar: {},
    brainSecret: crypto.randomBytes(32).toString("hex"), mockBlobs: {},
  };
}
// STATE_SEED (base64 state.json) lets a fresh deploy reuse existing bot accounts + brain key.
if (!fs.existsSync(FILE) && env.STATE_SEED) fs.writeFileSync(FILE, Buffer.from(env.STATE_SEED, "base64"));
export const state: State = fs.existsSync(FILE) ? JSON.parse(fs.readFileSync(FILE, "utf8")) : seed();
for (const b of Object.values(state.bots)) { b.brain.counters ??= []; b.brain.combos ??= []; } // state files from before rules existed
state.users ??= {}; state.queue ??= []; state.camps ??= {}; state.mockHbar ??= {}; // ...and before players existed
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
// `extra` is merged into the JSON error body (e.g. {reason: "needs_training"}).
export class HttpError extends Error { constructor(public status: number, msg: string, public extra?: Record<string, unknown>) { super(msg); } }
export const fail = (status: number, msg: string, extra?: Record<string, unknown>): never => { throw new HttpError(status, msg, extra); };
// 409 whose message is a machine-readable reason, as docs/flow-spec.md shows them.
export const refuse = (reason: string, extra?: Record<string, unknown>): never => fail(409, reason, { reason, ...extra });
