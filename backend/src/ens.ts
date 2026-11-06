// ENSv2 on Sepolia: every fighter is a name under ironcorner.eth (kal.ironcorner.eth), owned by the bot's own key.
// Each name gets its own PermissionedResolver, and Enhanced Access Control splits who may write what:
//   the arena (our server wallet) may write only the fight keys (record, rating, brain, ...)
//   the bot itself may write only "description" and "avatar"
// so the server can't rewrite how a fighter describes itself, and a fighter can't fake its record.
// Setup (once, in the background on boot): a UserRegistry for subnames, a resolver for the parent, then
// commit/reveal-register ironcorner.eth with that registry (paid in Sepolia MockUSDC).
import crypto from "node:crypto";
import { ethers } from "ethers";
import { env, state, save, fail, SELF_URL, type Bot } from "./state.ts";

export const ENS_LIVE = env.ENS_MODE === "live" && !!env.OG_OPS_PRIVATE_KEY;
const PARENT = env.ENS_PARENT ?? "ironcorner";
const A = { // live Sepolia ENSv2 set (contracts-v2 07e55a05, "Sepolia v2 redeploy (2026-10-01)")
  factory: "0xda70306c98e97ece36f997a21368e53298572991", resolverImpl: "0x115eb53f0c60696633855f90b138178fb40b2b2c",
  userRegistryImpl: "0x9bd8a88719068d09ecee662f36c0e3856708366a", registrar: "0xf633e7fc17e2bbe0d0965d18ec1821dcb754a3d3",
  ethRegistry: "0xd4ebcbbdf463c9c45784603db0ddd499bc44a8b4", usdc: "0x240b0316df57887dbbe58b586508b19e633a14aa",
  universal: "0xeEeEEEeE14D718C2B47D9923Deab1335E144EeEe",
};
const ABI = [
  "function deployProxy(address implementation, uint256 salt, bytes data) returns (address)",
  "event ProxyDeployed(address indexed sender, address indexed proxyAddress, uint256 salt, address implementation)",
  "function initialize((address account,uint256 roleBitmap)[] grants, bytes[] calls)",
  "function setText(bytes name, string key, string value)",
  "function setAddress(bytes name, uint256 coinType, bytes addressBytes)",
  "function grantSetterRoles(bytes setter, address account) returns (bool)",
  "function multicall(bytes[] calls) returns (bytes[])",
  "function register(string label, address owner, address registry, address resolver, uint256 roleBitmap, uint64 expiry) returns (uint256)",
  "function resolve(bytes name, bytes data) view returns (bytes, address)",
];
const iface = new ethers.Interface(ABI);
// initialize() is overloaded across the two impls; build each selector by hand.
const resolverInit = new ethers.Interface(["function initialize((address account,uint256 roleBitmap)[] grants, bytes[] calls)"]);
const registryInit = new ethers.Interface(["function initialize((address account,uint256 roleBitmap)[] grants)"]);
const ROLE_SET_ADDRESS = 1n << 0n, ROLE_SET_TEXT = 1n << 4n, ADMIN = (r: bigint) => r << 128n;
const ALL_ROLES = BigInt("0x" + "1".repeat(64));
export const SERVER_KEYS = ["ironcorner.record", "ironcorner.rating", "ironcorner.brain", "ironcorner.agentid", "ironcorner.hedera", "ironcorner.nft"];
export const OWNER_KEYS = ["description", "avatar"];

const provider = new ethers.JsonRpcProvider(env.SEPOLIA_RPC ?? "https://ethereum-sepolia-rpc.publicnode.com");
const ops = ENS_LIVE ? new ethers.Wallet(env.OG_OPS_PRIVATE_KEY!, provider) : null;
const dns = (name: string) => ethers.dnsEncode(name);
const at = (addr: string, signer: ethers.ContractRunner | null = ops) => new ethers.Contract(addr, ABI, signer);
const etherscan = (tx: string) => `https://sepolia.etherscan.io/tx/${tx}`;

// One queue for the server wallet so nonces never collide.
let q: Promise<unknown> = Promise.resolve();
function serial<T>(fn: () => Promise<T>): Promise<T> { const p = q.then(fn, fn); q = p.catch(() => {}); return p; }
async function send(p: Promise<ethers.ContractTransactionResponse>) { const tx = await p; const rc = await tx.wait(); if (rc?.status !== 1) throw new Error(`tx ${tx.hash} reverted`); return rc!; }
async function deployProxy(impl: string, data: string) {
  const rc = await send(at(A.factory).deployProxy(impl, BigInt("0x" + crypto.randomBytes(31).toString("hex")), data));
  const ev = rc.logs.map((l) => { try { return iface.parseLog(l); } catch { return null; } }).find((e) => e?.name === "ProxyDeployed");
  return ev!.args.proxyAddress as string;
}

// Lowercase letters, digits and dashes; unique among our fighters.
export function labelFor(name: string, taken: string[]) {
  const base = name.toLowerCase().trim().replace(/\s+/g, "-").replace(/[^a-z0-9-]/g, "").replace(/^-+|-+$/g, "").slice(0, 20) || "fighter";
  let l = base;
  for (let i = 2; taken.includes(l); i++) l = `${base}-${i}`;
  return l;
}

// ---- setup: registry + parent resolver + ironcorner.eth (each step saved, so a restart resumes) ----
export async function setup() {
  if (!ENS_LIVE) return;
  const s = (state.ens ??= { parent: `${PARENT}.eth`, registry: null, resolver: null, registered: null, commit: null });
  const me = ops!.address;
  if (!s.registry) { s.registry = await serial(() => deployProxy(A.userRegistryImpl, registryInit.encodeFunctionData("initialize", [[[me, ALL_ROLES]]]))); save(); console.log(`[ens] user registry ${s.registry}`); }
  if (!s.resolver) {
    const n = dns(s.parent);
    const calls = [iface.encodeFunctionData("setText", [n, "description", "Iron Corner: robot boxers trained by voice. Every fighter has a name here."]),
      iface.encodeFunctionData("setText", [n, "url", SELF_URL])];
    s.resolver = await serial(() => deployProxy(A.resolverImpl, resolverInit.encodeFunctionData("initialize", [[[me, ALL_ROLES]], calls]))); save();
    console.log(`[ens] parent resolver ${s.resolver}`);
  }
  if (s.registered) return;
  const year = 31_536_000n, registrar = new ethers.Contract(A.registrar, [
    "function makeCommitment(string label, address owner, bytes32 secret, address subregistry, address resolver, uint64 duration, bytes32 referrer) view returns (bytes32)",
    "function commit(bytes32 commitment)", "function getRegisterPrice(string label, uint64 duration, address paymentToken) view returns (uint256 base, uint256 premium)",
    "function register(string label, address owner, bytes32 secret, address subregistry, address resolver, uint64 duration, address paymentToken, bytes32 referrer) returns (uint256)",
  ], ops);
  if (!s.commit) {
    const secret = ethers.hexlify(crypto.randomBytes(32));
    const c = await registrar.makeCommitment(PARENT, me, secret, s.registry, s.resolver, year, ethers.ZeroHash);
    await serial(() => send(registrar.commit(c)));
    s.commit = { secret, at: Date.now() }; save();
  }
  await new Promise((r) => setTimeout(r, Math.max(0, s.commit!.at + 70_000 - Date.now())));
  const [base, premium] = await registrar.getRegisterPrice(PARENT, year, A.usdc);
  const cost = base + premium;
  const usdcAbi = new ethers.Contract(A.usdc, ["function mint(address to, uint256 amount)", "function approve(address spender, uint256 value) returns (bool)"], ops);
  await serial(() => send(usdcAbi.mint(me, cost)));
  await serial(() => send(usdcAbi.approve(A.registrar, cost)));
  const rc = await serial(() => send(registrar.register(PARENT, me, s.commit!.secret, s.registry, s.resolver, year, A.usdc, ethers.ZeroHash)));
  s.registered = { tx: rc.hash, at: new Date().toISOString() }; save();
  console.log(`[ens] registered ${s.parent} (${rc.hash})`);
}

// ---- per fighter ----
const fightKeys = (b: Bot): Record<string, string> => ({
  "ironcorner.record": `${b.record?.w ?? 0}-${b.record?.l ?? 0}`, "ironcorner.rating": String(b.rating ?? 1000),
  "ironcorner.brain": b.brainHistory.at(-1)?.root ?? "", "ironcorner.agentid": b.agentId?.tokenId ? `0G Agentic ID #${b.agentId.tokenId}` : "",
  "ironcorner.hedera": b.accountId ?? "", "ironcorner.nft": b.nft && state.hts ? `${state.hts.tokenId}/${b.nft.serial}` : "",
});

export async function issue(b: Bot) {
  if (!ENS_LIVE || b.ens?.registeredTx || b.payAs || !b.evmAddress || !state.ens?.registered) return;
  const label = b.ens?.label ?? labelFor(b.name, Object.values(state.bots).map((x) => x.ens?.label).filter(Boolean) as string[]);
  const name = `${label}.${state.ens.parent}`, n = dns(name), me = ops!.address;
  b.ens = { label, name, resolver: b.ens?.resolver ?? null, registeredTx: null, rolesTx: null, synced: {} }; save();
  if (!b.ens.resolver) {
    const keys = fightKeys(b), calls = [
      iface.encodeFunctionData("setAddress", [n, 60, b.evmAddress]),
      iface.encodeFunctionData("setText", [n, "description", `${b.name}, a robot boxer in Iron Corner.`]),
      iface.encodeFunctionData("setText", [n, "url", `${SELF_URL}/bots/${b.botId}`]),
      ...Object.entries(keys).filter(([, v]) => v).map(([k, v]) => iface.encodeFunctionData("setText", [n, k, v])),
    ];
    // The server holds only the admin side of text/address roles: it can hand out keys but not write any key it wasn't given.
    b.ens.resolver = await serial(() => deployProxy(A.resolverImpl, resolverInit.encodeFunctionData("initialize", [[[me, ADMIN(ROLE_SET_TEXT) | ADMIN(ROLE_SET_ADDRESS)]], calls])));
    b.ens.synced = keys; save();
  }
  const setter = (k: string) => iface.encodeFunctionData("setText", ["0x", k, ""]);
  const grants = [...SERVER_KEYS.map((k) => iface.encodeFunctionData("grantSetterRoles", [setter(k), me])),
    ...OWNER_KEYS.map((k) => iface.encodeFunctionData("grantSetterRoles", [setter(k), b.evmAddress]))];
  b.ens.rolesTx = (await serial(() => send(at(b.ens!.resolver!).multicall(grants)))).hash; save();
  // roleBitmap 0: the bot owns its name but can't point it at another resolver (and so can't fake its record).
  const expiry = BigInt(Math.floor(Date.now() / 1000) + 365 * 86400);
  b.ens.registeredTx = (await serial(() => send(at(state.ens!.registry!).register(label, b.evmAddress, ethers.ZeroAddress, b.ens!.resolver, 0n, expiry)))).hash; save();
  console.log(`[ens] ${name} → ${b.botId} (${b.ens.registeredTx})`);
}

// After a fight: write the fight keys that changed (server wallet, allowed only these keys).
const syncing = new Set<string>();
export async function sync(b: Bot) {
  if (!ENS_LIVE || !b.ens?.registeredTx || syncing.has(b.botId)) return;
  syncing.add(b.botId);
  try {
    const keys = fightKeys(b), n = dns(b.ens.name), changed = Object.entries(keys).filter(([k, v]) => v && b.ens!.synced[k] !== v);
    if (!changed.length) return;
    const rc = await serial(() => send(at(b.ens!.resolver!).multicall(changed.map(([k, v]) => iface.encodeFunctionData("setText", [n, k, v])))));
    for (const [k, v] of changed) b.ens.synced[k] = v;
    b.ens.lastSyncTx = rc.hash; save();
  } finally { syncing.delete(b.botId); }
}

// The owner edits the fighter's description: signed by the bot's own key, which holds only the description/avatar roles.
export async function setOwnerText(b: Bot, key: string, value: string, botKeyHex: string) {
  if (!OWNER_KEYS.includes(key)) fail(400, `owners can set ${OWNER_KEYS.join(", ")}`);
  if (!ENS_LIVE || !b.ens?.registeredTx) fail(409, "this fighter has no ENS name yet");
  const w = new ethers.Wallet(botKeyHex, provider);
  if ((await provider.getBalance(w.address)) < ethers.parseEther("0.0003"))
    await serial(async () => (await ops!.sendTransaction({ to: w.address, value: ethers.parseEther("0.001") })).wait()); // gas for the bot
  const rc = await send(at(b.ens!.resolver!, w).setText(dns(b.ens!.name), key, value));
  return { key, value, tx: rc.hash, etherscan: etherscan(rc.hash) };
}

// Read back through the Universal Resolver, the way any ENS client does.
export async function readText(name: string, key: string) {
  const data = new ethers.Interface(["function text(bytes32 node, string key) view returns (string)"]).encodeFunctionData("text", [ethers.namehash(name), key]);
  const [out] = await at(A.universal, provider).resolve(dns(name), data);
  return ethers.AbiCoder.defaultAbiCoder().decode(["string"], out)[0] as string;
}

export const view = (b: Bot) => (b.ens?.registeredTx
  ? { name: b.ens.name, resolver: b.ens.resolver, records: b.ens.synced, ownerKeys: OWNER_KEYS, serverKeys: SERVER_KEYS,
      app: `https://sepolia.app.ens.domains/${b.ens.name}`, tx: etherscan(b.ens.registeredTx) }
  : b.ens ? { name: b.ens.name, pending: true } : null);
export const parentView = () => (state.ens ? { name: state.ens.parent, registry: state.ens.registry, registered: !!state.ens.registered,
  tx: state.ens.registered ? etherscan(state.ens.registered.tx) : null } : null);

// Self-check: `npx tsx src/ens.ts` (labels, no network). `npx tsx src/ens.ts read kal.ironcorner.eth ironcorner.record` reads live.
if (process.argv[1]?.endsWith("ens.ts")) {
  if (process.argv[2] === "read") console.log(await readText(process.argv[3], process.argv[4]));
  else {
    const assert = (await import("node:assert")).strict;
    assert.equal(labelFor("Kal", []), "kal");
    assert.equal(labelFor("Brass Knuckle", ["brass-knuckle"]), "brass-knuckle-2");
    assert.equal(labelFor("  !!  ", []), "fighter");
    assert.equal(dns("kal.ironcorner.eth"), "0x036b616c0a69726f6e636f726e65720365746800");
    console.log("ens.ts self-check ok");
  }
}
