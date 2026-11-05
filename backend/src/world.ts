// World ID approvals via the World IdP OIDC device flow. Mock mode auto-approves after a short delay.
import { env, modes, state, save, id, fail, type Approval } from "./state.ts";

// Production issuer: World mocks proofs for the hackathon, so the sandbox app is no longer needed.
const ISSUER = env.WORLD_ISSUER ?? "https://auth.world.org";
const ACR = env.WORLD_ACR ?? "https://world.org/oidc/acr/orb-face";
const MOCK_APPROVE_MS = Number(env.MOCK_APPROVE_MS ?? 3000);
const running = new Map<string, AbortController>();

let cfg: Promise<any> | null = null;
async function config() {
  cfg ??= (async () => {
    const oc = await import("openid-client");
    const c = await oc.discovery(new URL(ISSUER), env.WORLD_CLIENT_ID!, env.WORLD_CLIENT_SECRET || undefined,
      env.WORLD_CLIENT_SECRET ? undefined : oc.None());
    return { oc, c };
  })();
  cfg.catch(() => (cfg = null));
  return cfg;
}

export async function create(botId: string, action: Approval["action"]): Promise<Approval> {
  if (!state.bots[botId]) fail(404, "unknown bot");
  if (!["high_stakes", "revive", "cash_out"].includes(action)) fail(400, "bad action");
  const approvalId = id("ap"), startedAt = Math.floor(Date.now() / 1000);
  let a: Approval;
  if (modes.world === "mock") {
    a = { approvalId, botId, action, status: "pending", qrUri: `https://world.org/mock-approve?id=${approvalId}`,
      userCode: approvalId.slice(-6).toUpperCase(), expiresAt: new Date(Date.now() + 300_000).toISOString(), startedAt, mock: true };
    setTimeout(() => { if (a.status === "pending") { a.status = "approved"; a.sub = "mock-sub"; save(); } }, MOCK_APPROVE_MS);
  } else {
    const { oc, c } = await config();
    const da = await oc.initiateDeviceAuthorization(c, {
      scope: "openid",
      claims: JSON.stringify({ id_token: { acr: { essential: true, values: [ACR] } } }),
    });
    a = { approvalId, botId, action, status: "pending", qrUri: da.verification_uri_complete ?? da.verification_uri,
      userCode: da.user_code, expiresAt: new Date(Date.now() + da.expires_in * 1000).toISOString(), startedAt };
    const ctl = new AbortController();
    running.set(approvalId, ctl);
    poll(a, oc, c, da, ctl.signal).finally(() => { running.delete(approvalId); save(); });
  }
  state.approvals[approvalId] = a;
  save();
  return a;
}

async function poll(a: Approval, oc: any, c: any, da: any, signal: AbortSignal) {
  try {
    const tokens = await oc.pollDeviceAuthorizationGrant(c, da, undefined, { signal });
    const cl = tokens.claims();
    const bot = state.bots[a.botId];
    const amr: string[] = Array.isArray(cl?.amr) ? cl.amr : [];
    const problem =
      !cl ? "no id_token" :
      cl.iss !== c.serverMetadata().issuer ? "wrong issuer" :
      cl.acr !== ACR ? `acr ${cl.acr}` :
      !amr.includes("pop") ? "amr lacks pop" :
      typeof cl.auth_time !== "number" || cl.auth_time < a.startedAt - 5 ? "stale auth_time" :
      bot.ownerSub && bot.ownerSub !== cl.sub ? "not this bot's owner" : null;
    if (a.status !== "pending") return; // cancelled meanwhile
    if (problem) { a.status = "denied"; a.error = problem; return; }
    bot.ownerSub ??= cl.sub; // first approval pins the owner
    a.sub = cl.sub; a.status = "approved";
  } catch (e: any) {
    if (a.status !== "pending") return;
    const code = e?.error ?? e?.code ?? e?.name;
    a.status = code === "access_denied" ? "denied" : code === "expired_token" ? "expired" : code === "AbortError" || signal.aborted ? "cancelled" : "denied";
    a.error = String(e?.error_description ?? e?.message ?? e).slice(0, 200);
  }
}

export function get(approvalId: string) {
  const a = state.approvals[approvalId] ?? fail(404, "unknown approval");
  if (a.status === "pending" && Date.parse(a.expiresAt) < Date.now()) { a.status = "expired"; running.get(approvalId)?.abort(); save(); }
  return a;
}

export function cancel(approvalId: string) {
  const a = get(approvalId);
  if (a.status === "pending" || a.status === "approved") { a.status = "cancelled"; running.get(approvalId)?.abort(); save(); }
  return a;
}

// Single use: check and mark used in one synchronous step, before the action runs.
export function consume(approvalId: unknown, botId: string, action: Approval["action"]) {
  if (typeof approvalId !== "string") fail(403, `${action} needs an approved approvalId`);
  const a = get(approvalId as string);
  if (a.status !== "approved") fail(403, `approval is ${a.status}`);
  if (a.action !== action || a.botId !== botId) fail(403, "approval is for a different action or bot");
  a.status = "used";
  save();
}
