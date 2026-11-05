using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
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
    Text badge, toast, cardName, cardMoney, cardBrain, cardAcct, cardDaily, serverStatus, brainList, trainDaily;
    InputField serverField, trainField;
    readonly List<string> counterAttacks = new(); // attacks your bot has a taught counter for; the sparring dummy throws these
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

    // feel: hit-stop, slow-mo, camera trauma, KO cam, post pulses, combo counter, banner, health-bar damage trail
    Fx fx;
    float hitStopUntil, slowmoUntil, koCamUntil, fovKick, baseFov, pulse;
    Fighter koTarget;
    Quaternion camRot = Quaternion.identity; bool camSnap;
    ChromaticAberration chroma; Vignette vignette; float baseVignette;
    RectTransform[] barGroup = new RectTransform[2]; Vector2[] barPos = new Vector2[2];
    RectTransform[] barTrail = new RectTransform[2]; float[] trailK = { 1, 1 }, trailHold = new float[2], barShake = new float[2];
    Text[] comboText = new Text[2]; int[] combo = new int[2]; float[] comboAt = new float[2];
    Text banner; float bannerAt = -9f;

    // Shadow sparring: the owner boxes with the move pad; each move becomes a demo (state, action, outcome) for the backend.
    class Demo { public JObject state; public string action; public float at; public string outcome; public bool gotHit; }
    readonly List<Demo> demoPending = new();
    readonly JArray demoBatch = new();
    readonly List<string> drilling = new(); // attacks the owner has been defending against; the dummy throws more of them
    bool roundOn; float roundEnds, lastFlush, roundSeconds = 30f; string roundId; int roundMoves, unsavedRounds;
    float aAttackAt = -99f, bAttackAt = -99f, bSwingAt = -99f; string aCur, bCur;
    Text roundTimer, roundInfo, energyPips, padFeed, summaryTitle, summaryBody; GameObject summary;
    Text cardLevel, cardXp, cardEnergy; RectTransform xpFill;
    readonly Text[] statVal = new Text[5]; readonly RectTransform[] statFill = new RectTransform[5];
    static readonly string[] StatKeys = { "reflexes", "accuracy", "defense", "ring_iq", "experience" };
    static readonly string[] StatNames = { "REFLEXES", "ACCURACY", "DEFENSE", "RING IQ", "EXPERIENCE" };

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
        fx = GetComponent<Fx>();
        a.OnSwing += (f, m) => fx.Swing(f.Fist(m), Fighter.Damage(m) >= 3);
        b.OnSwing += (f, m) => fx.Swing(f.Fist(m), Fighter.Damage(m) >= 3);
        b.OnSwing += (f, m) => bSwingAt = Time.time; // shadow sparring: a dummy punch reached its strike frame
        a.flashColor = new Color(1f, 0.55f, 0.15f); b.flashColor = new Color(1f, 0.3f, 0.15f);
        baseFov = cam.fieldOfView;
        var vol = FindFirstObjectByType<Volume>();
        if (vol != null && vol.sharedProfile != null)
        {
            vol.sharedProfile.TryGet(out chroma);
            if (vol.sharedProfile.TryGet(out vignette)) baseVignette = vignette.intensity.value;
        }
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
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-offline") >= 0) Api.Offline = true; // captures: force the local sim
        else yield return Api.Probe(null);
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
                ShowDaily(bot["daily"]);
                ShowProgress(bot["progress"]);
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

    void ShowDaily(JToken d)
    {
        if (d == null || d.Type != JTokenType.Object) return;
        int streak = (int?)d["streak"] ?? 0;
        string txt = ((bool?)d["done"] == true ? "<color=#5CDB85>✓ Daily training done</color>" : $"Daily goal: <b>{d["drills"]}/{d["goal"]}</b> drills")
            + $"  <color=#9EA5B0>·  {(streak > 0 ? $"{streak}-day streak" : "start a streak today")}</color>";
        cardDaily.text = txt; trainDaily.text = txt;
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
        camSnap = true; Time.timeScale = 1f; hitStopUntil = slowmoUntil = koCamUntil = 0; koTarget = null; bannerAt = -9f;
        for (int i = 0; i < 2; i++) { combo[i] = 0; trailK[i] = 1; }
        if (fx != null) fx.SetCrowd(m == Mode.Fight ? 0.35f : m == Mode.Train ? 0.08f : 0.15f);
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

    void GoHome()
    {
        if (mode == Mode.Train && roundOn) EndRound();
        if (mode == Mode.Train && unsavedRounds > 0) StartCoroutine(AutoSave());
        session++; replaying = false; a.scripted = b.scripted = false; Show(Mode.Home);
    }

    // Leaving training uploads the brain (rules + demos + stats) to 0G Storage as a new version, after the last demos land.
    IEnumerator AutoSave()
    {
        unsavedRounds = 0;
        yield return new WaitForSecondsRealtime(1.5f);
        yield return Api.Call("POST", $"/train/{a.botId}/save", new JObject(), r => Toast($"Brain v{r["version"]} saved to 0G Storage"), e => Toast("Brain save failed: " + e));
        if (mode == Mode.Home) yield return RefreshHome();
    }

    void StartTrain()
    {
        Show(Mode.Train);
        ResetFighters();
        a.active = b.active = true;
        trainTranscript.text = "Talk or type a rule: “when he throws a left hook, slip right and uppercut”.";
        trainSaved.text = "";
        roundOn = false; demoPending.Clear(); demoBatch.Clear(); drilling.Clear(); summary.SetActive(false);
        roundTimer.text = "READY"; roundInfo.text = $"Tap a move to start a {roundSeconds:0} s round · every move is a lesson"; padFeed.text = "";
        StartCoroutine(LoadBrainRules());
        StartCoroutine(SparLoop(session));
        StartCoroutine(DummyLoop(session));
    }

    // Which attacks have a counter, so the dummy can throw them, plus today's goal.
    IEnumerator LoadBrainRules()
    {
        yield return Api.Call("GET", $"/bots/{a.botId}", null, r =>
        {
            counterAttacks.Clear();
            if (r["brain"]?["counters"] is JArray cs) counterAttacks.AddRange(cs.Select(c => (string)c["when"]).Where(w => !string.IsNullOrEmpty(w)));
            ShowDaily(r["daily"]);
            ShowProgress(r["progress"]);
        });
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
            fightTranscript.text = "AUTONOMOUS  ·  no coaching mid-fight — your bot uses what it learned in training";
            foreach (Transform c in ticker) Destroy(c.gameObject);
            UpdateBars();
            Banner("FIGHT!", UI.Accent);
            fx.Bell(1); fx.Cheer(0.5f);
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
        ["sinceOppAttack"] = Math.Round(Mathf.Clamp(Time.time - (f == a ? bAttackAt : aAttackAt), 0f, 5f), 2),
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
            if (roundOn) { yield return new WaitForSecondsRealtime(0.3f); continue; } // you drive during a round; 0G drives between rounds
            float t0 = Time.realtimeSinceStartup;
            yield return Api.Call("POST", $"/train/{a.botId}/spar", new JObject { ["state"] = State(a) },
                r => { if (s == session) ApplyPlan(a, r); }, e => { if (s == session) ApplyPlan(a, new JObject { ["stance"] = "safe_stop" }); });
            yield return new WaitForSecondsRealtime(Mathf.Max(0.2f, 1.5f - (Time.realtimeSinceStartup - t0)));
        }
    }

    // The dummy is local on purpose: a low-aggression punching bag, not a second 0G client. Most of the time it
    // throws an attack you've taught a counter for, so the next 0G sparring plan can show that counter firing.
    IEnumerator DummyLoop(int s)
    {
        while (s == session)
        {
            var pool = counterAttacks.Concat(drilling).ToList();
            float roll = UnityEngine.Random.value;
            JObject Throw(string m) => new() { ["stance"] = "aggressive", ["combo"] = new JArray(m), ["trigger"] = "when_close" };
            if (b.Busy) { }
            else if (pool.Count > 0 && roll < (roundOn ? 0.55f : 0.65f)) ApplyPlan(b, Throw(pool[UnityEngine.Random.Range(0, pool.Count)]), quiet: true);
            else if (roundOn && roll < 0.8f) ApplyPlan(b, Throw(Attacks[UnityEngine.Random.Range(0, Attacks.Length)]), quiet: true);
            else ApplyPlan(b, OfflineSim.Plan(State(b), 0.25f), quiet: true);
            yield return new WaitForSecondsRealtime(roundOn ? 1.3f : 1.8f);
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
        string used = (string)plan["usedText"];
        bool demo = (string)plan["used"] == "demo";
        string usedLine = string.IsNullOrEmpty(used) ? "" : demo ? $"<color=#5CDB85><b>{used}</b></color>\n"
            : $"<color=#FF9421><b>Using: {used}</b></color> <color=#9EA5B0>(learned in training)</color>\n";
        Say(f, (string)plan["say"] ?? stance, $"{usedLine}{stance} · {string.Join(" ", moves)}   {meta}", UI.Ink);
        if (mode == Mode.Train && f == a && ((string)plan["used"] ?? "").StartsWith("counter")) Toast("Counter fired: " + used.Replace("counter: ", ""));
        if (mode == Mode.Train && f == a && demo && !roundOn) Toast(used);
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
        DemoHit(att, move, result);
        var def = att.opp;
        var head = Head(def) + Vector3.up * 0.25f;
        int dmg = Fighter.Damage(move), side = att == a ? 0 : 1;
        if (result == "clean") Popup(mode == Mode.Fight ? "-" + UI.Usd(dmg) : move.ToUpperInvariant().Replace('_', ' '), UI.Red, head, dmg >= 3 ? 1.35f : 1f);
        else Popup(result.ToUpperInvariant(), result == "blocked" ? UI.Dim : UI.Brass, head);
        // contact point: where the fist is, pulled onto the defender's head/chest so sparks sit on the armour
        var target = move == "body" ? def.Chest : Head(def);
        var at = Vector3.Lerp(att.Fist(move), target, 0.35f);
        fx.Impact(at, move, result);
        if (result == "clean")
        {
            hitStopUntil = Time.unscaledTime + 0.035f + dmg * 0.018f;
            if (dmg >= 3) { fovKick = Mathf.Max(fovKick, dmg * 0.9f); pulse = Mathf.Max(pulse, 0.25f * dmg); }
            barShake[1 - side] = 1f; trailHold[1 - side] = Time.unscaledTime + 0.45f;
            combo[side] = Time.unscaledTime - comboAt[side] < 1.8f ? combo[side] + 1 : 1; comboAt[side] = Time.unscaledTime;
            combo[1 - side] = 0;
            if (combo[side] >= 2) ShowCombo(side);
            if (combo[side] == 4) fx.Cheer(0.55f);
        }
        else if (result == "blocked") hitStopUntil = Time.unscaledTime + 0.02f;
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
        fx.KO(loser.Chest);
        koTarget = loser; koCamUntil = Time.unscaledTime + 2.4f; pulse = 1.2f; fovKick = 5f;
        Banner("K.O.", UI.Red);
        StartCoroutine(KOSequence(session, loser, winner));
    }

    IEnumerator KOSequence(int s, Fighter loser, Fighter winner)
    {
        slowmoUntil = Time.unscaledTime + 1.6f;
        yield return new WaitForSecondsRealtime(2.2f);
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
            if (k > trailK[i] || mode != Mode.Fight) { trailK[i] = k; SetTrail(i); }
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

    // ---------------------------------------------------------------- shadow sparring
    static readonly string[] Attacks = Fighter.Moves.Where(Fighter.IsAttack).ToArray();

    void PadMove(string m)
    {
        if (mode != Mode.Train) return;
        if (!roundOn) StartRound();
        demoPending.Add(new Demo { state = State(a), action = m, at = Time.time });
        a.holdBlock = false; a.SetQueue(new[] { m });
    }

    void StartRound()
    {
        roundOn = true; roundId = "r" + DateTime.UtcNow.Ticks.ToString("x"); roundMoves = 0;
        fightStart = Time.time; roundEnds = Time.time + roundSeconds; lastFlush = Time.time;
        summary.SetActive(false); padFeed.text = ""; roundInfo.text = "0 moves recorded";
        a.holdBlock = false; a.SetQueue(Array.Empty<string>());
        StartCoroutine(Api.Call("POST", $"/train/{a.botId}/demos", new JObject { ["round"] = roundId, ["demos"] = new JArray() }, ShowProgress));
    }

    // Outcome of a pad move: our punch landed / was blocked / dodged, or the dummy's punch hit us / was blocked / dodged.
    void DemoHit(Fighter att, string move, string result)
    {
        if (mode != Mode.Train || !roundOn) return;
        if (att == a)
        {
            var d = demoPending.FirstOrDefault(x => x.outcome == null && x.action == move);
            if (d != null) d.outcome = result == "clean" ? "landed" : result;
            return;
        }
        foreach (var d in demoPending.Where(x => x.outcome == null && Time.time - x.at < 1.2f))
        {
            if (result == "clean") d.gotHit = true;
            if (!Fighter.IsAttack(d.action)) d.outcome = result == "clean" ? "got_hit" : d.action == "block" ? "blocked" : "dodged";
        }
    }

    // Each frame: close demo windows (punch 1.4 s, defence 1 s), send batches every 4 s, run the round clock.
    void TickRound()
    {
        if (!roundOn) return;
        for (int i = demoPending.Count - 1; i >= 0; i--)
        {
            var d = demoPending[i];
            bool atk = Fighter.IsAttack(d.action);
            if (d.outcome == null && Time.time - d.at < (atk ? 1.4f : 1.0f)) continue;
            // A defensive move with a dummy punch striking into it that didn't land clean made it miss.
            d.outcome ??= d.gotHit ? "got_hit" : !atk && bSwingAt >= d.at ? (d.action == "block" ? "blocked" : "dodged") : "whiff";
            demoPending.RemoveAt(i);
            Recorded(d);
        }
        if (demoBatch.Count >= 10 || (demoBatch.Count > 0 && Time.time - lastFlush > 4f)) Flush(false);
        float left = roundEnds - Time.time;
        roundTimer.text = $"ROUND  <color=#FF9421>0:{Mathf.CeilToInt(Mathf.Max(0, left)):00}</color>";
        if (left <= 0) EndRound();
    }

    void Recorded(Demo d)
    {
        demoBatch.Add(new JObject { ["t"] = d.state["t"], ["state"] = d.state, ["action"] = d.action, ["outcome"] = d.outcome });
        bool atk = Fighter.IsAttack(d.action), ok = atk ? d.outcome == "landed" : d.outcome is "blocked" or "dodged";
        var o1 = (string)(d.state["oppLastMoves"] as JArray)?.LastOrDefault();
        if (!atk && Fighter.IsAttack(o1)) { drilling.Add(o1); if (drilling.Count > 8) drilling.RemoveAt(0); }
        roundMoves++;
        string mark = ok ? "<color=#5CDB85>✓</color>" : d.outcome == "got_hit" ? "<color=#F2523F>✗</color>" : "<color=#9EA5B0>·</color>";
        padFeed.text = $"{mark} {UI.Nice(d.action).ToUpperInvariant()} · {d.outcome.Replace('_', ' ')}   <color=#9EA5B0>demo recorded</color>";
        roundInfo.text = $"{roundMoves} moves recorded";
    }

    void Flush(bool end)
    {
        var body = new JObject { ["round"] = roundId, ["demos"] = new JArray(demoBatch), ["roundEnd"] = end };
        demoBatch.Clear(); lastFlush = Time.time;
        int s = session;
        StartCoroutine(Api.Call("POST", $"/train/{a.botId}/demos", body,
            r => { ShowProgress(r); if (end && s == session) RoundSummary(r, null); },
            e => { if (end && s == session) RoundSummary(null, e); }));
    }

    void EndRound()
    {
        roundOn = false;
        foreach (var d in demoPending) { d.outcome ??= d.gotHit ? "got_hit" : "whiff"; Recorded(d); }
        demoPending.Clear();
        a.SetQueue(Array.Empty<string>());
        if (roundMoves > 0) unsavedRounds++;
        roundTimer.text = "ROUND OVER";
        roundInfo.text = "Your bot spars on its own now, copying what worked"; padFeed.text = "";
        Flush(true);
    }

    void RoundSummary(JToken r, string err)
    {
        summary.SetActive(true);
        var rd = r?["round"];
        if (rd == null) { summaryTitle.text = "ROUND OVER"; summaryBody.text = "<color=#F2523F>Couldn't send the round: " + err + "</color>"; return; }
        int xp = (int?)rd["xpGained"] ?? 0;
        summaryTitle.text = xp > 0 ? $"ROUND OVER   <color=#5CDB85>+{xp} XP</color>" : "ROUND OVER";
        var lines = new List<string> { $"<b>{rd["moves"]} moves recorded</b>" + (rd["highlight"]?.Type == JTokenType.String ? $"  ·  {rd["highlight"]}" : "") };
        if (rd["reason"]?.Type == JTokenType.String) lines.Add($"<color=#F2A33A>{rd["reason"]}</color>");
        lines.Add($"<size=24><color=#9EA5B0>Energy {r["energy"]?["left"]}/{r["energy"]?["max"]}  ·  level {r["xp"]?["level"]}  ·  {r["total"]} demos in the brain</color></size>");
        summaryBody.text = string.Join("\n", lines);
    }

    // GET /bots puts growth under "progress"; POST demos returns energy/xp/stats at the top level.
    void ShowProgress(JToken r)
    {
        if (r == null || r.Type != JTokenType.Object) return;
        var p = r["progress"] is JObject pr ? pr : (JObject)r;
        var lv = p["xp"] as JObject ?? p;
        if (lv["level"] != null)
        {
            int into = (int?)lv["into"] ?? 0, next = Math.Max(1, (int?)lv["next"] ?? 100);
            cardLevel.text = $"LEVEL {lv["level"]}";
            cardXp.text = $"{into}/{next}";
            SetFill(xpFill, (float)into / next);
        }
        if (p["stats"] is JObject st)
            for (int i = 0; i < StatKeys.Length; i++) { int v = (int?)st[StatKeys[i]] ?? 0; statVal[i].text = v.ToString(); SetFill(statFill[i], v / 100f); }
        if (p["energy"] is JObject en)
        {
            int left = (int?)en["left"] ?? 0, max = (int?)en["max"] ?? 5;
            string pips = "<color=#FF9421>" + new string('●', left) + "</color><color=#3A3F48>" + new string('●', Math.Max(0, max - left)) + "</color>";
            cardEnergy.text = $"ENERGY  {pips}  <color=#9EA5B0>{left}/{max} rounds today · refills 00:00 UTC</color>";
            energyPips.text = $"ENERGY  {pips}";
        }
    }

    static void SetFill(RectTransform f, float k) { k = Mathf.Clamp01(k); f.anchorMax = new Vector2(k, 1); f.gameObject.SetActive(k > 0.02f); }

    // ---------------------------------------------------------------- coaching
    bool recording;
    void MicDown(Text transcript)
    {
        recording = Mic.Start();
        transcript.text = recording ? "<color=#FF9421>● Listening…</color> release to send" : (Api.Offline ? "No mic — offline sim will pretend you taught a left-hook counter." : "No microphone — type instead.");
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
    }

    void SendTyped(InputField field, Text transcript)
    {
        var text = field.text.Trim();
        if (text.Length == 0) return;
        field.text = "";
        transcript.text = "…";
        if (mode == Mode.Train) StartCoroutine(Api.Call("POST", $"/train/{a.botId}/coach-text", new JObject { ["text"] = text }, r => OnCoach(r, transcript), e => transcript.text = "Coach failed: " + e));
    }

    // Training only: show what the brain learned (diff + 0G id), drill it right away, track the daily goal.
    void OnCoach(JToken r, Text transcript)
    {
        string req = (string)r["requestId"], diff = (string)r["diff"];
        transcript.text = $"“{r["text"]}”" + (req != null ? $"  <size=20><color=#9EA5B0>0G {UI.Short(req, 8)}</color></size>" : "");
        ShowDaily(r["daily"]);
        if ((bool?)r["daily"]?["justDone"] == true) StartCoroutine(ToastLater($"Daily training done · {r["daily"]["streak"]}-day streak", 3.4f));
        if (r["learned"] is JObject learned)
        {
            Toast(diff ?? $"Learned: ‘{learned["say"]}’ = {learned["means"]}");
            Say(a, diff, $"learned in training · {learned["id"]}" + (req != null ? "   0G " + UI.Short(req, 8) : ""), UI.Accent);
            if ((string)learned["kind"] == "counter" && !counterAttacks.Contains((string)learned["when"])) counterAttacks.Add((string)learned["when"]);
            if (learned["moves"] is JArray mv && mv.Count > 0) { a.holdBlock = false; a.SetQueue(mv.Select(x => (string)x), front: true); }
            return;
        }
        var command = (string)r["command"];
        if (string.IsNullOrEmpty(command)) { if (r["error"] != null || req != null) Toast("Nothing to learn from that — try a counter, opener or finisher"); return; }
        Toast("Drill: " + command.Replace('_', ' '));
        a.holdBlock = false; a.SetQueue(new[] { command }, front: true);
    }

    IEnumerator ToastLater(string s, float delay) { yield return new WaitForSecondsRealtime(delay); Toast(s); }

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
            else foreach (var h in hist.Reverse().Take(5))
            {
                sb.Append($"<b>v{h["version"]}</b>" + (h["level"] != null ? $"  <color=#FF9421>Level {h["level"]}</color> · {h["demos"]} demos" : "")
                    + $"   root {UI.Short((string)h["root"], 10)}   <color=#9EA5B0>{FormatAt(h["at"])}</color>\n");
                if (h["stats"] is JObject st)
                    sb.Append($"<size=21><color=#9EA5B0>      reflexes {st["reflexes"]} · accuracy {st["accuracy"]} · defense {st["defense"]} · ring IQ {st["ring_iq"]} · experience {st["experience"]}</color></size>\n");
            }
            var brainObj = r["brain"];
            string Ms(JToken m) => string.Join(" ", (m as JArray ?? new JArray()).Select(x => (string)x));
            if (brainObj?["counters"] is JArray cs && cs.Count > 0)
            {
                sb.Append("\n<color=#E0AD57><b>COUNTERS</b></color>\n");
                foreach (var c in cs.Take(4)) sb.Append($"when he throws <b>{c["when"]}</b>  →  {Ms(c["moves"])}\n");
            }
            if (brainObj?["combos"] is JArray co && co.Count > 0)
            {
                sb.Append("\n<color=#E0AD57><b>COMBOS</b></color>\n");
                foreach (var c in co.Take(5)) sb.Append($"<b>{c["use"]}</b>  {Ms(c["moves"])}\n");
            }
            if (brainObj?["phrases"] is JArray ph && ph.Count > 0)
            {
                sb.Append("\n<color=#E0AD57><b>PHRASES</b></color>\n");
                foreach (var p in ph.Take(4)) sb.Append($"“{p["say"]}”  →  {p["means"]}  <color=#9EA5B0>({string.Join(" ", (p["moves"] as JArray ?? new JArray()).Select(x => (string)x))})</color>\n");
            }
            if (brainObj?["lessons"] is JArray le && le.Count > 0)
            {
                sb.Append("\n<color=#E0AD57><b>LESSONS</b></color>\n");
                foreach (var l in le.Take(3)) sb.Append("• " + l + "\n");
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
        if (a.Current != aCur) { aCur = a.Current; if (Fighter.IsAttack(aCur)) aAttackAt = Time.time; }
        if (b.Current != bCur) { bCur = b.Current; if (Fighter.IsAttack(bCur)) bAttackAt = Time.time; }
        TickRound();
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
        UpdateHud();
    }

    // Time scale, post pulses and smoke from damaged bots. Hit-stop beats slow-mo beats normal.
    void Update()
    {
        float now = Time.unscaledTime;
        Time.timeScale = now < hitStopUntil ? 0.06f : now < slowmoUntil ? 0.3f : 1f;
        pulse = Mathf.Max(0, pulse - Time.unscaledDeltaTime * 3f);
        if (chroma != null) chroma.intensity.value = Mathf.Clamp01(pulse * 0.8f);
        if (vignette != null) vignette.intensity.value = baseVignette + Mathf.Clamp01(pulse) * 0.15f;
        if (mode == Mode.Fight && fx != null)
            foreach (var f in new[] { a, b }) fx.Smoke(f.Chest, f.startBudget > 0 && !f.down ? Mathf.Clamp01((0.45f - f.Health01) / 0.45f) : f.down ? 0.6f : 0);
    }

    void UpdateHud()
    {
        if (mode != Mode.Fight) return;
        float now = Time.unscaledTime, dt = Time.unscaledDeltaTime;
        var fs = new[] { a, b };
        for (int i = 0; i < 2; i++)
        {
            // white trail catches up with the real bar after a beat, so every hit reads as a chunk off the bar
            float k = fs[i].Health01;
            if (now > trailHold[i] && trailK[i] > k) { trailK[i] = Mathf.Max(k, trailK[i] - dt * 0.6f); SetTrail(i); }
            barShake[i] = Mathf.Max(0, barShake[i] - dt * 4f);
            barGroup[i].anchoredPosition = barPos[i] + UnityEngine.Random.insideUnitCircle * barShake[i] * 9f;
            bool comboOn = combo[i] >= 2 && now - comboAt[i] < 1.6f;
            comboText[i].gameObject.SetActive(comboOn);
            if (comboOn) comboText[i].transform.localScale = Vector3.one * (1f + Mathf.Max(0, 0.35f - (now - comboAt[i])) * 1.4f);
        }
        float age = now - bannerAt;
        banner.gameObject.SetActive(age < 1.6f);
        if (age < 1.6f)
        {
            float s = age < 0.18f ? Mathf.Lerp(2.2f, 1f, age / 0.18f) : 1f + (age - 0.18f) * 0.06f;
            banner.transform.localScale = Vector3.one * s;
            var c = banner.color; c.a = Mathf.Clamp01((1.6f - age) / 0.35f); banner.color = c;
        }
    }

    void SetTrail(int i)
    {
        if (i == 0) barTrail[i].anchorMax = new Vector2(trailK[i], 1); else barTrail[i].anchorMin = new Vector2(1 - trailK[i], 0);
    }

    void ShowCombo(int side) => comboText[side].text = $"{combo[side]} HIT COMBO";

    void Banner(string s, Color c) { banner.text = s; banner.color = c; bannerAt = Time.unscaledTime; }

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
        float rate = 3f;
        if (koTarget != null && Time.unscaledTime < koCamUntil)
        {
            // KO cam: low and close on the falling bot, slowly orbiting
            var c = koTarget.transform.position;
            // stays on the loser's side of the ring (away from the winner) so the winner never blocks the shot
            var away = new Vector3(c.x - koTarget.opp.transform.position.x, 0, c.z - koTarget.opp.transform.position.z).normalized;
            var dir = Quaternion.AngleAxis(55f + Mathf.Sin(Time.unscaledTime * 0.6f) * 35f, Vector3.up) * away;
            pos = c + dir * 3.1f + Vector3.up * 1.3f; look = c + Vector3.up * 0.7f; rate = 2.2f;
        }
        float k = 1f - Mathf.Exp(-rate * Time.unscaledDeltaTime);
        if (Time.frameCount < 3 || camSnap) k = 1f;
        cam.transform.position = Vector3.Lerp(cam.transform.position, pos, k);
        var rot = Quaternion.LookRotation(look - cam.transform.position);
        camRot = Time.frameCount < 3 || camSnap ? rot : Quaternion.Slerp(camRot, rot, k);
        cam.transform.rotation = camRot; camSnap = false;
        // trauma shake (squared so small hits stay subtle) and a quick FOV punch-in on heavy shots
        if (fx != null)
        {
            float tr = fx.shake * fx.shake, tt2 = Time.unscaledTime * 32f;
            cam.transform.rotation *= Quaternion.Euler((Mathf.PerlinNoise(tt2, 1) - 0.5f) * 3.2f * tr, (Mathf.PerlinNoise(2, tt2) - 0.5f) * 3.2f * tr, (Mathf.PerlinNoise(tt2, 3) - 0.5f) * 2.4f * tr);
            fx.shake = Mathf.Max(0, fx.shake - Time.unscaledDeltaTime * 1.8f);
        }
        fovKick = Mathf.Max(0, fovKick - Time.unscaledDeltaTime * 12f);
        cam.fieldOfView = baseFov - fovKick;
    }

    void Say(Fighter f, string say, string meta, Color c)
    {
        var bub = f == a ? bubA : bubB;
        bub.say.text = say; bub.say.color = c; bub.meta.text = meta; bub.until = Time.unscaledTime + 2.6f;
    }

    void Popup(string s, Color c, Vector3 world, float scale = 1f)
    {
        var t = UI.Label(canvas, s, Mathf.RoundToInt(54 * scale), c, TextAnchor.MiddleCenter, FontStyle.Bold);
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
        UI.Label(home.transform, "Teach your robot by boxing with it. It fights on its own. Every clean hit pays the other corner.", 28, UI.Dim, TextAnchor.UpperLeft).At(0, 1, 0, 1, 60, -220, 1000, -160);

        var card = UI.Box(home.transform, UI.Card, true, "card").At(1, 0.5f, 1, 0.5f, -720, -410, -60, 480);
        var col = UI.Column(card.transform, 10).At(0, 0, 1, 1, 44, 32, -44, -30);
        UI.Label(col.transform, "YOUR CORNER", 24, UI.Accent, TextAnchor.MiddleLeft, FontStyle.Bold).H(28);
        cardName = UI.Label(col.transform, "BRASS KNUCKLE", 64, UI.Ink, TextAnchor.MiddleLeft, FontStyle.Bold).H(68);
        cardMoney = UI.Label(col.transform, "—", 40, UI.Brass, TextAnchor.MiddleLeft, FontStyle.Bold).H(46);
        cardBrain = UI.Label(col.transform, "Brain —", 26, UI.Ink).H(30);
        cardAcct = UI.Label(col.transform, "", 22, UI.Dim).H(26);
        // Growth: level + XP, the five brain stats, today's energy. Same body for every bot; only the brain grows.
        var lv = UI.Rt(col.transform, "level").H(42);
        cardLevel = UI.Label(lv, "LEVEL 1", 34, UI.Ink, TextAnchor.MiddleLeft, FontStyle.Bold).At(0, 0, 0, 1, 0, 0, 160, 0);
        var xbg = UI.Box(lv, new Color(0, 0, 0, 0.6f), true, "xpbg").At(0, 0.5f, 1, 0.5f, 170, -9, -84, 9);
        xpFill = UI.Box(xbg.transform, UI.Accent, true, "xp").At(0, 0, 1, 1, 2, 2, -2, -2).rectTransform;
        cardXp = UI.Label(lv, "0/100", 22, UI.Dim, TextAnchor.MiddleRight).At(1, 0, 1, 1, -80, 0, 0, 0);
        var stats = UI.Column(col.transform, 4, "stats").H(5 * 26 + 4 * 4);
        for (int i = 0; i < StatKeys.Length; i++)
        {
            var row = UI.Rt(stats.transform, StatKeys[i]).H(26);
            UI.Label(row, StatNames[i], 20, UI.Dim, TextAnchor.MiddleLeft, FontStyle.Bold).At(0, 0, 0, 1, 0, 0, 160, 0);
            var bg = UI.Box(row, new Color(0, 0, 0, 0.6f), true, "bg").At(0, 0.5f, 1, 0.5f, 170, -7, -84, 7);
            statFill[i] = UI.Box(bg.transform, UI.Brass, true, "fill").At(0, 0, 1, 1, 2, 2, -2, -2).rectTransform;
            statVal[i] = UI.Label(row, "0", 22, UI.Ink, TextAnchor.MiddleRight, FontStyle.Bold).At(1, 0, 1, 1, -80, 0, 0, 0);
            SetFill(statFill[i], 0);
        }
        cardEnergy = UI.Label(col.transform, "", 22, UI.Ink).H(28);
        cardDaily = UI.Label(col.transform, "", 22, UI.Ink).H(28);
        UI.Box(col.transform, UI.Line, false, "rule").H(2);
        UI.Btn(col.transform, "TRAIN", UI.Accent, Color.black, StartTrain, 40).H(84);
        var fr = UI.Rt(col.transform, "fightRow").H(76);
        fightBtn = UI.Btn(fr, "FIGHT", UI.Steel, UI.Ink, () => { budgetUsd = 0.5f; if (aAlive) StartFight("normal", null); else if (aNeedsRevive) Revive(); else Toast("Still repairing — a few seconds"); }, 34);
        fightBtn.At(0, 0, 0.49f, 1);
        highBtn = UI.Btn(fr, "HIGH STAKES  <size=20><color=#9EA5B0>World ID</color></size>", UI.Steel, UI.Ink, () => { budgetUsd = 0.5f; HighStakes(); }, 28);
        highBtn.At(0.51f, 0, 1, 1);
        UI.Btn(col.transform, "BRAIN", UI.Steel, UI.Ink, OpenBrain, 30).H(64);

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
        UI.Label(train.transform, "SPARRING", 60, UI.Ink, TextAnchor.UpperLeft, FontStyle.Bold).At(0, 1, 0, 1, 270, -112, 700, -30);

        // Round HUD, top centre: clock · energy, then what's happening.
        var hud = UI.Box(train.transform, new Color(0, 0, 0, 0.55f), true, "hud").At(0.5f, 1, 0.5f, 1, -330, -112, 330, -36);
        roundTimer = UI.Label(hud.transform, "", 34, UI.Ink, TextAnchor.MiddleLeft, FontStyle.Bold).At(0, 0, 1, 1, 24, 0, -24, 0);
        energyPips = UI.Label(hud.transform, "", 26, UI.Ink, TextAnchor.MiddleRight, FontStyle.Bold).At(0, 0, 1, 1, 24, 0, -24, 0);
        roundInfo = UI.Label(train.transform, "", 24, UI.Dim, TextAnchor.UpperCenter).At(0.5f, 1, 0.5f, 1, -420, -150, 420, -118);
        padFeed = UI.Label(train.transform, "", 28, UI.Ink, TextAnchor.UpperCenter, FontStyle.Bold).At(0.5f, 1, 0.5f, 1, -420, -190, 420, -152);

        UI.Btn(train.transform, "SAVE BRAIN", UI.Accent, Color.black, SaveBrain, 32).At(1, 1, 1, 1, -330, -112, -40, -36);
        trainSaved = UI.Label(train.transform, "", 24, UI.Ink, TextAnchor.UpperRight).At(1, 1, 1, 1, -560, -210, -40, -122);
        trainDaily = UI.Label(train.transform, "", 22, UI.Ink, TextAnchor.UpperRight).At(1, 1, 1, 1, -620, -250, -40, -214);

        // Move pad: defence under the left thumb, punches under the right. Pressed = played now and recorded as a demo.
        Pad(train.transform, new[] { ("dodge_l", "SLIP L"), ("block", "BLOCK"), ("dodge_r", "SLIP R"), ("step_back", "BACK"), ("step_in", "IN") }, false);
        Pad(train.transform, new[] { ("hook_l", "L HOOK"), ("uppercut", "UPPER"), ("hook_r", "R HOOK"), ("jab", "JAB"), ("cross", "CROSS"), ("body", "BODY") }, true);

        BuildCoachBar(train.transform, out trainTranscript, out trainField);

        summary = UI.Box(train.transform, new Color(0.05f, 0.055f, 0.07f, 0.95f), true, "summary").At(0.5f, 0.5f, 0.5f, 0.5f, -470, -60, 470, 200).gameObject;
        summary.AddComponent<Outline>().effectColor = new Color(1f, 0.58f, 0.13f, 0.5f);
        summaryTitle = UI.Label(summary.transform, "", 46, UI.Ink, TextAnchor.UpperCenter, FontStyle.Bold).At(0, 1, 1, 1, 30, -84, -30, -24);
        summaryBody = UI.Label(summary.transform, "", 28, UI.Ink, TextAnchor.UpperCenter).At(0, 0, 1, 1, 30, 70, -30, -96);
        UI.Btn(summary.transform, "OK", UI.Steel, UI.Ink, () => summary.SetActive(false), 26).At(0.5f, 0, 0.5f, 0, -90, 16, 90, 64);
        summary.SetActive(false);
    }

    // 3 columns, 2 rows of big thumb buttons in a bottom corner. Five-button pads stretch the bottom row to two.
    void Pad(Transform parent, (string move, string label)[] keys, bool right)
    {
        const float w = 164, h = 112, gap = 12;
        var pad = UI.Rt(parent, right ? "padAttack" : "padDefense");
        if (right) pad.At(1, 0, 1, 0, -(3 * w + 2 * gap) - 36, 30, -36, 30 + 2 * h + gap);
        else pad.At(0, 0, 0, 0, 36, 30, 36 + 3 * w + 2 * gap, 30 + 2 * h + gap);
        UI.Label(pad, right ? "PUNCH" : "DEFEND", 20, UI.Dim, right ? TextAnchor.LowerRight : TextAnchor.LowerLeft, FontStyle.Bold).At(0, 1, 1, 1, 4, 4, -4, 34);
        Color bg = right ? new Color(0.36f, 0.13f, 0.1f, 0.92f) : new Color(0.12f, 0.2f, 0.3f, 0.92f);
        for (int i = 0; i < keys.Length; i++)
        {
            int row = i / 3, n = row == 0 ? 3 : keys.Length - 3, c = i % 3;
            float cw = (3 * w + 2 * gap - (n - 1) * gap) / n, x = c * (cw + gap), y = (1 - row) * (h + gap);
            var key = keys[i].move;
            var btn = UI.Btn(pad, keys[i].label, bg, UI.Ink, null, 30);
            btn.At(0, 0, 0, 0, x, y, x + cw, y + h);
            btn.gameObject.AddComponent<HoldButton>().onDown = () => PadMove(key); // fires on touch-down: no release lag
        }
    }

    void BuildCoachBar(Transform parent, out Text transcript, out InputField field)
    {
        // Between the two pads: transcript line, hold-to-talk, typed fallback.
        var bar = UI.Rt(parent, "coach").At(0.5f, 0, 0.5f, 0, -370, 30, 370, 214);
        var tr = UI.Box(bar, new Color(0, 0, 0, 0.5f), true, "transcriptBg").At(0, 1, 1, 1, 0, -60, 0, 0);
        var t = UI.Label(tr.transform, "", 22, UI.Ink, TextAnchor.MiddleCenter).At(0, 0, 1, 1, 16, 0, -16, 0);
        transcript = t;
        var mic = UI.Box(bar, UI.Accent, true, "mic").At(0, 0, 0, 0, 0, 0, 250, 112);
        UI.Label(mic.transform, "●  HOLD TO TALK", 28, Color.black, TextAnchor.MiddleCenter, FontStyle.Bold);
        var hold = mic.gameObject.AddComponent<HoldButton>();
        hold.onDown = () => MicDown(t); hold.onUp = () => MicUp(t);
        var f = UI.Field(bar, "or type a rule…", "", 24).At(0, 0, 1, 0, 262, 26, -112, 86);
        UI.Btn(bar, "SEND", UI.Steel, UI.Ink, null, 24).At(1, 0, 1, 0, -100, 26, 0, 86).onClick.AddListener(() => SendTyped(f, t));
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
            barTrail[i] = UI.Box(bg.transform, new Color(1f, 0.92f, 0.8f, 0.85f), true, "trail").At(0, 0, 1, 1, 4, 4, -4, -4).rectTransform;
            var fill = UI.Box(bg.transform, UI.Brass, true, "fill").At(0, 0, 1, 1, 4, 4, -4, -4);
            barFill[i] = fill.rectTransform;
            barGroup[i] = g; barPos[i] = g.anchoredPosition;
            comboText[i] = UI.Label(fight.transform, "", 46, UI.Accent, left ? TextAnchor.UpperLeft : TextAnchor.UpperRight, FontStyle.Bold);
            if (left) comboText[i].At(0, 1, 0.5f, 1, 44, -250, -140, -160); else comboText[i].At(0.5f, 1, 1, 1, 140, -250, -44, -160);
            comboText[i].gameObject.AddComponent<Outline>().effectColor = new Color(0, 0, 0, 0.8f);
            UI.Label(g, left ? "health = fight budget" : "health = fight budget", 18, UI.Dim, left ? TextAnchor.LowerLeft : TextAnchor.LowerRight).At(0, 0, 1, 0, 0, 0, 0, 26);
        }
        UI.Label(fight.transform, "VS", 44, UI.Accent, TextAnchor.UpperCenter, FontStyle.Bold).At(0.5f, 1, 0.5f, 1, -100, -100, 100, -36);
        banner = UI.Label(fight.transform, "", 200, UI.Accent, TextAnchor.MiddleCenter, FontStyle.Bold).At(0, 0.5f, 1, 0.5f, 0, -150, 0, 170);
        var bo = banner.gameObject.AddComponent<Outline>(); bo.effectColor = new Color(0, 0, 0, 0.85f); bo.effectDistance = new Vector2(4, -4);
        banner.gameObject.AddComponent<Shadow>().effectColor = new Color(0, 0, 0, 0.5f);
        fightTitle = UI.Label(fight.transform, "", 20, UI.Dim, TextAnchor.UpperCenter).At(0.5f, 1, 0.5f, 1, -300, -150, 300, -104);

        var tk = UI.Rt(fight.transform, "ticker").At(1, 1, 1, 1, -620, -720, -40, -350);
        UI.Label(tk, "x402 PAYMENTS · HEDERA TESTNET", 20, UI.Dim, TextAnchor.UpperLeft, FontStyle.Bold).At(0, 1, 1, 1, 6, -28, 0, 0);
        var col = UI.Column(tk, 8, "rows");
        col.At(0, 0, 1, 1, 0, 0, 0, -34);
        col.childAlignment = TextAnchor.UpperLeft;
        ticker = col.transform;

        UI.Btn(fight.transform, "‹  HOME", UI.Steel, UI.Ink, GoHome, 28).At(0, 0, 0, 0, 40, 40, 240, 110);
        // Fights are autonomous: no mic or typing bar here, coaching happens in Train.
        var auto = UI.Box(fight.transform, new Color(0, 0, 0, 0.5f), true, "autonomous").At(0.5f, 0, 0.5f, 0, -620, 40, 620, 96);
        fightTranscript = UI.Label(auto.transform, "", 26, UI.Ink, TextAnchor.MiddleCenter).At(0, 0, 1, 1, 20, 0, -20, 0);
    }

    Bubble BuildBubble(Transform root, Fighter f)
    {
        var box = UI.Box(root, new Color(0.04f, 0.045f, 0.06f, 0.88f), true, "bubble:" + f.botId);
        var rt = box.rectTransform;
        rt.anchorMin = rt.anchorMax = Vector2.zero;
        rt.pivot = new Vector2(0.5f, 0);
        rt.sizeDelta = new Vector2(540, 168);
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
        if (!string.IsNullOrEmpty(w)) { var winner = w == a.botId ? a : b; winner.opp.KnockOut(); winner.Victory(); UpdateBars(); fx.KO(winner.opp.Chest); Banner("K.O.", UI.Red); }
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
        AudioListener.volume = 0f; // captures run unattended: never play sound
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
        trainField.text = "when he throws a left hook, slip right and uppercut";
        SendTyped(trainField, trainTranscript);
        yield return Wait(Api.Offline ? 0.9f : 2.5f);
        yield return Shot("04_train_learned");

        // Shadow sparring: a scripted thumb on the pad. Slip or block what the dummy throws, punch in the gaps.
        roundSeconds = 18f;
        float rEnd = Time.realtimeSinceStartup + 17.5f; bool midShot = false;
        string[] punches = { "jab", "cross", "uppercut", "hook_r", "body" };
        while (Time.realtimeSinceStartup < rEnd)
        {
            bool incoming = Fighter.IsAttack(b.Current);
            PadMove(incoming ? (b.Current == "hook_l" ? "dodge_r" : UnityEngine.Random.value < 0.5f ? "block" : "dodge_l")
                : a.lastMoves.Count > 0 && a.lastMoves[^1].StartsWith("dodge") ? "uppercut" : punches[UnityEngine.Random.Range(0, punches.Length)]);
            yield return Wait(0.75f);
            if (!midShot && roundMoves >= 6) { midShot = true; yield return Shot("05_round"); }
        }
        float su = Time.realtimeSinceStartup + 8f;
        while (!summary.activeSelf && Time.realtimeSinceStartup < su) yield return null;
        yield return Wait(0.6f);
        yield return Shot("06_round_summary");
        summary.SetActive(false);
        float du = Time.realtimeSinceStartup + 25f; // between rounds 0G drives; wait for a plan that copies a demo
        while (!(toast.text.StartsWith("Like your coach") && Time.unscaledTime < toastUntil) && Time.realtimeSinceStartup < du) yield return null;
        yield return Wait(0.3f);
        yield return Shot("07_train_like_coach");
        SaveBrain();
        yield return Wait(Api.Offline ? 1.5f : 4f);
        yield return Shot("08_train_saved");
        unsavedRounds = 0;

        GoHome();
        yield return Wait(2.5f);
        yield return Shot("09_home_growth");
        OpenBrain();
        yield return Wait(1.5f);
        yield return Shot("10_brain_versions");

        GoHome();
        yield return Wait(1f);
        HighStakes();
        yield return Wait(2f);
        yield return Shot("11_approval");
        CloseModal(true);

        Fighter.logStrikes = true;
        budgetUsd = 0.03f; // short fight so the KO lands inside the capture
        StartFight("normal", null);
        yield return Wait(0.3f);
        yield return Shot("12a_fight_banner");
        for (int n = 0; n < 3; n++)
        {
            float hu = Time.realtimeSinceStartup + 20f; // wait for a clean hit, then grab the frame with the sparks on it
            while (Time.unscaledTime >= hitStopUntil && Time.realtimeSinceStartup < hu && !over) yield return null;
            yield return null; yield return null;
            yield return Shot($"12b_fight_impact{n}");
            yield return Wait(0.5f);
        }
        yield return Shot("13_fight");
        float lu = Time.realtimeSinceStartup + 25f; // a plan that copies a demo shows "Like your coach" in the bubble
        while (!over && !bubA.meta.text.Contains("Like your coach") && Time.realtimeSinceStartup < lu) yield return null;
        if (!over && bubA.meta.text.Contains("Like your coach")) yield return Shot("13b_fight_like_coach");
        float until = Time.realtimeSinceStartup + 90f;
        while (!over && Time.realtimeSinceStartup < until) yield return null;
        yield return Wait(0.5f);
        yield return Shot("14_ko_slowmo");
        yield return Wait(2.5f);
        yield return Shot("15_ko");
        StartCoroutine(Replay(fightId));
        float ru = Time.realtimeSinceStartup + 75f;
        while (!replaying && Time.realtimeSinceStartup < ru) yield return null;
        yield return Wait(5f);
        yield return Shot("16_replay");
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
