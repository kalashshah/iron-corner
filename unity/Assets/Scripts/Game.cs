using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using TMPro;
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
    GameObject home, train, fight, brain, modal, koPanel, campDot, tabs;
    TMP_Text badge, toast, cardName, cardMoney, cardBrain, cardAcct, cardDaily, serverStatus, brainList, trainDaily;
    TMP_InputField serverField, trainField;
    readonly List<string> counterAttacks = new(); // attacks your bot has a taught counter for; the sparring dummy throws these
    Button fightBtn, highBtn; bool aAlive = true, aNeedsRevive;
    TMP_Text trainTranscript, trainSaved, fightTranscript, fightTitle;
    TMP_Text[] barName = new TMP_Text[2], barMoney = new TMP_Text[2];
    RectTransform[] barFill = new RectTransform[2];
    Transform ticker;
    Bubble bubA, bubB;
    TMP_Text modalAction, modalCode, modalStatus, modalExplain; RawImage modalQr; Button modalBtn, modalOpenBtn; string approvalId, approvalUri; bool approvalOpen;
    TMP_Text koTitle, koWin, koSub, koLessons; Button koLedger; GameObject koDetails;
    TMP_Text summaryXp, fightTag, brainVer, brainDemos, brainLearned; Transform brainRows; GameObject fightDetails;
    readonly List<(TMP_Text t, float born, Vector3 world)> popups = new();
    float toastUntil;
    string captureDir;

    // feel: hit-stop, slow-mo, camera trauma, KO cam, post pulses, combo counter, banner, health-bar damage trail
    Fx fx;
    float hitStopUntil, slowmoUntil, koCamUntil, fovKick, baseFov, pulse, slowScale = 0.3f;
    Fighter koTarget;
    // Special impacts: big clean hits get a slow-mo close-up (rate-limited so it stays special).
    float impactCamUntil, lastSpecial = -99f; Vector3 impactAt;
    Quaternion camRot = Quaternion.identity; bool camSnap;
    ChromaticAberration chroma; Vignette vignette; float baseVignette;
    RectTransform[] barGroup = new RectTransform[2]; Vector2[] barPos = new Vector2[2];
    RectTransform[] barTrail = new RectTransform[2]; float[] trailK = { 1, 1 }, trailHold = new float[2], barShake = new float[2];
    TMP_Text[] comboText = new TMP_Text[2], portrait = new TMP_Text[2]; int[] combo = new int[2]; float[] comboAt = new float[2];
    TMP_Text vsClock;
    TMP_Text banner; float bannerAt = -9f;

    // Shadow sparring: the owner boxes with the move pad; each move becomes a demo (state, action, outcome) for the backend.
    // A demo is born on the tap, but its clock, its state snapshot and its result all belong to the moment the bot
    // actually throws the move (Fighter.OnBegin): taps queue behind the current move and may step in first.
    class Demo { public JObject state; public string action; public float tapAt, at; public bool started; public int throwId; public string outcome; public bool gotHit; }
    readonly List<Demo> demoPending = new();
    readonly JArray demoBatch = new();
    readonly List<string> drilling = new(); // attacks the owner has been defending against; the dummy throws more of them
    bool roundOn; float roundEnds, lastFlush, roundSeconds = 30f; string roundId; int roundMoves, unsavedRounds;
    float aAttackAt = -99f, bAttackAt = -99f, bSwingAt = -99f; string aCur, bCur;
    TMP_Text roundTimer, roundInfo, energyPips, padFeed, summaryTitle, summaryBody; GameObject summary;
    TMP_Text cardLevel, cardXp, cardEnergy; RectTransform xpFill;
    readonly TMP_Text[] statVal = new TMP_Text[5]; readonly RectTransform[] statFill = new RectTransform[5];
    static readonly string[] StatKeys = { "reflexes", "accuracy", "defense", "ring_iq", "experience" };
    static readonly string[] StatNames = { "Reflexes", "Accuracy", "Defense", "Ring IQ", "Experience" };

    class Bubble { public RectTransform root; public TMP_Text say, meta; public float until; public Fighter f; }

    void Start()
    {
        Application.targetFrameRate = 60;
        Screen.orientation = ScreenOrientation.AutoRotation;
        Screen.autorotateToPortrait = Screen.autorotateToPortraitUpsideDown = false;
        Screen.autorotateToLandscapeLeft = Screen.autorotateToLandscapeRight = true;
        ringY = a.transform.position.y;
        a.opp = b; b.opp = a;
        a.OnHit += OnHit; b.OnHit += OnHit;
        a.OnBegin += (f, move, id) => // sparring: the tapped move is thrown now; start its demo from this moment
        {
            if (mode != Mode.Train || !roundOn) return;
            var d = demoPending.FirstOrDefault(x => !x.started && x.action == move);
            if (d == null) return;
            d.started = true; d.at = Time.time; d.throwId = id; d.state = State(a);
        };
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
            fightBtn.SetLabel(!aAlive ? (aNeedsRevive ? "Revive" : $"Repair {repair}s") : "Fight");
            campDot.SetActive(((int?)bot["progress"]?["demos"] ?? 0) >= 40 && bot["adapter"]?.Type != JTokenType.Object);
            highBtn.interactable = aAlive;
            if (!aAlive && !aNeedsRevive) StartCoroutine(RefreshSoon(Mathf.Clamp(repair, 1, 5)));
        }, e => { cardAcct.text = "<color=#F2523F>" + e + "</color>"; IdentityError(e); });
    }

    void ShowDaily(JToken d)
    {
        if (d == null || d.Type != JTokenType.Object) return;
        int streak = (int?)d["streak"] ?? 0;
        string txt = ((bool?)d["done"] == true ? "<color=#5CDB85>\ue5ca Daily training done</color>" : $"Daily goal: <b>{d["drills"]}/{d["goal"]}</b> drills")
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
        camSnap = true; Time.timeScale = 1f; hitStopUntil = slowmoUntil = koCamUntil = impactCamUntil = 0; koTarget = null; bannerAt = -9f;
        for (int i = 0; i < 2; i++) { combo[i] = 0; trailK[i] = 1; }
        // Crowd only really comes alive in fights; the lobby is quiet. Music follows the screen.
        if (fx != null) { fx.SetCrowd(m == Mode.Fight ? 0.32f : m == Mode.Train ? 0.06f : 0.02f); fx.SetMusic(m == Mode.Fight ? Music.Kind.Fight : m == Mode.Train ? Music.Kind.Train : Music.Kind.Lobby); }
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
        roundTimer.text = "Ready"; roundInfo.text = "Tap a move to start"; padFeed.text = "";
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
            fx.FightStart();
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
            if (combo[side] >= 2) { ShowCombo(side); fx.Combo(combo[side]); }
            if (combo[side] == 4) fx.Cheer(0.55f);
            bool big = move == "uppercut" || (dmg >= 3 && combo[side] >= 2) || combo[side] >= 3;
            // Fights: either bot's big hits. Sparring: your own big hits (the dummy's don't get the treatment).
            bool eligible = mode == Mode.Fight || (mode == Mode.Train && att == a);
            if (eligible && big && !over && !summary.activeSelf && Time.unscaledTime - lastSpecial > 5f) Special(at, move, combo[side]);
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
        slowmoUntil = Time.unscaledTime + 1.6f; slowScale = 0.3f;
        yield return new WaitForSecondsRealtime(2.2f);
        if (s != session) yield break;
        koTitle.color = UI.Amber; koTitle.text = "K.O."; koWin.text = $"{winner.displayName} Wins";
        koSub.text = $"{loser.displayName}'s fight budget is empty: it can't cover the next hit.";
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
            barFill[i].GetComponent<Image>().color = k <= 0.35f ? UI.Red : i == 0 ? UI.Accent : UI.Brass;
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
            string mark = st == "settled" ? "<color=#5CDB85>\ue5ca settled</color>" : st == "failed" ? "<color=#F2523F>\ue5cd failed</color>" : "<color=#F2A33A>… pending</color>";
            string url = (string)p["hashscan"];
            var row = UI.Btn(ticker, "", UI.Card, UI.Ink, string.IsNullOrEmpty(url) ? null : () => Application.OpenURL(url)).H(62);
            var lbl = row.GetComponentInChildren<TMP_Text>();
            lbl.SetAlign(TextAnchor.MiddleLeft); lbl.font = UI.Font; lbl.fontSize = lbl.fontSizeMax = 22;
            lbl.rectTransform.offsetMin = new Vector2(18, 0);
            lbl.text = $"<b><color=#F2523F>-{UI.Usd((double)p["cents"]).TrimStart('-')}</color></b>  {NameOf((string)p["from"])} → {NameOf((string)p["to"])}  {mark}\n<size=16><color=#8E8E93>tx {p["txId"]}{(string.IsNullOrEmpty(url) ? "" : "  \ue89e HashScan")}</color></size>";
        }
    }

    // ---------------------------------------------------------------- shadow sparring
    static readonly string[] Attacks = Fighter.Moves.Where(Fighter.IsAttack).ToArray();

    void PadMove(string m)
    {
        if (mode != Mode.Train) return;
        if (!roundOn) StartRound();
        // the new tap replaces anything still waiting in the bot's queue: those taps were never thrown, so they aren't demos
        demoPending.RemoveAll(x => !x.started);
        demoPending.Add(new Demo { action = m, tapAt = Time.time });
        a.holdBlock = false; a.SetQueue(new[] { m });
    }

    void StartRound()
    {
        roundOn = true; roundId = "r" + DateTime.UtcNow.Ticks.ToString("x"); roundMoves = 0;
        fightStart = Time.time; roundEnds = Time.time + roundSeconds; lastFlush = Time.time;
        summary.SetActive(false); padFeed.text = ""; roundInfo.text = "0 moves";
        a.holdBlock = false; a.SetQueue(Array.Empty<string>());
        StartCoroutine(Api.Call("POST", $"/train/{a.botId}/demos", new JObject { ["round"] = roundId, ["demos"] = new JArray() }, ShowProgress));
    }

    // Outcome of a pad move: our punch landed / was blocked / dodged, or the dummy's punch hit us / was blocked / dodged.
    void DemoHit(Fighter att, string move, string result)
    {
        if (mode != Mode.Train || !roundOn) return;
        if (att == a)
        {
            var d = demoPending.FirstOrDefault(x => x.started && x.outcome == null && x.throwId == a.ThrowId); // this exact throw
            if (d != null) d.outcome = result == "clean" ? "landed" : result;
            return;
        }
        foreach (var d in demoPending.Where(x => x.started && x.outcome == null && Time.time - x.at < 1.2f))
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
            if (!d.started) { if (Time.time - d.tapAt > 2.5f) demoPending.RemoveAt(i); continue; } // never thrown
            bool atk = Fighter.IsAttack(d.action);
            if (d.outcome == null && Time.time - d.at < (atk ? 1.4f : 1.0f)) continue;
            // A defensive move with a dummy punch striking into it that didn't land clean made it miss.
            d.outcome ??= d.gotHit ? "got_hit" : !atk && bSwingAt >= d.at ? (d.action == "block" ? "blocked" : "dodged") : "whiff";
            demoPending.RemoveAt(i);
            Recorded(d);
        }
        if (demoBatch.Count >= 10 || (demoBatch.Count > 0 && Time.time - lastFlush > 4f)) Flush(false);
        float left = roundEnds - Time.time;
        roundTimer.text = $"Round  ·  00:{Mathf.CeilToInt(Mathf.Max(0, left)):00}";
        if (left <= 0) EndRound();
    }

    void Recorded(Demo d)
    {
        demoBatch.Add(new JObject { ["t"] = d.state["t"], ["state"] = d.state, ["action"] = d.action, ["outcome"] = d.outcome });
        bool atk = Fighter.IsAttack(d.action), ok = atk ? d.outcome == "landed" : d.outcome is "blocked" or "dodged";
        var o1 = (string)(d.state["oppLastMoves"] as JArray)?.LastOrDefault();
        if (!atk && Fighter.IsAttack(o1)) { drilling.Add(o1); if (drilling.Count > 8) drilling.RemoveAt(0); }
        roundMoves++;
        if (Fighter.logStrikes) Debug.Log($"[demo] {d.action} {d.outcome} d={(float?)d.state?["distance"] ?? 0:F2} my_last={(string)(d.state?["selfLastMoves"] as JArray)?.LastOrDefault() ?? "-"} opp_last={(string)(d.state?["oppLastMoves"] as JArray)?.LastOrDefault() ?? "-"}");
        string mark = ok ? "<color=#5CDB85>\ue5ca</color>" : d.outcome == "got_hit" ? "<color=#F2523F>\ue5cd</color>" : "<color=#8E8E93>·</color>";
        padFeed.text = $"{mark} {char.ToUpperInvariant(UI.Nice(d.action)[0])}{UI.Nice(d.action).Substring(1)} · {d.outcome.Replace('_', ' ')}   <color=#8E8E93>demo recorded</color>";
        roundInfo.text = $"{roundMoves} moves";
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
        foreach (var d in demoPending.Where(x => x.started)) { d.outcome ??= d.gotHit ? "got_hit" : "whiff"; Recorded(d); }
        demoPending.Clear();
        a.SetQueue(Array.Empty<string>());
        if (roundMoves > 0) unsavedRounds++;
        roundTimer.text = "Round over";
        roundInfo.text = "Your bot spars on its own"; padFeed.text = "";
        Flush(true);
    }

    void RoundSummary(JToken r, string err)
    {
        summary.SetActive(true);
        var rd = r?["round"];
        if (rd != null) { guideRound = true; GuideCheck(); }
        if (rd == null) { summaryTitle.text = "Round over"; summaryXp.text = ""; summaryBody.text = "<color=#F2523F>Couldn't send the round: " + err + "</color>"; return; }
        int xp = (int?)rd["xpGained"] ?? 0;
        summaryTitle.text = "Round Complete"; summaryXp.text = xp > 0 ? $"+{xp} XP" : "";
        fx.Jingle();
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
            cardLevel.text = $"LV {lv["level"]}";
            cardXp.text = $"{into}/{next} XP";
            SetFill(xpFill, (float)into / next);
        }
        if (p["stats"] is JObject st)
            for (int i = 0; i < StatKeys.Length; i++) { int v = (int?)st[StatKeys[i]] ?? 0; statVal[i].text = v.ToString(); SetFill(statFill[i], v / 100f); }
        if (p["energy"] is JObject en)
        {
            int left = (int?)en["left"] ?? 0, max = (int?)en["max"] ?? 5;
            string pips = "<color=#FFFFFF>" + new string('●', left) + "</color><color=#3A3A40>" + new string('●', Math.Max(0, max - left)) + "</color>";
            cardEnergy.text = $"Energy  <b>{left}/{max}</b>  <color=#A8A399>rounds today</color>";
            energyPips.text = $"<color=#FFFFFF>\uea0b</color> {pips}";
        }
    }

    static void SetFill(RectTransform f, float k) { k = Mathf.Clamp01(k); f.anchorMax = new Vector2(k, 1); f.gameObject.SetActive(k > 0.02f); }

    // ---------------------------------------------------------------- coaching
    bool recording;
    void MicDown(TMP_Text transcript)
    {
        recording = Mic.Start();
        if (fx != null) fx.musicVolume = 0.05f; // keep the beat out of the speech-to-text
        transcript.text = recording ? "<color=#FFFFFF>● Listening…</color> release to send" : (Api.Offline ? "No mic — offline sim will pretend you taught a left-hook counter." : "No microphone — type instead.");
        if (!recording && Api.Offline) recording = true;
    }

    void MicUp(TMP_Text transcript)
    {
        if (fx != null) fx.musicVolume = 0.5f;
        if (!recording) return;
        recording = false;
        var wav = Mic.Stop();
        if (wav == null && !Api.Offline) { transcript.text = "Too short — hold the button while you talk."; return; }
        wav ??= Mic.Wav(new float[1600], 16000, 1);
        transcript.text = "Sending to 0G whisper…";
        if (mode == Mode.Train) StartCoroutine(Api.Upload($"/train/{a.botId}/coach", wav, null, r => OnCoach(r, transcript), e => transcript.text = "Coach failed: " + e));
    }

    void SendTyped(TMP_InputField field, TMP_Text transcript)
    {
        var text = field.text.Trim();
        if (text.Length == 0) return;
        field.text = "";
        transcript.text = "…";
        if (mode == Mode.Train) StartCoroutine(Api.Call("POST", $"/train/{a.botId}/coach-text", new JObject { ["text"] = text }, r => OnCoach(r, transcript), e => transcript.text = "Coach failed: " + e));
    }

    // Training only: show what the brain learned (diff + 0G id), drill it right away, track the daily goal.
    void OnCoach(JToken r, TMP_Text transcript)
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
    // A page that hands back to the app via ironcorner://: the in-app sheet on iOS, the system browser elsewhere.
    void OpenSheet(string url)
    {
#if UNITY_IOS && !UNITY_EDITOR
        IcWorldAuthOpen(url); return;
#else
        Application.OpenURL(url);
#endif
    }

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
                modalStatus.text = "<color=#5CDB85>Approved \ue5ca</color>";
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
        ShowPanel(brain);
        brainList.text = "Loading…";
        foreach (Transform t in brainRows) Destroy(t.gameObject);
        StartCoroutine(Api.Call("GET", $"/bots/{a.botId}", null, r =>
        {
            var b = r["brain"];
            string Ms(JToken m) => string.Join(" · ", (m as JArray ?? new JArray()).Select(x => Cap(UI.Nice((string)x))));
            int learned = 0;
            foreach (var c in b?["counters"] as JArray ?? new JArray()) { if (learned < 4) BrainRow(Cap(UI.Nice((string)c["when"])), Ms(c["moves"])); learned++; }
            foreach (var c in b?["combos"] as JArray ?? new JArray()) { if (learned < 4) BrainRow(Cap((string)c["use"]), Ms(c["moves"])); learned++; }
            foreach (var p in b?["phrases"] as JArray ?? new JArray()) { if (learned < 4) BrainRow($"“{p["say"]}”", Ms(p["moves"])); learned++; }
            if (learned == 0) BrainRow("Nothing learned yet", "spar a round or teach it a line in Training");
            brainVer.text = $"v{r["brainVersion"]}";
            brainDemos.text = $"{(int?)r["progress"]?["demos"] ?? 0}";
            brainLearned.text = learned.ToString();
            brainList.text = $"Encrypted on 0G Storage · root {UI.Short((string)r["brainRoot"], 10)}";
        }, e => brainList.text = "<color=#F2523F>" + e + "</color>"));
    }

    static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    // ---------------------------------------------------------------- per-frame
    void LateUpdate()
    {
        if (a.Current != aCur) { aCur = a.Current; if (Fighter.IsAttack(aCur)) aAttackAt = Time.time; }
        if (b.Current != bCur) { bCur = b.Current; if (Fighter.IsAttack(bCur)) bAttackAt = Time.time; }
        TickRound();
        UpdateCamera();
        foreach (var bub in new[] { bubA, bubB })
        {
            bool on = ((mode == Mode.Fight && fightDetails.activeSelf) || (mode == Mode.Train && bub.f == a)) && Time.unscaledTime < bub.until && !koPanel.activeSelf && !summary.activeSelf;
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
        toast.transform.parent.gameObject.SetActive(Time.unscaledTime < toastUntil && !summary.activeSelf);
        UpdateHud(); UpdateTabs();
    }

    // Time scale, post pulses and smoke from damaged bots. Hit-stop beats slow-mo beats normal.
    void Update()
    {
        float now = Time.unscaledTime;
        Time.timeScale = now < hitStopUntil ? 0.06f : now < slowmoUntil ? slowScale : 1f;
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
        fightTag.text = replaying ? "REPLAY" : Api.Offline ? "OFFLINE" : "LIVE";
        fightTag.color = replaying || Api.Offline ? UI.Dim : UI.Green;
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

    // Slow-mo close-up on a big hit: time to 22 %, camera in on the contact, a spark burst and a callout.
    void Special(Vector3 at, string move, int comboCount)
    {
        lastSpecial = Time.unscaledTime;
        slowmoUntil = Time.unscaledTime + 0.85f; slowScale = 0.22f;
        impactAt = at; impactCamUntil = Time.unscaledTime + 0.9f;
        fovKick = Mathf.Max(fovKick, 10f); pulse = Mathf.Max(pulse, 1.1f);
        fx.Special(at);
        var call = comboCount >= 3 ? $"COMBO x{comboCount}" : move == "uppercut" ? "UPPERCUT!" : move.StartsWith("hook") ? "HAYMAKER!" : "CRUSHED!";
        Banner($"<size=60%><voffset=1.3em>{call}</voffset></size>", UI.Accent); // smaller and above the contact point
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
            look = pa + Vector3.up * 1.05f - a.transform.right * (mode == Mode.Brain || !home.activeSelf ? 0.95f : 0.12f) * (cam.aspect / 1.78f);
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
        if (Time.unscaledTime < impactCamUntil && koTarget == null)
        {
            // Special impact: cut in close from the camera's side of the fight, slightly low, right on the contact point.
            var axis = new Vector3(pb.x - pa.x, 0, pb.z - pa.z).normalized;
            var sideDir = Vector3.Cross(Vector3.up, axis);
            pos = impactAt + sideDir * 2.1f - axis * 0.35f + Vector3.up * 0.15f; look = impactAt; rate = 9f;
        }
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
        Stroke(t, 0.2f);
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
        BuildTabs(root); BuildModal(root); BuildKO(root);

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
        UI.Fade(home.transform, 900);
        UI.FadeUp(home.transform, 260);

        // Top left: the big amber FIGHT block, flush to the edge, then the lobby's mode rows under it.
        fightBtn = UI.Big(home.transform, UI.I("play") + "  Fight", true, FindClicked, 66, pulse: true);
        fightBtn.At(0, 1, 0, 1, -20, -184, 560, -36);
        cardLeague = LobbyRow(home.transform, -204, "medal", null);
        cardEnergy = LobbyRow(home.transform, -276, "bolt", null);
        cardDaily = LobbyRow(home.transform, -348, "fire", null);
        UI.Big(home.transform, UI.I("camp") + "  Train", false, StartTrainGuided, 42).At(0, 1, 0, 1, -20, -540, 480, -436);
        highBtn = UI.Btn(home.transform, "", UI.Steel, UI.Ink, () => { budgetUsd = 0.5f; HighStakes(); }, 24);
        highBtn.At(0, 1, 0, 1, -20, -626, 480, -556);
        highBtn.GetComponent<Image>().sprite = UI.Ramp; highBtn.GetComponent<Image>().type = Image.Type.Simple;
        var ht = highBtn.GetComponentInChildren<TMP_Text>(); ht.SetAlign(TextAnchor.MiddleLeft); ht.text = $"<color=#FFA33D>{UI.I("shield")}</color>  High Stakes  <color=#A8A399>· World ID</color>";
        ht.At(0, 0, 1, 1, 40, 0, -12, 0);

        // Bottom strip: player plate left, brain line centre, wallet + stats right.
        var plate = UI.Rt(home.transform, "plate").At(0, 0, 0, 0, 40, 26, 760, 156);
        cardName = UI.Label(plate, "Brass Knuckle", 46, UI.Ink, TextAnchor.LowerLeft, FontStyle.BoldAndItalic).At(0, 0.42f, 1, 1);
        cardLevel = UI.Label(plate, "LV 1", 24, UI.Amber, TextAnchor.MiddleLeft, FontStyle.Bold).At(0, 0, 0, 0.42f, 0, 0, 80, 0);
        xpFill = UI.Meter(UI.Rt(plate, "xp").At(0, 0, 0, 0, 84, 20, 400, 28));
        cardXp = UI.Label(plate, "0/100 XP", 18, UI.Dim, TextAnchor.MiddleLeft).At(0, 0, 0, 0.42f, 414, 0, 640, 0);
        cardBrain = UI.Label(home.transform, "", 20, UI.Dim, TextAnchor.MiddleCenter).At(0.5f, 0, 0.5f, 0, -420, 40, 420, 90);
        cardAcct = UI.Label(home.transform, "", 17, UI.Dim, TextAnchor.MiddleCenter).At(0.5f, 0, 0.5f, 0, -420, 14, 420, 44);
        var wallet = UI.Btn(home.transform, "", new Color(0, 0, 0, 0), UI.Ink, OpenFund, 30).At(1, 0, 1, 0, -760, 44, -300, 120);
        cardMoney = wallet.GetComponentInChildren<TMP_Text>(); cardMoney.SetAlign(TextAnchor.MiddleRight);
        var stats = UI.Btn(home.transform, UI.I("stats") + "  Stats", UI.Steel, UI.Ink, null, 30).At(1, 0, 1, 0, -270, 44, -40, 120);
        UI.Panel(stats.GetComponent<Image>(), new Color(1, 1, 1, 0.2f));

        // Stats panel: the inventory-style panel on the right, toggled by Stats.
        var sp = UI.Box(home.transform, UI.Card, true, "statsPanel").At(1, 0, 1, 1, -820, 150, -40, -150);
        UI.Label(sp.transform, "Brain Stats", 44, UI.Ink, TextAnchor.UpperLeft, FontStyle.BoldAndItalic).At(0, 1, 1, 1, 36, -96, -36, -26);
        for (int i = 0; i < StatKeys.Length; i++)
        {
            float y = -130 - i * 92;
            var row = UI.Box(sp.transform, UI.Row, false, StatKeys[i]).At(0, 1, 1, 1, 24, y - 80, -24, y);
            UI.Label(row.transform, StatNames[i], 28, UI.Ink, TextAnchor.UpperLeft, FontStyle.Bold).At(0, 0, 1, 1, 24, 0, -24, -12);
            statVal[i] = UI.Label(row.transform, "0", 40, UI.Amber, TextAnchor.MiddleRight, FontStyle.BoldAndItalic).At(1, 0, 1, 1, -140, 0, -24, 0);
            statFill[i] = UI.Meter(UI.Rt(row.transform, "m").At(0, 0, 1, 0, 24, 18, -160, 26));
            SetFill(statFill[i], 0);
        }
        sp.gameObject.SetActive(false);
        stats.onClick.AddListener(() => sp.gameObject.SetActive(!sp.gameObject.activeSelf));

        // Server address: developer setting, opened from the gear in the tab bar.
        serverBox = UI.Box(home.transform, UI.Bg, true, "server").At(1, 1, 1, 1, -900, -280, -40, -140);
        serverField = UI.Field(serverBox.transform, "http://<laptop-ip>:8787", Api.BaseUrl, 24).At(0, 1, 1, 1, 24, -86, -210, -22);
        UI.Big(serverBox.transform, "Connect", true, () => { Api.BaseUrl = serverField.text; StartCoroutine(Connect()); }, 26).At(1, 1, 1, 1, -190, -86, -24, -22);
        serverStatus = UI.Label(serverBox.transform, "", 18, UI.Dim, TextAnchor.MiddleLeft).At(0, 0, 1, 0, 28, 10, -24, 46);
        serverBox.gameObject.SetActive(false);
    }

    // A lobby mode row: dark band fading to the right, amber icon, white label. Returns the label.
    TMP_Text LobbyRow(Transform p, float top, string icon, Action click)
    {
        var row = UI.Btn(p, "", UI.Steel, UI.Ink, click, 26).At(0, 1, 0, 1, -20, top - 62, 520, top);
        var img = row.GetComponent<Image>(); img.sprite = UI.Ramp; img.type = Image.Type.Simple;
        if (click == null) row.interactable = false;
        row.transition = Selectable.Transition.None;
        UI.IconImg(row.transform, icon, UI.Amber, 34).At(0, 0, 0, 1, 40, 0, 84, 0);
        var t = row.GetComponentInChildren<TMP_Text>(); t.SetAlign(TextAnchor.MiddleLeft); t.At(0, 0, 1, 1, 100, 0, -12, 0);
        return t;
    }

    // TMP_Text tabs, top right, over Home and its panels (Lobby, Brain, Camp, Wallet), with the settings gear.
    TMP_Text[] tabText; Image[] tabLine; Image serverBox;
    void BuildTabs(Transform root)
    {
        tabs = UI.Go(root, "tabs");
        ((RectTransform)tabs.transform).At(1, 1, 1, 1, -900, -112, -40, -30);
        var items = new (string label, Action go)[] { ("Lobby", () => Show(Mode.Home)), ("Brain", OpenBrain), ("Camp", OpenCamp), ("Wallet", OpenFund) };
        tabText = new TMP_Text[items.Length]; tabLine = new Image[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            var b = UI.Btn(tabs.transform, items[i].label, new Color(0, 0, 0, 0), UI.Ink, items[i].go, 38);
            b.At(i * 0.2f, 0, (i + 1) * 0.2f, 1);
            tabText[i] = b.GetComponentInChildren<TMP_Text>();
            tabLine[i] = UI.Box(b.transform, UI.Accent, false, "line").At(0.2f, 0, 0.8f, 0, 0, 4, 0, 9);
            if (i == 2) { campDot = UI.Badge(tabText[i]); campDot.SetActive(false); }
        }
        UI.IconBtn(tabs.transform, "gear", () => { Show(Mode.Home); serverBox.gameObject.SetActive(!serverBox.gameObject.activeSelf); }).At(1, 0.5f, 1, 0.5f, -72, -36, 0, 36);
    }

    void UpdateTabs()
    {
        bool show = mode == Mode.Home && !modal.activeSelf && !welcome.activeSelf && !create.activeSelf;
        tabs.SetActive(show);
        if (!show) return;
        int on = home.activeSelf ? 0 : brain.activeSelf ? 1 : camp.activeSelf ? 2 : fund.activeSelf ? 3 : -1;
        for (int i = 0; i < tabText.Length; i++) { tabText[i].color = i == on ? UI.Amber : UI.Ink; tabLine[i].enabled = i == on; }
    }

    void BuildTrain(Transform root)
    {
        train = UI.Go(root, "Train");
        ((RectTransform)train.transform).At(0, 0, 1, 1);
        UI.FadeUp(train.transform, 360);
        // Back leaves training; GoHome saves the brain to 0G Storage when there are unsaved rounds.
        UI.TopBar(train.transform, "Sparring", GoHome);

        // Round box, top centre (clock + energy); move count top right; the last move under the box.
        var hud = UI.Box(train.transform, UI.Card, true, "hud").At(0.5f, 1, 0.5f, 1, -260, -112, 260, -40);
        roundTimer = UI.Label(hud.transform, "", 32, UI.Ink, TextAnchor.MiddleLeft, FontStyle.Bold).At(0, 0, 1, 1, 28, 0, -28, 0);
        energyPips = UI.Label(hud.transform, "", 22, UI.Amber, TextAnchor.MiddleRight).At(0, 0, 1, 1, 28, 0, -28, 0);
        roundInfo = UI.Label(train.transform, "", 28, UI.Ink, TextAnchor.MiddleRight, FontStyle.Bold).At(1, 1, 1, 1, -560, -112, -40, -40);
        padFeed = UI.Label(train.transform, "", 24, UI.Ink, TextAnchor.UpperCenter, FontStyle.Bold).At(0.5f, 1, 0.5f, 1, -460, -160, 460, -122);
        trainSaved = UI.Label(train.transform, "", 20, UI.Ink, TextAnchor.UpperRight).At(1, 1, 1, 1, -560, -196, -40, -124);
        trainDaily = UI.Label(train.transform, "", 20, UI.Dim, TextAnchor.UpperRight).At(1, 1, 1, 1, -620, -232, -40, -200);

        // Left thumb: a round dodge pad like a remote (up = step in, down = step back, sides = slip, centre = block).
        // Right thumb: a punch cluster, big Cross in the corner, the other punches on an arc around it.
        DodgePad(train.transform);
        PunchCluster(train.transform);

        BuildCoachBar(train.transform, out trainTranscript, out trainField);

        // Round result: full-screen dim, check icon, title, big +XP, what it learned, Continue.
        summary = UI.Box(train.transform, new Color(0.01f, 0.01f, 0.012f, 0.8f), false, "summary").At(0, 0, 1, 1, -600, -600, 600, 600).gameObject; // bleeds past the safe area
        UI.IconImg(summary.transform, "check", UI.Amber, 120).At(0.5f, 0.5f, 0.5f, 0.5f, -80, 240, 80, 400);
        summaryTitle = UI.Label(summary.transform, "", 60, UI.Ink, TextAnchor.MiddleCenter, FontStyle.BoldAndItalic).At(0.5f, 0.5f, 0.5f, 0.5f, -700, 150, 700, 240);
        summaryXp = UI.Label(summary.transform, "", 120, UI.Amber, TextAnchor.MiddleCenter, FontStyle.Italic).At(0.5f, 0.5f, 0.5f, 0.5f, -700, 20, 700, 160);
        summaryBody = UI.Label(summary.transform, "", 26, UI.Ink, TextAnchor.UpperCenter).At(0.5f, 0.5f, 0.5f, 0.5f, -700, -110, 700, 10);
        UI.Big(summary.transform, "Continue  " + UI.I("chevron"), true, () => summary.SetActive(false), 40).At(0.5f, 0.5f, 0.5f, 0.5f, -260, -250, 260, -140);
        summary.SetActive(false);
    }

    // One round move button: icon (rotated / mirrored to show the punch's direction) over a small label.
    Button MoveBtn(Transform parent, string move, string icon, string label, float cx, float cy, float size, bool corner, bool punch, float rot = 0, bool mirror = false)
    {
        var img = UI.Go(parent, "move:" + move).AddComponent<Image>(); img.sprite = UI.Circle;
        img.color = punch ? new Color(0.06f, 0.035f, 0.01f, 0.88f) : new Color(0.02f, 0.02f, 0.024f, 0.85f);
        var ring = UI.Go(img.transform, "ring").AddComponent<Image>(); ring.sprite = UI.CircleRing; ring.raycastTarget = false; ring.At(0, 0, 1, 1);
        ring.color = punch ? new Color(1f, 0.64f, 0.24f, 0.85f) : new Color(1, 1, 1, 0.35f);
        var rt = (RectTransform)img.transform;
        float ax = corner ? 1 : 0; rt.anchorMin = rt.anchorMax = new Vector2(ax, 0);
        rt.sizeDelta = new Vector2(size, size); rt.anchoredPosition = new Vector2(cx, cy);
        var ic = UI.IconImg(img.transform, icon, punch ? UI.Accent : UI.Ink, Mathf.RoundToInt(size * 0.44f)).At(0.15f, 0.3f, 0.85f, 0.92f);
        ic.rectTransform.localEulerAngles = new Vector3(0, 0, rot);
        if (mirror) ic.rectTransform.localScale = new Vector3(-1, 1, 1);
        UI.Label(img.transform, label, Mathf.RoundToInt(Mathf.Clamp(size * 0.17f, 16, 26)), UI.Ink, TextAnchor.MiddleCenter, FontStyle.Bold).At(0, 0.08f, 1, 0.34f);
        var b = img.gameObject.AddComponent<Button>(); b.navigation = new Navigation { mode = Navigation.Mode.None };
        var cb = b.colors; cb.pressedColor = new Color(1.4f, 1.4f, 1.4f); cb.fadeDuration = 0.05f; b.colors = cb;
        img.gameObject.AddComponent<Press>();
        img.gameObject.AddComponent<HoldButton>().onDown = () => PadMove(move); // fires on touch-down: no release lag
        return b;
    }

    // Left thumb: a virtual joystick. Flick up = step in, down = step back, left / right = slip, tap = block.
    void DodgePad(Transform parent)
    {
        const float c = 250, size = 400; // centre from the bottom-left corner, base diameter
        var disc = UI.Go(parent, "joystick").AddComponent<Image>(); disc.sprite = UI.Circle; disc.color = new Color(0.01f, 0.01f, 0.012f, 0.72f);
        var drt = (RectTransform)disc.transform; drt.anchorMin = drt.anchorMax = Vector2.zero; drt.sizeDelta = new Vector2(size, size); drt.anchoredPosition = new Vector2(c, c);
        var ring = UI.Go(disc.transform, "ring").AddComponent<Image>(); ring.sprite = UI.CircleRing; ring.color = new Color(1, 1, 1, 0.22f); ring.raycastTarget = false; ring.At(0, 0, 1, 1);
        // direction hints round the edge, each lights up as it fires
        var hints = new System.Collections.Generic.Dictionary<string, TMP_Text>();
        foreach (var (move, icon, label, dx, dy) in new[] { ("step_in", "up", "IN", 0f, 1f), ("step_back", "down", "BACK", 0f, -1f), ("dodge_l", "left", "SLIP", -1f, 0f), ("dodge_r", "right", "SLIP", 1f, 0f) })
        {
            var h = UI.Rt(disc.transform, "hint:" + move);
            h.anchorMin = h.anchorMax = new Vector2(0.5f, 0.5f); h.sizeDelta = new Vector2(110, 90); h.anchoredPosition = new Vector2(dx, dy) * (size * 0.5f - 52);
            var ic = UI.IconImg(h, icon, UI.Ink, 46).At(0, 0.35f, 1, 1);
            UI.Label(h, label, 18, new Color(0.85f, 0.83f, 0.8f), TextAnchor.UpperCenter, FontStyle.Bold).At(0, 0, 1, 0.38f);
            hints[move] = ic;
        }
        var knob = UI.Go(disc.transform, "knob").AddComponent<Image>(); knob.sprite = UI.Circle; knob.color = new Color(0.08f, 0.08f, 0.09f, 0.95f); knob.raycastTarget = false;
        var krt = (RectTransform)knob.transform; krt.anchorMin = krt.anchorMax = new Vector2(0.5f, 0.5f); krt.sizeDelta = new Vector2(150, 150);
        var kr = UI.Go(knob.transform, "ring").AddComponent<Image>(); kr.sprite = UI.CircleRing; kr.color = new Color(1, 1, 1, 0.45f); kr.raycastTarget = false; kr.At(0, 0, 1, 1);
        UI.IconImg(knob.transform, "shield", UI.Ink, 44).At(0.2f, 0.36f, 0.8f, 0.9f);
        UI.Label(knob.transform, "TAP = BLOCK", 15, new Color(0.85f, 0.83f, 0.8f), TextAnchor.MiddleCenter, FontStyle.Bold).At(0.05f, 0.12f, 0.95f, 0.36f);
        var js = disc.gameObject.AddComponent<Joystick>();
        js.knob = krt; js.radius = size * 0.5f - 75;
        js.onMove = m =>
        {
            PadMove(m); UI.Click?.Invoke();
            if (hints.TryGetValue(m, out var hi)) { hi.color = UI.Accent; StartCoroutine(Unlight(hi)); }
        };
        UI.Caption(parent, "MOVE · DODGE").At(0, 0, 0, 0, c - 200, c + size * 0.5f + 10, c + 200, c + size * 0.5f + 42).SetAlign(TextAnchor.LowerCenter);
    }

    IEnumerator Unlight(TMP_Text t) { yield return new WaitForSecondsRealtime(0.25f); if (t != null) t.color = UI.Ink; }

    void PunchCluster(Transform parent)
    {
        const float cx = -190, cy = 190, r = 290; // Cross sits in the corner; the arc runs from left round to the top
        MoveBtn(parent, "cross", "glove", "Cross", cx, cy, 190, true, true, 0, true);
        var arc = new (string move, string label, float deg, float rot, bool mirror)[]
        {
            ("jab", "Jab", 184, 0, true), ("hook_l", "L Hook", 158, 35, false), ("uppercut", "Upper", 132, -60, true),
            ("hook_r", "R Hook", 106, -35, true), ("body", "Body", 80, -120, true),
        };
        foreach (var (move, label, deg, rot, mirror) in arc)
        {
            float a = deg * Mathf.Deg2Rad;
            MoveBtn(parent, move, "glove", label, cx + r * Mathf.Cos(a), cy + r * Mathf.Sin(a), 124, true, true, rot, mirror);
        }
        UI.Caption(parent, "ATTACK").At(1, 0, 1, 0, -300, cy + r + 80, -40, cy + r + 112).SetAlign(TextAnchor.LowerRight);
    }

    // Bottom bar: round amber mic, a "teach your fighter" field with send; what it last heard above it.
    void BuildCoachBar(Transform parent, out TMP_Text transcript, out TMP_InputField field)
    {
        var bar = UI.Rt(parent, "coach").At(0.5f, 0, 0.5f, 0, -400, 30, 400, 112);
        transcript = UI.Label(parent, "", 22, UI.Dim, TextAnchor.MiddleCenter).At(0.5f, 0, 0.5f, 0, -420, 130, 420, 170);
        var t = transcript;
        var mic = UI.IconBtn(bar, "mic", null, UI.Accent, UI.Dark).At(0, 0, 0, 1, 0, 0, 82, 0);
        var hold = mic.gameObject.AddComponent<HoldButton>();
        hold.onDown = () => MicDown(t); hold.onUp = () => MicUp(t);
        var f = UI.Field(bar, "Teach your fighter…", "", 26).At(0, 0, 1, 1, 100, 6, 0, -6);
        var send = UI.Btn(f.transform, UI.I("send"), new Color(0, 0, 0, 0), UI.Amber, null, 34).At(1, 0, 1, 1, -84, 0, 0, 0);
        send.onClick.AddListener(() => SendTyped(f, t));
        field = f;
    }

    void BuildFight(Transform root)
    {
        fight = UI.Go(root, "Fight");
        ((RectTransform)fight.transform).At(0, 0, 1, 1);
        for (int i = 0; i < 2; i++)
        {
            bool left = i == 0;
            // Portrait ring at the outer edge, heavy name above a framed bar, budget under it.
            var g = UI.Rt(fight.transform, left ? "barA" : "barB");
            if (left) g.At(0, 1, 0.5f, 1, 40, -156, -170, -32); else g.At(0.5f, 1, 1, 1, 170, -156, -40, -32);
            var por = UI.Portrait(g, left ? UI.Accent : UI.Ink, "");
            ((RectTransform)por.transform.parent).At(left ? 0 : 1, 0.5f, left ? 0 : 1, 0.5f, left ? 0 : -112, -56, left ? 112 : 0, 56);
            portrait[i] = por;
            float inL = left ? 132 : 0, inR = left ? 0 : -132;
            barName[i] = UI.Label(g, "", 40, UI.Ink, left ? TextAnchor.LowerLeft : TextAnchor.LowerRight, FontStyle.BoldAndItalic).At(0, 0.5f, 1, 1, inL, 6, inR, 0);
            barMoney[i] = UI.Label(g, "", 24, UI.Brass, left ? TextAnchor.UpperRight : TextAnchor.UpperLeft, FontStyle.Bold).At(0, 0, 1, 0.5f, inL, -6, inR, -30);
            var frame = UI.Box(g, new Color(0, 0, 0, 0.7f), false, "frame").At(0, 0.5f, 1, 0.5f, inL, -26, inR, 0);
            UI.Panel(frame, new Color(1, 1, 1, 0.25f));
            barTrail[i] = UI.Box(frame.transform, new Color(1f, 0.4f, 0.3f, 0.85f), false, "trail").At(0, 0, 1, 1, 3, 3, -3, -3).rectTransform;
            barFill[i] = UI.Box(frame.transform, left ? UI.Accent : UI.Brass, false, "fill").At(0, 0, 1, 1, 3, 3, -3, -3).rectTransform;
            barGroup[i] = g; barPos[i] = g.anchoredPosition;
            // Combo: big italic count + caption, mid-height on that fighter's side.
            comboText[i] = UI.Label(fight.transform, "", 40, UI.Ink, left ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight, FontStyle.Italic);
            comboText[i].rectTransform.pivot = new Vector2(left ? 0 : 1, 0.5f);
            if (left) comboText[i].At(0, 0.5f, 0.5f, 0.5f, 60, -70, 0, 90); else comboText[i].At(0.5f, 0.5f, 1, 0.5f, 0, -70, -60, 90);
            Stroke(comboText[i], 0.18f);
        }
        // Centre: VS over the fight clock.
        UI.Label(fight.transform, "VS", 60, UI.Accent, TextAnchor.MiddleCenter, FontStyle.Italic).At(0.5f, 1, 0.5f, 1, -150, -100, 150, -26);
        vsClock = UI.Label(fight.transform, "00:00", 36, UI.Ink, TextAnchor.MiddleCenter, FontStyle.Bold).At(0.5f, 1, 0.5f, 1, -150, -146, 150, -100);
        banner = UI.Label(fight.transform, "", 220, UI.Accent, TextAnchor.MiddleCenter, FontStyle.Italic).At(0, 0.5f, 1, 0.5f, 0, -160, 0, 180);
        Stroke(banner, 0.12f);

        // Details (plan bubbles, x402 payments, fight ids, autonomy note) stay hidden until the info button.
        fightDetails = UI.Go(fight.transform, "details"); ((RectTransform)fightDetails.transform).At(0, 0, 1, 1);
        fightTitle = UI.Label(fightDetails.transform, "", 20, UI.Dim, TextAnchor.UpperCenter).At(0.5f, 1, 0.5f, 1, -340, -210, 340, -154);
        var tk = UI.Rt(fightDetails.transform, "ticker").At(1, 1, 1, 1, -580, -780, -40, -420);
        UI.Caption(tk, "X402 PAYMENTS · HEDERA TESTNET").At(0, 1, 1, 1, 4, -28, 0, 0);
        var col = UI.Column(tk, 6, "rows");
        col.At(0, 0, 1, 1, 0, 0, 0, -36);
        col.childAlignment = TextAnchor.UpperLeft;
        ticker = col.transform;
        var auto = UI.Box(fightDetails.transform, UI.Card, true, "autonomous").At(0.5f, 0, 0.5f, 0, -580, 40, 580, 100);
        fightTranscript = UI.Label(auto.transform, "", 22, UI.Dim, TextAnchor.MiddleCenter).At(0, 0, 1, 1, 24, 0, -24, 0);
        fightDetails.SetActive(false);
        UI.IconBtn(fight.transform, "info", () => fightDetails.SetActive(!fightDetails.activeSelf)).At(0, 0, 0, 0, 40, 36, 116, 112);

        // Status tag bottom right: LIVE (green), OFFLINE or REPLAY.
        var tag = UI.Box(fight.transform, new Color(0, 0, 0, 0.6f), false, "live").At(1, 0, 1, 0, -210, 44, -40, 106);
        UI.Panel(tag, new Color(0.45f, 0.86f, 0.5f, 0.6f));
        fightTag = UI.Label(tag.transform, "LIVE", 30, UI.Green, TextAnchor.MiddleCenter, FontStyle.BoldAndItalic).At(0, 0, 1, 1);
    }

    // Black outline on big floating text (banners, combo, K.O.) so it reads over the ring.
    static void Stroke(TMP_Text t, float w) { t.outlineWidth = w; t.outlineColor = new Color32(0, 0, 0, 220); }

    Bubble BuildBubble(Transform root, Fighter f)
    {
        var box = UI.Box(root, UI.Card, true, "bubble:" + f.botId);
        UI.Box(box.transform, f == a ? UI.Accent : UI.Ink, false, "side").At(0, 0, 0, 1, 0, 0, 5, 0);
        var rt = box.rectTransform;
        rt.anchorMin = rt.anchorMax = Vector2.zero;
        rt.pivot = new Vector2(0.5f, 0);
        rt.sizeDelta = new Vector2(560, 130);
        var say = UI.Label(box.transform, "", 30, UI.Ink, TextAnchor.UpperLeft, FontStyle.Bold).At(0, 0, 1, 1, 28, 0, -24, -14);
        var meta = UI.Label(box.transform, "", 20, UI.Dim, TextAnchor.LowerLeft).At(0, 0, 1, 1, 28, 12, -24, 0);
        box.gameObject.SetActive(false);
        return new Bubble { root = rt, say = say, meta = meta, f = f };
    }

    // Brain: a panel over the lobby. Version chip, two big counts, then what it has learned as rows.
    void BuildBrain(Transform root)
    {
        var c = Page(root, "Brain", "Brain", null, out brain);
        var chip = UI.Box(c.parent, new Color(0, 0, 0, 0.5f), false, "ver").At(1, 1, 1, 1, -170, -106, -40, -40);
        UI.Panel(chip, new Color(1f, 0.64f, 0.24f, 0.6f));
        brainVer = UI.Label(chip.transform, "v—", 30, UI.Amber, TextAnchor.MiddleCenter, FontStyle.BoldAndItalic).At(0, 0, 1, 1);
        brainDemos = UI.Label(c, "0", 96, UI.Amber, TextAnchor.LowerLeft, FontStyle.Italic).At(0, 1, 0.5f, 1, 0, -120, 0, 0);
        UI.Caption(c, "DEMOS").At(0, 1, 0.5f, 1, 4, -156, 0, -122);
        brainLearned = UI.Label(c, "0", 96, UI.Amber, TextAnchor.LowerLeft, FontStyle.Italic).At(0.5f, 1, 1, 1, 0, -120, 0, 0);
        UI.Caption(c, "LEARNED").At(0.5f, 1, 1, 1, 4, -156, 0, -122);
        var rows = UI.Column(c, 10, "rows").At(0, 0, 1, 1, 0, 50, 0, -184);
        rows.childAlignment = TextAnchor.UpperLeft;
        brainRows = rows.transform;
        brainList = UI.Label(c, "", 18, UI.Dim, TextAnchor.LowerLeft).At(0, 0, 1, 0, 0, 0, 0, 36);
    }

    // One learned rule: what triggers it, and the moves it answers with.
    void BrainRow(string title, string moves)
    {
        var row = UI.Box(brainRows, UI.Row, false, "rule").H(92);
        row.sprite = UI.Ramp; row.color = new Color(1, 1, 1, 0.09f);
        UI.Box(row.transform, UI.Accent, false, "side").At(0, 0, 0, 1, 0, 0, 5, 0);
        UI.Label(row.transform, title, 30, UI.Ink, TextAnchor.LowerLeft, FontStyle.Bold).At(0, 0.5f, 1, 1, 28, 0, -24, -10);
        UI.Label(row.transform, UI.I("chevron") + " " + moves, 22, UI.Dim, TextAnchor.UpperLeft).At(0, 0, 1, 0.5f, 28, 10, -24, -2);
    }

    void BuildModal(Transform root)
    {
        modal = UI.Box(root, new Color(0, 0, 0, 0.75f), false, "modal").At(0, 0, 1, 1, -600, -600, 600, 600).gameObject; // bleeds past the safe area
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

    // Result: huge K.O. (or TIME / DRAW), who won, Fight Again + Details. Details holds rating, lessons, proof links.
    void BuildKO(Transform root)
    {
        koPanel = UI.Box(root, new Color(0, 0, 0, 0), false, "ko").At(0, 0, 1, 1).gameObject;
        UI.Box(koPanel.transform, new Color(0.01f, 0.01f, 0.012f, 0.74f), false, "dim").At(0, 0, 1, 1, -600, -600, 600, 600); // bleeds past the safe area
        koTitle = UI.Label(koPanel.transform, "", 280, UI.Amber, TextAnchor.MiddleCenter, FontStyle.Italic).At(0, 0.5f, 1, 0.5f, 40, 70, -40, 400);
        Stroke(koTitle, 0.1f);
        koWin = UI.Label(koPanel.transform, "", 64, UI.Ink, TextAnchor.MiddleCenter, FontStyle.BoldAndItalic).At(0, 0.5f, 1, 0.5f, 40, -20, -40, 80);
        UI.Big(koPanel.transform, UI.I("refresh") + "  Fight Again", true, () => { GoHome(); FindClicked(); }, 44, pulse: true).At(0.5f, 0.5f, 0.5f, 0.5f, -560, -180, -100, -70);
        UI.Big(koPanel.transform, UI.I("home") + "  Lobby", false, GoHome, 36).At(0.5f, 0.5f, 0.5f, 0.5f, -80, -180, 200, -70);
        UI.Big(koPanel.transform, "Details", false, () => koDetails.SetActive(!koDetails.activeSelf), 36).At(0.5f, 0.5f, 0.5f, 0.5f, 220, -180, 500, -70);
        koDetails = UI.Box(koPanel.transform, UI.Card, true, "details").At(0.5f, 0, 0.5f, 0, -800, 30, 800, 320).gameObject;
        koSub = UI.Label(koDetails.transform, "", 26, UI.Ink, TextAnchor.UpperCenter).At(0, 1, 1, 1, 30, -100, -30, -20);
        koLessons = UI.Label(koDetails.transform, "", 22, UI.Dim, TextAnchor.UpperCenter).At(0, 0, 1, 1, 30, 96, -30, -104);
        var row = UI.Rt(koDetails.transform, "buttons").At(0.5f, 0, 0.5f, 0, -500, 18, 500, 84);
        koLedger = UI.Big(row, "Fight Ledger on HashScan", false, () => Application.OpenURL("https://hashscan.io/testnet/topic/" + ledgerTopic), 26);
        koLedger.At(0, 0, 0.5f, 1, 0, 0, -8, 0);
        UI.Big(row, UI.I("refresh") + "  Replay from 0G", false, () => StartCoroutine(Replay(fightId)), 26).At(0.5f, 0, 1, 1, 8, 0, 0, 0);
        koDetails.SetActive(false);
        koPanel.SetActive(false);
    }

    // ---------------------------------------------------------------- capture mode
    // -capture <dir> [-captureTag <prefix>]: walks every screen, saves PNGs, quits. Uses the server if it answers.
    IEnumerator AutoPunch(int s)
    {
        string[] all = { "jab", "cross", "hook_l", "hook_r", "uppercut", "body" };
        var args = Environment.GetCommandLineArgs(); int gi = Array.IndexOf(args, "-tapgap");
        float gap = gi >= 0 && gi + 1 < args.Length && float.TryParse(args[gi + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var g) ? g : 1.1f;
        for (int i = 0; s == session; i++) { PadMove(all[i % all.Length]); yield return new WaitForSeconds(gap); }
    }

    IEnumerator AutoDodge(int s)
    {
        string last = null;
        while (s == session)
        {
            if (Fighter.IsAttack(b.Current) && b.Current != last) { last = b.Current; PadMove(b.Current is "hook_l" ? "dodge_r" : b.Current is "hook_r" ? "dodge_l" : UnityEngine.Random.value < 0.5f ? "dodge_l" : "dodge_r"); }
            if (!Fighter.IsAttack(b.Current)) last = null;
            yield return null;
        }
    }

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
        // -record: an offline fight as a 30 fps frame sequence (fixed timestep, so motion is judged, not frame drops).
        if (Array.IndexOf(args, "-record") >= 0)
        {
            yield return Connect();
            a.botId = "bot_a"; a.displayName = "Brass Knuckle"; // the offline sim's demo bot (a stored id from an older run won't exist)
            bool dodges = Array.IndexOf(args, "-dodges") >= 0 || Array.IndexOf(args, "-strikes") >= 0; // sparring (dodges: we slip; strikes: we punch every move in turn)
            if (dodges) { StartTrain(); counterAttacks.AddRange(new[] { "jab", "hook_l", "cross", "hook_r" }); }
            else { budgetUsd = 0.25f; StartFight("normal", null); }
            yield return Wait(0.5f);
            Time.captureFramerate = 30;
            if (dodges && Array.IndexOf(args, "-strikes") < 0) StartCoroutine(AutoDodge(session));
            if (Array.IndexOf(args, "-strikes") >= 0) { Fighter.logStrikes = true; StartCoroutine(AutoPunch(session)); }
            for (int i = 0; i < 30 * 14 && !(over && koPanel.activeSelf); i++)
            {
                yield return new WaitForEndOfFrame();
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes(Path.Combine(captureDir, $"f{i:0000}.jpg"), tex.EncodeToJPG(85));
                Destroy(tex);
            }
            Debug.Log("[capture] done"); Application.Quit(); yield break;
        }
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

// A virtual stick: drag past half the radius to fire that direction once (it re-arms back near the centre); a tap
// (press and release without leaving the centre) fires "block". The knob follows the thumb, clamped to the base.
public class Joystick : MonoBehaviour, UnityEngine.EventSystems.IPointerDownHandler, UnityEngine.EventSystems.IDragHandler, UnityEngine.EventSystems.IPointerUpHandler
{
    public RectTransform knob; public float radius = 125f;
    public Action<string> onMove;
    bool armed, moved; float downAt;
    RectTransform rt;
    void Awake() => rt = (RectTransform)transform;

    public void OnPointerDown(UnityEngine.EventSystems.PointerEventData e) { armed = true; moved = false; downAt = Time.unscaledTime; Track(e); }
    public void OnDrag(UnityEngine.EventSystems.PointerEventData e) => Track(e);
    public void OnPointerUp(UnityEngine.EventSystems.PointerEventData e)
    {
        if (!moved && Time.unscaledTime - downAt < 0.35f) onMove?.Invoke("block");
        knob.anchoredPosition = Vector2.zero;
    }

    void Track(UnityEngine.EventSystems.PointerEventData e)
    {
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, e.position, e.pressEventCamera, out var p)) return;
        p -= rt.rect.center;
        var k = Vector2.ClampMagnitude(p, radius);
        knob.anchoredPosition = k;
        float m = k.magnitude / radius;
        if (m > 0.25f) moved = true;
        if (m < 0.3f) { armed = true; return; }   // back near the centre: ready for the next flick
        if (!armed || m < 0.55f) return;
        armed = false;
        onMove?.Invoke(Mathf.Abs(k.x) > Mathf.Abs(k.y) ? (k.x < 0 ? "dodge_l" : "dodge_r") : (k.y > 0 ? "step_in" : "step_back"));
    }
}
