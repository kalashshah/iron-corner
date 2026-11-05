using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

// One scene, four modes. Owns the camera, the decision/poll loops, and all screens.
public class Game : MonoBehaviour
{
    public Fighter a, b;   // a = your bot (bot_a), b = opponent / sparring dummy (bot_b)
    public Camera cam;

    enum Mode { Home, Train, Fight, Brain }
    Mode mode = Mode.Home;
    int session;           // bumps on every mode change; loops from an old session exit
    string fightId, stakes = "normal", ledgerTopic;
    bool over, replaying;
    float fightStart;
    float budgetUsd = 0.5f;
    readonly Vector3 startA = new(0, 0, -1.15f), startB = new(0, 0, 1.15f);
    float ringY;

    // UI
    Transform canvas;
    GameObject home, train, fight, brain, modal, koPanel;
    Text badge, toast, cardName, cardMoney, cardBrain, cardAcct, serverStatus, brainList;
    InputField serverField, trainField, fightField;
    Button fightBtn, highBtn; bool aAlive = true, aNeedsRevive;
    Text trainTranscript, trainSaved, fightTranscript, fightTitle;
    Text[] barName = new Text[2], barMoney = new Text[2];
    RectTransform[] barFill = new RectTransform[2];
    Transform ticker;
    Bubble bubA, bubB;
    Text modalAction, modalCode, modalStatus; RawImage modalQr; Button modalBtn; string approvalId; bool approvalOpen;
    Text koTitle, koSub, koLessons; Button koLedger;
    readonly List<(Text t, float born, Vector3 world)> popups = new();
    float toastUntil;
    string captureDir;

    class Bubble { public RectTransform root; public Text say, meta; public float until; public Fighter f; }

    void Start()
    {
        Application.targetFrameRate = 60;
        Screen.orientation = ScreenOrientation.AutoRotation;
        Screen.autorotateToPortrait = Screen.autorotateToPortraitUpsideDown = false;
        Screen.autorotateToLandscapeLeft = Screen.autorotateToLandscapeRight = true;
        ringY = a.transform.position.y;
        a.opp = b; b.opp = a;
        a.OnHit += OnHit; b.OnHit += OnHit;
        UI.Init();
        BuildUI();
        ResetFighters();
        Show(Mode.Home);
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "-capture");
        if (i >= 0 && i + 1 < args.Length) { captureDir = args[i + 1]; StartCoroutine(Capture()); }
        else StartCoroutine(Connect());
    }

    // ---------------------------------------------------------------- connection
    IEnumerator Connect()
    {
        serverStatus.text = "Connecting to " + Api.BaseUrl + " …";
        yield return Api.Probe(null);
        serverStatus.text = Api.Offline ? $"<color=#F2A33A>Can't reach {Api.BaseUrl} — running the local OFFLINE sim</color>" : $"<color=#5CDB85>Connected</color> · {Api.BaseUrl}";
        badge.text = Api.Offline ? "OFFLINE · local sim, no real payments" : "LIVE · " + Api.BaseUrl.Replace("http://", "").Replace("https://", "");
        badge.color = Api.Offline ? UI.Accent : UI.Green;
        yield return RefreshHome();
    }

    IEnumerator RefreshHome()
    {
        yield return Api.Call("GET", "/bots", null, r =>
        {
            foreach (var bot in r)
            {
                var f = (string)bot["botId"] == a.botId ? a : (string)bot["botId"] == b.botId ? b : null;
                if (f != null) f.displayName = (string)bot["name"] ?? f.displayName;
                if (f != a) continue;
                cardName.text = a.displayName.ToUpperInvariant();
                cardMoney.text = (string)bot["payAsset"] == "hbar"
                    ? $"{(double?)bot["hbar"] ?? 0:0.00} <size=26><color=#9EA5B0>HBAR · Hedera testnet</color></size>"
                    : $"${(double?)bot["usdc"] ?? 0:0.00} <size=26><color=#9EA5B0>USDC · Hedera testnet</color></size>";
                cardBrain.text = $"Brain v{bot["brainVersion"]}  <color=#9EA5B0>root {UI.Short((string)bot["brainRoot"])}</color>";
                aAlive = (bool?)bot["alive"] != false;
                aNeedsRevive = (bool?)bot["needsRevive"] == true;
                int repair = (int?)bot["repairIn"] ?? 0;
                cardAcct.text = $"{bot["accountId"]}" + (aAlive ? "" : aNeedsRevive
                    ? "  <color=#F2523F>KNOCKED OUT in a high-stakes fight — World ID revive needed</color>"
                    : $"  <color=#F2A33A>Repairing after a KO · {repair} s</color>");
                fightBtn.SetLabel(aAlive ? "FIGHT" : aNeedsRevive ? "REVIVE  <size=24><color=#9EA5B0>World ID</color></size>" : $"REPAIRING · {repair} s");
                highBtn.gameObject.SetActive(aAlive);
                if (!aAlive && !aNeedsRevive) StartCoroutine(RefreshSoon(Mathf.Clamp(repair, 1, 5)));
            }
        }, e => cardAcct.text = "<color=#F2523F>" + e + "</color>");
    }

    IEnumerator RefreshSoon(float s)
    {
        int sess = session;
        yield return new WaitForSecondsRealtime(s);
        if (sess == session && mode == Mode.Home) yield return RefreshHome();
    }

    // ---------------------------------------------------------------- modes
    void Show(Mode m)
    {
        session++;
        mode = m;
        Time.timeScale = 1f;
        home.SetActive(m == Mode.Home);
        train.SetActive(m == Mode.Train);
        fight.SetActive(m == Mode.Fight);
        brain.SetActive(m == Mode.Brain);
        koPanel.SetActive(false);
        bubA.root.gameObject.SetActive(false); bubB.root.gameObject.SetActive(false);
        if (m == Mode.Home || m == Mode.Brain) { ResetFighters(); }
        if (m == Mode.Home) StartCoroutine(RefreshHome());
    }

    void ResetFighters()
    {
        foreach (var f in new[] { a, b }) { f.active = false; f.ResetFor(f.startBudget); }
        a.transform.SetPositionAndRotation(startA + Vector3.up * ringY, Quaternion.identity);
        b.transform.SetPositionAndRotation(startB + Vector3.up * ringY, Quaternion.Euler(0, 180, 0));
    }

    void GoHome() { session++; replaying = false; a.scripted = b.scripted = false; Show(Mode.Home); }

    void StartTrain()
    {
        Show(Mode.Train);
        ResetFighters();
        a.active = b.active = true;
        trainTranscript.text = "Hold the mic and coach: try “light him up”.";
        trainSaved.text = "";
        StartCoroutine(SparLoop(session));
        StartCoroutine(DummyLoop(session));
    }

    void StartFight(string stakesKind, string approval)
    {
        var body = new JObject { ["a"] = a.botId, ["b"] = b.botId, ["stakes"] = stakesKind, ["budgetUsd"] = budgetUsd };
        if (approval != null) body["approvalId"] = approval;
        StartCoroutine(Api.Call("POST", "/fights", body, r =>
        {
            Show(Mode.Fight);
            ResetFighters();
            fightId = (string)r["fightId"]; stakes = stakesKind; over = false; ledgerTopic = null; fightStart = Time.time;
            a.ResetFor((int?)r["budgets"]?[a.botId] ?? 50);
            b.ResetFor((int?)r["budgets"]?[b.botId] ?? 50);
            a.active = b.active = true;
            fightTitle.text = (stakesKind == "high" ? "<color=#FF9421>HIGH STAKES</color>  ·  " : "") + $"<color=#9EA5B0>{fightId}</color>";
            fightTranscript.text = "Hold to coach live — your call overrides the next plan.";
            foreach (Transform c in ticker) Destroy(c.gameObject);
            UpdateBars();
            StartCoroutine(DecideLoop(session, a));
            StartCoroutine(DecideLoop(session, b));
            StartCoroutine(PollLoop(session));
        }, e => Toast("Couldn't start fight: " + e)));
    }

    // ---------------------------------------------------------------- loops
    JObject State(Fighter f) => new()
    {
        ["t"] = Math.Round(Time.time - fightStart, 2),
        ["selfBudget"] = f.budget, ["oppBudget"] = f.opp.budget,
        ["distance"] = Math.Round(f.Distance, 2),
        ["selfGuard"] = Math.Round(f.guard, 2), ["oppGuard"] = Math.Round(f.opp.guard, 2),
        ["oppLastMoves"] = new JArray(f.opp.lastMoves), ["selfLastMoves"] = new JArray(f.lastMoves),
        ["lastHitBy"] = f.lastHitBy,
    };

    // Every ~1.5 s per bot. The coroutine waits for the reply, so only one request is in flight per bot,
    // and the fighter keeps working through its current queue meanwhile.
    IEnumerator DecideLoop(int s, Fighter f)
    {
        yield return new WaitForSecondsRealtime(f == a ? 0.1f : 0.8f);
        while (s == session && !over)
        {
            float t0 = Time.realtimeSinceStartup;
            yield return Api.Call("POST", $"/fights/{fightId}/decide", new JObject { ["botId"] = f.botId, ["state"] = State(f) },
                r => { if (s == session && !over) ApplyPlan(f, r); },
                e => { if (s == session) ApplyPlan(f, new JObject { ["stance"] = "safe_stop" }); });
            yield return new WaitForSecondsRealtime(Mathf.Max(0.2f, 1.5f - (Time.realtimeSinceStartup - t0)));
        }
    }

    IEnumerator SparLoop(int s)
    {
        while (s == session)
        {
            float t0 = Time.realtimeSinceStartup;
            yield return Api.Call("POST", $"/train/{a.botId}/spar", new JObject { ["state"] = State(a) },
                r => { if (s == session) ApplyPlan(a, r); }, e => { if (s == session) ApplyPlan(a, new JObject { ["stance"] = "safe_stop" }); });
            yield return new WaitForSecondsRealtime(Mathf.Max(0.2f, 1.5f - (Time.realtimeSinceStartup - t0)));
        }
    }

    // The dummy is local on purpose: a low-aggression punching bag, not a second 0G client.
    IEnumerator DummyLoop(int s)
    {
        while (s == session)
        {
            if (!b.Busy) ApplyPlan(b, OfflineSim.Plan(State(b), 0.25f), quiet: true);
            yield return new WaitForSecondsRealtime(1.8f);
        }
    }

    void ApplyPlan(Fighter f, JToken plan, bool quiet = false)
    {
        var stance = (string)plan["stance"];
        if (stance == "safe_stop")
        {
            f.holdBlock = true;
            f.SetQueue(Array.Empty<string>());
            if (!quiet) Say(f, "Covering up — no fresh plan (safe stop)", "0G timed out or failed · holding guard", UI.Accent);
            return;
        }
        f.holdBlock = false;
        var moves = new List<string>();
        if ((string)plan["trigger"] == "when_close" && f.Distance > Fighter.Reach) moves.Add("step_in");
        if ((string)plan["trigger"] == "after_block") moves.Add("block");
        if (plan["combo"] is JArray combo) moves.AddRange(combo.Select(x => (string)x));
        f.SetQueue(moves);
        if (quiet) return;
        string req = (string)plan["requestId"];
        bool tee = (bool?)plan["teeVerified"] == true;
        string meta = (string.IsNullOrEmpty(req) ? "" : "0G " + UI.Short(req, 8)) + (tee ? "  <color=#5CDB85>TEE verified</color>" : (bool?)plan["mock"] == true ? "  <color=#F2A33A>offline</color>" : "");
        Say(f, (string)plan["say"] ?? stance, $"{stance} · {string.Join(" ", moves)}\n{meta}", UI.Ink);
    }

    IEnumerator PollLoop(int s)
    {
        while (s == session)
        {
            yield return Api.Call("GET", $"/fights/{fightId}", null, r =>
            {
                if (s != session) return;
                ledgerTopic = (string)r["ledgerTopic"];
                if (r["budgets"] is JObject bud) { a.budget = (int?)bud[a.botId] ?? a.budget; b.budget = (int?)bud[b.botId] ?? b.budget; UpdateBars(); }
                if (r["payments"] is JArray pays) RenderTicker(pays);
                if ((string)r["status"] == "over" && !over)
                {
                    var w = (string)r["winner"];
                    KO(w == a.botId ? b : a);
                }
            });
            yield return new WaitForSecondsRealtime(0.5f);
        }
    }

    // ---------------------------------------------------------------- hits & KO
    void OnHit(Fighter att, string move, string result)
    {
        var def = att.opp;
        var head = Head(def) + Vector3.up * 0.25f;
        if (result == "clean") Popup(mode == Mode.Fight ? "-" + UI.Usd(Fighter.Damage(move)) : move.ToUpperInvariant().Replace('_', ' '), UI.Red, head);
        else Popup(result.ToUpperInvariant(), result == "blocked" ? UI.Dim : UI.Brass, head);
        if (mode != Mode.Fight || over || replaying) return;
        int s = session;
        StartCoroutine(Api.Call("POST", $"/fights/{fightId}/hit", new JObject { ["attacker"] = att.botId, ["defender"] = def.botId, ["move"] = move, ["result"] = result }, r =>
        {
            if (s != session) return;
            if (r["budgets"] is JObject bud) { a.budget = (int?)bud[a.botId] ?? a.budget; b.budget = (int?)bud[b.botId] ?? b.budget; UpdateBars(); }
            var ko = (string)r["ko"];
            if (!string.IsNullOrEmpty(ko) && !over) KO(ko == a.botId ? a : b);
        }));
    }

    void KO(Fighter loser)
    {
        over = true;
        var winner = loser.opp;
        loser.KnockOut();
        winner.Victory();
        UpdateBars();
        StartCoroutine(KOSequence(session, loser, winner));
    }

    IEnumerator KOSequence(int s, Fighter loser, Fighter winner)
    {
        Time.timeScale = 0.3f;
        yield return new WaitForSecondsRealtime(1.6f);
        Time.timeScale = 1f;
        if (s != session) yield break;
        koTitle.text = "KNOCKED OUT — wallet empty";
        koSub.text = $"{winner.displayName} wins. {loser.displayName}'s fight budget can't cover the next hit.";
        koLessons.text = "Saving lessons to 0G…";
        koLedger.gameObject.SetActive(!Api.Offline && !string.IsNullOrEmpty(ledgerTopic) && ledgerTopic != "offline");
        koPanel.SetActive(true);
        yield return Api.Call("POST", $"/fights/{fightId}/end", new JObject(), r =>
        {
            var l = r["lessons"]?[a.botId] as JArray;
            koLessons.text = l != null && l.Count > 0 ? "Lessons for " + a.displayName + ":\n• " + string.Join("\n• ", l.Select(x => (string)x)) : "";
        }, e => koLessons.text = "");
    }

    void UpdateBars()
    {
        var fs = new[] { a, b };
        for (int i = 0; i < 2; i++)
        {
            var f = fs[i];
            float k = f.startBudget > 0 ? Mathf.Clamp01((float)f.budget / f.startBudget) : 0;
            barName[i].text = f.displayName.ToUpperInvariant() + (f.down ? "  <color=#F2523F>KO</color>" : "");
            barMoney[i].text = UI.Usd(f.budget);
            if (i == 0) barFill[i].anchorMax = new Vector2(k, 1); else barFill[i].anchorMin = new Vector2(1 - k, 0);
            barFill[i].GetComponent<Image>().color = k > 0.35f ? UI.Brass : UI.Red;
        }
    }

    string NameOf(string botId) => botId == a.botId ? a.displayName : botId == b.botId ? b.displayName : botId;

    void RenderTicker(JArray pays)
    {
        foreach (Transform c in ticker) Destroy(c.gameObject);
        foreach (var p in pays.Reverse().Take(5))
        {
            string st = (string)p["status"];
            string mark = st == "settled" ? "<color=#5CDB85>✓ settled</color>" : st == "failed" ? "<color=#F2523F>✗ failed</color>" : "<color=#F2A33A>… pending</color>";
            string url = (string)p["hashscan"];
            var row = UI.Btn(ticker, "", new Color(0, 0, 0, 0.55f), UI.Ink, string.IsNullOrEmpty(url) ? null : () => Application.OpenURL(url)).H(70);
            var lbl = row.GetComponentInChildren<Text>();
            lbl.alignment = TextAnchor.MiddleLeft; lbl.fontStyle = FontStyle.Normal; lbl.fontSize = 25;
            lbl.text = $"<b><color=#F2523F>-{UI.Usd((double)p["cents"]).TrimStart('-')}</color></b>  {NameOf((string)p["from"])} → {NameOf((string)p["to"])}  {mark}\n<size=19><color=#9EA5B0>tx {p["txId"]}{(string.IsNullOrEmpty(url) ? "" : "  ↗ HashScan")}</color></size>";
        }
    }

    // ---------------------------------------------------------------- coaching
    bool recording;
    void MicDown(Text transcript)
    {
        recording = Mic.Start();
        transcript.text = recording ? "<color=#FF9421>● Listening…</color> release to send" : (Api.Offline ? "No mic — offline sim will pretend you said “light him up”." : "No microphone — type instead.");
        if (!recording && Api.Offline) recording = true;
    }

    void MicUp(Text transcript)
    {
        if (!recording) return;
        recording = false;
        var wav = Mic.Stop();
        if (wav == null && !Api.Offline) { transcript.text = "Too short — hold the button while you talk."; return; }
        wav ??= Mic.Wav(new float[1600], 16000, 1);
        transcript.text = "Sending to 0G whisper…";
        if (mode == Mode.Train) StartCoroutine(Api.Upload($"/train/{a.botId}/coach", wav, null, r => OnCoach(r, transcript), e => transcript.text = "Coach failed: " + e));
        else if (mode == Mode.Fight) StartCoroutine(Api.Upload($"/fights/{fightId}/coach", wav, new Dictionary<string, string> { ["botId"] = a.botId }, r => OnCoach(r, transcript), e => transcript.text = "Coach failed: " + e));
    }

    void SendTyped(InputField field, Text transcript)
    {
        var text = field.text.Trim();
        if (text.Length == 0) return;
        field.text = "";
        transcript.text = "…";
        if (mode == Mode.Train) StartCoroutine(Api.Call("POST", $"/train/{a.botId}/coach-text", new JObject { ["text"] = text }, r => OnCoach(r, transcript), e => transcript.text = "Coach failed: " + e));
        else if (mode == Mode.Fight) StartCoroutine(Api.Call("POST", $"/fights/{fightId}/coach-text", new JObject { ["botId"] = a.botId, ["text"] = text }, r => OnCoach(r, transcript), e => transcript.text = "Coach failed: " + e));
    }

    void OnCoach(JToken r, Text transcript)
    {
        transcript.text = $"“{r["text"]}”" + (r["requestId"] != null ? $"  <size=20><color=#9EA5B0>0G {UI.Short((string)r["requestId"], 8)}</color></size>" : "");
        if (r["learned"] is JObject learned)
        {
            Toast($"Learned: ‘{learned["say"]}’ = {learned["means"]}");
            if (learned["moves"] is JArray mv) a.SetQueue(mv.Select(x => (string)x), front: true);
            a.holdBlock = false;
            return;
        }
        var moves = r["moves"] is JArray mvs && mvs.Count > 0 ? mvs.Select(x => (string)x).ToArray() : !string.IsNullOrEmpty((string)r["command"]) ? new[] { (string)r["command"] } : null;
        if (moves == null) return;
        Toast("Coach: " + string.Join(" ", moves).Replace('_', ' '));
        // In a fight the backend turns the call into the bot's next plan (override); in training we play it right away.
        if (mode == Mode.Train) { a.holdBlock = false; a.SetQueue(moves, front: true); }
    }

    void SaveBrain()
    {
        trainSaved.text = "Saving encrypted brain to 0G Storage…";
        StartCoroutine(Api.Call("POST", $"/train/{a.botId}/save", new JObject(), r =>
        {
            trainSaved.text = $"<b>Brain v{r["version"]} saved</b>\n<size=22><color=#9EA5B0>root {UI.Short((string)r["root"], 10)}</color></size>" + ((bool?)r["mock"] == true ? "  <size=20><color=#F2A33A>offline</color></size>" : "");
        }, e => trainSaved.text = "<color=#F2523F>Save failed: " + e + "</color>"));
    }

    // ---------------------------------------------------------------- approvals
    void HighStakes() => Approve("high_stakes", "Enter high-stakes match", id => StartFight("high", id));

    // A KO leaves your bot alive=false; it needs a human-approved revive before its next fight.
    void Revive() => Approve("revive", "Revive " + a.displayName, id =>
        StartCoroutine(Api.Call("POST", $"/bots/{a.botId}/revive", new JObject { ["approvalId"] = id }, _ => { Toast(a.displayName + " is back on its feet"); StartCoroutine(RefreshHome()); }, e => Toast("Revive failed: " + e))));

    void Approve(string action, string label, Action<string> onApproved)
    {
        StartCoroutine(Api.Call("POST", "/approvals", new JObject { ["botId"] = a.botId, ["action"] = action }, r =>
        {
            approvalId = (string)r["approvalId"];
            approvalOpen = true;
            modal.SetActive(true);
            modalAction.text = label;
            modalCode.text = $"Code  <b>{r["userCode"]}</b>";
            modalStatus.text = "<color=#F2A33A>Waiting for World ID approval…</color>" + (Api.Offline ? "\n<size=20>OFFLINE: placeholder code, auto-approves in a few seconds</size>" : "");
            modalBtn.SetLabel("CANCEL");
            modalQr.texture = null;
            StartCoroutine(Api.Texture($"/approvals/{approvalId}/qr.png", t => { if (modalQr != null) modalQr.texture = t; }));
            StartCoroutine(PollApproval(approvalId, onApproved));
        }, e => Toast("Approval failed: " + e)));
    }

    IEnumerator PollApproval(string id, Action<string> onApproved)
    {
        while (approvalOpen && approvalId == id)
        {
            yield return new WaitForSecondsRealtime(1f);
            string status = null;
            yield return Api.Call("GET", $"/approvals/{id}", null, r => status = (string)r["status"]);
            if (!approvalOpen || approvalId != id) yield break;
            if (status == "approved")
            {
                modalStatus.text = "<color=#5CDB85>Approved ✓</color>";
                yield return new WaitForSecondsRealtime(0.8f);
                CloseModal(false);
                onApproved(id);
                yield break;
            }
            if (status is "denied" or "expired" or "cancelled" or "used")
            {
                modalStatus.text = $"<color=#F2523F>Not approved — nothing happened</color>\n<size=22>({status})</size>";
                modalBtn.SetLabel("CLOSE");
                approvalOpen = false;
                yield break;
            }
        }
    }

    void CloseModal(bool cancel)
    {
        if (cancel && approvalOpen && approvalId != null) StartCoroutine(Api.Call("POST", $"/approvals/{approvalId}/cancel", new JObject(), null));
        approvalOpen = false;
        modal.SetActive(false);
    }

    // ---------------------------------------------------------------- brain screen
    void OpenBrain()
    {
        Show(Mode.Brain);
        brainList.text = "Loading…";
        StartCoroutine(Api.Call("GET", $"/bots/{a.botId}", null, r =>
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"<size=30><b>{r["name"]}</b></size>   <color=#9EA5B0>current v{r["brainVersion"]} · root {UI.Short((string)r["brainRoot"], 10)}</color>\n\n");
            sb.Append("<color=#E0AD57><b>VERSION HISTORY</b></color>\n");
            var hist = r["brainHistory"] as JArray;
            if (hist == null || hist.Count == 0) sb.Append("<color=#9EA5B0>No saves yet. Train and press Save Brain.</color>\n");
            else foreach (var h in hist.Take(8))
                sb.Append($"v{h["version"]}   root {UI.Short((string)h["root"], 10)}   <color=#9EA5B0>tx {UI.Short((string)h["txHash"], 8)}   {FormatAt(h["at"])}</color>\n");
            var brainObj = r["brain"];
            if (brainObj?["phrases"] is JArray ph && ph.Count > 0)
            {
                sb.Append("\n<color=#E0AD57><b>PHRASES</b></color>\n");
                foreach (var p in ph.Take(6)) sb.Append($"“{p["say"]}”  →  {p["means"]}  <color=#9EA5B0>({string.Join(" ", (p["moves"] as JArray ?? new JArray()).Select(x => (string)x))})</color>\n");
            }
            if (brainObj?["lessons"] is JArray le && le.Count > 0)
            {
                sb.Append("\n<color=#E0AD57><b>LESSONS</b></color>\n");
                foreach (var l in le.Take(5)) sb.Append("• " + l + "\n");
            }
            brainList.text = sb.ToString();
        }, e => brainList.text = "<color=#F2523F>" + e + "</color>"));
    }

    static string FormatAt(JToken at)
    {
        if (at == null) return "";
        if (at.Type == JTokenType.Date) return ((DateTime)at).ToLocalTime().ToString("MMM d HH:mm");
        return DateTime.TryParse((string)at, out var d) ? d.ToLocalTime().ToString("MMM d HH:mm") : (string)at;
    }

    // ---------------------------------------------------------------- per-frame
    void LateUpdate()
    {
        UpdateCamera();
        foreach (var bub in new[] { bubA, bubB })
        {
            bool on = (mode == Mode.Fight || (mode == Mode.Train && bub.f == a)) && Time.unscaledTime < bub.until && !koPanel.activeSelf;
            // Pinned under each health bar (A left, B right) so the two thoughts never overlap each other or the ticker.
            bub.root.gameObject.SetActive(on);
            if (on) bub.root.position = new Vector3(Screen.width * (bub.f == a ? 0.17f : 0.83f), Screen.height * 0.70f, 0);
        }
        for (int i = popups.Count - 1; i >= 0; i--)
        {
            var (t, born, world) = popups[i];
            float age = Time.unscaledTime - born;
            if (age > 1.1f || t == null) { if (t != null) Destroy(t.gameObject); popups.RemoveAt(i); continue; }
            var sp = cam.WorldToScreenPoint(world);
            t.transform.position = new Vector3(sp.x, sp.y + age * 90f, 0);
            t.color = new Color(t.color.r, t.color.g, t.color.b, 1f - age / 1.1f);
        }
        toast.transform.parent.gameObject.SetActive(Time.unscaledTime < toastUntil);
    }

    static Vector3 Head(Fighter f)
    {
        var h = f.anim.GetBoneTransform(HumanBodyBones.Head);
        return h != null ? h.position : f.transform.position + Vector3.up * 1.7f;
    }

    void UpdateCamera()
    {
        Vector3 pa = a.transform.position, pb = b.transform.position, pos, look;
        float tt = Time.unscaledTime;
        if (mode == Mode.Home || mode == Mode.Brain)
        {
            // Your bot, front three-quarter, sitting in the left half so the card has room on the right.
            var fwd = a.transform.forward;
            var yaw = Quaternion.Euler(0, -18f + Mathf.Sin(tt * 0.2f) * 10f, 0);
            pos = pa + yaw * fwd * 3.6f + Vector3.up * 1.55f;
            look = pa + Vector3.up * 1.05f - a.transform.right * 0.95f * (cam.aspect / 1.78f);
        }
        else
        {
            var mid = (pa + pb) * 0.5f;
            var axis = new Vector3(pb.x - pa.x, 0, pb.z - pa.z).normalized;
            var side = Vector3.Cross(axis, Vector3.up); // camera on this side sees a on the left
            float sep = Vector3.Distance(pa, pb);
            float hfov = 2f * Mathf.Atan(Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * cam.aspect);
            float dist = Mathf.Max(4.4f, (sep * 0.5f + 1.5f) / Mathf.Tan(hfov * 0.5f));
            var dir = Quaternion.AngleAxis(Mathf.Sin(tt * 0.15f) * 24f, Vector3.up) * side;
            pos = mid + dir * dist + Vector3.up * 1.75f;
            look = mid + Vector3.up * 0.95f;
        }
        float k = 1f - Mathf.Exp(-3f * Time.unscaledDeltaTime);
        if (Time.frameCount < 3) k = 1f;
        cam.transform.position = Vector3.Lerp(cam.transform.position, pos, k);
        var rot = Quaternion.LookRotation(look - cam.transform.position);
        cam.transform.rotation = Quaternion.Slerp(cam.transform.rotation, rot, k);
    }

    void Say(Fighter f, string say, string meta, Color c)
    {
        var bub = f == a ? bubA : bubB;
        bub.say.text = say; bub.say.color = c; bub.meta.text = meta; bub.until = Time.unscaledTime + 2.6f;
    }

    void Popup(string s, Color c, Vector3 world)
    {
        var t = UI.Label(canvas, s, 54, c, TextAnchor.MiddleCenter, FontStyle.Bold);
        t.rectTransform.anchorMin = t.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        t.rectTransform.sizeDelta = new Vector2(400, 80);
        t.gameObject.AddComponent<Outline>().effectColor = new Color(0, 0, 0, 0.8f);
        popups.Add((t, Time.unscaledTime, world));
    }

    void Toast(string s) { toast.text = s; toastUntil = Time.unscaledTime + 3.2f; }

    // ---------------------------------------------------------------- UI build
    void BuildUI()
    {
        var cgo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var cv = cgo.GetComponent<Canvas>(); cv.renderMode = RenderMode.ScreenSpaceOverlay;
        var sc = cgo.GetComponent<CanvasScaler>();
        sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; sc.referenceResolution = new Vector2(1920, 1080); sc.matchWidthOrHeight = 1f;
        canvas = cgo.transform;
        var root = UI.Go(canvas, "safe").AddComponent<SafeArea>().transform;

        BuildHome(root); BuildTrain(root); BuildFight(root); BuildBrain(root);
        bubA = BuildBubble(root, a); bubB = BuildBubble(root, b);
        BuildModal(root); BuildKO(root);

        var tb = UI.Box(root, new Color(0.08f, 0.08f, 0.1f, 0.95f), true, "toast").At(0.5f, 1, 0.5f, 1, -480, -250, 480, -170);
        toast = UI.Label(tb.transform, "", 34, UI.Brass, TextAnchor.MiddleCenter, FontStyle.Bold);
        tb.gameObject.AddComponent<Outline>().effectColor = new Color(1f, 0.58f, 0.13f, 0.6f);
        tb.gameObject.SetActive(false);

        badge = UI.Label(root, "…", 22, UI.Accent, TextAnchor.UpperRight, FontStyle.Bold).At(1, 1, 1, 1, -700, -44, -26, -14);
    }

    void BuildHome(Transform root)
    {
        home = UI.Go(root, "Home");
        ((RectTransform)home.transform).At(0, 0, 1, 1);
        UI.Label(home.transform, "IRON <color=#FF9421>CORNER</color>", 92, UI.Ink, TextAnchor.UpperLeft, FontStyle.Bold).At(0, 1, 0, 1, 56, -170, 1000, -40);
        UI.Label(home.transform, "Coach your robot by voice. Every clean hit pays the other corner.", 28, UI.Dim, TextAnchor.UpperLeft).At(0, 1, 0, 1, 60, -220, 1000, -160);

        var card = UI.Box(home.transform, UI.Card, true, "card").At(1, 0.5f, 1, 0.5f, -720, -430, -60, 430);
        var col = UI.Column(card.transform, 14).At(0, 0, 1, 1, 44, 40, -44, -40);
        UI.Label(col.transform, "YOUR CORNER", 24, UI.Accent, TextAnchor.MiddleLeft, FontStyle.Bold).H(30);
        cardName = UI.Label(col.transform, "BRASS KNUCKLE", 74, UI.Ink, TextAnchor.MiddleLeft, FontStyle.Bold).H(84);
        cardMoney = UI.Label(col.transform, "—", 46, UI.Brass, TextAnchor.MiddleLeft, FontStyle.Bold).H(56);
        cardBrain = UI.Label(col.transform, "Brain —", 28, UI.Ink).H(36);
        cardAcct = UI.Label(col.transform, "", 24, UI.Dim).H(30);
        UI.Box(col.transform, UI.Line, false, "rule").H(2);
        UI.Btn(col.transform, "TRAIN", UI.Accent, Color.black, StartTrain, 40).H(96);
        fightBtn = UI.Btn(col.transform, "FIGHT", UI.Steel, UI.Ink, () => { budgetUsd = 0.5f; if (aAlive) StartFight("normal", null); else if (aNeedsRevive) Revive(); else Toast("Still repairing — a few seconds"); }, 40).H(96);
        highBtn = UI.Btn(col.transform, "HIGH-STAKES FIGHT  <size=24><color=#9EA5B0>World ID</color></size>", UI.Steel, UI.Ink, () => { budgetUsd = 0.5f; HighStakes(); }, 34).H(84);
        UI.Btn(col.transform, "BRAIN", UI.Steel, UI.Ink, OpenBrain, 34).H(84);

        var srv = UI.Rt(home.transform, "server").At(0, 0, 0, 0, 56, 40, 1000, 170);
        UI.Label(srv, "SERVER", 22, UI.Dim, TextAnchor.UpperLeft, FontStyle.Bold).At(0, 1, 1, 1, 0, -30, 0, 0);
        serverField = UI.Field(srv, "http://<laptop-ip>:8787", Api.BaseUrl).At(0, 1, 1, 1, 0, -98, -200, -38);
        UI.Btn(srv, "CONNECT", UI.Steel, UI.Ink, () => { Api.BaseUrl = serverField.text; StartCoroutine(Connect()); }, 26).At(1, 1, 1, 1, -186, -98, 0, -38);
        serverStatus = UI.Label(srv, "", 22, UI.Dim).At(0, 0, 1, 0, 0, 0, 0, 28);
    }

    void BuildTrain(Transform root)
    {
        train = UI.Go(root, "Train");
        ((RectTransform)train.transform).At(0, 0, 1, 1);
        UI.Btn(train.transform, "‹  HOME", UI.Steel, UI.Ink, GoHome, 28).At(0, 1, 0, 1, 40, -110, 240, -40);
        UI.Label(train.transform, "SPARRING", 60, UI.Ink, TextAnchor.UpperLeft, FontStyle.Bold).At(0, 1, 0, 1, 270, -112, 1000, -30);
        UI.Label(train.transform, "Brass Knuckle vs a low-aggression Rustbucket dummy. Nothing is paid in training.", 24, UI.Dim, TextAnchor.UpperLeft).At(0, 1, 0, 1, 272, -150, 1200, -108);

        UI.Btn(train.transform, "SAVE BRAIN", UI.Accent, Color.black, SaveBrain, 32).At(1, 1, 1, 1, -330, -140, -40, -60);
        trainSaved = UI.Label(train.transform, "", 26, UI.Ink, TextAnchor.UpperRight).At(1, 1, 1, 1, -700, -250, -40, -150);

        BuildCoachBar(train.transform, out trainTranscript, out trainField);
    }

    void BuildCoachBar(Transform parent, out Text transcript, out InputField field)
    {
        var bar = UI.Rt(parent, "coach").At(0.5f, 0, 0.5f, 0, -620, 30, 620, 250);
        var tr = UI.Box(bar, new Color(0, 0, 0, 0.5f), true, "transcriptBg").At(0, 1, 1, 1, 0, -56, 0, 0);
        var t = UI.Label(tr.transform, "", 28, UI.Ink, TextAnchor.MiddleCenter).At(0, 0, 1, 1, 20, 0, -20, 0);
        transcript = t;
        var mic = UI.Box(bar, UI.Accent, true, "mic").At(0.5f, 0, 0.5f, 0, -150, 0, 150, 140);
        UI.Label(mic.transform, "●  HOLD TO TALK", 36, Color.black, TextAnchor.MiddleCenter, FontStyle.Bold);
        var hold = mic.gameObject.AddComponent<HoldButton>();
        hold.onDown = () => MicDown(t); hold.onUp = () => MicUp(t);
        var f = UI.Field(bar, "or type a call…", "").At(0.5f, 0, 0.5f, 0, 170, 40, 520, 100);
        UI.Btn(bar, "SEND", UI.Steel, UI.Ink, null, 26).At(0.5f, 0, 0.5f, 0, 530, 40, 640, 100).onClick.AddListener(() => SendTyped(f, t));
        field = f;
    }

    void BuildFight(Transform root)
    {
        fight = UI.Go(root, "Fight");
        ((RectTransform)fight.transform).At(0, 0, 1, 1);
        for (int i = 0; i < 2; i++)
        {
            bool left = i == 0;
            var g = UI.Rt(fight.transform, left ? "barA" : "barB");
            if (left) g.At(0, 1, 0.5f, 1, 40, -150, -140, -30); else g.At(0.5f, 1, 1, 1, 140, -150, -40, -30);
            barName[i] = UI.Label(g, "", 36, UI.Ink, left ? TextAnchor.UpperLeft : TextAnchor.UpperRight, FontStyle.Bold).At(0, 1, 1, 1, 0, -48, 0, 0);
            barMoney[i] = UI.Label(g, "", 40, UI.Brass, left ? TextAnchor.UpperRight : TextAnchor.UpperLeft, FontStyle.Bold).At(0, 1, 1, 1, 0, -50, 0, 0);
            var bg = UI.Box(g, new Color(0, 0, 0, 0.6f), true, "bg").At(0, 0, 1, 0, 0, 30, 0, 66);
            var fill = UI.Box(bg.transform, UI.Brass, true, "fill").At(0, 0, 1, 1, 4, 4, -4, -4);
            barFill[i] = fill.rectTransform;
            UI.Label(g, left ? "health = fight budget" : "health = fight budget", 18, UI.Dim, left ? TextAnchor.LowerLeft : TextAnchor.LowerRight).At(0, 0, 1, 0, 0, 0, 0, 26);
        }
        UI.Label(fight.transform, "VS", 44, UI.Accent, TextAnchor.UpperCenter, FontStyle.Bold).At(0.5f, 1, 0.5f, 1, -100, -100, 100, -36);
        fightTitle = UI.Label(fight.transform, "", 20, UI.Dim, TextAnchor.UpperCenter).At(0.5f, 1, 0.5f, 1, -300, -150, 300, -104);

        var tk = UI.Rt(fight.transform, "ticker").At(1, 1, 1, 1, -620, -720, -40, -350);
        UI.Label(tk, "x402 PAYMENTS · HEDERA TESTNET", 20, UI.Dim, TextAnchor.UpperLeft, FontStyle.Bold).At(0, 1, 1, 1, 6, -28, 0, 0);
        var col = UI.Column(tk, 8, "rows");
        col.At(0, 0, 1, 1, 0, 0, 0, -34);
        col.childAlignment = TextAnchor.UpperLeft;
        ticker = col.transform;

        UI.Btn(fight.transform, "‹  HOME", UI.Steel, UI.Ink, GoHome, 28).At(0, 0, 0, 0, 40, 40, 240, 110);
        BuildCoachBar(fight.transform, out fightTranscript, out fightField);
    }

    Bubble BuildBubble(Transform root, Fighter f)
    {
        var box = UI.Box(root, new Color(0.04f, 0.045f, 0.06f, 0.88f), true, "bubble:" + f.botId);
        var rt = box.rectTransform;
        rt.anchorMin = rt.anchorMax = Vector2.zero;
        rt.pivot = new Vector2(0.5f, 0);
        rt.sizeDelta = new Vector2(480, 132);
        var say = UI.Label(box.transform, "", 30, UI.Ink, TextAnchor.UpperLeft, FontStyle.Bold).At(0, 0, 1, 1, 20, 0, -20, -12);
        var meta = UI.Label(box.transform, "", 19, UI.Dim, TextAnchor.LowerLeft).At(0, 0, 1, 1, 20, 10, -20, 0);
        box.gameObject.SetActive(false);
        return new Bubble { root = rt, say = say, meta = meta, f = f };
    }

    void BuildBrain(Transform root)
    {
        brain = UI.Go(root, "Brain");
        ((RectTransform)brain.transform).At(0, 0, 1, 1);
        var panel = UI.Box(brain.transform, UI.Bg, true, "panel").At(0.36f, 0, 1, 1, 0, 40, -40, -40);
        UI.Label(panel.transform, "BRAIN <size=30><color=#9EA5B0>· encrypted on 0G Storage</color></size>", 60, UI.Ink, TextAnchor.UpperLeft, FontStyle.Bold).At(0, 1, 1, 1, 48, -120, -40, -36);
        brainList = UI.Label(panel.transform, "", 27, UI.Ink, TextAnchor.UpperLeft).At(0, 0, 1, 1, 50, 40, -40, -150);
        brainList.lineSpacing = 1.15f;
        UI.Btn(brain.transform, "‹  HOME", UI.Steel, UI.Ink, GoHome, 28).At(0, 1, 0, 1, 40, -110, 240, -40);
    }

    void BuildModal(Transform root)
    {
        modal = UI.Box(root, new Color(0, 0, 0, 0.75f), false, "modal").At(0, 0, 1, 1).gameObject;
        var card = UI.Box(modal.transform, UI.Card, true, "card").At(0.5f, 0.5f, 0.5f, 0.5f, -520, -380, 520, 380);
        UI.Label(card.transform, "WORLD ID APPROVAL", 26, UI.Accent, TextAnchor.UpperLeft, FontStyle.Bold).At(0, 1, 1, 1, 50, -80, -50, -40);
        modalAction = UI.Label(card.transform, "", 54, UI.Ink, TextAnchor.UpperLeft, FontStyle.Bold).At(0, 1, 1, 1, 50, -160, -50, -84);
        var qrBg = UI.Box(card.transform, Color.white, true, "qrBg").At(0, 0, 0, 0, 50, 120, 470, 540);
        modalQr = UI.Go(qrBg.transform, "qr").AddComponent<RawImage>().At(0, 0, 1, 1, 20, 20, -20, -20);
        UI.Label(card.transform, "Scan with World App on your phone. A human must OK every high-stakes action; each approval works once.", 28, UI.Dim, TextAnchor.UpperLeft).At(0, 0, 1, 0, 520, 380, -50, 540);
        modalCode = UI.Label(card.transform, "", 40, UI.Ink, TextAnchor.UpperLeft).At(0, 0, 1, 0, 520, 300, -50, 370);
        modalStatus = UI.Label(card.transform, "", 30, UI.Ink, TextAnchor.UpperLeft).At(0, 0, 1, 0, 520, 140, -50, 290);
        modalBtn = UI.Btn(card.transform, "CANCEL", UI.Steel, UI.Ink, () => CloseModal(true), 30);
        modalBtn.At(0, 0, 1, 0, 50, 36, -50, 100);
        modal.SetActive(false);
    }

    // ---------------------------------------------------------------- replay
    // Rebuilds the fight from the episode log stored on 0G Storage (GET /fights/:id/episode), not from local memory:
    // each recorded hit is replayed with its recorded result, and the 0G request id of the attacker's plan is shown.
    IEnumerator Replay(string id)
    {
        if (string.IsNullOrEmpty(id)) yield break;
        koPanel.SetActive(false);
        Toast("Fetching the episode log from 0G Storage…");
        JObject ep = null; string root = null, err = null;
        float until = Time.realtimeSinceStartup + 60f; // the upload starts at /end and takes ~15 s
        while (ep == null && Time.realtimeSinceStartup < until)
        {
            yield return Api.Call("GET", $"/fights/{id}/episode", null, r => { ep = r["episode"] as JObject; root = (string)r["root"]; }, e => err = e);
            if (ep == null) yield return new WaitForSecondsRealtime(3f);
        }
        if (ep == null) { Toast("Episode not on 0G yet: " + err); koPanel.SetActive(true); yield break; }

        int s = ++session; replaying = true; over = true;
        Show(Mode.Fight);
        ResetFighters();
        int start = (int?)ep["startBudget"] ?? 50;
        a.ResetFor(start); b.ResetFor(start);
        a.active = b.active = true; a.scripted = b.scripted = true;
        fightTitle.text = $"<color=#5CDB85>REPLAY</color>  ·  <color=#9EA5B0>0G root {UI.Short(root, 10)}</color>";
        fightTranscript.text = "Replaying from the 0G Storage episode log";
        foreach (Transform c in ticker) Destroy(c.gameObject);
        UpdateBars();

        var hits = (ep["hits"] as JArray) ?? new JArray();
        var decisions = (ep["decisions"] as JArray) ?? new JArray();
        DateTime? prev = null;
        foreach (var h in hits)
        {
            if (s != session) yield break;
            var at = (DateTime?)h["at"];
            float gap = prev.HasValue && at.HasValue ? Mathf.Clamp((float)(at.Value - prev.Value).TotalSeconds, 0.5f, 1.6f) : 0.6f;
            prev = at;
            yield return new WaitForSecondsRealtime(gap);
            var att = (string)h["attacker"] == a.botId ? a : b; var def = att.opp;
            string move = (string)h["move"], result = (string)h["result"];
            var plan = decisions.LastOrDefault(d => (string)d["botId"] == att.botId && (DateTime?)d["at"] <= at);
            if (plan != null) Say(att, $"{(string)plan["stance"]} · {move}", "0G " + UI.Short((string)plan["requestId"] ?? "—", 8), UI.Ink);
            if (result == "blocked") def.SetQueue(new[] { "block" }); else if (result == "dodged") def.SetQueue(new[] { "dodge_r" });
            att.SetQueue(new[] { move });
            yield return new WaitForSecondsRealtime(0.35f);
            if (result == "clean") { def.TakeHit(move); def.budget = Mathf.Max(0, def.budget - ((int?)h["cents"] ?? 0)); }
            OnHit(att, move, result);
            UpdateBars();
        }
        yield return new WaitForSecondsRealtime(1f);
        if (s != session) yield break;
        var w = (string)ep["winner"];
        if (!string.IsNullOrEmpty(w)) { var winner = w == a.botId ? a : b; winner.opp.KnockOut(); winner.Victory(); UpdateBars(); }
        Toast($"Replay done · {hits.Count} hits · {decisions.Count} 0G decisions");
        a.scripted = b.scripted = false; replaying = false;
    }

    void BuildKO(Transform root)
    {
        koPanel = UI.Box(root, new Color(0, 0, 0, 0.55f), false, "ko").At(0, 0, 1, 1).gameObject;
        var band = UI.Box(koPanel.transform, new Color(0.05f, 0.05f, 0.06f, 0.94f), false, "band").At(0, 0.5f, 1, 0.5f, 0, -250, 0, 250);
        koTitle = UI.Label(band.transform, "", 104, UI.Red, TextAnchor.MiddleCenter, FontStyle.Bold).At(0, 1, 1, 1, 40, -150, -40, -20);
        koSub = UI.Label(band.transform, "", 34, UI.Ink, TextAnchor.MiddleCenter).At(0, 1, 1, 1, 40, -210, -40, -150);
        koLessons = UI.Label(band.transform, "", 26, UI.Dim, TextAnchor.UpperCenter).At(0, 0, 1, 1, 200, 110, -200, -230);
        var row = UI.Rt(band.transform, "buttons").At(0.5f, 0, 0.5f, 0, -620, 26, 620, 100);
        koLedger = UI.Btn(row, "FIGHT LEDGER ON HASHSCAN ↗", UI.Steel, UI.Ink, () => Application.OpenURL("https://hashscan.io/testnet/topic/" + ledgerTopic), 26);
        koLedger.At(0, 0, 0.4f, 1);
        UI.Btn(row, "REPLAY FROM 0G ↺", UI.Steel, UI.Ink, () => StartCoroutine(Replay(fightId)), 26).At(0.42f, 0, 0.74f, 1);
        UI.Btn(row, "HOME", UI.Accent, Color.black, GoHome, 30).At(0.76f, 0, 1, 1);
        koPanel.SetActive(false);
    }

    // ---------------------------------------------------------------- capture mode
    // -capture <dir> [-captureTag <prefix>]: walks every screen, saves PNGs, quits. Uses the server if it answers.
    IEnumerator Capture()
    {
        var args = Environment.GetCommandLineArgs();
        int ti = Array.IndexOf(args, "-captureTag");
        string tag = ti >= 0 && ti + 1 < args.Length ? args[ti + 1] + "_" : "";
        Directory.CreateDirectory(captureDir);
        IEnumerator Shot(string name)
        {
            yield return new WaitForEndOfFrame();
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(Path.Combine(captureDir, tag + name + ".png"), tex.EncodeToPNG());
            Destroy(tex);
            Debug.Log("[capture] " + name);
        }
        IEnumerator Wait(float s) { yield return new WaitForSecondsRealtime(s); }

        yield return Connect();
        yield return Wait(2.5f);
        yield return Shot("01_home");

        OpenBrain();
        yield return Wait(1.5f);
        yield return Shot("02_brain");

        StartTrain();
        yield return Wait(4f);
        yield return Shot("03_train");
        trainField.text = "light him up";
        SendTyped(trainField, trainTranscript);
        yield return Wait(0.9f);
        yield return Shot("04_train_learned");
        SaveBrain();
        yield return Wait(1.5f);
        yield return Shot("05_train_saved");

        GoHome();
        yield return Wait(1f);
        HighStakes();
        yield return Wait(2f);
        yield return Shot("06_approval");
        CloseModal(true);

        budgetUsd = 0.05f; // short fight so the KO lands inside the capture
        StartFight("normal", null);
        yield return Wait(4f);
        yield return Shot("07_fight");
        yield return Wait(3.5f);
        yield return Shot("08_fight");
        float until = Time.realtimeSinceStartup + 90f;
        while (!over && Time.realtimeSinceStartup < until) yield return null;
        yield return Wait(0.5f);
        yield return Shot("09_ko_slowmo");
        yield return Wait(2.5f);
        yield return Shot("10_ko");
        StartCoroutine(Replay(fightId));
        float ru = Time.realtimeSinceStartup + 75f;
        while (!replaying && Time.realtimeSinceStartup < ru) yield return null;
        yield return Wait(5f);
        yield return Shot("11_replay");
        Debug.Log("[capture] done");
        Application.Quit();
    }
}

// Keeps UI inside the notch/home-indicator safe area.
public class SafeArea : MonoBehaviour
{
    Rect last;
    void Update()
    {
        var sa = Screen.safeArea;
        if (sa == last) return;
        last = sa;
        var rt = (RectTransform)transform;
        rt.anchorMin = new Vector2(sa.xMin / Screen.width, sa.yMin / Screen.height);
        rt.anchorMax = new Vector2(sa.xMax / Screen.width, sa.yMax / Screen.height);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }
}
