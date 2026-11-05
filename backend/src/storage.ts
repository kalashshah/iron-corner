// Brain persistence on 0G Storage, AES-256 encrypted with a per-bot key derived from a server secret.
import crypto from "node:crypto";
import { env, modes, state, save, type Bot, type Brain } from "./state.ts";
import { progressView } from "./demos.ts";

const RPC = env.ZG_RPC ?? "https://evmrpc-testnet.0g.ai";
const INDEXER = env.ZG_INDEXER ?? "https://indexer-storage-testnet-turbo.0g.ai";

// Only this server can read a bot's brain: key = HKDF(server secret, botId).
const keyFor = (botId: string) =>
  Buffer.from(crypto.hkdfSync("sha256", env.BRAIN_SECRET ?? state.brainSecret, "iron-corner-brain", botId, 32));

let sdk: Promise<{ idx: any; signer: any; MemData: any }> | null = null;
function live() {
  sdk ??= (async () => {
    const { Indexer, MemData } = await import("@0gfoundation/0g-storage-ts-sdk");
    const { ethers } = await import("ethers");
    const signer = new ethers.Wallet(env.OG_OPS_PRIVATE_KEY!, new ethers.JsonRpcProvider(RPC));
    return { idx: new Indexer(INDEXER), signer, MemData };
  })();
  return sdk;
}

// Mock: local AES-256-GCM so the mock path still proves the key separation.
function mockEncrypt(botId: string, bytes: Buffer) {
  const iv = crypto.randomBytes(12), c = crypto.createCipheriv("aes-256-gcm", keyFor(botId), iv);
  return Buffer.concat([iv, c.update(bytes), c.final(), c.getAuthTag()]);
}
function mockDecrypt(botId: string, blob: Buffer) {
  const d = crypto.createDecipheriv("aes-256-gcm", keyFor(botId), blob.subarray(0, 12));
  d.setAuthTag(blob.subarray(-16));
  return Buffer.concat([d.update(blob.subarray(12, -16)), d.final()]);
}

export async function saveBrain(bot: Bot) {
  const brain: Brain = { ...bot.brain, botId: bot.botId, version: bot.brain.version + 1, savedAt: new Date().toISOString() };
  // The encrypted blob is the whole learned mind: rules + the shadow-sparring demo library + a growth snapshot,
  // so each version proves what the bot knew and how good it was at that point.
  const g = progressView(bot), growth = { level: g.level, xp: g.xp, stats: g.stats, demos: g.demos };
  const bytes = Buffer.from(JSON.stringify({ ...brain, demos: bot.demos ?? [], growth })); // version + timestamp make every save unique (0G skips identical bytes)
  let root: string, txHash: string, mock: boolean | undefined;
  if (modes.storage === "live") {
    const { idx, signer, MemData } = await live();
    const f = new MemData(bytes);
    const [, treeErr] = await f.merkleTree();
    if (treeErr) throw treeErr;
    const [res, err] = await idx.upload(f, RPC, signer, { encryption: { type: "aes256", key: keyFor(bot.botId) } });
    if (err) throw err;
    root = res.rootHash; txHash = res.txHash;
  } else {
    const blob = mockEncrypt(bot.botId, bytes);
    root = "0x" + crypto.createHash("sha256").update(blob).digest("hex");
    txHash = "mock-" + root.slice(2, 18); mock = true;
    state.mockBlobs[root] = blob.toString("base64");
  }
  bot.brain.version = brain.version; bot.brain.savedAt = brain.savedAt;
  const entry = { version: brain.version, root, txHash, at: brain.savedAt!, level: growth.level, stats: growth.stats, demos: growth.demos, ...(mock ? { mock } : {}) };
  bot.brainHistory.push(entry);
  save();
  return entry;
}

// Download + decrypt a stored brain (proves the round trip; wrong key → error, never ciphertext).
export async function loadBrain(botId: string, root: string): Promise<Brain> {
  let bytes: Buffer;
  if (state.mockBlobs[root]) bytes = mockDecrypt(botId, Buffer.from(state.mockBlobs[root], "base64"));
  else {
    if (modes.storage !== "live") throw new Error("unknown root (mock storage)");
    const { idx } = await live();
    const [blob, err] = await idx.downloadToBlob(root, { proof: true, decryption: { symmetricKey: keyFor(botId) } });
    if (err) throw err;
    bytes = Buffer.from(await blob.arrayBuffer());
  }
  const brain = JSON.parse(bytes.toString("utf8"));
  if (brain?.botId !== botId) throw new Error("brain does not belong to this bot");
  return brain;
}

// Fight episode log (public, unencrypted): every decision with its 0G request id, every hit and payment.
// Uploaded once per fight so the fight can be replayed and audited from 0G Storage alone.
export async function saveEpisode(episode: object): Promise<{ root: string; txHash: string; mock?: boolean }> {
  const bytes = Buffer.from(JSON.stringify({ ...episode, savedAt: new Date().toISOString() }));
  if (modes.storage !== "live") {
    const root = "0x" + crypto.createHash("sha256").update(bytes).digest("hex");
    state.mockBlobs[root] = bytes.toString("base64"); save();
    return { root, txHash: "mock-" + root.slice(2, 18), mock: true };
  }
  const { idx, signer, MemData } = await live();
  const f = new MemData(bytes);
  const [, treeErr] = await f.merkleTree();
  if (treeErr) throw treeErr;
  const [res, err] = await idx.upload(f, RPC, signer);
  if (err) throw err;
  return { root: res.rootHash, txHash: res.txHash };
}

export async function loadEpisode(root: string): Promise<unknown> {
  if (state.mockBlobs[root]) return JSON.parse(Buffer.from(state.mockBlobs[root], "base64").toString("utf8"));
  if (modes.storage !== "live") throw new Error("unknown root (mock storage)");
  const { idx } = await live();
  const [blob, err] = await idx.downloadToBlob(root, { proof: true });
  if (err) throw err;
  return JSON.parse(Buffer.from(await blob.arrayBuffer()).toString("utf8"));
}
