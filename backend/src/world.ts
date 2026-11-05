// World ID approvals via the World IdP OIDC device flow. Mock mode auto-approves after a short delay.
import { env, modes, state, save, id, fail, type Approval } from "./state.ts";

// Production issuer: World mocks proofs for the hackathon, so the sandbox app is no longer needed.
const ISSUER = env.WORLD_ISSUER ?? "https://auth.world.org";
// WORLD_ACR unset: no acr claim is sent, so the app's portal default applies (Selfie Check or Orb, with Face Auth
// on each new proof). Set it (e.g. .../orb-face) to require one level. Either way the token must show a fresh proof.
const ACR = env.WORLD_ACR || null;
const ACCEPT = (env.WORLD_ACCEPT_ACR ?? "https://world.org/oidc/acr/orb,https://world.org/oidc/acr/orb-face,https://world.org/oidc/acr/selfie,https://world.org/oidc/acr/selfie-face").split(",");
const MOCK_APPROVE_MS = Number(env.MOCK_APPROVE_MS ?? 3000);
const running = new Map<string, AbortController>();

let cfg: Promise<any> | null = null;
async function config() {
  cfg ??= (async () => {
    const oc = await import("openid-client");
    // The app is registered for client_secret_basic; openid-client defaults to client_secret_post when only a
    // secret string is passed, which World rejects with invalid_client. So name the method explicitly.
    const c = await oc.discovery(new URL(ISSUER), env.WORLD_CLIENT_ID!, undefined,
      env.WORLD_CLIENT_SECRET ? oc.ClientSecretBasic(env.WORLD_CLIENT_SECRET) : oc.None());
    return { oc, c };
  })();
  cfg.catch(() => (cfg = null));
  return cfg;
}

// sign_in: botId is "" and userId names the player; approval sets user.worldSub (one verified fighter per World subject).
export async function create(botId: string, action: Approval["action"], userId?: string, mockSub?: string): Promise<Approval> {
  if (action === "sign_in") { if (!userId || !state.users[userId]) fail(401, "sign in needs a user"); }
  else if (!state.bots[botId]) fail(404, "unknown bot");
  if (!["high_stakes", "revive", "cash_out", "sign_in"].includes(action)) fail(400, "bad action");
  const approvalId = id("ap"), startedAt = Math.floor(Date.now() / 1000);
  let a: Approval;
  if (modes.world === "mock") {
    a = { approvalId, botId, action, status: "pending", qrUri: `https://world.org/mock-approve?id=${approvalId}`,
      userCode: approvalId.slice(-6).toUpperCase(), expiresAt: new Date(Date.now() + 300_000).toISOString(), startedAt, mock: true,
      ...(userId ? { userId } : {}) };
    setTimeout(() => {
      if (a.status !== "pending") return;
      if (action === "sign_in") signIn(a, mockSub || `mock-sub-${userId}`); else { a.status = "approved"; a.sub = "mock-sub"; }
      save();
    }, MOCK_APPROVE_MS);
  } else {
    const { oc, c } = await config();
    const da = await oc.initiateDeviceAuthorization(c, {
      scope: "openid",
      ...(ACR ? { claims: JSON.stringify({ id_token: { acr: { essential: true, values: [ACR] } } }) } : {}),
    });
    a = { approvalId, botId, action, status: "pending", qrUri: da.verification_uri_complete ?? da.verification_uri,
      userCode: da.user_code, expiresAt: new Date(Date.now() + da.expires_in * 1000).toISOString(), startedAt, ...(userId ? { userId } : {}) };
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
      (ACR ? cl.acr !== ACR : !ACCEPT.includes(String(cl.acr))) ? `acr ${cl.acr ?? "missing (unverified account)"}` :
      !amr.includes("pop") ? "amr lacks pop" :
      typeof cl.auth_time !== "number" || cl.auth_time < a.startedAt - 5 ? "stale auth_time" :
      bot?.ownerSub && bot.ownerSub !== cl.sub ? "not this bot's owner" : null;
    if (a.status !== "pending") return; // cancelled meanwhile
    if (problem) { a.status = "denied"; a.error = problem; return; }
    if (a.action === "sign_in") return signIn(a, cl.sub);
    bot.ownerSub ??= cl.sub; // first approval pins the owner
    a.sub = cl.sub; a.status = "approved";
  } catch (e: any) {
    if (a.status !== "pending") return;
    const code = e?.error ?? e?.code ?? e?.name;
    a.status = code === "access_denied" ? "denied" : code === "expired_token" ? "expired" : code === "AbortError" || signal.aborted ? "cancelled" : "denied";
    a.error = String(e?.error_description ?? e?.message ?? e).slice(0, 200);
  }
}

// One verified human, one verified fighter: a World subject already behind another player's fighter is refused.
// A previous fighterless sign-in with the same subject (lost token) is released.
function signIn(a: Approval, sub: string) {
  const u = state.users[a.userId!];
  const others = Object.values(state.users).filter((x) => x.userId !== u.userId && x.worldSub === sub);
  if (others.some((x) => x.botId)) { a.status = "denied"; a.error = "world_already_used"; return; }
  for (const x of others) { x.worldSub = null; x.worldVerified = false; }
  u.worldSub = sub; u.worldVerified = true;
  a.sub = sub; a.status = "approved";
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
