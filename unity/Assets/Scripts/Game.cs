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
public partial class Game : MonoBehaviour
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
    GameObject home, train, fight, brain, modal, koPanel, campDot;
    Text badge, toast, cardName, cardMoney, cardBrain, cardAcct, cardDaily, serverStatus, brainList, trainDaily;
    InputField serverField, trainField;
    readonly List<string> counterAttacks = new(); // attacks your bot has a taught counter for; the sparring dummy throws these
    Button fightBtn, highBtn; bool aAlive = true, aNeedsRevive;
    Text trainTranscript, trainSaved, fightTranscript, fightTitle;
    Text[] barName = new Text[2], barMoney = new Text[2];
    RectTransform[] barFill = new RectTransform[2];
    Transform ticker;
    Bubble bubA, bubB;
    Text modalAction, modalCode, modalStatus, modalExplain; RawImage modalQr; Button modalBtn, modalOpenBtn; string approvalId, approvalUri; bool approvalOpen;
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
    Text[] comboText = new Text[2], portrait = new Text[2]; int[] combo = new int[2]; float[] comboAt = new float[2];
    Text vsClock;
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
    static readonly string[] StatNames = { "Reflexes", "Accuracy", "Defense", "Ring IQ", "Experience" };

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
        Matte(a); Matte(b); Monochrome();
        baseFov = cam.fieldOfView;
        var vol = FindFirstObjectByType<Volume>();
        if (vol != null && vol.sharedProfile != null)
        {
            vol.sharedProfile.TryGet(out chroma);
            if (vol.sharedProfile.TryGet(out vignette)) baseVignette = vignette.intensity.value;
        }
        UI.Init();
        if (fx != null) UI.Click = fx.Tick;
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
        yield return Enter(); // Flow.cs: first run → Welcome, returning player → Home
    }

    IEnumerator RefreshHome()
    {
        yield return Api.Call("GET", $"/bots/{a.botId}", null, bot =>
        {
            a.displayName = (string)bot["name"] ?? a.displayName;
            ShowCorner(bot); // Flow.cs: paint, league, rating, record, training lock
            cardMoney.text = Money(bot);
            cardBrain.text = $"Brain v{bot["brainVersion"]}  <color=#8E8E93>root {UI.Short((string)bot["brainRoot"])}</color>";
            ShowDaily(bot["daily"]);
            ShowProgress(bot["progress"]);
            aAlive = (bool?)bot["alive"] != false;
            aNeedsRevive = (bool?)bot["needsRevive"] == true;
            int repair = (int?)bot["repairIn"] ?? 0;
            cardAcct.text = $"{bot["accountId"]}" + ((bot["agentId"] is JObject ag && (ag["tokenId"]?.Type is JTokenType.Integer or JTokenType.String)) ? $"  ·  Agentic ID #{ag["tokenId"]}" : "") + (aAlive ? "" : aNeedsRevive
                ? "  <color=#F2523F>KNOCKED OUT in a high-stakes fight — World ID revive needed</color>"
                : $"  <color=#F2A33A>Repairing after a KO · {repair} s</color>");
            fightBtn.SetLabel(!aAlive ? (aNeedsRevive ? "REVIVE" : $"REPAIR {repair}s") : "FIGHT");
            campDot.SetActive(((int?)bot["progress"]?["demos"] ?? 0) >= 40 && bot["adapter"]?.Type != JTokenType.Object);
            highBtn.interactable = aAlive;
            if (!aAlive && !aNeedsRevive) StartCoroutine(RefreshSoon(Mathf.Clamp(repair, 1, 5)));
        }, e => { cardAcct.text = "<color=#F2523F>" + e + "</color>"; IdentityError(e); });
    }

    void ShowDaily(JToken d)
    {
        if (d == null || d.Type != JTokenType.Object) return;
        int streak = (int?)d["streak"] ?? 0;
        string txt = ((bool?)d["done"] == true ? "<color=#5CDB85>✓ Daily training done</color>" : $"Daily goal: <b>{d["drills"]}/{d["goal"]}</b> drills")
            + $"  <color=#8E8E93>·  {(streak > 0 ? $"{streak}-day streak" : "start a streak today")}</color>";
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
        HideFlow();
        bubA.root.gameObject.SetActive(false); bubB.root.gameObject.SetActive(false);
        if (m == Mode.Home || m == Mode.Brain) { ResetFighters(); }
        // Home/Brain frame only your bot; the dummy would stand between it and the camera.
        foreach (var r in b.GetComponentsInChildren<Renderer>(true)) r.enabled = m is not (Mode.Home or Mode.Brain);
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
        session++; replaying = false; live = false; a.scripted = b.scripted = false; RestoreOpponent(); Show(Mode.Home);
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
        roundTimer.text = "Ready to spar"; roundInfo.text = $"Tap a move to start a {roundSeconds:0} s round · every move is a lesson"; padFeed.text = "";
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
            fightTitle.text = (stakesKind == "high" ? "<color=#FFFFFF>HIGH STAKES</color>  ·  " : "") + $"<color=#8E8E93>{fightId}</color>";
            fightTranscript.text = "<color=#FFFFFF><b>Autonomous</b></color>  ·  no coaching mid-fight, your bot uses what it learned in training";
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
        ShowPlan(f, plan, moves);
    }

    // Thought bubble for a plan: say line, "Using: …" / "Like your coach: …", stance, moves, 0G request id.
    void ShowPlan(Fighter f, JToken plan, IEnumerable<string> moves)
    {
        var stance = (string)plan["stance"];
        string req = (string)plan["requestId"];
        bool tee = (bool?)plan["teeVerified"] == true;
        string meta = (string.IsNullOrEmpty(req) ? "" : "0G " + UI.Short(req, 8)) + (tee ? "  <color=#5CDB85>TEE verified</color>" : (bool?)plan["mock"] == true ? "  <color=#F2A33A>offline</color>" : "");
        string used = (string)plan["usedText"];
        bool demo = (string)plan["used"] == "demo";
        string usedLine = string.IsNullOrEmpty(used) ? "" : demo ? $"<color=#5CDB85><b>{used}</b></color>\n"
            : $"<color=#FFFFFF><b>Using: {used}</b></color> <color=#8E8E93>(learned in training)</color>\n";
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
        if (mode != Mode.Fight || over || replaying || live) return;
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
        koTitle.color = loser == a ? UI.Red : UI.Ink; koTitle.text = loser == a ? "DEFEAT" : "VICTORY";
        koSub.text = $"K.O.  ·  {winner.displayName} wins. {loser.displayName}'s wallet is empty: its fight budget can't cover the next hit.";
        koLessons.text = "Saving lessons to 0G…";
        koLedger.gameObject.SetActive(!Api.Offline && !string.IsNullOrEmpty(ledgerTopic) && ledgerTopic != "offline");
        koPanel.SetActive(true);
        if (live) { yield return LiveEnd(s); yield break; }
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
            portrait[i].text = string.IsNullOrEmpty(f.displayName) ? "" : f.displayName.Substring(0, 1).ToUpperInvariant();
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
        foreach (var p in pays.Reverse().Take(4))
        {
            string st = (string)p["status"];
            string mark = st == "settled" ? "<color=#5CDB85>✓ settled</color>" : st == "failed" ? "<color=#F2523F>✗ failed</color>" : "<color=#F2A33A>… pending</color>";
            string url = (string)p["hashscan"];
            var row = UI.Btn(ticker, "", UI.Card, UI.Ink, string.IsNullOrEmpty(url) ? null : () => Application.OpenURL(url)).H(62);
            var lbl = row.GetComponentInChildren<Text>();
            lbl.alignment = TextAnchor.MiddleLeft; lbl.fontStyle = FontStyle.Normal; lbl.fontSize = lbl.resizeTextMaxSize = 22; lbl.lineSpacing = 0.95f;
            lbl.rectTransform.offsetMin = new Vector2(18, 0);
            lbl.text = $"<b><color=#F2523F>-{UI.Usd((double)p["cents"]).TrimStart('-')}</color></b>  {NameOf((string)p["from"])} → {NameOf((string)p["to"])}  {mark}\n<size=16><color=#8E8E93>tx {p["txId"]}{(string.IsNullOrEmpty(url) ? "" : "  ↗ HashScan")}</color></size>";
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
        roundTimer.text = $"Round  <color=#FFFFFF>0:{Mathf.CeilToInt(Mathf.Max(0, left)):00}</color>";
        if (left <= 0) EndRound();
    }

    void Recorded(Demo d)
    {
        demoBatch.Add(new JObject { ["t"] = d.state["t"], ["state"] = d.state, ["action"] = d.action, ["outcome"] = d.outcome });
        bool atk = Fighter.IsAttack(d.action), ok = atk ? d.outcome == "landed" : d.outcome is "blocked" or "dodged";
        var o1 = (string)(d.state["oppLastMoves"] as JArray)?.LastOrDefault();
        if (!atk && Fighter.IsAttack(o1)) { drilling.Add(o1); if (drilling.Count > 8) drilling.RemoveAt(0); }
        roundMoves++;
        string mark = ok ? "<color=#5CDB85>✓</color>" : d.outcome == "got_hit" ? "<color=#F2523F>✗</color>" : "<color=#8E8E93>·</color>";
        padFeed.text = $"{mark} {char.ToUpperInvariant(UI.Nice(d.action)[0])}{UI.Nice(d.action).Substring(1)} · {d.outcome.Replace('_', ' ')}   <color=#8E8E93>demo recorded</color>";
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
        roundTimer.text = "Round over";
        roundInfo.text = "Your bot spars on its own now, copying what worked"; padFeed.text = "";
        Flush(true);
    }

    void RoundSummary(JToken r, string err)
    {
        summary.SetActive(true);
        var rd = r?["round"];
        if (rd != null) { guideRound = true; GuideCheck(); }
        if (rd == null) { summaryTitle.text = "ROUND OVER"; summaryBody.text = "<color=#F2523F>Couldn't send the round: " + err + "</color>"; return; }
        int xp = (int?)rd["xpGained"] ?? 0;
        summaryTitle.text = xp > 0 ? $"ROUND COMPLETE  <color=#66DB8C>+{xp} XP</color>" : "ROUND COMPLETE";
        var lines = new List<string> { $"<b>{rd["moves"]} moves recorded</b>" + (rd["highlight"]?.Type == JTokenType.String ? $"  ·  {rd["highlight"]}" : "") };
        if (rd["reason"]?.Type == JTokenType.String) lines.Add($"<color=#F2A33A>{rd["reason"]}</color>");
        lines.Add($"<size=24><color=#8E8E93>Energy {r["energy"]?["left"]}/{r["energy"]?["max"]}  ·  level {r["xp"]?["level"]}  ·  {r["total"]} demos in the brain</color></size>");
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
            cardLevel.text = $"{lv["level"]}";
            cardXp.text = $"{into}/{next} XP";
            SetFill(xpFill, (float)into / next);
        }
        if (p["stats"] is JObject st)
            for (int i = 0; i < StatKeys.Length; i++) { int v = (int?)st[StatKeys[i]] ?? 0; statVal[i].text = v.ToString(); SetFill(statFill[i], v / 100f); }
        if (p["energy"] is JObject en)
        {
            int left = (int?)en["left"] ?? 0, max = (int?)en["max"] ?? 5;
            string pips = "<color=#FFFFFF>" + new string('●', left) + "</color><color=#3A3A40>" + new string('●', Math.Max(0, max - left)) + "</color>";
            cardEnergy.text = $"{pips}  {left}/{max}";
            energyPips.text = $"<color=#FFFFFF>⚡</color> {pips}";
        }
    }

    static void SetFill(RectTransform f, float k) { k = Mathf.Clamp01(k); f.anchorMax = new Vector2(k, 1); f.gameObject.SetActive(k > 0.02f); }

    // ---------------------------------------------------------------- coaching
    bool recording;
    void MicDown(Text transcript)
    {
        recording = Mic.Start();
        transcript.text = recording ? "<color=#FFFFFF>● Listening…</color> release to send" : (Api.Offline ? "No mic — offline sim will pretend you taught a left-hook counter." : "No microphone — type instead.");
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
        guideCoach = true; GuideCheck();
        transcript.text = $"“{r["text"]}”" + (req != null ? $"  <size=20><color=#8E8E93>0G {UI.Short(req, 8)}</color></size>" : "");
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
            trainSaved.text = $"<b>Brain v{r["version"]} saved</b>\n<size=22><color=#8E8E93>root {UI.Short((string)r["root"], 10)}</color></size>" + ((bool?)r["mock"] == true ? "  <size=20><color=#F2A33A>offline</color></size>" : "");
        }, e => trainSaved.text = "<color=#F2523F>Save failed: " + e + "</color>"));
    }

    // ---------------------------------------------------------------- approvals
    void HighStakes() => Approve("high_stakes", "Enter high-stakes match", id => StartFight("high", id));

    // A KO leaves your bot alive=false; it needs a human-approved revive before its next fight.
    void Revive() => Approve("revive", "Revive " + a.displayName, id =>
        StartCoroutine(Api.Call("POST", $"/bots/{a.botId}/revive", new JObject { ["approvalId"] = id }, _ => { Toast(a.displayName + " is back on its feet"); StartCoroutine(RefreshHome()); }, e => Toast("Revive failed: " + e))));

    void Approve(string action, string label, Action<string> onApproved) =>
        Approve("/approvals", new JObject { ["botId"] = a.botId, ["action"] = action }, label,
            "Tap Open World App (or scan from another phone). A human must OK every high-stakes action; each approval works once.", "Cancel", onApproved, null);

    // Any World ID device-flow approval (high stakes, revive, sign-in). onClosed runs when the player closes it without approving.
    Action modalClosed;
    void Approve(string path, JObject body, string label, string explain, string closeLabel, Action<string> onApproved, Action onClosed)
    {
        StartCoroutine(Api.Call("POST", path, body, r =>
        {
            approvalId = (string)r["approvalId"];
            approvalOpen = true;
            modalClosed = onClosed;
            modal.SetActive(true);
            modalAction.text = label;
            modalExplain.text = explain;
            modalCode.text = $"Code  <b>{r["userCode"]}</b>";
            approvalUri = Api.Offline ? null : (string)(r["appUri"] ?? r["qrUri"]);
            modalOpenBtn.gameObject.SetActive(approvalUri != null);
            if (approvalUri != null && Application.isMobilePlatform) OpenApprovalSheet(); // same phone: straight to World
            modalStatus.text = "<color=#F2A33A>Waiting for World ID approval…</color>" + (Api.Offline ? "\n<size=20>OFFLINE: placeholder code, auto-approves in a few seconds</size>" : "");
            modalBtn.SetLabel(closeLabel);
            modalQr.texture = null;
            StartCoroutine(Api.Texture($"/approvals/{approvalId}/qr.png", t => { if (modalQr != null) modalQr.texture = t; }));
            StartCoroutine(PollApproval(approvalId, onApproved));
        }, e => { Toast("Approval failed: " + e); onClosed?.Invoke(); }));
    }

#if UNITY_IOS && !UNITY_EDITOR
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern void IcWorldAuthOpen(string url);
#endif
    // iOS: in-app browser sheet (closes itself on the ironcorner:// redirect). Elsewhere: the system browser.
    void OpenApprovalSheet()
    {
        if (string.IsNullOrEmpty(approvalUri)) return;
#if UNITY_IOS && !UNITY_EDITOR
        if (approvalUri.Contains("/world/go/")) { IcWorldAuthOpen(approvalUri + "?p=sheet"); return; }
#endif
        Application.OpenURL(approvalUri);
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
                modalClosed = null;
                CloseModal(false);
                onApproved(id);
                yield break;
            }
            if (status is "denied" or "expired" or "cancelled" or "used")
            {
                modalStatus.text = $"<color=#F2523F>Not approved — nothing happened</color>\n<size=22>({status})</size>";
                modalBtn.SetLabel("Close");
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
        var closed = modalClosed; modalClosed = null;
        if (cancel) closed?.Invoke();
    }

    // ---------------------------------------------------------------- brain screen
    void OpenBrain()
    {
        Show(Mode.Brain);
        brainList.text = "Loading…";
        StartCoroutine(Api.Call("GET", $"/bots/{a.botId}", null, r =>
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"<size=30><b>{r["name"]}</b></size>   <color=#8E8E93>current v{r["brainVersion"]} · root {UI.Short((string)r["brainRoot"], 10)}</color>\n\n");
            sb.Append(Sec("Version history"));
            var hist = r["brainHistory"] as JArray;
            if (hist == null || hist.Count == 0) sb.Append("<color=#8E8E93>No saves yet. Train and press Save Brain.</color>\n");
            else foreach (var h in hist.Reverse().Take(5))
            {
                sb.Append($"<b>v{h["version"]}</b>" + (h["level"] != null ? $"  <color=#FFFFFF>Level {h["level"]}</color> · {h["demos"]} demos" : "")
                    + $"   <color=#8E8E93>root {UI.Short((string)h["root"], 10)}   {FormatAt(h["at"])}</color>\n");
                if (h["stats"] is JObject st)
                    sb.Append($"<size=20><color=#8E8E93>      reflexes {st["reflexes"]} · accuracy {st["accuracy"]} · defense {st["defense"]} · ring IQ {st["ring_iq"]} · experience {st["experience"]}</color></size>\n");
            }
            var brainObj = r["brain"];
            string Ms(JToken m) => string.Join(" ", (m as JArray ?? new JArray()).Select(x => (string)x));
            if (brainObj?["counters"] is JArray cs && cs.Count > 0)
            {
                sb.Append("\n" + Sec("Counters"));
                foreach (var c in cs.Take(4)) sb.Append($"when he throws <b>{c["when"]}</b>  →  {Ms(c["moves"])}\n");
            }
            if (brainObj?["combos"] is JArray co && co.Count > 0)
            {
                sb.Append("\n" + Sec("Combos"));
                foreach (var c in co.Take(5)) sb.Append($"<b>{c["use"]}</b>  {Ms(c["moves"])}\n");
            }
            if (brainObj?["phrases"] is JArray ph && ph.Count > 0)
            {
                sb.Append("\n" + Sec("Phrases"));
                foreach (var p in ph.Take(4)) sb.Append($"“{p["say"]}”  <color=#FFFFFF>→</color>  {p["means"]}  <color=#8E8E93>({string.Join(" ", (p["moves"] as JArray ?? new JArray()).Select(x => (string)x))})</color>\n");
            }
            if (brainObj?["lessons"] is JArray le && le.Count > 0)
            {
                sb.Append("\n" + Sec("Lessons"));
                foreach (var l in le.Take(3)) sb.Append("• " + l + "\n");
            }
            brainList.text = sb.ToString();
        }, e => brainList.text = "<color=#F2523F>" + e + "</color>"));
    }

    static string Sec(string t) => $"<size=21><color=#FFFFFF><b>{t.ToUpperInvariant()}</b></color></size>\n";

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
        int secs = Mathf.Max(0, Mathf.FloorToInt(Time.time - fightStart));
        vsClock.text = $"{secs / 60:00}:{secs % 60:00}";
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

    void ShowCombo(int side) => comboText[side].text = $"<size=110><color=#FFFFFF>{combo[side]}</color></size> HIT COMBO";

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
            // Your bot, front three-quarter. Home: centred between the menu and the card. Brain: left, beside the wide panel.
            var fwd = a.transform.forward;
            var yaw = Quaternion.Euler(0, -18f + Mathf.Sin(tt * 0.2f) * 10f, 0);
            pos = pa + yaw * fwd * 3.6f + Vector3.up * 1.55f;
            look = pa + Vector3.up * 1.05f - a.transform.right * (mode == Mode.Brain ? 0.95f : 0.12f) * (cam.aspect / 1.78f);
        }
        else
        {
            var mid = (pa + pb) * 0.5f;
            var axis = new Vector3(pb.x - pa.x, 0, pb.z - pa.z).normalized;
            var side = Vector3.Cross(Vector3.up, axis); // camera on this side sees a on the left (under its health bar)
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

        BuildHome(root); BuildTrain(root); BuildFight(root); BuildBrain(root); BuildFlow(root);
        bubA = BuildBubble(root, a); bubB = BuildBubble(root, b);
        BuildModal(root); BuildKO(root);

        var tb = UI.Panel(UI.Box(root, UI.Card, true, "toast"), new Color(1, 1, 1, 0.22f)).At(0.5f, 1, 0.5f, 1, -440, -262, 440, -192);
        toast = UI.Label(tb.transform, "", 28, UI.Ink, TextAnchor.MiddleCenter, FontStyle.Bold);
        tb.gameObject.Appear(0.9f).SetActive(false);
        foreach (var g in new[] { home, train, fight, brain, koPanel, modal, welcome, create, fund, find, camp }) g.Appear();
        summary.Appear(0.9f); guide.Appear(0.94f);

        badge = UI.Label(root, "…", 18, UI.Dim, TextAnchor.UpperCenter, FontStyle.Bold).At(0.5f, 1, 0.5f, 1, -500, -30, 500, -6); // top centre, clear of names and buttons
    }

    void BuildHome(Transform root)
    {
        home = UI.Go(root, "Home");
        ((RectTransform)home.transform).At(0, 0, 1, 1);
        UI.Fade(home.transform, 1000); // dark wash on the left so text never sits on bright ropes

        // Player card, top left: level badge, name, league, XP.
        var pc = UI.Rt(home.transform, "player").At(0, 1, 0, 1, 48, -230, 900, -40);
        var lvl = UI.Go(pc, "level").AddComponent<Image>(); lvl.sprite = UI.Circle; lvl.color = new Color(0.06f, 0.06f, 0.07f, 0.95f); lvl.raycastTarget = false;
        lvl.At(0, 1, 0, 1, 0, -150, 150, 0); lvl.gameObject.AddComponent<SoftShadow>();
        var lr = UI.Go(lvl.transform, "ring").AddComponent<Image>(); lr.sprite = UI.CircleRing; lr.color = UI.Ink; lr.raycastTarget = false; lr.At(0, 0, 1, 1);
        UI.Label(lvl.transform, "LV", 18, UI.Dim, TextAnchor.UpperCenter, FontStyle.Bold).At(0, 0, 1, 1, 0, 0, 0, -24);
        cardLevel = UI.Title(lvl.transform, "1", 64, UI.Ink, TextAnchor.MiddleCenter).At(0, 0, 1, 1, 0, -16, 0, 0);
        cardName = UI.Title(pc, "BRASS KNUCKLE", 62, UI.Ink).At(0, 1, 1, 1, 176, -84, 0, 0);
        cardLeague = UI.Label(pc, "", 20, UI.Dim, TextAnchor.MiddleLeft, FontStyle.Bold).At(0, 1, 1, 1, 180, -118, 0, -86);
        xpFill = UI.Meter(UI.Rt(pc, "xp").At(0, 1, 0, 1, 180, -146, 560, -134));
        cardXp = UI.Label(pc, "0/100 XP", 18, UI.Dim, TextAnchor.MiddleLeft).At(0, 1, 0, 1, 576, -154, 800, -126);

        // Stats card under it: the five brain stats as rows with bars.
        var sc = UI.Box(home.transform, UI.Card, true, "stats").At(0, 1, 0, 1, 48, -700, 620, -270);
        UI.Caption(sc.transform, "BRAIN STATS").At(0, 1, 1, 1, 32, -52, -32, -18);
        for (int i = 0; i < StatKeys.Length; i++)
        {
            float y = -78 - i * 52;
            UI.Label(sc.transform, StatNames[i], 24, UI.Ink).At(0, 1, 0, 1, 32, y - 40, 230, y);
            statFill[i] = UI.Meter(UI.Rt(sc.transform, "m" + i).At(0, 1, 1, 1, 230, y - 25, -110, y - 15));
            statVal[i] = UI.Title(sc.transform, "0", 30, UI.Ink, TextAnchor.MiddleRight).At(1, 1, 1, 1, -100, y - 42, -32, y + 2);
            SetFill(statFill[i], 0);
        }
        cardBrain = UI.Label(sc.transform, "Brain —", 18, UI.Dim).At(0, 0, 1, 0, 32, 34, -24, 62);
        cardAcct = UI.Label(sc.transform, "", 18, UI.Dim).At(0, 0, 1, 0, 32, 10, -24, 36);

        // Daily goal chip, bottom left, like a quest tracker.
        var dq = UI.Box(home.transform, UI.Card, true, "daily").At(0, 0, 0, 0, 48, 56, 620, 150);
        UI.Caption(dq.transform, "DAILY").At(0, 1, 0, 1, 30, -40, 200, -12);
        cardDaily = UI.Label(dq.transform, "", 22, UI.Ink, TextAnchor.MiddleLeft).At(0, 0, 1, 1, 30, 8, -24, -36);

        // Top right: energy and wallet pills, settings.
        cardEnergy = UI.Pill(UI.Rt(home.transform, "energy").At(1, 1, 1, 1, -900, -112, -620, -44), "", 24);
        var wallet = UI.Btn(home.transform, "", UI.Steel, UI.Ink, OpenFund, 26).At(1, 1, 1, 1, -600, -112, -160, -44);
        cardMoney = wallet.GetComponentInChildren<Text>(); cardMoney.rectTransform.offsetMax = new Vector2(-64, 0);
        UI.Label(wallet.transform, "+", 34, UI.Ink, TextAnchor.MiddleRight, FontStyle.Bold).At(0, 0, 1, 1, 0, 2, -24, 0);
        var srv = UI.Box(home.transform, UI.Card, true, "server").At(1, 1, 1, 1, -900, -268, -64, -136);
        serverField = UI.Field(srv.transform, "http://<laptop-ip>:8787", Api.BaseUrl, 22).At(0, 1, 1, 1, 24, -84, -196, -24);
        UI.Btn(srv.transform, "Connect", UI.Accent, UI.Dark, () => { Api.BaseUrl = serverField.text; StartCoroutine(Connect()); }, 22).At(1, 1, 1, 1, -180, -84, -24, -24);
        serverStatus = UI.Label(srv.transform, "", 18, UI.Dim, TextAnchor.MiddleLeft).At(0, 0, 1, 0, 28, 10, -24, 44);
        srv.gameObject.SetActive(false);
        UI.Round1(home.transform, "≡", () => srv.gameObject.SetActive(!srv.gameObject.activeSelf), 34).At(1, 1, 1, 1, -136, -112, -64, -40);

        // Right edge: side tiles. Camp shows a dot once there are enough demos for a fine-tune.
        var tiles = new (string label, Action go)[] { ("BRAIN", OpenBrain), ("CAMP", OpenCamp), ("WALLET", OpenFund) };
        for (int i = 0; i < tiles.Length; i++)
        {
            float top = -170 - i * 150;
            var t = UI.Big(home.transform, tiles[i].label, false, tiles[i].go, 24).At(1, 1, 1, 1, -200, top - 130, -64, top);
            if (i == 1) campDot = UI.Badge(t.transform.Find("face"));
        }
        campDot.SetActive(false);

        // Bottom right: the two calls to action. Fight breathes; high stakes sits above it.
        UI.Big(home.transform, "TRAIN", false, StartTrainGuided, 46).At(1, 0, 1, 0, -900, 56, -580, 206);
        fightBtn = UI.Big(home.transform, "FIGHT", true, FindClicked, 58, pulse: true);
        fightBtn.At(1, 0, 1, 0, -560, 56, -64, 206);
        highBtn = UI.Btn(home.transform, "High stakes  <size=18><color=#8E8E93>World ID</color></size>", UI.Outline, UI.Ink, () => { budgetUsd = 0.5f; HighStakes(); }, 22);
        highBtn.At(1, 0, 1, 0, -560, 226, -64, 286);
    }

    void BuildTrain(Transform root)
    {
        train = UI.Go(root, "Train");
        ((RectTransform)train.transform).At(0, 0, 1, 1);
        UI.Title(train.transform, "SPARRING", 46, UI.Ink).At(0, 1, 0, 1, 48, -112, 700, -36);

        // Round HUD, top centre: clock · energy pill, then what's happening under it.
        var hud = UI.Panel(UI.Box(train.transform, UI.Card, true, "hud")).At(0.5f, 1, 0.5f, 1, -300, -108, 300, -44);
        roundTimer = UI.Label(hud.transform, "", 30, UI.Ink, TextAnchor.MiddleLeft, FontStyle.Bold).At(0, 0, 1, 1, 28, 0, -28, 0);
        energyPips = UI.Label(hud.transform, "", 24, UI.Ink, TextAnchor.MiddleRight, FontStyle.Bold).At(0, 0, 1, 1, 28, 0, -28, 0);
        roundInfo = UI.Label(train.transform, "", 22, UI.Dim, TextAnchor.UpperCenter).At(0.5f, 1, 0.5f, 1, -420, -146, 420, -116);
        padFeed = UI.Label(train.transform, "", 26, UI.Ink, TextAnchor.UpperCenter, FontStyle.Bold).At(0.5f, 1, 0.5f, 1, -420, -184, 420, -150);

        // Done leaves training; GoHome saves the brain to 0G Storage when there are unsaved rounds.
        UI.Big(train.transform, "DONE", true, GoHome, 32).At(1, 1, 1, 1, -260, -118, -40, -36);
        trainSaved = UI.Label(train.transform, "", 22, UI.Ink, TextAnchor.UpperRight).At(1, 1, 1, 1, -560, -196, -40, -120);
        trainDaily = UI.Label(train.transform, "", 21, UI.Dim, TextAnchor.UpperRight).At(1, 1, 1, 1, -620, -236, -40, -204);

        // Move pad: defence under the left thumb, punches under the right. Pressed = played now and recorded as a demo.
        Pad(train.transform, new[] { ("dodge_l", "Slip L"), ("block", "Block"), ("dodge_r", "Slip R"), ("step_back", "← Back"), ("step_in", "In →") }, false);
        Pad(train.transform, new[] { ("hook_l", "L hook"), ("uppercut", "Upper"), ("hook_r", "R hook"), ("jab", "Jab"), ("cross", "Cross"), ("body", "Body") }, true);

        BuildCoachBar(train.transform, out trainTranscript, out trainField);

        // Round result card: amber check badge, title, what the round taught, OK.
        summary = UI.Panel(UI.Box(train.transform, UI.Card, true, "summary"), new Color(1, 1, 1, 0.16f)).At(0.5f, 0.5f, 0.5f, 0.5f, -440, -190, 440, 250).gameObject;
        var badgeImg = UI.Go(summary.transform, "check").AddComponent<Image>(); badgeImg.sprite = UI.Circle; badgeImg.color = UI.Accent; badgeImg.raycastTarget = false;
        badgeImg.At(0.5f, 1, 0.5f, 1, -40, -112, 40, -32);
        UI.Label(badgeImg.transform, "✓", 46, UI.Dark, TextAnchor.MiddleCenter, FontStyle.Bold);
        summaryTitle = UI.Title(summary.transform, "", 50, UI.Ink, TextAnchor.UpperCenter).At(0, 1, 1, 1, 30, -192, -30, -122);
        summaryBody = UI.Label(summary.transform, "", 26, UI.Ink, TextAnchor.UpperCenter).At(0, 0, 1, 1, 40, 110, -40, -196);
        summaryBody.lineSpacing = 1.15f;
        UI.Big(summary.transform, "KEEP TRAINING", true, () => summary.SetActive(false), 32, pulse: true).At(0, 0, 1, 0, 40, 26, -40, 112);
        summary.SetActive(false);
    }

    // 3 columns, 2 rows of big thumb buttons in a bottom corner. Five-button pads stretch the bottom row to two.
    void Pad(Transform parent, (string move, string label)[] keys, bool right)
    {
        const float w = 158, h = 104, gap = 14;
        var pad = UI.Rt(parent, right ? "padAttack" : "padDefense");
        if (right) pad.At(1, 0, 1, 0, -(3 * w + 2 * gap) - 36, 30, -36, 30 + 2 * h + gap);
        else pad.At(0, 0, 0, 0, 36, 30, 36 + 3 * w + 2 * gap, 30 + 2 * h + gap);
        UI.Caption(pad, right ? "PUNCH" : "DEFEND").At(0, 1, 1, 1, 4, 6, -4, 36).alignment = right ? TextAnchor.LowerRight : TextAnchor.LowerLeft;
        // Chunky keys; punches get a brighter rim than defence so the two pads read apart at a glance.
        var rim = right ? new Color(1, 1, 1, 0.5f) : new Color(1, 1, 1, 0.16f);
        for (int i = 0; i < keys.Length; i++)
        {
            int row = i / 3, n = row == 0 ? 3 : keys.Length - 3, c = i % 3;
            float cw = (3 * w + 2 * gap - (n - 1) * gap) / n, x = c * (cw + gap), y = (1 - row) * (h + gap);
            var key = keys[i].move;
            var btn = UI.Big(pad, keys[i].label.ToUpperInvariant(), false, null, 28);
            btn.At(0, 0, 0, 0, x, y, x + cw, y + h);
            var r = btn.transform.Find("face/rim")?.GetComponent<Image>(); if (r != null) r.color = rim;
            btn.gameObject.AddComponent<HoldButton>().onDown = () => PadMove(key); // fires on touch-down: no release lag
        }
    }

    void BuildCoachBar(Transform parent, out Text transcript, out InputField field)
    {
        // Between the two pads: transcript line, hold-to-talk, typed fallback.
        var bar = UI.Rt(parent, "coach").At(0.5f, 0, 0.5f, 0, -370, 30, 370, 214);
        var tr = UI.Panel(UI.Box(bar, UI.Card, true, "transcriptBg")).At(0, 1, 1, 1, 0, -60, 0, 0);
        var t = UI.Label(tr.transform, "", 22, UI.Ink, TextAnchor.MiddleCenter).At(0, 0, 1, 1, 16, 0, -16, 0);
        transcript = t;
        var mic = UI.Panel(UI.Box(bar, UI.Accent, true, "mic", 0.4f), new Color(1, 1, 1, 0f)).At(0, 0, 0, 0, 0, 0, 250, 112);
        mic.gameObject.AddComponent<Press>();
        UI.Label(mic.transform, "●  Hold to talk", 28, UI.Dark, TextAnchor.MiddleCenter, FontStyle.Bold);
        var hold = mic.gameObject.AddComponent<HoldButton>();
        hold.onDown = () => MicDown(t); hold.onUp = () => MicUp(t);
        var f = UI.Field(bar, "or type a rule…", "", 24).At(0, 0, 1, 0, 262, 26, -112, 86);
        UI.Btn(bar, "Send", UI.Steel, UI.Ink, null, 24).At(1, 0, 1, 0, -100, 26, 0, 86).onClick.AddListener(() => SendTyped(f, t));
        field = f;
    }

    void BuildFight(Transform root)
    {
        fight = UI.Go(root, "Fight");
        ((RectTransform)fight.transform).At(0, 0, 1, 1);
        for (int i = 0; i < 2; i++)
        {
            bool left = i == 0;
            // Fighting-game header: big portrait badge at the outer edge, heavy name, thick framed bar.
            var g = UI.Rt(fight.transform, left ? "barA" : "barB");
            if (left) g.At(0, 1, 0.5f, 1, 36, -156, -110, -28); else g.At(0.5f, 1, 1, 1, 110, -156, -36, -28);
            var por = UI.Portrait(g, left ? UI.Ink : UI.Dim, "");
            ((RectTransform)por.transform.parent).At(left ? 0 : 1, 0.5f, left ? 0 : 1, 0.5f, left ? 0 : -128, -64, left ? 128 : 0, 64);
            portrait[i] = por; por.font = UI.Display; por.fontSize = por.resizeTextMaxSize = 56;
            float inL = left ? 146 : 0, inR = left ? 0 : -146;
            barName[i] = UI.Title(g, "", 36, UI.Ink, left ? TextAnchor.LowerLeft : TextAnchor.LowerRight).At(0, 0.5f, 1, 1, inL, 8, inR, 0);
            barMoney[i] = UI.Title(g, "", 34, UI.Brass, left ? TextAnchor.LowerRight : TextAnchor.LowerLeft).At(0, 0.5f, 1, 1, inL, 8, inR, 0);
            var frame = UI.Box(g, new Color(0.02f, 0.02f, 0.025f, 0.92f), true, "frame", 0.5f).At(0, 0.5f, 1, 0.5f, inL, -40, inR, 0);
            frame.gameObject.AddComponent<SoftShadow>();
            var bg = UI.Box(frame.transform, new Color(1, 1, 1, 0.08f), true, "bg", 0.5f).At(0, 0, 1, 1, 5, 5, -5, -5);
            barTrail[i] = UI.Box(bg.transform, new Color(0.95f, 0.35f, 0.3f, 0.75f), true, "trail", 0.5f).At(0, 0, 1, 1).rectTransform;
            var fill = UI.Shine(UI.Box(bg.transform, UI.Brass, true, "fill", 0.5f)).At(0, 0, 1, 1);
            barFill[i] = fill.rectTransform;
            UI.Label(g, "health = fight budget", 17, UI.Dim, left ? TextAnchor.UpperLeft : TextAnchor.UpperRight).At(0, 0, 1, 0.5f, inL, -14, inR, -46);
            barGroup[i] = g; barPos[i] = g.anchoredPosition;
            // Combo: big italic count + caption, mid-height on that fighter's side (clear of the plan bubble above).
            comboText[i] = UI.Title(fight.transform, "", 34, UI.Ink, left ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight);
            comboText[i].rectTransform.pivot = new Vector2(left ? 0 : 1, 0.5f);
            if (left) comboText[i].At(0, 0.5f, 0.5f, 0.5f, 60, -60, 0, 80); else comboText[i].At(0.5f, 0.5f, 1, 0.5f, 0, -60, -60, 80);
            comboText[i].gameObject.AddComponent<Outline>().effectColor = new Color(0, 0, 0, 0.7f);
        }
        // Round clock in a ringed badge between the bars.
        var clock = UI.Go(fight.transform, "clock").AddComponent<Image>(); clock.sprite = UI.Circle; clock.color = new Color(0.03f, 0.03f, 0.035f, 0.95f); clock.raycastTarget = false;
        clock.At(0.5f, 1, 0.5f, 1, -76, -176, 76, -24); clock.gameObject.AddComponent<SoftShadow>();
        var cr = UI.Go(clock.transform, "ring").AddComponent<Image>(); cr.sprite = UI.CircleRing; cr.color = UI.Ink; cr.raycastTarget = false; cr.At(0, 0, 1, 1);
        UI.Title(clock.transform, "VS", 22, UI.Dim, TextAnchor.UpperCenter).At(0, 0, 1, 1, 0, 0, 0, -26);
        vsClock = UI.Title(clock.transform, "00:00", 40, UI.Ink, TextAnchor.MiddleCenter).At(0, 0, 1, 1, 0, -12, 0, 0);
        banner = UI.Title(fight.transform, "", 210, UI.Accent, TextAnchor.MiddleCenter).At(0, 0.5f, 1, 0.5f, 0, -150, 0, 170);
        var bo = banner.gameObject.AddComponent<Outline>(); bo.effectColor = new Color(0, 0, 0, 0.85f); bo.effectDistance = new Vector2(4, -4);
        banner.gameObject.AddComponent<Shadow>().effectColor = new Color(0, 0, 0, 0.5f);
        fightTitle = UI.Label(fight.transform, "", 18, UI.Dim, TextAnchor.UpperCenter).At(0.5f, 1, 0.5f, 1, -300, -230, 300, -184);

        // x402 payments: compact rows, right edge, below the opponent's thought bubble.
        var tk = UI.Rt(fight.transform, "ticker").At(1, 1, 1, 1, -560, -780, -40, -420);
        UI.Caption(tk, "x402 PAYMENTS  ·  HEDERA TESTNET").At(0, 1, 1, 1, 4, -26, 0, 0);
        var col = UI.Column(tk, 6, "rows");
        col.At(0, 0, 1, 1, 0, 0, 0, -34);
        col.childAlignment = TextAnchor.UpperLeft;
        ticker = col.transform;

        // Fights are autonomous: no mic or typing bar here, coaching happens in Train.
        var auto = UI.Panel(UI.Box(fight.transform, UI.Card, true, "autonomous")).At(0.5f, 0, 0.5f, 0, -560, 40, 560, 92);
        fightTranscript = UI.Label(auto.transform, "", 22, UI.Dim, TextAnchor.MiddleCenter).At(0, 0, 1, 1, 20, 0, -20, 0);
    }

    Bubble BuildBubble(Transform root, Fighter f)
    {
        var box = UI.Panel(UI.Box(root, UI.Card, true, "bubble:" + f.botId), new Color(1, 1, 1, f == a ? 0.3f : 0.14f));
        var rt = box.rectTransform;
        rt.anchorMin = rt.anchorMax = Vector2.zero;
        rt.pivot = new Vector2(0.5f, 0);
        rt.sizeDelta = new Vector2(540, 134);
        var say = UI.Label(box.transform, "", 30, UI.Ink, TextAnchor.UpperLeft, FontStyle.Bold).At(0, 0, 1, 1, 24, 0, -24, -16);
        var meta = UI.Label(box.transform, "", 19, UI.Dim, TextAnchor.LowerLeft).At(0, 0, 1, 1, 24, 14, -24, 0);
        box.gameObject.SetActive(false);
        return new Bubble { root = rt, say = say, meta = meta, f = f };
    }

    void BuildBrain(Transform root)
    {
        brain = UI.Go(root, "Brain");
        ((RectTransform)brain.transform).At(0, 0, 1, 1);
        var panel = UI.Box(brain.transform, UI.Card, true, "panel").At(0.38f, 0, 1, 1, 0, 40, -40, -40);
        UI.Label(panel.transform, "Brain", 52, UI.Ink, TextAnchor.UpperLeft, FontStyle.Bold).At(0, 1, 1, 1, 52, -110, -40, -40);
        UI.Label(panel.transform, "What your fighter has learned. Encrypted on 0G Storage.", 24, UI.Dim, TextAnchor.UpperLeft).At(0, 1, 1, 1, 54, -150, -40, -112);
        UI.Box(panel.transform, UI.Line, false, "rule").At(0, 1, 1, 1, 52, -172, -52, -170);
        brainList = UI.Label(panel.transform, "", 25, UI.Ink, TextAnchor.UpperLeft).At(0, 0, 1, 1, 54, 40, -48, -196);
        brainList.lineSpacing = 1.2f;
        UI.Dismiss(brain.transform, GoHome);
        UI.Close(panel.transform, GoHome);
    }

    void BuildModal(Transform root)
    {
        modal = UI.Box(root, new Color(0, 0, 0, 0.75f), false, "modal").At(0, 0, 1, 1).gameObject;
        var card = UI.Box(modal.transform, UI.Card, true, "card").At(0.5f, 0.5f, 0.5f, 0.5f, -520, -380, 520, 380);
        UI.Caption(card.transform, "WORLD ID APPROVAL").At(0, 1, 1, 1, 50, -76, -50, -40);
        modalAction = UI.Label(card.transform, "", 48, UI.Ink, TextAnchor.UpperLeft, FontStyle.Bold).At(0, 1, 1, 1, 50, -156, -50, -84);
        var qrBg = UI.Box(card.transform, Color.white, true, "qrBg").At(0, 0, 0, 0, 50, 120, 470, 540);
        modalQr = UI.Go(qrBg.transform, "qr").AddComponent<RawImage>().At(0, 0, 1, 1, 20, 20, -20, -20);
        modalExplain = UI.Label(card.transform, "", 28, UI.Dim, TextAnchor.UpperLeft).At(0, 0, 1, 0, 520, 380, -50, 540);
        modalCode = UI.Label(card.transform, "", 40, UI.Ink, TextAnchor.UpperLeft).At(0, 0, 1, 0, 520, 300, -50, 370);
        modalStatus = UI.Label(card.transform, "", 30, UI.Ink, TextAnchor.UpperLeft).At(0, 0, 1, 0, 520, 140, -50, 290);
        modalBtn = UI.Btn(card.transform, "Cancel", UI.Steel, UI.Ink, () => CloseModal(true), 28);
        // Playing on the phone that has World App: open the approval link there (World App takes it), then come back.
        modalOpenBtn = UI.Btn(card.transform, "Open World App", UI.Accent, UI.Dark, OpenApprovalSheet, 28);
        modalOpenBtn.At(0, 0, 0.5f, 0, 50, 36, -10, 100);
        modalBtn.At(0.5f, 0, 1, 0, 10, 36, -50, 100);
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

        int s = ++session; replaying = true; over = true; fightStart = Time.time;
        Show(Mode.Fight);
        ResetFighters();
        int start = (int?)ep["startBudget"] ?? 50;
        a.ResetFor(start); b.ResetFor(start);
        a.active = b.active = true; a.scripted = b.scripted = true;
        fightTitle.text = $"<color=#5CDB85>REPLAY</color>  ·  <color=#8E8E93>0G root {UI.Short(root, 10)}</color>";
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
            var att = (string)h["attacker"] == a.botId ? a : b;
            string move = (string)h["move"], result = (string)h["result"];
            var plan = decisions.LastOrDefault(d => (string)d["botId"] == att.botId && (DateTime?)d["at"] <= at);
            if (plan != null) Say(att, $"{(string)plan["stance"]} · {move}", "0G " + UI.Short((string)plan["requestId"] ?? "—", 8), UI.Ink);
            yield return PlayHit(att, move, result, (int?)h["cents"] ?? 0);
        }
        yield return new WaitForSecondsRealtime(1f);
        if (s != session) yield break;
        var w = (string)ep["winner"];
        if (!string.IsNullOrEmpty(w)) { var winner = w == a.botId ? a : b; winner.opp.KnockOut(); winner.Victory(); UpdateBars(); fx.KO(winner.opp.Chest); Banner("K.O.", UI.Red); }
        Toast($"Replay done · {hits.Count} hits · {decisions.Count} 0G decisions");
        yield return new WaitForSecondsRealtime(1.5f);
        if (s == session) koPanel.SetActive(true); // the result screen is the way out of a replay
        a.scripted = b.scripted = false; replaying = false;
    }

    void BuildKO(Transform root)
    {
        koPanel = UI.Box(root, new Color(0, 0, 0, 0.55f), false, "ko").At(0, 0, 1, 1).gameObject;
        var band = UI.Box(koPanel.transform, new Color(0.035f, 0.04f, 0.05f, 0.95f), false, "band").At(0, 0.5f, 1, 0.5f, 0, -250, 0, 250);
        foreach (var edge in new[] { 1f, 0f }) UI.Box(band.transform, UI.Line, false, "edge").At(0, edge, 1, edge, 0, -1, 0, 1);
        koPanel.GetComponent<Image>().color = new Color(0, 0, 0, 0.65f);
        koTitle = UI.Title(band.transform, "", 130, UI.Red, TextAnchor.MiddleCenter).At(0, 1, 1, 1, 40, -170, -40, -10);
        koSub = UI.Label(band.transform, "", 30, UI.Ink, TextAnchor.MiddleCenter).At(0, 1, 1, 1, 40, -234, -40, -174);
        koLessons = UI.Label(band.transform, "", 26, UI.Dim, TextAnchor.UpperCenter).At(0, 0, 1, 1, 200, 110, -200, -230);
        var row = UI.Rt(band.transform, "buttons").At(0.5f, 0, 0.5f, 0, -620, 26, 620, 100);
        koLedger = UI.Btn(row, "Fight ledger on HashScan  ↗", UI.Steel, UI.Ink, () => Application.OpenURL("https://hashscan.io/testnet/topic/" + ledgerTopic), 26);
        koLedger.At(0, 0, 0.4f, 1);
        UI.Big(row, "REPLAY FROM 0G", false, () => StartCoroutine(Replay(fightId)), 28).At(0.42f, 0, 0.74f, 1, 0, -10, 0, 10);
        UI.Big(row, "CONTINUE", true, GoHome, 32, pulse: true).At(0.76f, 0, 1, 1, 0, -10, 0, 10);
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

        if (Array.IndexOf(args, "-flow") >= 0) { yield return CaptureFlow(Shot); Debug.Log("[capture] done"); Application.Quit(); yield break; }
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
