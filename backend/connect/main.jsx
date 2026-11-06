// The /connect page: sign in with Privy (our app), paste a pc.0g.ai API key, link it to the fighter.
// Bundled to ../public/connect.js (npm run build); the server's HTML sets window.PRIVY_APP_ID.
import React, { useState } from "react";
import { createRoot } from "react-dom/client";
import { PrivyProvider, usePrivy } from "@privy-io/react-auth";

const code = new URLSearchParams(location.search).get("c");

function App() {
  const { ready, authenticated, login, getAccessToken, user, logout } = usePrivy();
  const [key, setKey] = useState(""), [msg, setMsg] = useState(null), [done, setDone] = useState(null), [busy, setBusy] = useState(false);
  if (!code) return <p className="err">Open this page from the game.</p>;
  if (!ready) return <p>Loading…</p>;
  if (done) return (<div><h1 className="ok">Linked ✓</h1>
    <p>{done.name}'s 0G calls now use your credits (key …{done.last4}).</p><p>You can close this and go back to the game.</p></div>);
  if (!authenticated) return (<div><h1>Use your own 0G credits</h1>
    <p>Sign in with your 0G wallet. Your fighter's AI (fight decisions, coaching, lessons) will run on your 0G Private Computer credits, including daily iAI credits.</p>
    <button onClick={login}>Sign in</button></div>);
  const wallet = user?.wallet?.address ?? null;
  async function go() {
    setBusy(true); setMsg(null);
    try {
      const r = await fetch("/connect", { method: "POST", headers: { "content-type": "application/json" },
        body: JSON.stringify({ code, privyToken: await getAccessToken(), apiKey: key.trim(), wallet }) });
      const j = await r.json();
      if (!r.ok) throw new Error(j.error || `error ${r.status}`);
      setDone(j); location.href = "ironcorner://zg?linked=1";
    } catch (e) { setMsg(e.message); } finally { setBusy(false); }
  }
  return (<div><h1>Link your 0G account</h1>
    <p>Signed in{wallet ? <> as <code>{wallet.slice(0, 6)}…{wallet.slice(-4)}</code></> : null}. <a href="#" onClick={(e) => { e.preventDefault(); logout(); }}>Switch</a></p>
    <p>Open <a href="https://pc.0g.ai/dashboard" target="_blank">pc.0g.ai → API Keys</a>, create a key for Iron Corner and paste it here. It stays on our server, encrypted, and is only used for your fighter.</p>
    <input placeholder="sk-…" value={key} onChange={(e) => setKey(e.target.value)} autoComplete="off" spellCheck={false} />
    <button onClick={go} disabled={busy || !key.trim()}>{busy ? "Checking with 0G…" : "Link"}</button>
    {msg ? <p className="err">{msg}</p> : null}</div>);
}

createRoot(document.getElementById("root")).render(
  <PrivyProvider appId={window.PRIVY_APP_ID} config={{ loginMethods: ["wallet", "email", "google"], appearance: { theme: "dark" },
    embeddedWallets: { ethereum: { createOnLogin: "users-without-wallets" } } }}><App /></PrivyProvider>);
