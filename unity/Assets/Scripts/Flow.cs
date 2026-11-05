using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

// New-player flow and server-run fights (docs/flow-spec.md): Welcome → Create → World ID → first training round →
// Fund → Find a fight → live fight played from the server's event stream. Panels sit on top of Home mode, so the
// camera keeps the Home framing (your painted bot on the left, a card on the right).
public partial class Game
{
    GameObject welcome, create, fund, find, camp, guide, findPick, findSearch;
    Button findWorldBtn;
    Text cardLeague, createErr, fundBal, fundMsg, findMsg, findStatus, findSub, findLeague, campBody, guideBody;
    InputField createName;
    Button guideBtn, campBtn;
    readonly List<(string id, Image img, Text label)> swatches = new(), chips = new();
    string paint = "brass", league; int stakeCents = 25;
    bool trained = true, guideRound, guideCoach, searching;

    // live fight
    bool live, house;
    readonly JArray livePays = new();
    string seed, seedHash; JToken liveEnd;
    string oppId0, oppName0;

    static readonly (string id, string label, Color c)[] Paints =
    {
        ("brass", "BRASS", new Color(0.95f, 0.72f, 0.38f)), ("gunmetal", "GUNMETAL", new Color(0.42f, 0.45f, 0.5f)),
        ("crimson", "CRIMSON", new Color(0.82f, 0.13f, 0.14f)), ("cobalt", "COBALT", new Color(0.16f, 0.36f, 0.9f)),
        ("jade", "JADE", new Color(0.16f, 0.66f, 0.46f)), ("bone", "BONE", new Color(0.93f, 0.9f, 0.82f)),
    };
    static Color PaintColor(string id) => Paints.FirstOrDefault(p => p.id == id).c is var c && c.a > 0 ? c : Paints[0].c;

    // Body materials take the paint, accent (visor) a darker shade, eyes stay lit. Instanced, so the other bot keeps its own.
    void ApplyPaint(Fighter f, string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        var c = PaintColor(id);
        foreach (var r in f.GetComponentsInChildren<Renderer>())
        {
            if (r.name == "eye" || r is ParticleSystemRenderer) continue;
            foreach (var m in r.materials)
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", m.name.Contains("_accent") ? new Color(c.r * 0.5f, c.g * 0.5f, c.b * 0.5f, 1f) : c);
        }
    }

    // ---------------------------------------------------------------- identity
    // Identity is per server: a token from one backend means nothing to another (or to the offline sim).
    string Key(string k) => k + "@" + (Api.Offline ? "offline" : Api.BaseUrl);
    void Remember(string k, string v) { PlayerPrefs.SetString(Key(k), v); PlayerPrefs.Save(); }
    void ForgetPlayer() { PlayerPrefs.DeleteKey(Key("token")); PlayerPrefs.DeleteKey(Key("botId")); PlayerPrefs.Save(); Api.Token = null; }

    // After Connect: returning players go straight Home, everyone else starts at Welcome.
    IEnumerator Enter()
    {
        Api.Token = PlayerPrefs.GetString(Key("token"), "");
        string bot = PlayerPrefs.GetString(Key("botId"), "");
        if (bot == "" || (bot != "bot_a" && Api.Token == "")) { ShowPanel(welcome); yield break; }
        a.botId = bot;
        Show(Mode.Home);
    }

    // The stored fighter is gone or not ours (server reset, other server): start over. Network errors keep the identity.
    void IdentityError(string e)
    {
        var l = (e ?? "").ToLowerInvariant();
        if (!new[] { "not found", "unknown", "unauthor", "forbidden", "401", "403", "404" }.Any(l.Contains)) return;
        ForgetPlayer();
        a.botId = "bot_a";
        ShowPanel(welcome);
    }

    void ShowPanel(GameObject p) { Show(Mode.Home); home.SetActive(false); p.SetActive(true); }

    void HideFlow()
    {
        foreach (var p in new[] { welcome, create, fund, find, camp, guide }) if (p != null) p.SetActive(false);
        if (searching && mode != Mode.Fight) LeaveQueue();
    }

    static string Money(JToken bot) => (string)bot["payAsset"] == "hbar"
        ? $"{(double?)bot["hbar"] ?? 0:0.00} <size=26><color=#9EA5B0>HBAR · Hedera testnet</color></size>"
        : $"${(double?)bot["usdc"] ?? 0:0.00} <size=26><color=#9EA5B0>USDC · Hedera testnet</color></size>";

    // Home card extras: paint chip, league badge, rating, record. Bots without onboarding (demo bots) count as trained.
    void ShowCorner(JToken bot)
    {
        if (bot["paint"] is JValue pv && pv.Type == JTokenType.String) { paint = (string)pv; ApplyPaint(a, paint); }
        string hex = ColorUtility.ToHtmlStringRGB(PaintColor(paint));
        cardName.text = (bot["paint"] != null ? $"<color=#{hex}>●</color> " : "") + a.displayName.ToUpperInvariant();
        trained = (bool?)bot["onboarding"]?["trained"] != false;
        league = (string)bot["league"];
        var parts = new List<string> { "YOUR CORNER" };
        if (league != null) parts.Add(league == "verified" ? "<color=#5CDB85>◆ VERIFIED</color>" : "<color=#9EA5B0>◇ OPEN LEAGUE</color>");
        if (bot["rating"] != null) parts.Add($"<color=#F2F2F5>RATING {bot["rating"]}</color>");
        if (bot["record"] is JObject rec) parts.Add($"<color=#F2F2F5>{rec["w"]}W {rec["l"]}L</color>");
        cardLeague.text = string.Join("   ", parts);
    }

    // ---------------------------------------------------------------- screens
    // Right-hand card like Home's, so the painted robot stays visible on the left.
    Transform Card(Transform root, string name, out GameObject panel, float h = 820)
    {
        panel = UI.Go(root, name);
        ((RectTransform)panel.transform).At(0, 0, 1, 1);
        var card = UI.Box(panel.transform, UI.Card, true, "card").At(1, 0.5f, 1, 0.5f, -760, -h / 2, -60, h / 2);
        var col = UI.Column(card.transform, 14).At(0, 0, 1, 1, 48, 36, -48, -34);
        col.childAlignment = TextAnchor.UpperLeft;
        return col.transform;
    }

    void BuildFlow(Transform root)
    {
        // Welcome
        var w = Card(root, "Welcome", out welcome, 660);
        UI.Label(w, "IRON <color=#FF9421>CORNER</color>", 88, UI.Ink, TextAnchor.MiddleLeft, FontStyle.Bold).H(100);
        UI.Label(w, "Robot boxing. Your coaching is the only edge.", 30, UI.Ink, TextAnchor.MiddleLeft, FontStyle.Bold).H(40);
        UI.Label(w, "<color=#FF9421>1</color>  Teach it by boxing with it and talking to it.\n<color=#FF9421>2</color>  It fights other players' bots on its own, thinking on 0G.\n<color=#FF9421>3</color>  Every clean hit pays the other corner over x402 on Hedera.",
            26, UI.Dim, TextAnchor.UpperLeft).H(150);
        UI.Box(w, UI.Line, false, "rule").H(2);
        UI.Btn(w, "CREATE YOUR FIGHTER", UI.Accent, Color.black, OpenCreate, 40).H(96);
        UI.Label(w, "Testnet only. No real money anywhere.", 22, UI.Dim).H(30);
        UI.Btn(w, "Use the demo corner (Brass Knuckle) ›", new Color(0, 0, 0, 0), UI.Dim, () => { Remember("botId", "bot_a"); a.botId = "bot_a"; Show(Mode.Home); }, 22).H(48);

        // Create
        var c = Card(root, "Create", out create, 820);
        UI.Label(c, "CREATE YOUR FIGHTER", 24, UI.Accent, TextAnchor.MiddleLeft, FontStyle.Bold).H(30);
        UI.Label(c, "NAME", 20, UI.Dim, TextAnchor.LowerLeft, FontStyle.Bold).H(30);
        createName = UI.Field(c, "2-20 letters, digits or spaces", "", 36).H(84);
        createName.characterLimit = 20;
        UI.Label(c, "PAINT", 20, UI.Dim, TextAnchor.LowerLeft, FontStyle.Bold).H(36);
        var sw = UI.Rt(c, "swatches").H(132);
        for (int i = 0; i < Paints.Length; i++)
        {
            var (id, label, col) = Paints[i];
            float x0 = i / 6f, x1 = (i + 1) / 6f;
            var ring = UI.Box(sw, new Color(0, 0, 0, 0), true, "ring:" + id).At(x0, 0, x1, 1, 4, 30, -4, 0);
            var btn = UI.Btn(ring.transform, "", col, Color.black, () => PickPaint(id));
            btn.At(0, 0, 1, 1, 7, 7, -7, -7);
            var lbl = UI.Label(sw, label, 17, UI.Dim, TextAnchor.LowerCenter, FontStyle.Bold).At(x0, 0, x1, 0, 0, 0, 0, 26);
            swatches.Add((id, ring, lbl));
        }
        UI.Label(c, "Same body for every bot. The brain you teach is the only thing that differs.", 22, UI.Dim).H(34);
        createErr = UI.Label(c, "", 22, UI.Ink).H(56);
        UI.Btn(c, "CREATE FIGHTER", UI.Accent, Color.black, CreateFighter, 38).H(92);
        UI.Btn(c, "‹  BACK", UI.Steel, UI.Ink, () => ShowPanel(welcome), 24).H(56);

        // Fund
        var f = Card(root, "Fund", out fund, 760);
        UI.Label(f, "FUND YOUR FIGHTER", 24, UI.Accent, TextAnchor.MiddleLeft, FontStyle.Bold).H(30);
        UI.Label(f, "WALLET", 20, UI.Dim, TextAnchor.LowerLeft, FontStyle.Bold).H(34);
        fundBal = UI.Label(f, "—", 60, UI.Brass, TextAnchor.MiddleLeft, FontStyle.Bold).H(72);
        UI.Label(f, "Your fighter's own Hedera testnet wallet pays for every clean hit it takes. Test funds come from the house faucet: once a day, up to 0.5 HBAR, while the wallet holds less than 0.5. Testnet HBAR has no value.",
            24, UI.Dim, TextAnchor.UpperLeft).H(130);
        UI.Btn(f, "ADD TEST FUNDS", UI.Accent, Color.black, AddFunds, 36).H(88);
        fundMsg = UI.Label(f, "", 22, UI.Ink, TextAnchor.UpperLeft).H(62);
        UI.Btn(f, "FIND A FIGHT  ›", UI.Steel, UI.Ink, FindClicked, 32).H(80);
        UI.Btn(f, "‹  HOME", UI.Steel, UI.Ink, () => Show(Mode.Home), 24).H(56);

        // Find a fight: stake picker, then the searching panel
        var q = Card(root, "Find", out find, 740);
        UI.Label(q, "FIND A FIGHT", 24, UI.Accent, TextAnchor.MiddleLeft, FontStyle.Bold).H(30);
        var lr = UI.Rt(q, "leagueRow").H(40);
        findLeague = UI.Label(lr, "", 24, UI.Ink).At(0, 0, 0.62f, 1);
        // Players who skipped World ID at first run can connect it later from here (Verified league).
        findWorldBtn = UI.Btn(lr, "CONNECT WORLD ID", UI.Steel, UI.Ink, () => { Show(Mode.Home); OpenWorld(); }, 20);
        findWorldBtn.At(0.64f, 0, 1, 1);
        var body = UI.Rt(q, "body").H(560);
        findPick = UI.Go(body, "pick"); ((RectTransform)findPick.transform).At(0, 0, 1, 1);
        UI.Label(findPick.transform, "STAKE · YOUR FIGHT BUDGET", 20, UI.Dim, TextAnchor.UpperLeft, FontStyle.Bold).At(0, 1, 1, 1, 0, -30, 0, 0);
        int[] stakes = { 10, 25, 50 };
        for (int i = 0; i < 3; i++)
        {
            int cents = stakes[i];
            var chip = UI.Btn(findPick.transform, UI.Usd(cents), UI.Steel, UI.Ink, () => PickStake(cents), 40);
            chip.At(i / 3f, 1, (i + 1) / 3f, 1, i == 0 ? 0 : 8, -140, i == 2 ? 0 : -8, -40);
            chips.Add((cents.ToString(), chip.GetComponent<Image>(), chip.GetComponentInChildren<Text>()));
        }
        UI.Label(findPick.transform, "Health = budget. Every clean hit your bot takes pays the other corner; an empty budget is a KO. Your bot fights on its own with what you taught it.",
            24, UI.Dim, TextAnchor.UpperLeft).At(0, 1, 1, 1, 0, -280, 0, -160);
        findMsg = UI.Label(findPick.transform, "", 22, UI.Ink, TextAnchor.UpperLeft).At(0, 1, 1, 1, 0, -370, 0, -290);
        UI.Btn(findPick.transform, "JOIN QUEUE", UI.Accent, Color.black, JoinQueue, 40).At(0, 0, 1, 0, 0, 80, 0, 172);
        UI.Btn(findPick.transform, "‹  HOME", UI.Steel, UI.Ink, () => Show(Mode.Home), 24).At(0, 0, 1, 0, 0, 0, 0, 60);
        findSearch = UI.Go(body, "search"); ((RectTransform)findSearch.transform).At(0, 0, 1, 1);
        findStatus = UI.Label(findSearch.transform, "SEARCHING", 64, UI.Ink, TextAnchor.MiddleLeft, FontStyle.Bold).At(0, 1, 1, 1, 0, -150, 0, -50);
        findSub = UI.Label(findSearch.transform, "", 28, UI.Dim, TextAnchor.UpperLeft).At(0, 1, 1, 1, 0, -330, 0, -170);
        UI.Btn(findSearch.transform, "CANCEL", UI.Steel, UI.Ink, () => { LeaveQueue(); ShowFind(false); }, 32).At(0, 0, 1, 0, 0, 0, 0, 84);
        findSearch.SetActive(false);

        // Camp
        var k = Card(root, "Camp", out camp, 640);
        UI.Label(k, "FIGHT CAMP", 24, UI.Accent, TextAnchor.MiddleLeft, FontStyle.Bold).H(30);
        UI.Label(k, "Fine-tunes your fighter's own model on 0G", 34, UI.Ink, TextAnchor.UpperLeft, FontStyle.Bold).H(50);
        campBody = UI.Label(k, "", 25, UI.Dim, TextAnchor.UpperLeft).H(250);
        campBtn = UI.Btn(k, "SEND TO CAMP", UI.Accent, Color.black, SendToCamp, 36);
        campBtn.H(88);
        UI.Btn(k, "‹  HOME", UI.Steel, UI.Ink, () => Show(Mode.Home), 24).H(56);

        // First-training guide, over the Train screen (right side, clear of the pads and the bubble)
        var g = UI.Box(train.transform, new Color(0.05f, 0.055f, 0.07f, 0.92f), true, "guide").At(1, 0, 1, 0, -500, 410, -40, 640);
        g.gameObject.AddComponent<Outline>().effectColor = new Color(1f, 0.58f, 0.13f, 0.5f);
        guide = g.gameObject;
        UI.Label(g.transform, "FIRST TRAINING ROUND", 24, UI.Accent, TextAnchor.UpperLeft, FontStyle.Bold).At(0, 1, 1, 1, 26, -56, -26, -20);
        guideBody = UI.Label(g.transform, "", 25, UI.Ink, TextAnchor.UpperLeft).At(0, 1, 1, 1, 26, -230, -26, -64);
        guideBody.lineSpacing = 1.1f;
        guideBtn = UI.Btn(g.transform, "CONTINUE  ›  FUND", UI.Accent, Color.black, GuideContinue, 30);
        guideBtn.At(0, 0, 1, 0, 26, 22, -26, 86);
        guide.SetActive(false);
    }

    // ---------------------------------------------------------------- create
    void OpenCreate() { ShowPanel(create); createErr.text = ""; PickPaint(paint); }

    void PickPaint(string id)
    {
        paint = id;
        foreach (var (sid, img, label) in swatches) { img.color = sid == id ? UI.Ink : new Color(0, 0, 0, 0); label.color = sid == id ? UI.Ink : UI.Dim; }
        ApplyPaint(a, id); // live preview on your robot
    }

    void CreateFighter()
    {
        var name = Regex.Replace(createName.text.Trim(), @"\s+", " ");
        if (!Regex.IsMatch(name, "^[A-Za-z0-9 ]{2,20}$")) { createErr.text = "<color=#F2523F>Name: 2-20 letters, digits or spaces.</color>"; return; }
        StartCoroutine(CreateRoutine(name));
    }

    IEnumerator CreateRoutine(string name)
    {
        createErr.text = "<color=#F2A33A>Creating your fighter: Hedera account, 0G Agentic ID…</color>";
        string err = null;
        if (string.IsNullOrEmpty(Api.Token))
            yield return Api.Call("POST", "/users", new JObject(), r => { Api.Token = (string)r["token"]; Remember("token", Api.Token); }, e => err = e);
        JToken bot = null;
        if (err == null) yield return Api.Call("POST", "/bots", new JObject { ["name"] = name, ["paint"] = paint }, r => bot = r, e => err = e);
        string id = (string)bot?["botId"];
        // One fighter per player: if this token already owns one (prefs lost the id), adopt it.
        if (id == null && err != null && !string.IsNullOrEmpty(Api.Token))
            yield return Api.Call("GET", "/me", null, r => id = (string)r["botId"]);
        if (id == null) { createErr.text = $"<color=#F2523F>Couldn't create the fighter: {err}</color>"; yield break; }
        a.botId = id; Remember("botId", id);
        a.displayName = (string)bot?["name"] ?? name;
        trained = (bool?)bot?["onboarding"]?["trained"] == true;
        create.SetActive(false);
        OpenWorld();
    }

    // ---------------------------------------------------------------- World ID
    void OpenWorld() => Approve("/auth/world/start", new JObject(), "One human, one fighter",
        "Connect World ID to enter the Verified league: each verified human owns one fighter there, so every opponent is a real person's bot. Skip and you fight in the Open league.",
        "SKIP FOR NOW  ·  OPEN LEAGUE", _ => StartCoroutine(WorldDone()), AfterWorld);

    IEnumerator WorldDone()
    {
        bool? ok = null;
        yield return Api.Call("GET", "/me", null, r => ok = (bool?)r["worldVerified"] == true);
        Toast(ok == true ? "Verified human ✓  ·  Verified league" : "That World ID already has a verified fighter · Open league for now");
        AfterWorld();
    }

    void AfterWorld() { if (!trained) StartTrainGuided(); else Show(Mode.Home); }

    // ---------------------------------------------------------------- first training round
    void StartTrainGuided()
    {
        StartTrain();
        guideRound = guideCoach = false;
        guide.SetActive(!trained);
        GuideCheck();
    }

    // Done when a round summary came back and one coaching line was sent. The server keeps its own flag for the queue.
    void GuideCheck()
    {
        if (guide == null || !guide.activeSelf) return;
        string Step(bool done, string n, string s) => done ? $"<color=#5CDB85>✓</color>  <color=#9EA5B0>{s}</color>" : $"<color=#FF9421>{n}</color>  {s}";
        bool done = guideRound && guideCoach;
        guideBody.text = Step(guideRound, "1", "Spar one round: tap moves on the pads") + "\n" + Step(guideCoach, "2", "Teach one line with the mic or text")
            + (done ? "\n<color=#5CDB85><b>Trained.</b></color> Your fighter can enter the queue." : "\n<size=21><color=#9EA5B0>Every move you make is a demo your bot learns from.</color></size>");
        guideBtn.gameObject.SetActive(done);
        ((RectTransform)guide.transform).offsetMin = new Vector2(-500, done ? 300 : 410);
        if (done) trained = true;
    }

    void GuideContinue()
    {
        if (roundOn) EndRound();
        if (unsavedRounds > 0) StartCoroutine(AutoSave());
        OpenFund();
    }

    // ---------------------------------------------------------------- fund
    void OpenFund() { ShowPanel(fund); fundMsg.text = ""; StartCoroutine(LoadBalance()); }

    IEnumerator LoadBalance()
    {
        fundBal.text = "…";
        yield return Api.Call("GET", $"/bots/{a.botId}", null, r => fundBal.text = Money(r), e => fundBal.text = "—");
    }

    void AddFunds()
    {
        fundMsg.text = "<color=#F2A33A>Asking the house faucet…</color>";
        StartCoroutine(Api.Call("POST", $"/bots/{a.botId}/fund", new JObject(), r =>
        {
            fundMsg.text = $"<color=#5CDB85>Test funds sent ✓</color>  <color=#9EA5B0>tx {r["txId"]}</color>";
            StartCoroutine(LoadBalance());
        }, e => fundMsg.text = $"<color=#F2A33A>{e}</color>"));
    }

    // ---------------------------------------------------------------- find a fight
    void FindClicked()
    {
        if (!aAlive) { if (aNeedsRevive) Revive(); else Toast("Still repairing — a few seconds"); return; }
        if (a.botId == "bot_a") { budgetUsd = 0.5f; StartFight("normal", null); return; } // demo corner: the old house fight
        if (!trained) { Toast("Locked: spar one round and teach one line in Train first"); return; }
        ShowPanel(find); ShowFind(false); PickStake(stakeCents);
        findMsg.text = "";
        findLeague.text = league == "verified" ? "<color=#5CDB85>◆ Verified league</color> <color=#9EA5B0>· verified humans only</color>" : "<color=#9EA5B0>◇ Open league</color>";
        findWorldBtn.gameObject.SetActive(league != "verified");
    }

    void PickStake(int cents)
    {
        stakeCents = cents;
        foreach (var (id, img, label) in chips) { bool on = id == cents.ToString(); img.color = on ? UI.Accent : UI.Steel; label.color = on ? Color.black : UI.Ink; }
    }

    void ShowFind(bool search) { findPick.SetActive(!search); findSearch.SetActive(search); }

    void JoinQueue()
    {
        findMsg.text = "";
        StartCoroutine(Api.Call("POST", "/queue", new JObject { ["botId"] = a.botId, ["stakeCents"] = stakeCents }, r =>
        {
            searching = true; ShowFind(true);
            findStatus.text = "SEARCHING"; findSub.text = "";
            StartCoroutine(QueueLoop(session));
        }, e => findMsg.text = "<color=#F2523F>" + (e.Contains("needs_training") ? "Finish your first training round first." : e) + "</color>"));
    }

    IEnumerator QueueLoop(int s)
    {
        while (s == session && searching)
        {
            JToken r = null;
            yield return Api.Call("GET", $"/queue/{a.botId}", null, x => r = x);
            if (s != session || !searching) yield break;
            var st = (string)r?["status"];
            if (st == "matched" && r["fightId"] != null) { searching = false; findStatus.text = "<color=#5CDB85>MATCHED</color>"; StartCoroutine(StartLive((string)r["fightId"])); yield break; }
            if (st == "idle") { searching = false; ShowFind(false); findMsg.text = "<color=#F2A33A>Dropped from the queue. Join again.</color>"; yield break; }
            if (r != null)
            {
                int waited = (int?)r["waitedSec"] ?? 0;
                findStatus.text = "SEARCHING" + new string('.', 1 + waited % 3);
                findSub.text = $"Waited {waited} s  ·  {r["queueSize"] ?? 1} in queue\n{UI.Usd(stakeCents)} stake  ·  {(league == "verified" ? "Verified" : "Open")} league\n<size=22>The search widens with time; after 25 s you get a house sparring bot.</size>";
            }
            yield return new WaitForSecondsRealtime(1f);
        }
    }

    void LeaveQueue()
    {
        if (!searching) return;
        searching = false;
        StartCoroutine(Api.Call("DELETE", $"/queue/{a.botId}", null, null));
    }

    // ---------------------------------------------------------------- live fight (server-run, played from events)
    IEnumerator StartLive(string id)
    {
        JToken fv = null, ob = null;
        yield return Api.Call("GET", $"/fights/{id}", null, r => fv = r);
        string opp = (string)fv?["a"] == a.botId ? (string)fv?["b"] : (string)fv?["a"];
        if (opp == null && fv?["budgets"] is JObject bk) opp = bk.Properties().Select(p => p.Name).FirstOrDefault(n => n != a.botId);
        if (opp != null) yield return Api.Call("GET", $"/bots/{opp}", null, r => ob = r);

        Show(Mode.Fight);
        ResetFighters();
        live = true; over = false; fightId = id; stakes = "normal"; ledgerTopic = (string)fv?["ledgerTopic"]; fightStart = Time.time;
        seed = null; seedHash = (string)fv?["seedHash"]; liveEnd = null; livePays.Clear();
        oppId0 ??= b.botId; oppName0 ??= b.displayName;
        house = (bool?)ob?["house"] == true || (bool?)fv?["house"] == true;
        b.botId = opp ?? "opponent"; b.displayName = ((string)ob?["name"] ?? b.botId) + (house ? " (House)" : "");
        int start = (int?)fv?["budgets"]?[a.botId] ?? (int?)fv?["stakeCents"] ?? stakeCents;
        a.ResetFor(start); b.ResetFor((int?)fv?["budgets"]?[b.botId] ?? start);
        a.active = b.active = true; a.scripted = b.scripted = true;
        fightTitle.text = $"<color=#5CDB85>LIVE</color>  ·  {UI.Usd(start)} stake  ·  <color=#9EA5B0>{id}</color>" + (seedHash != null ? $"\n<size=18>seed hash {UI.Short(seedHash, 8)}</size>" : "");
        fightTranscript.text = "AUTONOMOUS  ·  the server runs the fight; both bots think on 0G with what they learned in training";
        foreach (Transform c in ticker) Destroy(c.gameObject);
        UpdateBars();
        Banner("FIGHT!", UI.Accent);
        fx.Bell(1); fx.Cheer(0.5f);
        int s = session;
        var q = new List<JToken>();
        StartCoroutine(LivePlay(s, q));
        StartCoroutine(LivePoll(s, q));
    }

    void RestoreOpponent()
    {
        koTitle.color = UI.Red;
        if (oppId0 == null) return;
        b.botId = oppId0; b.displayName = oppName0; oppId0 = oppName0 = null;
    }

    IEnumerator LivePoll(int s, List<JToken> q)
    {
        int since = 0; bool done = false;
        while (s == session && !done)
        {
            yield return Api.Call("GET", $"/fights/{fightId}/events?since={since}", null, r =>
            {
                if (s != session) return;
                foreach (var e in r["events"] as JArray ?? new JArray()) { int n = (int?)e["seq"] ?? since + 1; if (n <= since) continue; since = n; q.Add(e); }
                seedHash = (string)r["seedHash"] ?? seedHash;
                seed = (string)r["seed"] ?? seed;
                done = (string)r["status"] == "over" && q.Any(e => (string)e["type"] == "end");
            });
            yield return new WaitForSecondsRealtime(0.5f);
        }
    }

    // Plays events in order about 1 s behind the server. Hits are played (not resolved): both fighters are scripted.
    IEnumerator LivePlay(int s, List<JToken> q)
    {
        float? clock = null; float lastT = 0; int i = 0, hitNo = 0;
        while (s == session)
        {
            if (i >= q.Count) { yield return null; continue; }
            var e = q[i];
            float raw = e["t"]?.Type is JTokenType.Integer or JTokenType.Float ? (float)e["t"] : lastT + 0.4f;
            float t = raw > 600f ? raw / 1000f : raw; // seconds, or milliseconds from a server clock
            lastT = t;
            clock ??= Time.realtimeSinceStartup - t + 1f; // ~1 s buffer
            while (s == session && Time.realtimeSinceStartup < clock.Value + t) yield return null;
            if (s != session) yield break;
            i++;
            var who = (string)e["botId"] == a.botId ? a : b;
            switch ((string)e["type"])
            {
                case "plan":
                    var moves = (e["moves"] as JArray ?? e["combo"] as JArray ?? new JArray()).Select(x => (string)x).ToList();
                    who.holdBlock = (string)e["stance"] == "safe_stop";
                    if (who.holdBlock) Say(who, "Covering up — no fresh plan (safe stop)", "0G timed out or failed · holding guard", UI.Accent);
                    else ShowPlan(who, e, moves);
                    break;
                case "move":
                    if (!PlayedByHit(q, i, (string)e["botId"], (string)e["move"])) { who.holdBlock = false; who.SetQueue(new[] { (string)e["move"] }); }
                    break;
                case "hit":
                    var att = (string)e["attacker"] == a.botId ? a : b;
                    string move = (string)e["move"], result = (string)e["result"];
                    int cents = (int?)e["cents"] ?? (result == "clean" ? Fighter.Damage(move) : 0);
                    if (result == "clean")
                    {
                        livePays.Add(new JObject { ["hitNo"] = ++hitNo, ["from"] = att.opp.botId, ["to"] = att.botId, ["cents"] = cents, ["status"] = "pending", ["txId"] = "queued" });
                        RenderTicker(livePays);
                    }
                    yield return PlayHit(att, move, result, cents);
                    break;
                case "pay":
                    var row = livePays.FirstOrDefault(p => (int?)p["hitNo"] == (int?)e["hitNo"]);
                    if (row != null)
                    {
                        row["txId"] = e["txId"]; row["hashscan"] = e["hashscan"];
                        row["status"] = (string)e["status"] ?? (e["txId"]?.Type == JTokenType.String ? "settled" : "pending");
                        RenderTicker(livePays);
                    }
                    break;
                case "ko":
                    if (!over) KO((string)e["loser"] == a.botId ? a : b);
                    break;
                case "end":
                    liveEnd = e;
                    if (!over) TimeUp((string)e["winner"]);
                    break;
            }
        }
    }

    // A block/dodge or punch "move" that the next hit event already plays is skipped, so it isn't queued twice.
    static bool PlayedByHit(List<JToken> q, int from, string botId, string move)
    {
        for (int j = from; j < Math.Min(q.Count, from + 4); j++)
        {
            var e = q[j];
            if ((string)e["type"] != "hit") continue;
            if ((string)e["attacker"] == botId && (string)e["move"] == move) return true;
            if ((string)e["defender"] == botId && !Fighter.IsAttack(move) && move != "step_in" && move != "step_back") return true;
        }
        return false;
    }

    // One recorded hit, played on scripted fighters: the punch, the defence just before contact, then the result at the strike frame.
    // Shared by live fights and the 0G replay. Positions stay with Fighter.Move (ring bounds + min gap): we only queue moves.
    IEnumerator PlayHit(Fighter att, string move, string result, int cents)
    {
        var def = att.opp;
        att.holdBlock = false;
        att.SetQueue(new[] { move });
        float until = Time.time + 1.6f; // it may step into range first
        while (att.Current != move && Time.time < until && !att.down) yield return null;
        if (result is "blocked" or "dodged") { def.holdBlock = false; def.SetQueue(new[] { result == "blocked" ? "block" : "dodge_r" }, front: true); }
        yield return new WaitForSeconds(0.3f); // strike peak: 0.28-0.42 of the clip at 1.3-1.45x
        if (result == "clean") { def.TakeHit(move); def.budget = Mathf.Max(0, def.budget - cents); }
        OnHit(att, move, result);
        UpdateBars();
    }

    // Time limit (no KO): more budget left wins, equal is a draw.
    void TimeUp(string winnerId)
    {
        over = true;
        var w = winnerId == a.botId ? a : winnerId == b.botId ? b : null;
        if (w != null) w.Victory(); else { a.PlayIdle(); b.PlayIdle(); }
        Banner(w == null ? "DRAW" : "TIME", UI.Accent);
        StartCoroutine(TimeUpPanel(session, w));
    }

    IEnumerator TimeUpPanel(int s, Fighter w)
    {
        yield return new WaitForSecondsRealtime(1.6f);
        if (s != session) yield break;
        koTitle.color = UI.Accent;
        koTitle.text = w == null ? "DRAW — time" : $"TIME — {w.displayName.ToUpperInvariant()} WINS";
        koSub.text = w == null ? "Both budgets equal after 90 s." : $"{w.displayName} has more fight budget left after 90 s.";
        koPanel.SetActive(true);
        yield return LiveEnd(s);
    }

    // End screen text for a server-run fight: winner, rating change, record, provably-fair seed.
    IEnumerator LiveEnd(int s)
    {
        koLessons.text = "";
        float until = Time.realtimeSinceStartup + 6f;
        while (liveEnd == null && s == session && Time.realtimeSinceStartup < until) yield return null;
        JToken me = null, fv = null;
        yield return Api.Call("GET", $"/bots/{a.botId}", null, r => me = r);
        yield return Api.Call("GET", $"/fights/{fightId}", null, r => fv = r);
        if (s != session) yield break;
        ledgerTopic = (string)fv?["ledgerTopic"] ?? ledgerTopic;
        seed = (string)fv?["seed"] ?? seed; seedHash = (string)fv?["seedHash"] ?? seedHash;
        koLedger.gameObject.SetActive(!Api.Offline && !string.IsNullOrEmpty(ledgerTopic) && ledgerTopic != "offline");
        var rd = liveEnd?["ratingDelta"];
        int? delta = rd is JObject o ? (int?)o[a.botId] : rd?.Type is JTokenType.Integer or JTokenType.Float ? (int?)(int)Math.Round((double)rd) : null;
        string rating = house ? "House sparring fight · no rating change"
            : delta != null ? $"Rating <b>{(delta >= 0 ? "+" : "")}{delta}</b>" + (me?["rating"] != null ? $" → {me["rating"]}" : "") : (me?["rating"] != null ? $"Rating {me["rating"]}" : "");
        string record = me?["record"] is JObject rec ? $"Record {rec["w"]}W {rec["l"]}L" : "";
        string w = (string)liveEnd?["winner"];
        if ((string)liveEnd?["reason"] == "ko" && w == a.botId) { koTitle.text = "K.O. — YOU WIN"; koTitle.color = UI.Green; }
        string result = w == null || koTitle.text.Contains("YOU WIN") ? "" : w == a.botId ? "<color=#5CDB85><b>You win.</b></color>  " : "<color=#F2523F><b>You lose.</b></color>  ";
        koSub.text = result + string.Join("  ·  ", new[] { rating, record }.Where(x => x != ""));
        string fair = seed != null
            ? $"Provably fair: seed {UI.Short(seed, 10)}, hash {UI.Short(seedHash, 10)}  " + (SeedMatches(seed, seedHash) ? "<color=#5CDB85>✓ hash checked on this device</color>" : "<color=#F2A33A>hash not checked</color>")
            : seedHash != null ? $"Provably fair: hash {UI.Short(seedHash, 10)} · seed revealed when the fight closes" : "";
        koLessons.text = fair + "\nThe full episode is saved to 0G Storage: replay it below." + (Api.Offline ? "\n<color=#F2A33A>offline sim: no payments moved</color>" : "");
    }

    // sha256(seed) == seedHash, trying the seed as hex bytes, then as text.
    static bool SeedMatches(string seed, string hash)
    {
        if (string.IsNullOrEmpty(seed) || string.IsNullOrEmpty(hash)) return false;
        string h = hash.StartsWith("0x") ? hash.Substring(2) : hash, sd = seed.StartsWith("0x") ? seed.Substring(2) : seed;
        using var sha = SHA256.Create();
        string Hex(byte[] b) => string.Concat(b.Select(x => x.ToString("x2")));
        try { if (sd.Length % 2 == 0 && Hex(sha.ComputeHash(Enumerable.Range(0, sd.Length / 2).Select(i => Convert.ToByte(sd.Substring(i * 2, 2), 16)).ToArray())) == h.ToLowerInvariant()) return true; } catch { }
        return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(seed))) == h.ToLowerInvariant();
    }

    // ---------------------------------------------------------------- camp (LoRA on 0G)
    void OpenCamp() { ShowPanel(camp); campBody.text = "Loading…"; campBtn.interactable = false; StartCoroutine(CampLoop(session)); }

    IEnumerator CampLoop(int s)
    {
        while (s == session)
        {
            int demos = 0; JToken st = null; string err = null;
            yield return Api.Call("GET", $"/bots/{a.botId}", null, r => demos = (int?)r["progress"]?["demos"] ?? 0);
            yield return Api.Call("GET", $"/bots/{a.botId}/camp", null, r => st = r, e => err = e);
            if (s != session) yield break;
            string status = (string)st?["status"] ?? "idle";
            bool running = status is "queued" or "running" or "training";
            double p = (double?)st?["progress"] ?? 0; if (p <= 1) p *= 100;
            var lines = new List<string>
            {
                $"Your bot's own adapter is trained from its sparring demos on 0G Compute. Each camp makes a new version, recorded on its Agentic ID.",
                $"<color=#F2F2F5>Demos: <b>{demos}</b>/40 needed</color>" + (demos < 40 ? "  <color=#F2A33A>· spar more rounds first</color>" : "  <color=#5CDB85>✓</color>"),
                err != null ? $"<color=#F2523F>{err}</color>" : $"<color=#F2F2F5>Status: <b>{status}</b></color>" + (running ? $"  {p:0}%" : "")
                    + (st?["version"] != null && st["version"].Type != JTokenType.Null ? $"  ·  adapter v{st["version"]}" : "")
                    + (st?["loss"] != null && st["loss"].Type != JTokenType.Null ? $"  ·  loss {(double)st["loss"]:0.000}" : "")
                    + (st?["adapterRoot"]?.Type == JTokenType.String ? $"\n<size=21>root {UI.Short((string)st["adapterRoot"], 10)}</size>" : ""),
                "<size=21>One camp per bot per day.</size>",
            };
            campBody.text = string.Join("\n", lines);
            campBtn.interactable = demos >= 40 && !running;
            campBtn.SetLabel(running ? "IN CAMP…" : "SEND TO CAMP");
            yield return new WaitForSecondsRealtime(running ? 3f : 10f);
        }
    }

    void SendToCamp() => StartCoroutine(Api.Call("POST", $"/bots/{a.botId}/camp", new JObject(), r => { Toast("Off to camp: fine-tuning on 0G"); OpenCamp(); }, e => Toast("Camp: " + e)));

    // ---------------------------------------------------------------- capture: first-run sequence (-capture <dir> -flow [-offline])
    IEnumerator CaptureFlow(Func<string, IEnumerator> shot)
    {
        IEnumerator Wait(float s) { yield return new WaitForSecondsRealtime(s); }
        IEnumerator Until(Func<bool> c, float max) { float u = Time.realtimeSinceStartup + max; while (!c() && Time.realtimeSinceStartup < u) yield return null; }

        yield return Connect();
        ForgetPlayer();
        yield return Enter();
        yield return Wait(2f);
        yield return shot("01_welcome");

        OpenCreate();
        createName.text = "Copper Kid";
        PickPaint("cobalt");
        yield return Wait(1.2f);
        yield return shot("02_create");

        CreateFighter();
        yield return Until(() => modal.activeSelf, 15f);
        yield return Wait(1.2f);
        yield return shot("03_world");
        if (!Api.Offline) CloseModal(true); // same as tapping Skip: live World needs an Orb-verified account (Open league)
        yield return Until(() => mode == Mode.Train, 20f); // offline sim approves after ~6 s
        yield return Wait(2f);
        yield return shot("04_train_guide");

        GoHome();
        yield return Wait(2.5f);
        yield return shot("05_home_locked");
        StartTrainGuided();
        yield return Wait(1.5f);

        trainField.text = "when he throws a left hook, slip right and uppercut";
        SendTyped(trainField, trainTranscript);
        roundSeconds = 16f;
        float rEnd = Time.realtimeSinceStartup + 16.5f; bool mid = false;
        string[] punches = { "jab", "cross", "uppercut", "hook_r", "body" };
        while (Time.realtimeSinceStartup < rEnd)
        {
            bool incoming = Fighter.IsAttack(b.Current);
            PadMove(incoming ? (b.Current == "hook_l" ? "dodge_r" : "block") : punches[UnityEngine.Random.Range(0, punches.Length)]);
            yield return Wait(0.75f);
            if (!mid && roundMoves >= 6) { mid = true; yield return shot("06_train_round"); }
        }
        yield return Until(() => summary.activeSelf, 8f);
        yield return Wait(0.8f);
        yield return shot("07_train_done");
        unsavedRounds = 0;

        GuideContinue();
        yield return Wait(1.5f);
        yield return shot("08_fund");
        AddFunds();
        yield return Wait(1.5f);
        yield return shot("09_fund_done");

        FindClicked();
        PickStake(25);
        yield return Wait(1f);
        yield return shot("10_find");
        JoinQueue();
        yield return Wait(2.2f);
        yield return shot("11_searching");

        yield return Until(() => mode == Mode.Fight, 60f); // house fallback after 25 s + matcher tick
        yield return Wait(0.4f);
        yield return shot("12_live_start");
        yield return Until(() => bubA.root.gameObject.activeSelf || bubB.root.gameObject.activeSelf, 8f);
        yield return Wait(0.5f);
        yield return shot("13_live_plan");
        for (int n = 0; n < 3 && !over; n++)
        {
            yield return Until(() => Time.unscaledTime < hitStopUntil || over, 20f);
            yield return null; yield return null;
            yield return shot($"14_live_hit{n}");
            yield return Wait(0.6f);
        }
        yield return Wait(1f);
        yield return shot("15_live_ticker");
        yield return Until(() => over, 100f);
        yield return Wait(0.5f);
        yield return shot("16_ko");
        yield return Until(() => koPanel.activeSelf && koLessons.text.Contains("Provably"), 15f);
        yield return Wait(0.6f);
        yield return shot("17_end");
        StartCoroutine(Replay(fightId)); // episode rebuilt by the sim from the event log
        yield return Until(() => replaying, 20f);
        yield return Wait(4f);
        yield return shot("17b_replay");

        // A returning player: same token, straight to Home.
        GoHome();
        yield return Enter();
        yield return Wait(2.5f);
        yield return shot("18_home_returning");
        OpenCamp();
        yield return Wait(2f);
        yield return shot("19_camp");
    }
}
