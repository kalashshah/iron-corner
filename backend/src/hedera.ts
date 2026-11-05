// Hedera: bot accounts, x402 arena route + per-bot payers, HCS fight ledger. Mock mode fakes all of it.
import { env, modes, state, save, SELF_URL, type Fight, type Payment } from "./state.ts";
// Hedera SDK classes come through @x402/hedera so there is exactly one SDK instance.
import { Client, PrivateKey, AccountId, Hbar, TokenId, TransferTransaction, createClientHederaSigner } from "@x402/hedera";
// Not re-exported by @x402/hedera; this resolves to the same single install it depends on (npm ls @hiero-ledger/sdk).
import { AccountCreateTransaction, TopicCreateTransaction, TopicMessageSubmitTransaction } from "@hiero-ledger/sdk";
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
    const signer = createClientHederaSigner(acct, key, { network: NET });
    const client = x402Client.fromConfig({
      schemes: [{ network: "hedera:*", client: new ClientScheme(signer) }],
      spendControls: { maxAmountPerPayment: "$0.05", allowedAssets: [{ network: NET, asset: "0.0.0", maxAmountPerPayment: String(5 * TINYBAR_PER_CENT) }] },
    });
    payers[botId] = wrapFetchWithPayment(fetch, client);
  }
  if (!state.ledgerTopic) {
    const rc = await (await new TopicCreateTransaction().setTopicMemo("Iron Corner fight ledger").setSubmitKey(opKey.publicKey).execute(op)).getReceipt(op);
    state.ledgerTopic = rc.topicId!.toString();
    console.log(`[hedera] created ledger topic ${state.ledgerTopic}`);
  }
  save();
  await topUp().catch((e) => console.warn("[hedera] top-up skipped:", e.message));
}

// Keep bots able to pay: HBAR ≥ 1, and USDC ≥ $0.50 when the operator holds USDC.
async function topUp() {
  const opBal = await balance(operatorId, true);
  for (const bot of Object.values(state.bots)) {
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
  if (modes.hedera === "mock") return { hbar: 0, usdc: 0, mock: true };
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
  }, server);
}

// ---- payer side: one serial queue per paying bot so the fight never waits ----
const queues: Record<string, Promise<unknown>> = {};
export function queuePayment(fight: Fight, p: Payment, onFail: () => void) {
  queues[p.from] = (queues[p.from] ?? Promise.resolve()).then(() => settle(fight, p)).catch((e) => {
    p.status = "failed"; p.error = String(e?.message ?? e).slice(0, 200);
    console.warn(`[pay] hit ${p.hitNo} failed: ${p.error}`);
    onFail();
  }).finally(() => {
    save();
    ledger({ t: "pay", fight: fight.fightId, hit: p.hitNo, from: p.from, to: p.to, cents: p.cents, status: p.status, tx: p.txId });
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
        const res = await payers[p.from](`${SELF_URL}/arena/hit`, {
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
