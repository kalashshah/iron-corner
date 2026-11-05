// Fight camp: fine-tune a bot's own LoRA adapter on 0G Compute (testnet, Qwen2.5-0.5B-Instruct).
//   node camp.mjs dataset <demos.json|--synthetic> out.jsonl   build chat-format training data
//   node camp.mjs account                                       open/fund the 0G compute ledger (testnet)
//   node camp.mjs start out.jsonl                              upload dataset (0G Storage) + create task
//   node camp.mjs status [taskId]                               task progress / logs
//   node camp.mjs ack <taskId>                                  acknowledge the delivered adapter
//   node camp.mjs deploy <taskId> | chat <adapter> "<state>"   serve the adapter and ask it for a move
// Runs with ZG_DEV_MODE=true (the testnet fine-tuning provider lives on the dev contracts).
import fs from "node:fs";
import os from "node:os";
import { ethers } from "ethers";
import { createZGComputeNetworkBroker } from "@0gfoundation/0g-compute-ts-sdk";

process.env.ZG_DEV_MODE ??= "true";
const MODEL = "Qwen2.5-0.5B-Instruct";
const MOVES = ["jab", "cross", "hook_l", "hook_r", "uppercut", "body", "block", "dodge_l", "dodge_r", "step_in", "step_back"];
const SYSTEM = "You are a robot boxer's trained instincts. Given the fight state, reply with your next 1-3 moves, space separated, from: " + MOVES.join(" ") + ".";
const j = (x) => JSON.stringify(x, (k, v) => (typeof v === "bigint" ? v.toString() : v), 1);
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);

// State → one compact line. The same function formats prompts at fight time, so train and serve match.
export function stateLine(s) {
  return `opp_last=${s.o1 ?? "none"} opp_prev=${s.o2 ?? "none"} my_last=${s.m1 ?? "none"} dist=${(+s.dist).toFixed(1)} ` +
    `my_budget=${s.mine} opp_budget=${s.theirs} my_guard=${(+s.guard).toFixed(1)}`;
}

function synthetic(n = 150) {
  // A recognisable coaching style: counter hooks with slip-uppercut, jab-cross when far, block body shots, finish with body.
  const rnd = (a) => a[Math.floor(Math.random() * a.length)];
  const out = [];
  for (let i = 0; i < n; i++) {
    const s = { o1: rnd([null, "jab", "cross", "hook_l", "hook_r", "body", "uppercut"]), o2: rnd([null, "jab", "hook_l", "block"]),
      m1: rnd([null, "jab", "block", "cross"]), dist: 0.9 + Math.random() * 1.2, mine: 10 + Math.floor(Math.random() * 40),
      theirs: 5 + Math.floor(Math.random() * 45), guard: Math.random() };
    let a;
    if (s.o1 === "hook_l") a = "dodge_r uppercut";
    else if (s.o1 === "hook_r") a = "dodge_l hook_l";
    else if (s.o1 === "body") a = "block cross";
    else if (s.dist > 1.6) a = "step_in jab cross";
    else if (s.theirs < 12) a = "body body hook_r";
    else if (s.o1 === "jab") a = "block jab cross";
    else a = rnd(["jab cross", "jab jab cross", "body hook_l"]);
    out.push({ s, a });
  }
  return out;
}

function toJsonl(examples) {
  return examples.map(({ s, a }) => JSON.stringify({ messages: [
    { role: "system", content: SYSTEM }, { role: "user", content: stateLine(s) }, { role: "assistant", content: a }] })).join("\n") + "\n";
}

async function broker() {
  const env = Object.fromEntries(fs.readFileSync(os.homedir() + "/.iron-corner/wallets.env", "utf8").split("\n")
    .filter((l) => l.includes("=") && !l.startsWith("#")).map((l) => [l.slice(0, l.indexOf("=")), l.slice(l.indexOf("=") + 1)]));
  const w = new ethers.Wallet(env.OG_OPS_PRIVATE_KEY, new ethers.JsonRpcProvider("https://evmrpc-testnet.0g.ai"));
  return { b: await createZGComputeNetworkBroker(w), w };
}
async function provider(b) {
  const [svc] = await b.fineTuning.listService();
  if (!svc) throw new Error("no fine-tuning provider online");
  return svc[0];
}

const [cmd, a1, a2] = process.argv.slice(2);
if (cmd === "dataset") {
  const ex = a1 === "--synthetic" ? synthetic() : JSON.parse(fs.readFileSync(a1, "utf8"));
  fs.writeFileSync(a2, toJsonl(ex));
  log("wrote", ex.length, "examples to", a2);
} else if (cmd === "account") {
  const { b, w } = await broker();
  try { log("ledger exists", j(await b.ledger.getLedger()).slice(0, 300)); }
  catch { log("creating ledger with 0.3 0G"); await b.ledger.addLedger(0.3); log("ledger", j(await b.ledger.getLedger()).slice(0, 300)); }
  log("wallet balance", ethers.formatEther(await w.provider.getBalance(w.address)));
} else if (cmd === "start") {
  const { b } = await broker();
  const p = await provider(b);
  fs.writeFileSync("training.json", JSON.stringify({ neftune_noise_alpha: 5, num_train_epochs: 3, per_device_train_batch_size: 2, learning_rate: 0.0002, max_steps: 120 }));
  log("provider", p); log("acknowledging provider signer");
  await b.fineTuning.acknowledgeProviderSigner(p).catch((e) => log("ack signer:", e.message.slice(0, 160)));
  log("transferring 0.15 0G to the provider's fine-tuning account");
  await b.ledger.transferFund(p, "fine-tuning", ethers.parseEther("0.15")).catch((e) => log("transfer:", e.message.slice(0, 200)));
  log("uploading dataset", a1);
  const dataRoot = await b.fineTuning.uploadDataset(a1);
  log("dataset root (0G Storage)", dataRoot);
  const taskId = await b.fineTuning.createTask(p, MODEL, dataRoot, "training.json");
  log("TASK", taskId);
  fs.writeFileSync("last-task.json", j({ provider: p, taskId, dataRoot, at: new Date().toISOString() }));
} else if (cmd === "status") {
  const { b } = await broker();
  const p = await provider(b);
  const t = await b.fineTuning.getTask(p, a1);
  log("task", j(t).slice(0, 900));
  const l = await b.fineTuning.getLog(p, a1).catch((e) => "log: " + e.message);
  log("log tail\n" + String(l).split("\n").slice(-12).join("\n"));
} else if (cmd === "ack") {
  const { b } = await broker();
  const p = await provider(b);
  fs.mkdirSync("adapters", { recursive: true });
  await b.fineTuning.acknowledgeModel(p, a1, `adapters/${a1}.enc`);
  await b.fineTuning.decryptModel(p, a1, `adapters/${a1}.enc`, `adapters/${a1}.zip`);
  log("adapter saved", `adapters/${a1}.zip`);
} else if (cmd === "deploy") {
  const { b } = await broker();
  const p = await provider(b);
  log("deploy", j(await b.inference.deployAdapter(p, MODEL, a1, { wait: true })).slice(0, 600));
} else if (cmd === "chat") {
  const { b } = await broker();
  const p = await provider(b);
  const t0 = Date.now();
  const r = await b.inference.chatWithFineTunedModel(p, a1, a2);
  log(`(${Date.now() - t0} ms)`, j(r).slice(0, 600));
} else console.log("usage: dataset|account|start|status|ack|deploy|chat");
