// Full loop against a running server. Usage: npx tsx scripts/smoke.ts [baseUrl]
const BASE = process.argv[2] ?? process.env.BASE_URL ?? "http://127.0.0.1:8787";
let failed = 0;
const proofs: string[] = [];

async function call(method: string, path: string, body?: unknown) {
  const init: RequestInit = { method };
  if (body instanceof FormData) init.body = body;
  else if (body !== undefined) { init.body = JSON.stringify(body); init.headers = { "content-type": "application/json" }; }
  const r = await fetch(BASE + path, init);
  const ct = r.headers.get("content-type") ?? "";
  return { status: r.status, body: ct.includes("json") ? await r.json() : Buffer.from(await r.arrayBuffer()) } as { status: number; body: any };
}
async function step(name: string, fn: () => Promise<string | void>) {
  const t = Date.now();
  try { const info = await fn(); console.log(`PASS ${name} (${Date.now() - t} ms)${info ? "  " + info : ""}`); }
  catch (e: any) { failed++; console.log(`FAIL ${name}: ${e?.message ?? e}`); }
}
function check(cond: unknown, msg: string): asserts cond { if (!cond) throw new Error(msg); }
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

function wav(seconds = 0.5) { // 16 kHz mono 16-bit silence
  const n = 16000 * seconds * 2, b = Buffer.alloc(44 + n);
  b.write("RIFF", 0); b.writeUInt32LE(36 + n, 4); b.write("WAVEfmt ", 8); b.writeUInt32LE(16, 16); b.writeUInt16LE(1, 20);
  b.writeUInt16LE(1, 22); b.writeUInt32LE(16000, 24); b.writeUInt32LE(32000, 28); b.writeUInt16LE(2, 32); b.writeUInt16LE(16, 34);
  b.write("data", 36); b.writeUInt32LE(n, 40);
  return new Blob([b], { type: "audio/wav" });
}

let health: any, fightId = "", rootA = "", approvalId = "";

await step("health", async () => {
  const r = await call("GET", "/health"); check(r.status === 200, `status ${r.status}`); health = r.body;
  return JSON.stringify(r.body.modes) + ` pay=${r.body.payAsset} ledger=${r.body.ledgerTopic}`;
});
await step("list bots", async () => {
  const r = await call("GET", "/bots");
  check(r.body.length === 2 && r.body[0].botId === "bot_a", "expected bot_a and bot_b");
  return r.body.map((b: any) => `${b.botId}=${b.accountId} hbar=${b.hbar} usdc=${b.usdc} v${b.brainVersion}`).join(", ");
});
await step("coach-text command word", async () => {
  const r = await call("POST", "/train/bot_a/coach-text", { text: "Go body!" });
  check(r.body.command === "body" && r.body.learned === null, JSON.stringify(r.body));
});
await step("coach-text learns a phrase", async () => {
  const r = await call("POST", "/train/bot_a/coach-text", { text: "light him up" });
  check(r.body.learned?.moves?.length, JSON.stringify(r.body));
  return `${JSON.stringify(r.body.learned)} req=${r.body.requestId}`;
});
await step("coach audio (multipart)", async () => {
  const fd = new FormData(); fd.append("audio", wav(), "coach.wav"); fd.append("text", "back off");
  const r = await call("POST", "/train/bot_a/coach", fd);
  check(r.status === 200 && typeof r.body.text === "string", JSON.stringify(r.body));
  return `text="${r.body.text}" command=${r.body.command} req=${r.body.requestId}`;
});
await step("spar plan", async () => {
  const r = await call("POST", "/train/bot_a/spar", { state: { t: 3, selfBudget: 50, oppBudget: 50, distance: 1, oppLastMoves: ["jab"], selfLastMoves: [], lastHitBy: null } });
  check(["aggressive", "defensive", "counter", "safe_stop"].includes(r.body.stance), JSON.stringify(r.body));
  return JSON.stringify(r.body);
});
await step("save brain to 0G Storage", async () => {
  const r = await call("POST", "/train/bot_a/save");
  check(r.status === 200 && r.body.root, JSON.stringify(r.body)); rootA = r.body.root;
  proofs.push(`0G storage bot_a v${r.body.version}: root ${r.body.root} tx ${r.body.txHash}`);
  return `v${r.body.version} root=${r.body.root} tx=${r.body.txHash}`;
});
await step("read brain back (download + decrypt)", async () => {
  const r = await call("GET", `/bots/bot_a/brain?root=${rootA}`);
  check(r.status === 200 && r.body.brain.botId === "bot_a", JSON.stringify(r.body).slice(0, 200));
  return `version ${r.body.brain.version}, ${r.body.brain.phrases.length} phrases`;
});
await step("create fight", async () => {
  const r = await call("POST", "/fights", { a: "bot_a", b: "bot_b", stakes: "normal", budgetUsd: 0.1 });
  check(r.status === 200 && r.body.budgets.bot_a === 10, JSON.stringify(r.body)); fightId = r.body.fightId;
  return `${fightId} ledger=${r.body.ledgerTopic}`;
});
await step("decide x2", async () => {
  const s = { t: 1, selfBudget: 10, oppBudget: 10, distance: 1, selfGuard: 1, oppGuard: 1, oppLastMoves: [], selfLastMoves: [], lastHitBy: null };
  const a = await call("POST", `/fights/${fightId}/decide`, { botId: "bot_a", state: s });
  const b = await call("POST", `/fights/${fightId}/decide`, { botId: "bot_b", state: { ...s, distance: 3 } });
  check(a.body.stance && b.body.stance, "missing stance");
  return `a=${a.body.stance}:${a.body.combo ?? ""} b=${b.body.stance}:${b.body.combo ?? ""}`;
});
await step("live coach overrides next plan", async () => {
  const fd = new FormData(); fd.append("audio", wav(), "c.wav"); fd.append("botId", "bot_a"); fd.append("text", "uppercut");
  const c = await call("POST", `/fights/${fightId}/coach`, fd);
  check(c.status === 200, JSON.stringify(c.body));
  if (c.body.command) {
    const p = await call("POST", `/fights/${fightId}/decide`, { botId: "bot_a", state: {} });
    check(p.body.override && p.body.combo[0] === c.body.command, JSON.stringify(p.body));
  }
  return `text="${c.body.text}" command=${c.body.command}`;
});
await step("hits (clean, blocked, dodged, clean)", async () => {
  const hits = [["bot_a", "bot_b", "jab", "clean"], ["bot_b", "bot_a", "cross", "blocked"], ["bot_b", "bot_a", "hook_l", "dodged"], ["bot_b", "bot_a", "cross", "clean"]];
  let last: any;
  for (const [attacker, defender, move, result] of hits) {
    const t = Date.now();
    last = await call("POST", `/fights/${fightId}/hit`, { attacker, defender, move, result });
    check(last.status === 200 && Date.now() - t < 1000, `hit slow or failed: ${JSON.stringify(last.body)}`);
  }
  check(last.body.budgets.bot_b === 9 && last.body.budgets.bot_a === 8, JSON.stringify(last.body));
  return JSON.stringify(last.body.budgets);
});
await step("payments settle", async () => {
  for (let i = 0; i < 60; i++) {
    const r = await call("GET", `/fights/${fightId}`);
    const ps = r.body.payments;
    if (ps.length === 2 && ps.every((p: any) => p.status !== "pending")) {
      check(ps.every((p: any) => p.status === "settled"), JSON.stringify(ps));
      for (const p of ps) proofs.push(`x402 hit ${p.hitNo} ${p.from}->${p.to} ${p.cents}c: ${p.txId} ${p.hashscan ?? ""}`);
      return ps.map((p: any) => `${p.txId}`).join(", ");
    }
    await sleep(2000);
  }
  throw new Error("payments still pending after 120 s");
});
await step("end fight (lessons + brain save queued)", async () => {
  const r = await call("POST", `/fights/${fightId}/end`);
  check(r.status === 200 && r.body.lessons?.bot_a, JSON.stringify(r.body));
  return `winner=${r.body.winner} lessons=${JSON.stringify(r.body.lessons)}`;
});
await step("approval create", async () => {
  const r = await call("POST", "/approvals", { botId: "bot_a", action: "high_stakes" });
  check(r.body.approvalId && r.body.qrUri && r.body.userCode, JSON.stringify(r.body)); approvalId = r.body.approvalId;
  return `${approvalId} code=${r.body.userCode} qr=${r.body.qrUri}`;
});
await step("approval qr.png", async () => {
  const r = await call("GET", `/approvals/${approvalId}/qr.png`);
  check(r.status === 200 && Buffer.isBuffer(r.body) && r.body.subarray(1, 4).toString() === "PNG", "not a PNG");
});
await step("high stakes refused before approval", async () => {
  const r = await call("POST", "/fights", { stakes: "high", approvalId });
  check(r.status === 403, `status ${r.status}`);
});
await step("approval poll → approved", async () => {
  for (let i = 0; i < 20; i++) {
    const r = await call("GET", `/approvals/${approvalId}`);
    if (r.body.status !== "pending") { check(r.body.status === "approved", r.body.status); return; }
    await sleep(500);
  }
  throw new Error("still pending (live World mode needs a human)");
});
await step("high stakes fight uses approval once", async () => {
  const r = await call("POST", "/fights", { stakes: "high", approvalId, budgetUsd: 0.05 });
  check(r.status === 200, JSON.stringify(r.body));
  const again = await call("POST", "/fights", { stakes: "high", approvalId });
  check(again.status === 403, "approval reused");
  const s = await call("GET", `/approvals/${approvalId}`);
  check(s.body.status === "used", s.body.status);
});
await step("approval cancel", async () => {
  const c = await call("POST", "/approvals", { botId: "bot_a", action: "revive" });
  const r = await call("POST", `/approvals/${c.body.approvalId}/cancel`);
  check(r.body.status === "cancelled", r.body.status);
});
await step("bad input gives 4xx JSON error", async () => {
  const r = await call("POST", `/fights/${fightId}/hit`, { attacker: "bot_a", defender: "bot_b", move: "laser", result: "clean" });
  check(r.status === 400 && r.body.error, JSON.stringify(r.body));
});

console.log("\n" + (proofs.length ? proofs.join("\n") : "") + `\n${failed ? `${failed} step(s) FAILED` : "ALL PASS"}`);
process.exit(failed ? 1 : 0);
