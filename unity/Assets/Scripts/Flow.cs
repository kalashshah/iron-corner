using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;
using TMPro;
using UnityEngine.UI;

// New-player flow and server-run fights (docs/flow-spec.md): Welcome → Create → World ID → first training round →
// Fund → Find a fight → live fight played from the server's event stream. Panels sit on top of Home mode, so the
// camera keeps the Home framing (your painted bot on the left, a card on the right).
public partial class Game
{
    GameObject welcome, create, fund, find, camp, guide, findPick, findSearch;
    Button findWorldBtn;
    TMP_Text cardLeague, createErr, fundBal, fundMsg, findMsg, findStatus, findSub, findLeague, campBody, guideBody;
    TMP_InputField createName;
    Button guideBtn, campBtn;
    readonly List<(string id, Image img, TMP_Text label)> swatches = new(), chips = new();
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

    // Matte graphite armour: the paint is a tint over dark grey, not a chrome colour, so the rim light shapes the bot.
    static readonly Color Graphite = new(0.2f, 0.2f, 0.21f);
    static Color Tint(Color c, float k) => new(Mathf.Lerp(Graphite.r, c.r, k), Mathf.Lerp(Graphite.g, c.g, k), Mathf.Lerp(Graphite.b, c.b, k), 1f);

    // Arena in the same monochrome as the UI: grey ropes and pads, white LEDs, neutral steel rim. The warm rim stays.
    static void Monochrome()
    {
        void Base(Material m, Color c) { if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c); }
        foreach (var r in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            foreach (var m in r.sharedMaterials)
            {
                if (m == null) continue;
                switch (m.name)
                {
                    case "rope_mid": Base(m, new Color(0.7f, 0.7f, 0.73f)); break;
                    case "ring_logo": Base(m, new Color(0.035f, 0.035f, 0.04f)); break;
                    case "pad_brass": case "pad_steel": Base(m, new Color(0.13f, 0.13f, 0.14f)); break;
                    case "led_brass": case "led_steel": m.SetColor("_EmissionColor", new Color(0.9f, 0.92f, 1f) * 2.2f); break;
                }
            }
        foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
        {
            if (l.name == "RimSteel") l.color = new Color(0.9f, 0.92f, 1f);
            if (l.name is "RimSteel" or "RimBrass") l.intensity *= 1.35f; // matte armour needs more rim to read
        }
    }

    void Matte(Fighter f)
    {
        foreach (var r in f.GetComponentsInChildren<Renderer>(true))
        {
            if (r.name == "eye" || r is ParticleSystemRenderer) continue;
            foreach (var m in r.materials)
            {
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.4f);
                if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.5f);
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Tint(m.GetColor("_BaseColor"), 0.5f));
            }
        }
    }

    // Body materials take the paint, accent (visor) a darker shade, eyes stay lit. Instanced, so the other bot keeps its own.
    void ApplyPaint(Fighter f, string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        var c = PaintColor(id);
        foreach (var r in f.GetComponentsInChildren<Renderer>())
        {
            if (r.name == "eye" || r is ParticleSystemRenderer) continue;
            foreach (var m in r.materials)
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", m.name.Contains("_accent") ? Tint(c, 0.3f) : Tint(c, 0.55f));
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
        ? $"{(double?)bot["hbar"] ?? 0:0.00} <size=26><color=#8E8E93>HBAR · Hedera testnet</color></size>"
        : $"${(double?)bot["usdc"] ?? 0:0.00} <size=26><color=#8E8E93>USDC · Hedera testnet</color></size>";

    // Home card extras: paint chip, league badge, rating, record. Bots without onboarding (demo bots) count as trained.
    void ShowCorner(JToken bot)
    {
        if (bot["paint"] is JValue pv && pv.Type == JTokenType.String) { paint = (string)pv; ApplyPaint(a, paint); }
        string hex = ColorUtility.ToHtmlStringRGB(PaintColor(paint));
        cardName.text = a.displayName + (bot["paint"] != null ? $" <size=34><color=#{hex}>●</color></size>" : "");
        trained = (bool?)bot["onboarding"]?["trained"] != false;
        league = (string)bot["league"];
        var parts = new List<string>();
        if (league != null) parts.Add(league == "verified" ? "<color=#6BD694>\uead5 VERIFIED</color>" : "\uead5 OPEN LEAGUE");
        if (bot["rating"] != null) parts.Add($"RATING {bot["rating"]}");
        if (bot["record"] is JObject rec) parts.Add($"{rec["w"]}W {rec["l"]}L");
        cardLeague.text = parts.Count > 0 ? string.Join("  ·  ", parts) : "YOUR CORNER";
    }

    // ---------------------------------------------------------------- screens
    // A panel over the lobby, like a shooter's inventory: dark translucent card on the right under the tabs,
    // a heavy title, content below. back != null adds a back arrow top left (screens outside the tabs).
    Transform Page(Transform root, string name, string title, Action back, out GameObject panel)
    {
        panel = UI.Go(root, name);
        ((RectTransform)panel.transform).At(0, 0, 1, 1);
        UI.Fade(panel.transform, 1300, right: true);
        if (back != null) UI.TopBar(panel.transform, "", back);
        var card = UI.Box(panel.transform, UI.Card, true, "card").At(1, 0, 1, 1, -900, 60, -40, -140);
        UI.Box(card.transform, UI.Accent, false, "accent").At(0, 1, 0, 1, 0, -110, 6, -30);
        UI.Label(card.transform, title, 56, UI.Ink, TextAnchor.MiddleLeft, FontStyle.BoldAndItalic).At(0, 1, 1, 1, 40, -112, -40, -28);
        UI.Box(card.transform, UI.Line, false, "rule").At(0, 1, 1, 1, 40, -132, -40, -131);
        return UI.Rt(card.transform, "content").At(0, 0, 1, 1, 40, 36, -40, -160);
    }

    static VerticalLayoutGroup Stack(Transform content, float gap = 16)
    {
        var col = UI.Column(content, gap).At(0, 0, 1, 1);
        col.childAlignment = TextAnchor.UpperLeft;
        return col;
    }

    void BuildFlow(Transform root)
    {
        // Welcome
        var w = Stack(Page(root, "Welcome", "Iron Corner", null, out welcome)).transform;
        UI.Label(w, "Robot boxing. Teach your fighter by boxing with it. It fights on its own.", 30, UI.Ink).H(84);
        UI.Label(w, "Every clean hit pays the other corner. Testnet only, no real money.", 24, UI.Dim).H(60);
        UI.Rt(w, "gap").H(30);
        UI.Big(w, UI.I("play") + "  Create Your Fighter", true, OpenCreate, 44, pulse: true).H(118);
        UI.Btn(w, "Use the demo corner (Brass Knuckle)  " + UI.I("chevron"), new Color(0, 0, 0, 0), UI.Dim, () => { Remember("botId", "bot_a"); a.botId = "bot_a"; Show(Mode.Home); }, 26).H(64);

        // Create
        var c = Stack(Page(root, "Create", "Create Fighter", () => ShowPanel(welcome), out create)).transform;
        UI.Caption(c, "NAME").H(30);
        createName = UI.Field(c, "2-20 letters, digits or spaces", "", 34).H(84);
        createName.characterLimit = 20;
        UI.Caption(c, "PAINT").H(40);
        var sw = UI.Rt(c, "swatches").H(116);
        for (int i = 0; i < Paints.Length; i++)
        {
            var (id, label, col) = Paints[i];
            float x0 = i / 6f, x1 = (i + 1) / 6f;
            var frame = UI.Box(sw, new Color(0, 0, 0, 0), false, "frame:" + id).At(x0, 0, x1, 1, 4, 0, -4, 0);
            var dot = UI.Btn(frame.transform, "", col, UI.Dark, () => PickPaint(id));
            dot.At(0, 0, 1, 1, 8, 8, -8, -8);
            swatches.Add((id, frame, UI.Label(sw, "", 10, UI.Dim)));
        }
        createErr = UI.Label(c, "", 22, UI.Ink).H(56);
        UI.Big(c, "Create Fighter  " + UI.I("chevron"), true, CreateFighter, 42).H(110);

        // Wallet
        var f = Stack(Page(root, "Fund", "Wallet", null, out fund)).transform;
        UI.Caption(f, "FIGHT BUDGET").H(30);
        fundBal = UI.Label(f, "—", 76, UI.Ink, TextAnchor.MiddleLeft, FontStyle.BoldAndItalic).H(100);
        UI.Label(f, "Your fighter's Hedera testnet wallet pays for every clean hit it takes. The faucet tops it up once a day.", 24, UI.Dim, TextAnchor.UpperLeft).H(70);
        UI.Big(f, UI.I("wallet") + "  Add Test Funds", true, AddFunds, 40).H(108);
        fundMsg = UI.Label(f, "", 22, UI.Ink, TextAnchor.UpperLeft).H(54);
        // Your own 0G account pays for this fighter's AI calls instead of the house (linked on a Privy page in the in-app sheet).
        UI.Box(f, UI.Line, false, "rule").H(2);
        UI.Caption(f, "0G CREDITS").H(34);
        zgLine = UI.Label(f, "", 24, UI.Ink, TextAnchor.UpperLeft).H(64);
        zgBtn = UI.Big(f, "", false, ZgClicked, 30); zgBtn.H(80);

        // Find a fight: stake picker, then the searching panel
        var q = Page(root, "Find", "Find a Fight", null, out find);
        findPick = UI.Go(q, "pick"); ((RectTransform)findPick.transform).At(0, 0, 1, 1);
        var p = findPick.transform;
        UI.Caption(p, "STAKE · YOUR FIGHT BUDGET").At(0, 1, 1, 1, 0, -34, 0, 0);
        int[] stakes = { 10, 25, 50 };
        for (int i = 0; i < 3; i++)
        {
            int cents = stakes[i];
            var chip = UI.Btn(p, UI.Usd(cents), UI.Steel, UI.Ink, () => PickStake(cents), 44);
            chip.At(i / 3f, 1, (i + 1) / 3f, 1, i == 0 ? 0 : 8, -160, i == 2 ? 0 : -8, -50);
            UI.Panel(chip.GetComponent<Image>(), new Color(1, 1, 1, 0.18f));
            chips.Add((cents.ToString(), chip.GetComponent<Image>(), chip.GetComponentInChildren<TMP_Text>()));
        }
        UI.Label(p, "Health is the budget. Each clean hit your bot takes pays the other corner. An empty budget is a K.O.", 22, UI.Dim, TextAnchor.UpperLeft).At(0, 1, 1, 1, 0, -250, 0, -178);
        UI.Big(p, UI.I("play") + "  Find Opponent", true, JoinQueue, 48, pulse: true).At(0, 1, 1, 1, 0, -390, 0, -272);
        findMsg = UI.Label(p, "", 22, UI.Ink, TextAnchor.UpperLeft).At(0, 1, 1, 1, 0, -450, 0, -400);
        findLeague = UI.Label(p, "", 22, UI.Dim).At(0, 0, 1, 0, 0, 84, 0, 120);
        // Players who skipped World ID at first run can connect it later (Verified league).
        findWorldBtn = UI.Big(p, UI.I("shield") + "  Connect World ID", false, () => { Show(Mode.Home); OpenWorld(); }, 26);
        findWorldBtn.At(0, 0, 1, 0, 0, 0, 0, 70);
        findSearch = UI.Go(q, "search"); ((RectTransform)findSearch.transform).At(0, 0, 1, 1);
        findStatus = UI.Label(findSearch.transform, "Searching", 84, UI.Ink, TextAnchor.MiddleLeft, FontStyle.Italic).At(0, 1, 1, 1, 0, -150, 0, -20);
        findSub = UI.Label(findSearch.transform, "", 26, UI.Dim, TextAnchor.UpperLeft).At(0, 1, 1, 1, 0, -300, 0, -170);
        UI.Big(findSearch.transform, UI.I("close") + "  Cancel", false, () => { LeaveQueue(); ShowFind(false); }, 30).At(0, 0, 1, 0, 0, 0, 0, 84);
        findSearch.SetActive(false);

        // Camp
        var kc = Page(root, "Camp", "Fight Camp", null, out camp);
        var k = Stack(UI.Rt(kc, "text").At(0, 0, 1, 1, 0, 140, 0, 0)).transform; // text up top, the button pinned to the bottom
        UI.Label(k, "Train your fighter's own model", 40, UI.Ink, TextAnchor.MiddleLeft, FontStyle.Bold).H(56);
        UI.Label(k, "A LoRA fine-tune on 0G Compute, built from its sparring demos. Each camp is a new adapter version on its Agentic ID.", 22, UI.Dim, TextAnchor.UpperLeft).H(70);
        campBody = UI.Label(k, "", 26, UI.Ink, TextAnchor.UpperLeft).H(170);
        campBtn = UI.Big(kc, UI.I("play") + "  Start Training", true, SendToCamp, 42);
        campBtn.At(0, 0, 1, 0, 0, 0, 0, 110);

        // First-training guide, over the Train screen (right side, under the move count)
        var g = UI.Box(train.transform, UI.Card, true, "guide").At(1, 1, 1, 1, -1140, -620, -620, -260); // clear of the punch cluster
        guide = g.gameObject;
        UI.Caption(g.transform, "FIRST TRAINING ROUND").At(0, 1, 1, 1, 26, -56, -26, -20);
        guideBody = UI.Label(g.transform, "", 24, UI.Ink, TextAnchor.UpperLeft).At(0, 1, 1, 1, 26, -260, -26, -64);
        guideBtn = UI.Big(g.transform, "Continue  " + UI.I("chevron") + "  Wallet", true, GuideContinue, 28);
        guideBtn.At(0, 0, 1, 0, 26, 22, -26, 96);
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
        "Skip for now  ·  Open league", _ => StartCoroutine(WorldDone()), AfterWorld);

    IEnumerator WorldDone()
    {
        bool? ok = null;
        yield return Api.Call("GET", "/me", null, r => ok = (bool?)r["worldVerified"] == true);
        Toast(ok == true ? "Verified human \ue5ca  ·  Verified league" : "That World ID already has a verified fighter · Open league for now");
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
        string Step(bool done, string n, string s) => done ? $"<color=#5CDB85>\ue5ca</color>  <color=#8E8E93>{s}</color>" : $"<color=#FFFFFF>{n}</color>  {s}";
        bool done = guideRound && guideCoach;
        guideBody.text = Step(guideRound, "1", "Spar one round: tap moves on the pads") + "\n" + Step(guideCoach, "2", "Teach one line with the mic or text")
            + (done ? "\n<color=#5CDB85><b>Trained.</b></color> Your fighter can enter the queue." : "\n<size=21><color=#8E8E93>Every move you make is a demo your bot learns from.</color></size>");
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
        yield return Api.Call("GET", $"/bots/{a.botId}", null, r => { fundBal.text = Money(r); ZgShow(r); }, e => fundBal.text = "—");
    }

    void AddFunds()
    {
        fundMsg.text = "<color=#F2A33A>Asking the house faucet…</color>";
        StartCoroutine(Api.Call("POST", $"/bots/{a.botId}/fund", new JObject(), r =>
        {
            fundMsg.text = $"<color=#5CDB85>Test funds sent \ue5ca</color>  <color=#8E8E93>tx {r["txId"]}</color>";
            StartCoroutine(LoadBalance());
        }, e => fundMsg.text = $"<color=#F2A33A>{e}</color>"));
    }

    // ---------------------------------------------------------------- 0G credits (bring your own 0G account)
    TMP_Text zgLine; Button zgBtn; bool zgLinked;

    void ZgShow(JToken bot)
    {
        var z = bot?["zgAccount"];
        zgLinked = (bool?)z?["linked"] == true;
        zgLine.text = zgLinked
            ? $"0G credits: <b>yours</b> (…{z["last4"]})  <color=#8E8E93>· spent ${(double?)z["spentUsd"] ?? 0:0.0000} over {z["calls"] ?? 0} calls</color>"
            : "0G credits: <b>House</b>  <color=#8E8E93>· the house pays for your fighter's AI calls</color>";
        zgBtn.SetLabel(zgLinked ? "Unlink" : UI.I("bolt") + "  Use my 0G credits");
    }

    string ZgRefusal(string e) => Api.LastStatus == 503 ? "Coming soon" : Api.LastStatus is 401 or 403 ? "Create your own fighter to use your 0G credits" : e;

    void ZgClicked()
    {
        zgBtn.interactable = false;
        if (zgLinked)
        {
            StartCoroutine(Api.Call("DELETE", $"/bots/{a.botId}/zg", null,
                r => { zgBtn.interactable = true; Toast("Back on house 0G credits"); StartCoroutine(LoadBalance()); },
                e => { zgBtn.interactable = true; Toast(ZgRefusal(e)); }));
            return;
        }
        StartCoroutine(Api.Call("POST", $"/bots/{a.botId}/zg/link", new JObject(), r =>
        {
            zgBtn.interactable = true;
            var url = (string)r["url"];
            if (string.IsNullOrEmpty(url)) { Toast("Couldn't start the 0G link"); return; }
            OpenSheet(url);
            StartCoroutine(ZgPoll(session));
        }, e => { zgBtn.interactable = true; var m = ZgRefusal(e); Toast(m); if (m == "Coming soon") zgLine.text = "0G credits: <b>House</b>  <color=#8E8E93>· your own 0G credits are coming soon</color>"; }));
    }

    // The sheet closes itself on the ironcorner://zg redirect and tells us nothing; watch the bot until it shows linked.
    IEnumerator ZgPoll(int s)
    {
        for (int i = 0; i < 60 && s == session && !zgLinked; i++)
        {
            yield return new WaitForSecondsRealtime(3f);
            if (s != session) yield break;
            yield return Api.Call("GET", $"/bots/{a.botId}", null, r => { fundBal.text = Money(r); ZgShow(r); });
            if (zgLinked) Toast("Your 0G credits are linked");
        }
    }

    // ---------------------------------------------------------------- find a fight
    void FindClicked()
    {
        if (!aAlive) { if (aNeedsRevive) Revive(); else Toast("Still repairing — a few seconds"); return; }
        if (a.botId == "bot_a") { budgetUsd = 0.5f; StartFight("normal", null); return; } // demo corner: the old house fight
        if (!trained) { Toast("Locked: spar one round and teach one line in Train first"); return; }
        ShowPanel(find); ShowFind(false); PickStake(stakeCents);
        findMsg.text = "";
        findLeague.text = league == "verified" ? "<color=#5CDB85>\uead5 Verified league</color> <color=#8E8E93>· verified humans only</color>" : "<color=#8E8E93>\uead5 Open league</color>";
        findWorldBtn.gameObject.SetActive(league != "verified");
    }

    void PickStake(int cents)
    {
        stakeCents = cents;
        foreach (var (id, img, label) in chips) { bool on = id == cents.ToString(); img.color = on ? UI.Accent : new Color(0, 0, 0, 0); label.color = on ? UI.Dark : UI.Ink; }
    }

    void ShowFind(bool search) { findPick.SetActive(!search); findSearch.SetActive(search); }

    void JoinQueue()
    {
        findMsg.text = "";
        StartCoroutine(Api.Call("POST", "/queue", new JObject { ["botId"] = a.botId, ["stakeCents"] = stakeCents }, r =>
        {
            searching = true; ShowFind(true);
            findStatus.text = "Searching"; findSub.text = "";
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
                findStatus.text = "Searching" + new string('.', 1 + waited % 3);
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
        fightTitle.text = $"<color=#5CDB85>LIVE</color>  ·  {UI.Usd(start)} stake  ·  <color=#8E8E93>{id}</color>" + (seedHash != null ? $"\n<size=18>seed hash {UI.Short(seedHash, 8)}</size>" : "");
        fightTranscript.text = "<color=#FFFFFF><b>Autonomous</b></color>  ·  the server runs the fight, both bots think on 0G with what they learned in training";
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
        if (result is "blocked" or "dodged") { def.holdBlock = false; def.SetQueue(new[] { result == "blocked" ? "block" : UnityEngine.Random.value < 0.5f ? "dodge_l" : "dodge_r" }, front: true); }
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
        koTitle.color = UI.Amber;
        koTitle.text = w == null ? "DRAW" : "TIME"; koWin.text = w == null ? "Even on budget" : $"{w.displayName} Wins";
        koSub.text = w == null ? "Both budgets equal after 90 s." : $"{w.displayName} wins on time: more fight budget left after 90 s.";
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
        string result = w == null ? "" : w == a.botId ? "<color=#6BD694><b>You win.</b></color>  " : "<color=#EE5C52><b>You lose.</b></color>  ";
        koSub.text = result + string.Join("  ·  ", new[] { rating, record }.Where(x => x != ""));
        string fair = seed != null
            ? $"Provably fair: seed {UI.Short(seed, 10)}, hash {UI.Short(seedHash, 10)}  " + (SeedMatches(seed, seedHash) ? "<color=#5CDB85>\ue5ca hash checked on this device</color>" : "<color=#F2A33A>hash not checked</color>")
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
            // progress is a number while queued and a stage name ("Init", "Training") once 0G picks the task up
            var pt = st?["progress"]; bool pct = pt != null && pt.Type is JTokenType.Float or JTokenType.Integer;
            double p = pct ? (double)pt : 0; if (pct && p <= 1) p *= 100;
            string stage = pct ? $"  {p:0}%" : pt?.Type == JTokenType.String ? $"  · {pt}" : "";
            var lines = new List<string>
            {
                $"<color=#F2F2F5>Demos: <b>{demos}</b>/40 needed</color>" + (demos < 40 ? "  <color=#F2A33A>· spar more rounds first</color>" : "  <color=#5CDB85>\ue5ca</color>"),
                err != null ? $"<color=#F2523F>{err}</color>" : $"<color=#F2F2F5>Status: <b>{status}</b></color>" + (running ? stage : "")
                    + (st?["version"] != null && st["version"].Type != JTokenType.Null ? $"  ·  adapter v{st["version"]}" : "")
                    + (st?["loss"] != null && st["loss"].Type != JTokenType.Null ? $"  ·  loss {(double)st["loss"]:0.000}" : "")
                    + (st?["adapterRoot"]?.Type == JTokenType.String ? $"\n<size=21>root {UI.Short((string)st["adapterRoot"], 10)}</size>" : ""),
                "<size=21><color=#8E8E93>One camp per bot per day.</color></size>",
            };
            campBody.text = string.Join("\n", lines);
            campBtn.interactable = demos >= 40 && !running && Time.unscaledTime >= campCoolUntil;
            campBtn.SetLabel(running ? "In camp…" : "Start Training  \ue037");
            yield return new WaitForSecondsRealtime(running ? 3f : 10f);
        }
    }

    // One tap per few seconds (the server allows 3 camp requests a minute), and refusals in plain words.
    void SendToCamp()
    {
        campBtn.interactable = false; campCoolUntil = Time.unscaledTime + 4f;
        StartCoroutine(Api.Call("POST", $"/bots/{a.botId}/camp", new JObject(),
            r => { Toast("Off to camp: fine-tuning on 0G"); OpenCamp(); },
            e => { Toast(CampRefusal(e)); StartCoroutine(ReenableCamp(session)); }));
    }

    float campCoolUntil;
    IEnumerator ReenableCamp(int s) { yield return new WaitForSecondsRealtime(4f); if (s == session) campBtn.interactable = true; }

    string CampRefusal(string e)
    {
        var body = Api.LastBody;
        string reason = (string)body?["reason"] ?? (string)body?["error"] ?? e ?? "";
        if (Api.LastStatus == 429) return "Slow down, try again in a minute";
        if (reason.StartsWith("camp_budget")) return "Camp is full for today, try tomorrow";
        if (reason.StartsWith("camp_today")) return $"{a.displayName} already went to camp today";
        if (reason.StartsWith("camp_busy")) return "Another fighter is in camp, try in a few minutes";
        if (reason.StartsWith("not_enough_demos")) return $"Spar more first: {body?["have"] ?? "?"}/{body?["need"] ?? 40} moves";
        return "Camp: " + e;
    }

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
