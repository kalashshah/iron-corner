// Fight camp: fine-tune this bot's own LoRA adapter on 0G Compute from its demos (the pipeline proven in camp/camp.mjs:
// regular Galileo testnet, CommonJS SDK + CommonJS ethers wallet, uploadDatasetToTEE, Alpaca instruction/input/output,
// the fight state in words). Real camps spend testnet 0G, so they run only with CAMP_MODE=live; otherwise a mock
// pipeline walks the same statuses.
//
// Serving (ZG_ADAPTER_SERVING=on): we run the adapter the way 0G's fine-tuning docs say to use one, base model + LoRA
// on our own machine (serve_adapter.py, spawned below). 0G's own adapter hosting (deployAdapter) is undocumented and its
// endpoint was down on 6 Nov. The adapter's answer is a suggestion: qwen-flash on 0G still makes every fight decision.
import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";
import { execFileSync, spawn } from "node:child_process";
import { createRequire } from "node:module";
import { env, state, save, id, fail, refuse, type Bot, type Demo, type Camp } from "./state.ts";
import { DAMAGE, cleanMoves, type FightState } from "./game.ts";
import { worked } from "./demos.ts";
import { saveBrain } from "./storage.ts";

export const MODEL = "Qwen2.5-0.5B-Instruct";
export const INSTRUCTION = "Robot boxer: next moves for this fight state.";
export const MIN_DEMOS = 40;
const MAX_PER_DAY = Number(env.CAMP_MAX_PER_DAY ?? 3);
const LIVE = env.CAMP_MODE === "live";
export const ADAPTER_SERVING = env.ZG_ADAPTER_SERVING === "on";
const RPC = env.ZG_RPC ?? "https://evmrpc-testnet.0g.ai";
const DIR = path.resolve(import.meta.dirname, "../data/camps");
const day = () => new Date().toISOString().slice(0, 10);

// ---- train/serve format (identical to camp.mjs stateLine: words, not numbers) ----
type Line = { o1: string | null; o2: string | null; m1: string | null; dist: number; mine: number; theirs: number; guard: number };
export function stateLine(s: Line) {
  const dist = s.dist > 1.6 ? "far" : s.dist < 1.1 ? "close" : "mid";
  const money = (v: number) => (v < 12 ? "almost_broke" : v < 25 ? "low" : "ok");
  return `opp_last=${s.o1 ?? "none"} opp_prev=${s.o2 ?? "none"} my_last=${s.m1 ?? "none"} distance=${dist} ` +
    `my_money=${money(s.mine)} opp_money=${money(s.theirs)} my_guard=${s.guard < 0.4 ? "low" : "high"}`;
}
// Demo features: f = [distance/3, lead, selfGuard, oppGuard, sinceOppAttack/5]; mo = [self, opp] cents (older demos: from lead).
export const demoLine = (d: Demo) => stateLine({ o1: d.o1, o2: d.o2, m1: d.m1, dist: d.f[0] * 3, guard: d.f[2],
  mine: d.mo?.[0] ?? d.f[1] * 100, theirs: d.mo?.[1] ?? (1 - d.f[1]) * 100 });
export const fightLine = (s: FightState) => stateLine({ o1: s.oppLastMoves?.at(-1) ?? null, o2: s.oppLastMoves?.at(-2) ?? null, m1: s.selfLastMoves?.at(-1) ?? null,
  dist: s.distance ?? 1.2, mine: s.selfBudget ?? 50, theirs: s.oppBudget ?? 50, guard: s.selfGuard ?? 1 });
export const alpaca = (input: string) => "Below is an instruction that describes a task, paired with an input that provides further context. " +
  `Write a response that appropriately completes the request.\n\n### Instruction:\n${INSTRUCTION}\n\n### Input:\n${input}\n\n### Response:\n`;

// Demos → Alpaca rows. got_hit is dropped; what worked is repeated (2×, 3× for strong demos); a slip/block that worked
// takes the punch that landed right after it ("dodge_r uppercut"). Fight demos count only when they worked.
export function dataset(demos: Demo[]) {
  const lib = [...demos].sort((x, y) => x.at - y.at), rows: { instruction: string; input: string; output: string }[] = [];
  let used = 0;
  lib.forEach((d, i) => {
    if (d.out === "got_hit") return;
    const ok = worked(d.a, d.out);
    if (d.src === "fight" && !ok) return;
    let out = d.a;
    if (ok && !DAMAGE[d.a]) {
      const nx = lib.slice(i + 1, i + 4).find((x) => x.r === d.r && x.m1 === d.a && DAMAGE[x.a] && x.out === "landed");
      if (nx) out += ` ${nx.a}`;
    }
    const row = { instruction: INSTRUCTION, input: demoLine(d), output: out };
    for (let k = ok ? (d.w >= 1.5 ? 3 : 2) : 1; k > 0; k--) rows.push(row);
    used++;
  });
  return { rows, used };
}

// ---- API ----
export const view = (c: Camp | undefined) => c
  ? { campId: c.campId, status: c.status, taskId: c.taskId, progress: c.progress, version: c.version, adapterRoot: c.adapterRoot, loss: c.loss,
    examples: c.examples, startedAt: c.startedAt, ...(c.error ? { error: c.error } : {}), ...(c.mock ? { mock: true } : {}) }
  : { status: "none", taskId: null, progress: null, version: null };

export async function start(b: Bot) {
  if (b.house) fail(400, "house bots don't go to camp");
  const { rows, used } = dataset(b.demos ?? []);
  if (used < MIN_DEMOS) refuse("not_enough_demos", { have: used, need: MIN_DEMOS });
  const camps = Object.values(state.camps), today = day();
  if (camps.some((c) => c.botId === b.botId && c.day === today && c.status !== "failed")) refuse("camp_today");
  if (camps.some((c) => c.status !== "done" && c.status !== "failed")) refuse("camp_busy"); // one wallet, one camp at a time
  // Failed camps cost (almost) nothing, so only camps that ran count toward the daily cap.
  if (camps.filter((c) => c.day === today && !c.mock && c.status !== "failed").length >= MAX_PER_DAY && LIVE) refuse("camp_budget");
  const c: Camp = { campId: id("camp"), botId: b.botId, day: today, status: "queued", taskId: null, progress: null, version: null,
    adapterRoot: null, loss: null, examples: rows.length, startedAt: new Date().toISOString(), ...(LIVE ? {} : { mock: true }) };
  state.camps[c.campId] = c; b.campId = c.campId; save();
  (LIVE ? runLive : runMock)(c, b, rows).catch((e) => {
    c.status = "failed"; c.error = String(e?.shortMessage ?? e?.message ?? e).slice(0, 300); save();
    console.warn(`[camp] ${c.campId} ${b.botId} failed: ${c.error}`);
  });
  return c;
}

// Each finished camp = a new adapter version; the brain save that follows records it on the Agentic ID.
function delivered(c: Camp, b: Bot, root: string | null, loss: number | null) {
  const version = (b.adapter?.version ?? 0) + 1;
  b.adapter = { version, taskId: c.taskId!, root, loss, at: new Date().toISOString(), ...(c.mock ? { mock: true } : {}) };
  Object.assign(c, { status: "done", version, adapterRoot: root, loss }); save();
  void saveBrain(b).catch((e) => console.warn(`[camp] brain save after camp failed: ${e.message}`));
}

async function runMock(c: Camp, b: Bot, rows: unknown[]) {
  const step = Number(env.CAMP_MOCK_STEP_MS ?? 1000), wait = () => new Promise((r) => setTimeout(r, step));
  c.status = "uploading"; save(); await wait();
  c.taskId = `mock-${crypto.randomUUID()}`; c.status = "training"; c.progress = "Training"; save(); await wait();
  c.status = "delivered"; c.progress = "Delivered"; save(); await wait();
  c.status = "downloading"; save(); await wait();
  delivered(c, b, "0x" + crypto.createHash("sha256").update(JSON.stringify(rows)).digest("hex"), 0.1);
}

let brokerP: Promise<{ broker: any; provider: string; eth: any; wallet: any }> | null = null;
function broker() {
  brokerP ??= (async () => {
    process.env.ZG_DEV_MODE ??= "false"; // regular testnet: 0.1 0G ledger minimum instead of 3
    const req = createRequire(import.meta.url);
    const sdk = req("@0gfoundation/0g-compute-ts-sdk");
    // The broker only builds fine-tuning when the signer is an ethers.Wallet from the SDK's own (CommonJS) ethers copy.
    const eth = createRequire(req.resolve("@0gfoundation/0g-compute-ts-sdk"))("ethers");
    const wallet = new eth.Wallet(env.OG_OPS_PRIVATE_KEY, new eth.JsonRpcProvider(RPC));
    const broker = await sdk.createZGComputeNetworkBroker(wallet);
    if (!broker.fineTuning) throw new Error("0G fine-tuning broker missing (ethers Wallet copy mismatch)");
    const [svc] = await broker.fineTuning.listService();
    if (!svc) throw new Error("no 0G fine-tuning provider online");
    return { broker, provider: svc[0] as string, eth, wallet };
  })();
  brokerP.catch(() => (brokerP = null));
  return brokerP;
}

async function runLive(c: Camp, b: Bot, rows: unknown[]) {
  const { broker: br, provider: p, eth, wallet } = await broker(), ft = br.fineTuning;
  const dir = path.join(DIR, c.campId), enc = path.join(dir, "adapter.enc"), zip = path.join(dir, "adapter.zip");
  const deliverable = async () => ((await ft.getAccount(p)).deliverables ?? []).find((d: any) => d.id === c.taskId);
  if (!c.taskId) { // fresh camp (a resumed one already has its task)
    if ((await wallet.provider.getBalance(wallet.address)) < eth.parseEther("0.03")) throw new Error("camp_budget: trainer wallet below 0.03 0G");
    await ft.acknowledgeProviderSigner(p).catch((e: any) => console.log(`[camp] ack signer: ${String(e.message).slice(0, 120)}`));
    // The fee grows with the dataset (0.023 to 0.1 0G so far), and the provider refuses a task the sub-account can't
    // cover in full, so keep it at 0.15+ (topping the ledger up from the wallet when needed).
    const acct = await ft.getAccount(p).catch(() => null), have = BigInt(acct?.balance ?? 0), want = eth.parseEther("0.15");
    if (have < want) {
      const need = want - have, [, avail] = (await br.ledger.getLedger().catch(() => [])) as bigint[];
      if (BigInt(avail ?? 0) < need) await br.ledger.depositFund(Number(eth.formatEther(need)) + 0.01);
      await br.ledger.transferFund(p, "fine-tuning", need).catch((e: any) => { throw new Error(`camp_budget: 0G ledger can't fund the provider (${String(e.message).slice(0, 120)})`); });
    }
    fs.mkdirSync(dir, { recursive: true });
    const data = path.join(dir, "data.jsonl"), params = path.join(dir, "training.json");
    fs.writeFileSync(data, rows.map((r) => JSON.stringify(r)).join("\n") + "\n");
    // ~1.5 passes over the data, 60..150 steps: v1 (120 steps) cost 0.023 0G.
    fs.writeFileSync(params, JSON.stringify({ neftune_noise_alpha: 5, num_train_epochs: 3, per_device_train_batch_size: 2, learning_rate: 0.0002,
      max_steps: Math.max(60, Math.min(150, Math.round(rows.length * 0.75))) }));
    c.status = "uploading"; save();
    const up = await ft.uploadDatasetToTEE(p, data);
    c.taskId = await ft.createTask(p, MODEL, up.datasetHash, params);
    c.status = "training"; save();
    console.log(`[camp] ${b.botId} task ${c.taskId} (${rows.length} rows)`);
  }
  c.status = "training"; delete c.error; save();
  // 0G progress runs ... Training → Delivering → Delivered → UserAcknowledged → Finished. Ack only once Delivered:
  // acknowledging during "Delivering" reverts on chain.
  for (const until = Date.now() + 45 * 60_000; ; await new Promise((r) => setTimeout(r, 20_000))) {
    if (Date.now() > until) throw new Error("training took over 45 min");
    const t = await ft.getTask(p, c.taskId).catch(() => null);
    if (t?.progress && t.progress !== c.progress) { c.progress = t.progress; save(); }
    if (/fail/i.test(c.progress ?? "")) throw new Error(`task ${c.progress}`);
    if (/^(delivered|useracknowledged|finished)$/i.test(c.progress ?? "")) break;
  }
  c.status = "delivered"; save();
  c.status = "downloading"; save();
  if (!/^delivered$/i.test(c.progress ?? "") && fs.existsSync(enc)) console.log(`[camp] ${c.taskId} already acknowledged`);
  else await ft.acknowledgeModel(p, c.taskId, enc);
  // decryptModel right after the ack fails until the provider publishes the key: retry for a few minutes.
  for (let i = 0; ; i++) {
    try { await ft.decryptModel(p, c.taskId, enc, zip); break; }
    catch (e) { if (i >= 8) throw e; await new Promise((r) => setTimeout(r, 30_000)); }
  }
  const root = (await deliverable().catch(() => null))?.modelRootHash ?? null;
  delivered(c, b, root, lossFrom(zip));
}

// Pick up live camps that stopped mid-way (process restart, or a failed step after the task was created).
export function resume(c: Camp) {
  const b = state.bots[c.botId];
  if (!LIVE || c.mock || !c.taskId || !b || c.status === "done") return;
  runLive(c, b, []).catch((e) => { c.status = "failed"; c.error = String(e?.shortMessage ?? e?.message ?? e).slice(0, 300); save(); });
}
export const resumeAll = () => Object.values(state.camps).filter((c) => c.status !== "done" && c.status !== "failed").forEach(resume);

// Final training loss from the last checkpoint's trainer_state.json inside the decrypted zip.
function lossFrom(zip: string): number | null {
  try {
    const names = execFileSync("unzip", ["-Z1", zip], { encoding: "utf8" }).split("\n").filter((n) => n.endsWith("trainer_state.json"));
    const last = names.sort((x, y) => Number(/checkpoint-(\d+)/.exec(x)?.[1] ?? 0) - Number(/checkpoint-(\d+)/.exec(y)?.[1] ?? 0)).at(-1);
    if (!last) return null;
    const ts = JSON.parse(execFileSync("unzip", ["-p", zip, last], { encoding: "utf8", maxBuffer: 1 << 24 }));
    const l = (ts.log_history ?? []).filter((h: any) => typeof h.loss === "number").at(-1)?.loss;
    return typeof l === "number" ? Math.round(l * 10000) / 10000 : null;
  } catch { return null; }
}

// The adapter server: one Python process on 127.0.0.1, started with the game server when serving is on.
const SERVE = `http://127.0.0.1:${env.CAMP_SERVE_PORT ?? 8790}`;
if (ADAPTER_SERVING && !process.argv[1]?.endsWith("camp.ts")) {
  const start = () => {
    const py = spawn(env.CAMP_PYTHON ?? "python3", [path.resolve(import.meta.dirname, "../serve_adapter.py")], { stdio: ["ignore", "inherit", "inherit"] });
    py.on("exit", (code) => { console.warn(`[adapter] server exited (${code}), restarting in 10 s`); setTimeout(start, 10_000); });
  };
  start();
}

// The bot's newest camp adapter, as decrypted on this machine (data/camps/<campId>/adapter.zip).
const adapterZip = (b: Bot) => {
  const c = Object.values(state.camps).find((c) => c.taskId === b.adapter?.taskId && c.botId === b.botId);
  const zip = c && path.join(DIR, c.campId, "adapter.zip");
  return zip && fs.existsSync(zip) ? zip : null;
};

// What this bot's own fine-tuned model would do now. null when it has no adapter or the server is slow or down.
export async function adapterMoves(b: Bot, s: FightState, timeoutMs = 1500): Promise<{ moves: string[]; ms: number } | null> {
  if (!ADAPTER_SERVING || !b.adapter || b.adapter.mock) return null;
  const zip = adapterZip(b);
  if (!zip) return null;
  const r = await fetch(`${SERVE}/moves`, { method: "POST", headers: { "content-type": "application/json" },
    body: JSON.stringify({ zip, prompt: alpaca(fightLine(s)) }), signal: AbortSignal.timeout(timeoutMs) });
  if (!r.ok) throw new Error(`adapter server ${r.status}: ${(await r.text()).slice(0, 160)}`);
  const { text, ms } = await r.json() as { text: string; ms: number };
  const moves = cleanMoves(String(text).trim().split(/\s+/)).slice(0, 3);
  return moves.length ? { moves, ms } : null;
}

// Self-check: `npx tsx src/camp.ts` (dataset + format only; no network)
if (process.argv[1]?.endsWith("camp.ts")) {
  const assert = (await import("node:assert")).strict;
  assert.equal(stateLine({ o1: "hook_l", o2: null, m1: "jab", dist: 2, mine: 10, theirs: 30, guard: 0.3 }),
    "opp_last=hook_l opp_prev=none my_last=jab distance=far my_money=almost_broke opp_money=ok my_guard=low");
  const d = (a: string, out: string, o: Partial<Demo> = {}): Demo => ({ at: 1, f: [0.33, 0.5, 1, 1, 0.1], o1: "hook_l", o2: null, m1: null, mo: [50, 20], a, out, w: 1, src: "train", r: "r1", ...o });
  // train and serve format the same moment the same way
  assert.equal(demoLine(d("jab", "landed")), fightLine({ distance: 0.99, selfBudget: 50, oppBudget: 20, selfGuard: 1, oppLastMoves: ["hook_l"], selfLastMoves: [] }));
  const ds = dataset([d("dodge_r", "dodged", { at: 1 }), d("uppercut", "landed", { at: 2, m1: "dodge_r" }), d("block", "got_hit", { at: 3 }),
    d("jab", "whiff", { at: 4 }), d("cross", "landed", { at: 5, src: "fight", w: 0.3 }), d("hook_l", "blocked", { at: 6, src: "fight" })]);
  assert.equal(ds.used, 4); // got_hit and a failed fight demo dropped
  assert.equal(ds.rows.filter((r) => r.output === "dodge_r uppercut").length, 2); // worked → 2 copies, with the follow-up punch
  assert.equal(ds.rows.filter((r) => r.output === "jab").length, 1);
  assert.ok(ds.rows.every((r) => r.instruction === INSTRUCTION && /^opp_last=/.test(r.input)));
  assert.match(alpaca("x"), /### Instruction:\nRobot boxer: next moves for this fight state.\n\n### Input:\nx\n\n### Response:\n$/);
  console.log("camp.ts self-check ok");
}
