// 0G Agentic ID: one IronCornerAgent token (ERC-7857 style, 0G Galileo) per player bot, minted to the bot's EVM address.
// Data entries: "brain" = latest encrypted brain root on 0G Storage (zero until the first save), plus "adapter vN" once
// a fight camp delivered one. Every brain save records the new root/version/level (recordTraining, trainer = OG_OPS).
// Failures never break the caller: they set `retry` and a timer tries again.
import { ethers } from "ethers";
import { env, modes, state, save, type Bot, type AgentId } from "./state.ts";
import { progressView } from "./demos.ts";

export const CONTRACT = env.AGENT_CONTRACT ?? "0x4Ed79445a389C9442657773138e2ffFabb25d170";
const RPC = env.ZG_RPC ?? "https://evmrpc-testnet.0g.ai";
export const chainscan = (tx: string) => `https://chainscan-galileo.0g.ai/tx/${tx}`;
const ABI = [
  "function mint(address to, (string dataDescription, bytes32 dataHash)[] datas, uint256 version, uint16 lvl) returns (uint256)",
  "function recordTraining(uint256 id, (string dataDescription, bytes32 dataHash)[] datas, uint256 version, uint16 lvl)",
  "event Trained(uint256 indexed tokenId, uint256 version, uint16 level, bytes32 brainRoot)",
];
const live = modes.storage === "live"; // same OG_OPS key as storage
let c: ethers.Contract | null = null;
const contract = () => (c ??= new ethers.Contract(CONTRACT, ABI, new ethers.Wallet(env.OG_OPS_PRIVATE_KEY!, new ethers.JsonRpcProvider(RPC))));

// One tx at a time from this wallet (nonces). ponytail: storage uploads use their own signer on the same key; a nonce
// clash there just lands in `retry`.
let chain: Promise<unknown> = Promise.resolve();
const serial = <T>(fn: () => Promise<T>): Promise<T> => { const p = chain.then(fn); chain = p.catch(() => {}); return p; };

const h32 = (x: string | null | undefined) => (x && ethers.isHexString(x, 32) ? x : ethers.ZeroHash);
function datas(b: Bot) {
  return [{ dataDescription: "brain", dataHash: h32(b.brainHistory.at(-1)?.root) },
    ...(b.adapter ? [{ dataDescription: `adapter v${b.adapter.version}`, dataHash: h32(b.adapter.root) }] : [])];
}
const lvl = (b: Bot) => Math.min(65535, progressView(b).level);

async function mint(b: Bot, a: AgentId) {
  if (!live) { Object.assign(a, { tokenId: String(Object.values(state.bots).filter((x) => x.agentId?.tokenId).length + 1), tx: `mock-mint-${b.botId}`, mock: true }); return; }
  const tx = await contract().mint(b.evmAddress, datas(b), 0, lvl(b));
  a.tx = tx.hash; save(); // keep the hash even if the receipt wait fails
  const rc = await tx.wait();
  const ev = rc.logs.map((l: any) => { try { return contract().interface.parseLog(l); } catch { return null; } }).find((e: any) => e?.name === "Trained");
  if (!ev) throw new Error("mint receipt has no Trained event");
  a.tokenId = ev.args.tokenId.toString();
  console.log(`[agentid] ${b.botId} minted token ${a.tokenId} ${chainscan(tx.hash)}`);
}
async function record(b: Bot, a: AgentId) {
  const version = b.brain.version;
  if (version <= a.version) return;
  let hash: string;
  if (!live) hash = `mock-record-${b.botId}-${version}`;
  else {
    const tx = await contract().recordTraining(a.tokenId, datas(b), version, lvl(b));
    await tx.wait(); hash = tx.hash;
    console.log(`[agentid] ${b.botId} token ${a.tokenId} v${version} ${chainscan(hash)}`);
  }
  a.version = version; a.records.push({ version, tx: hash, at: new Date().toISOString() });
}

// Mint if needed, then record the latest brain. Fire and forget; returns when done (for tests).
export function sync(b: Bot): Promise<void> {
  if (!b.owner || !b.evmAddress) return Promise.resolve(); // demo/house bots have no Agentic ID
  const a = (b.agentId ??= { contract: CONTRACT, tokenId: null, tx: null, version: 0, records: [], retry: "mint" });
  return serial(async () => {
    for (let attempt = 0; ; attempt++) {
      try {
        if (!a.tokenId) { a.retry = "mint"; await mint(b, a); }
        a.retry = "record"; await record(b, a);
        a.retry = null; delete a.error;
        break;
      } catch (e: any) {
        a.error = String(e?.shortMessage ?? e?.message ?? e).slice(0, 200);
        console.warn(`[agentid] ${b.botId} ${a.retry} failed: ${a.error}`);
        // A storage upload on the same key took our nonce: try again shortly instead of waiting for the timer.
        if (attempt >= 3 || !/nonce|replacement|already known/i.test(a.error)) break;
        await new Promise((r) => setTimeout(r, 5000));
      } finally { save(); }
    }
  });
}
export const view = (b: Bot) => b.agentId ? { tokenId: b.agentId.tokenId, contract: b.agentId.contract, tx: b.agentId.tx,
  chainscan: b.agentId.tx && !b.agentId.mock ? chainscan(b.agentId.tx) : null, version: b.agentId.version, retry: b.agentId.retry,
  ...(b.agentId.mock ? { mock: true } : {}) } : null;

// ponytail: a mint whose receipt wait failed after the tx landed gets minted again on retry (a spare token, harmless on testnet).
setInterval(() => { for (const b of Object.values(state.bots)) if (b.agentId?.retry) void sync(b); }, Number(env.AGENTID_RETRY_MS ?? 60_000)).unref();
