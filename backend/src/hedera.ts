// Hedera: bot accounts, x402 arena route + per-bot payers, HCS fight ledger. Mock mode fakes all of it.
import crypto from "node:crypto";
import { env, modes, state, save, fail, SELF_URL, type Bot, type Fight, type Payment } from "./state.ts";
// Hedera SDK classes come through @x402/hedera so there is exactly one SDK instance.
import { Client, PrivateKey, AccountId, Hbar, TokenId, TransferTransaction, createClientHederaSigner } from "@x402/hedera";
// Not re-exported by @x402/hedera; this resolves to the same single install it depends on (npm ls @hiero-ledger/sdk).
import { AccountCreateTransaction, TopicCreateTransaction, TopicMessageSubmitTransaction, TokenCreateTransaction, TokenMintTransaction, TokenType, TokenSupplyType, NftId } from "@hiero-ledger/sdk";
import { ExactHederaScheme as ClientScheme } from "@x402/hedera/exact/client";
import { ExactHederaScheme as ServerScheme } from "@x402/hedera/exact/server";
import { x402Client } from "@x402/core/client";
import { x402ResourceServer, HTTPFacilitatorClient } from "@x402/core/server";
import { paymentMiddleware } from "@x402/express";
import { wrapFetchWithPayment, decodePaymentResponseHeader } from "@x402/fetch";

const NET = "hedera:testnet";
const MIRROR = "https://testnet.mirrornode.hedera.com/api/v1";
export const USDC = env.USDC_TOKEN_ID ?? "0.0.429274";
export const PAY_ASSET = (env.PAY_ASSET ?? "hbar") === "usdc" ? "usdc" : "hbar";
const TINYBAR_PER_CENT = Number(env.TINYBAR_PER_CENT ?? 1_000_000); // 1 cent of damage = 0.01 testnet HBAR
const FACILITATOR = env.X402_FACILITATOR_URL ?? "https://x402.org/facilitator";
const KEYS: Record<string, string | undefined> = { bot_a: env.BOT_A_PRIVATE_KEY, bot_b: env.BOT_B_PRIVATE_KEY };

export const hashscan = (txId: string) => {
  const m = txId.match(/^(\d+\.\d+\.\d+)@(\d+)\.(\d+)$/); // 0.0.x@s.n → 0.0.x-s-n
  return `https://hashscan.io/testnet/transaction/${m ? `${m[1]}-${m[2]}-${m[3]}` : txId}`;
};

async function mirror(path: string) {
  const r = await fetch(MIRROR + path);
  return r.ok ? r.json() : null;
}

let op: Client | null = null;
let operatorId = "";
const payers: Record<string, (i: RequestInfo | URL, init?: RequestInit) => Promise<Response>> = {};

export async function init() {
  if (modes.hedera === "mock") {
    state.bots.bot_a.accountId = "0.0.1001"; state.bots.bot_b.accountId = "0.0.1002";
    state.bots.bot_a.evmAddress = state.bots.bot_b.evmAddress = null;
    return;
  }
  const opKey = PrivateKey.fromStringECDSA(env.HEDERA_OPERATOR_PRIVATE_KEY!);
  operatorId = env.HEDERA_OPERATOR_ID ?? (await mirror(`/accounts/0x${opKey.publicKey.toEvmAddress()}`))?.account;
  if (!operatorId) throw new Error("operator account not found on mirror node; set HEDERA_OPERATOR_ID");
  op = Client.forTestnet().setOperator(AccountId.fromString(operatorId), opKey);

  for (const botId of ["bot_a", "bot_b"]) {
    const bot = state.bots[botId];
    const key = PrivateKey.fromStringECDSA(KEYS[botId]!);
    const evm = "0x" + key.publicKey.toEvmAddress();
    let acct = (await mirror(`/accounts/${evm}`))?.account as string | undefined;
    if (!acct) {
      const rc = await (await new AccountCreateTransaction().setECDSAKeyWithAlias(key).setInitialBalance(new Hbar(2))
        .setMaxAutomaticTokenAssociations(-1).execute(op)).getReceipt(op);
      acct = rc.accountId!.toString();
      console.log(`[hedera] created ${botId} account ${acct}`);
    }
    bot.accountId = acct; bot.evmAddress = evm;
    addPayer(botId, acct, key);
  }
  // Player bots: their keys live sealed in state.
  for (const bot of Object.values(state.bots)) {
    if (!bot.hkey || bot.hkey === "mock" || !bot.accountId) continue;
    try { addPayer(bot.botId, bot.accountId, PrivateKey.fromStringECDSA(unseal(bot))); }
    catch (e: any) { console.warn(`[hedera] ${bot.botId} key unseal failed (BRAIN_SECRET changed?): ${e.message}`); }
  }
  if (!state.ledgerTopic) {
    const rc = await (await new TopicCreateTransaction().setTopicMemo("Iron Corner fight ledger").setSubmitKey(opKey.publicKey).execute(op)).getReceipt(op);
    state.ledgerTopic = rc.topicId!.toString();
    console.log(`[hedera] created ledger topic ${state.ledgerTopic}`);
  }
  save();
  await topUp().catch((e) => console.warn("[hedera] top-up skipped:", e.message));
  await initNfts(opKey).catch((e) => console.warn("[hts] setup failed:", e.message));
}

function addPayer(botId: string, acct: string, key: PrivateKey) {
  const signer = createClientHederaSigner(acct, key, { network: NET });
  const client = x402Client.fromConfig({
    schemes: [{ network: "hedera:*", client: new ClientScheme(signer) }],
    spendControls: { maxAmountPerPayment: "$0.05", allowedAssets: [{ network: NET, asset: "0.0.0", maxAmountPerPayment: String(5 * TINYBAR_PER_CENT) }] },
  });
  payers[botId] = wrapFetchWithPayment(fetch, client);
}

// ---- player bots: account from the operator, sealed key, faucet ----
// Bot keys are sealed with AES-256-GCM under HKDF(server secret, botId), so state.json alone doesn't leak them.
const sealKey = (botId: string) => Buffer.from(crypto.hkdfSync("sha256", env.BRAIN_SECRET ?? state.brainSecret, "iron-corner-hedera-key", botId, 32));
function seal(botId: string, text: string) {
  const iv = crypto.randomBytes(12), c = crypto.createCipheriv("aes-256-gcm", sealKey(botId), iv);
  return Buffer.concat([iv, c.update(text, "utf8"), c.final(), c.getAuthTag()]).toString("base64");
}
function unseal(bot: Bot) {
  const blob = Buffer.from(bot.hkey!, "base64"), d = crypto.createDecipheriv("aes-256-gcm", sealKey(bot.botId), blob.subarray(0, 12));
  d.setAuthTag(blob.subarray(-16));
  return Buffer.concat([d.update(blob.subarray(12, -16)), d.final()]).toString("utf8");
}
export const NEW_BOT_HBAR = Number(env.NEW_BOT_HBAR ?? 0.3);
// New ECDSA account (alias = EVM address, unlimited auto-associations) funded by the operator.
export async function createBotAccount(bot: Bot) {
  if (modes.hedera === "mock") {
    bot.accountId = `0.0.${2000 + Object.values(state.bots).filter((b) => b.hkey).length}`;
    bot.evmAddress = "0x" + crypto.randomBytes(20).toString("hex"); bot.hkey = "mock";
    state.mockHbar[bot.accountId] = NEW_BOT_HBAR;
    return;
  }
  const key = PrivateKey.generateECDSA();
  const rc = await (await new AccountCreateTransaction().setECDSAKeyWithAlias(key).setInitialBalance(new Hbar(NEW_BOT_HBAR))
    .setMaxAutomaticTokenAssociations(-1).execute(op!)).getReceipt(op!);
  bot.accountId = rc.accountId!.toString(); bot.evmAddress = "0x" + key.publicKey.toEvmAddress();
  bot.hkey = seal(bot.botId, key.toStringRaw());
  addPayer(bot.botId, bot.accountId, key);
  console.log(`[hedera] created ${bot.botId} account ${bot.accountId}`);
  void mintFighter(bot).catch((e) => console.warn(`[hts] mint ${bot.botId} failed: ${e.message}`));
}
export const operatorBalance = async () => (modes.hedera === "mock" ? { hbar: 100, usdc: 0 } : balance(operatorId, true));
// Faucet: operator → bot, testnet HBAR.
export async function sendHbar(bot: Bot, hbar: number): Promise<string> {
  if (modes.hedera === "mock") { state.mockHbar[bot.accountId!] = (state.mockHbar[bot.accountId!] ?? 0) + hbar; save(); return `mock-fund-${bot.botId}-${Date.now()}`; }
  const tiny = Math.round(hbar * 1e8);
  const resp = await new TransferTransaction().addHbarTransfer(operatorId, Hbar.fromTinybars(-tiny)).addHbarTransfer(bot.accountId!, Hbar.fromTinybars(tiny))
    .setTransactionMemo(`Iron Corner test funds ${bot.botId}`).execute(op!);
  await resp.getReceipt(op!);
  balCache.delete(bot.accountId!);
  return resp.transactionId.toString();
}

// Keep bots able to pay: HBAR ≥ 1, and USDC ≥ $0.50 when the operator holds USDC.
async function topUp() {
  const opBal = await balance(operatorId, true);
  for (const bot of [state.bots.bot_a, state.bots.bot_b]) { // demo bots only; player bots use the capped faucet
    const b = await balance(bot.accountId!, true);
    if (b.hbar < 1) {
      await (await new TransferTransaction().addHbarTransfer(operatorId, new Hbar(-1)).addHbarTransfer(bot.accountId!, new Hbar(1)).execute(op!)).getReceipt(op!);
      console.log(`[hedera] topped up ${bot.botId} with 1 HBAR`);
    }
    if (b.usdc < 0.5 && opBal.usdc >= 1.5) {
      const tok = TokenId.fromString(USDC);
      await (await new TransferTransaction().addTokenTransfer(tok, operatorId, -1_000_000).addTokenTransfer(tok, bot.accountId!, 1_000_000).execute(op!)).getReceipt(op!);
      opBal.usdc -= 1;
      console.log(`[hedera] sent ${bot.botId} 1 USDC`);
    }
  }
  if (PAY_ASSET === "hbar" && opBal.usdc > 0) console.log("[hedera] operator now holds USDC: set PAY_ASSET=usdc to pay in USDC");
}

const balCache = new Map<string, { at: number; v: { hbar: number; usdc: number } }>();
export async function balance(accountId: string, fresh = false) {
  if (modes.hedera === "mock") return { hbar: state.mockHbar[accountId] ?? 0, usdc: 0, mock: true };
  const c = balCache.get(accountId);
  if (c && !fresh && Date.now() - c.at < 5000) return c.v;
  const a = await mirror(`/accounts/${accountId}`);
  const v = {
    hbar: (a?.balance?.balance ?? 0) / 1e8,
    usdc: (a?.balance?.tokens?.find((t: any) => t.token_id === USDC)?.balance ?? 0) / 1e6,
  };
  balCache.set(accountId, { at: Date.now(), v });
  return v;
}

// ---- HCS ledger (fire and forget, serialized) ----
let hcsChain: Promise<unknown> = Promise.resolve();
export function ledger(msg: Record<string, unknown>) {
  if (modes.hedera === "mock" || !state.ledgerTopic) return;
  const text = JSON.stringify(msg).slice(0, 1000);
  hcsChain = hcsChain.then(() =>
    new TopicMessageSubmitTransaction().setTopicId(state.ledgerTopic!).setMessage(text).execute(op!).then((r) => r.getReceipt(op!))
  ).catch((e) => console.warn("[hcs] submit failed:", e.message));
}
export const ledgerTopic = () => (modes.hedera === "mock" ? "0.0.mock-ledger" : state.ledgerTopic);

// ---- x402 arena route (server side) ----
const pending = new Map<string, { to: string; cents: number }>(); // "fightId:hitNo" → what this hit costs
function hitFor(ctx: { adapter: { getHeader(n: string): string | undefined } }) {
  const p = pending.get(ctx.adapter.getHeader("x-hit-ref") ?? "");
  if (!p) throw new Error("unknown hit ref");
  return p;
}
export function arenaMiddleware() {
  if (modes.hedera === "mock") return (_req: any, _res: any, next: any) => next();
  const server = new x402ResourceServer(new HTTPFacilitatorClient({ url: FACILITATOR })).register("hedera:*", new ServerScheme());
  return paymentMiddleware({
    "POST /arena/hit": {
      accepts: [{
        scheme: "exact", network: NET,
        price: (ctx) => {
          const { cents } = hitFor(ctx);
          return PAY_ASSET === "usdc" ? { asset: USDC, amount: String(cents * 10_000) } : { asset: "0.0.0", amount: String(cents * TINYBAR_PER_CENT) };
        },
        payTo: (ctx) => hitFor(ctx).to,
      }],
      description: "Iron Corner: damage for one clean hit", mimeType: "application/json",
    },
    "POST /arena/scout": {
      accepts: [{
        scheme: "exact", network: NET,
        price: (ctx) => ({ asset: "0.0.0", amount: String(hitFor(ctx).cents * TINYBAR_PER_CENT) }),
        payTo: (ctx) => hitFor(ctx).to,
      }],
      description: "Iron Corner: a scouting report on the next opponent", mimeType: "application/json",
    },
  }, server);
}

// ---- payer side: one serial queue per paying bot so the fight never waits ----
const queues: Record<string, Promise<unknown>> = {};
export const HBAR_PER_CENT = TINYBAR_PER_CENT / 1e8;
export function queuePayment(fight: Fight, p: Payment, onFail: () => void, onDone?: (p: Payment) => void) {
  const q = state.bots[p.from]?.accountId ?? p.from; // house extras share bot_b's account: one queue per paying account
  queues[q] = (queues[q] ?? Promise.resolve()).then(() => settle(fight, p)).catch((e) => {
    p.status = "failed"; p.error = String(e?.message ?? e).slice(0, 200);
    console.warn(`[pay] hit ${p.hitNo} failed: ${p.error}`);
    onFail();
  }).finally(() => {
    save();
    ledger({ t: "pay", fight: fight.fightId, hit: p.hitNo, from: p.from, to: p.to, cents: p.cents, status: p.status, tx: p.txId });
    onDone?.(p);
  });
}

async function settle(fight: Fight, p: Payment) {
  // Spending control: never pay more in one fight than the bot brought.
  const spent = fight.payments.filter((x) => x.from === p.from && x.status !== "failed" && x.hitNo <= p.hitNo).reduce((s, x) => s + x.cents, 0);
  if (spent > fight.startBudget) throw new Error(`budget exceeded (${spent} > ${fight.startBudget})`);
  if (modes.hedera === "mock") {
    await new Promise((r) => setTimeout(r, 300));
    Object.assign(p, { status: "settled", txId: `mock-${fight.fightId}-${p.hitNo}`, hashscan: null, mock: true });
    return;
  }
  const ref = `${fight.fightId}:${p.hitNo}`;
  pending.set(ref, { to: state.bots[p.to].accountId!, cents: p.cents });
  try {
    // ponytail: blind single retry; a settle that timed out but landed could pay twice. Check mirror by memo if that matters.
    let lastErr: unknown;
    for (let attempt = 0; attempt < 2; attempt++) {
      try {
        const pay = payers[p.from] ?? payers[state.bots[p.from]?.payAs ?? ""];
        if (!pay) throw new Error(`no payer for ${p.from}`);
        const res = await pay(`${SELF_URL}/arena/hit`, {
          method: "POST", headers: { "content-type": "application/json", "x-hit-ref": ref }, body: JSON.stringify({ ref }),
        });
        const hdr = res.headers.get("payment-response");
        if (!res.ok || !hdr) throw new Error(`arena ${res.status}: ${(await res.text()).slice(0, 150)}`);
        const s = decodePaymentResponseHeader(hdr);
        if (!s.success) throw new Error(s.errorReason ?? "settle failed");
        Object.assign(p, { status: "settled", txId: s.transaction, hashscan: hashscan(s.transaction) });
        return;
      } catch (e) { lastErr = e; await new Promise((r) => setTimeout(r, 1000)); }
    }
    throw lastErr;
  } finally { pending.delete(ref); }
}

// ---- x402 scouting: a bot buys a report on its next opponent from the arena (the operator) ----
export const SCOUT_CENTS = Number(env.SCOUT_CENTS ?? 2);
export async function payScout(buyer: Bot, ref: string): Promise<{ txId: string; hashscan: string | null; mock?: boolean }> {
  if (modes.hedera === "mock") return { txId: `mock-scout-${ref}`, hashscan: null, mock: true };
  const pay = payers[buyer.botId] ?? payers[buyer.payAs ?? ""] ?? fail(500, `no payer for ${buyer.botId}`);
  pending.set(ref, { to: operatorId, cents: SCOUT_CENTS });
  try {
    const res = await pay(`${SELF_URL}/arena/scout`, { method: "POST", headers: { "content-type": "application/json", "x-hit-ref": ref }, body: JSON.stringify({ ref }) });
    const hdr = res.headers.get("payment-response");
    if (!res.ok || !hdr) throw new Error(`scout ${res.status}: ${(await res.text()).slice(0, 150)}`);
    const r = decodePaymentResponseHeader(hdr);
    if (!r.success) throw new Error(r.errorReason ?? "settle failed");
    return { txId: r.transaction, hashscan: hashscan(r.transaction) };
  } finally { pending.delete(ref); }
}

// ---- HTS: one NFT collection, no contracts. Serial 1 is the title belt; every other serial is a fighter's card. ----
// Each NFT's metadata is a short URL to its HIP-412 JSON (GET /nft/belt or /nft/:botId), which points at the fighter's brain on 0G.
// The belt changes hands on Hedera when its holder loses a ranked fight (or goes to the first winner while vacant).
export const keyOf = (b: Bot) => (KEYS[b.botId] ? PrivateKey.fromStringECDSA(KEYS[b.botId]!) : b.hkey && b.hkey !== "mock" ? PrivateKey.fromStringECDSA(unseal(b)) : null);
let supplyKey: PrivateKey | null = null;
const nftUrl = (key: string) => `${SELF_URL}/nft/${key}`; // "belt" or a botId; HIP-412 caps metadata at 100 bytes
async function mint(url: string) {
  const rc = await (await (await new TokenMintTransaction().setTokenId(state.hts!.tokenId).setMetadata([Buffer.from(url)]).freezeWith(op!)
    .sign(supplyKey!)).execute(op!)).getReceipt(op!);
  return Number(rc.serials[0]);
}
async function initNfts(opKey: PrivateKey) {
  supplyKey = opKey;
  if (!state.hts) {
    const rc = await (await new TokenCreateTransaction().setTokenName("Iron Corner").setTokenSymbol("IRON").setTokenType(TokenType.NonFungibleUnique)
      .setSupplyType(TokenSupplyType.Infinite).setTreasuryAccountId(operatorId).setSupplyKey(opKey.publicKey).setAdminKey(opKey.publicKey)
      .setTokenMemo("Iron Corner fighters + the Mumbai belt").execute(op!)).getReceipt(op!);
    state.hts = { tokenId: rc.tokenId!.toString(), belt: { serial: 0, holder: null, tx: null } };
    save(); console.log(`[hts] created collection ${state.hts.tokenId}`);
  }
  if (!state.hts.belt.serial) { state.hts.belt.serial = await mint(nftUrl("belt")); save(); console.log(`[hts] belt is serial ${state.hts.belt.serial}`); }
  for (const b of Object.values(state.bots)) if (!b.nft && b.accountId && !b.payAs && keyOf(b)) await mintFighter(b).catch((e) => console.warn(`[hts] mint ${b.botId}: ${e.message}`));
}
export async function mintFighter(b: Bot) {
  if (b.nft || !state.hts) return;
  if (modes.hedera === "mock") { b.nft = { serial: Object.values(state.bots).filter((x) => x.nft).length + 2, tx: "mock" }; return save(); }
  const serial = await mint(nftUrl(b.botId));
  const tx = await (await new TransferTransaction().addNftTransfer(new NftId(TokenId.fromString(state.hts.tokenId), serial), operatorId, b.accountId!)
    .setTransactionMemo(`Iron Corner fighter card ${b.name}`).execute(op!));
  await tx.getReceipt(op!);
  b.nft = { serial, tx: tx.transactionId.toString() }; save();
  console.log(`[hts] ${b.botId} card serial ${serial}`);
}
const tokenLink = (serial: number) => `https://hashscan.io/testnet/token/${state.hts!.tokenId}/${serial}`;
export const nftView = (b: Bot) => (b.nft && state.hts ? { tokenId: state.hts.tokenId, serial: b.nft.serial, hashscan: tokenLink(b.nft.serial), holdsBelt: state.hts.belt.holder === b.botId } : null);
export const beltView = () => (state.hts?.belt.serial ? { tokenId: state.hts.tokenId, serial: state.hts.belt.serial, holder: state.hts.belt.holder, tx: state.hts.belt.tx, hashscan: tokenLink(state.hts.belt.serial) } : null);
export function nftMeta(key: string) {
  const h = state.hts;
  if (!h) return null;
  if (key === "belt") {
    const holder = h.belt.holder ? state.bots[h.belt.holder] : null;
    return { name: "Iron Corner Mumbai Belt", description: "The title belt. It moves on Hedera to whoever beats its holder.", type: "application/json",
      properties: { holder: holder ? { botId: holder.botId, name: holder.name, account: holder.accountId } : "vacant", lastTransfer: h.belt.tx } };
  }
  const b = state.bots[key];
  if (!b?.nft) return null;
  return { name: `${b.name} (Iron Corner fighter)`, description: "A robot boxer trained by voice. Its brain is encrypted on 0G Storage.", type: "application/json",
    properties: { botId: b.botId, account: b.accountId, record: b.record ?? { w: 0, l: 0 }, rating: b.rating ?? 1000,
      brainRoot: b.brainHistory.at(-1)?.root ?? null, agentId: b.agentId?.tokenId ?? null, paint: b.paint ?? null } };
}
// Called after a ranked fight with a winner. Vacant belt → winner; holder lost → the belt moves from the holder's account.
export async function beltAfter(f: Fight) {
  const h = state.hts, w = f.winner;
  if (!h?.belt.serial || f.house || !w) return;
  const loser = w === f.a ? f.b : f.a;
  if (h.belt.holder && h.belt.holder !== loser) return;
  const to = state.bots[w], from = h.belt.holder ? state.bots[h.belt.holder] : null;
  if (!to.accountId || to.payAs) return;
  if (modes.hedera === "mock") { Object.assign(h.belt, { holder: w, tx: `mock-belt-${f.fightId}` }); save(); return; }
  const fromAcct = from?.accountId ?? operatorId;
  let t = new TransferTransaction().addNftTransfer(new NftId(TokenId.fromString(h.tokenId), h.belt.serial), fromAcct, to.accountId)
    .setTransactionMemo(`Iron Corner belt: ${to.name} wins ${f.fightId}`).freezeWith(op!);
  if (from) { const k = keyOf(from); if (!k) throw new Error(`no key for ${from.botId}`); t = await t.sign(k); }
  const resp = await t.execute(op!); await resp.getReceipt(op!);
  Object.assign(h.belt, { holder: w, tx: resp.transactionId.toString() }); save();
  ledger({ t: "belt", fight: f.fightId, from: from?.botId ?? "vacant", to: w, serial: h.belt.serial, tx: h.belt.tx });
  console.log(`[hts] belt → ${w} (${h.belt.tx})`);
}
