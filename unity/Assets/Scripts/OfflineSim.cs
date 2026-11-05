using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Random = UnityEngine.Random;

// In-game stand-in for the backend so every screen works without a server. Everything it returns is
// marked "mock": true and the UI shows an OFFLINE badge. No money moves, nothing reaches 0G/Hedera/World.
public static class OfflineSim
{
    class Fight { public string a, b; public Dictionary<string, int> budgets = new(); public JArray payments = new(); public JArray decisions = new(); public string winner; public int hitNo; }
    class Approval { public float at; public string status = "pending"; public string action; }

    static readonly Dictionary<string, JObject> bots = new()
    {
        ["bot_a"] = Bot("bot_a", "Brass Knuckle", "0.0.5001001", 5.00, 1),
        ["bot_b"] = Bot("bot_b", "Rustbucket", "0.0.5001002", 5.00, 1),
    };
    static readonly Dictionary<string, JArray> phrases = new() { ["bot_a"] = new JArray(), ["bot_b"] = new JArray() };
    static readonly Dictionary<string, JArray> history = new() { ["bot_a"] = new JArray(), ["bot_b"] = new JArray() };
    static readonly Dictionary<string, Fight> fights = new();
    static readonly Dictionary<string, Approval> approvals = new();
    static int ids;

    static readonly string[] Lines =
    {
        "Testing his guard.", "He drops the left after a hook.", "Body first, head later.", "Keep it tight.",
        "He's slow on the counter.", "Cover up, reset.", "Light him up.", "Make him pay for that.",
    };

    static JObject Bot(string id, string name, string acct, double usdc, int ver) => new()
    {
        ["botId"] = id, ["name"] = name, ["accountId"] = acct, ["evmAddress"] = "0x0000000000000000000000000000000000000000",
        ["usdc"] = usdc, ["brainVersion"] = ver, ["brainRoot"] = Hex(64), ["alive"] = true, ["mock"] = true,
    };

    static string Hex(int n) { var c = "0123456789abcdef"; var s = new char[n]; for (int i = 0; i < n; i++) s[i] = c[Random.Range(0, 16)]; return "0x" + new string(s); }
    static string NewId(string p) => $"{p}_{++ids:000}";

    public static JToken Handle(string method, string path, JToken body)
    {
        var seg = path.Trim('/').Split('/');
        body ??= new JObject();
        switch (seg[0])
        {
            case "bots":
                if (seg.Length == 1) return new JArray(bots.Values.Select(b => b.DeepClone()));
                if (seg.Length == 3 && seg[2] == "revive") { Get(bots, seg[1])["alive"] = true; return new JObject { ["botId"] = seg[1], ["alive"] = true, ["mock"] = true }; }
                var bot = (JObject)Get(bots, seg[1]).DeepClone();
                bot["brain"] = new JObject { ["botId"] = seg[1], ["version"] = bot["brainVersion"], ["phrases"] = phrases[seg[1]].DeepClone(), ["lessons"] = new JArray("offline: no lessons yet"), ["style"] = new JObject { ["aggression"] = 0.6, ["guard"] = 0.5 } };
                bot["brainHistory"] = history[seg[1]].DeepClone();
                return bot;

            case "train":
                var botId = seg[1];
                switch (seg[2])
                {
                    case "coach": return Coach(botId, "light him up", true);
                    case "coach-text": return Coach(botId, (string)body["text"] ?? "", true);
                    case "spar": return Plan(body["state"], 0.7f);
                    case "save":
                        var b = Get(bots, botId);
                        b["brainVersion"] = (int)b["brainVersion"] + 1;
                        b["brainRoot"] = Hex(64);
                        var row = new JObject { ["version"] = b["brainVersion"], ["root"] = b["brainRoot"], ["txHash"] = Hex(64), ["at"] = DateTime.UtcNow.ToString("o") };
                        history[botId].Insert(0, row);
                        return new JObject { ["version"] = row["version"], ["root"] = row["root"], ["txHash"] = row["txHash"], ["mock"] = true };
                }
                break;

            case "fights":
                if (seg.Length == 1)
                {
                    var f = new Fight { a = (string)body["a"] ?? "bot_a", b = (string)body["b"] ?? "bot_b" };
                    if ((bool?)Get(bots, f.a)["alive"] == false) throw new Exception("knocked out: revive first");
                    int cents = Mathf.RoundToInt((float)(body["budgetUsd"] ?? 0.5) * 100);
                    f.budgets[f.a] = cents; f.budgets[f.b] = cents;
                    if ((string)body["stakes"] == "high")
                    {
                        var ap = Get(approvals, (string)body["approvalId"] ?? "");
                        if (ap.status != "approved") throw new Exception("approval not approved");
                        ap.status = "used";
                    }
                    var id = NewId("fight");
                    fights[id] = f;
                    return new JObject { ["fightId"] = id, ["a"] = f.a, ["b"] = f.b, ["budgets"] = JObject.FromObject(f.budgets), ["mock"] = true };
                }
                var fight = Get(fights, seg[1]);
                if (seg.Length == 2) return FightView(fight);
                switch (seg[2])
                {
                    case "decide":
                        var plan = Plan(body["state"], (string)body["botId"] == "bot_a" ? 0.7f : 0.55f);
                        fight.decisions.Add(new JObject { ["botId"] = body["botId"], ["requestId"] = plan["requestId"], ["stance"] = plan["stance"], ["at"] = DateTime.UtcNow.ToString("o") });
                        return plan;
                    case "hit": return Hit(fight, body);
                    case "coach":
                    case "coach-text":
                        var c = Coach((string)body["botId"] ?? "bot_a", (string)body["text"] ?? "light him up", false);
                        return new JObject { ["text"] = c["text"], ["command"] = c["command"], ["requestId"] = c["requestId"], ["mock"] = true };
                    case "end":
                        return new JObject { ["winner"] = fight.winner, ["lessons"] = new JObject { [fight.a] = new JArray("offline: lessons need the 0G backend"), [fight.b] = new JArray() }, ["mock"] = true };
                }
                break;

            case "approvals":
                if (seg.Length == 1)
                {
                    var id = NewId("appr");
                    approvals[id] = new Approval { at = Time.realtimeSinceStartup, action = (string)body["action"] };
                    return new JObject { ["approvalId"] = id, ["qrUri"] = "offline://" + id, ["userCode"] = "OFFL-" + Random.Range(1000, 9999), ["expiresAt"] = DateTime.UtcNow.AddMinutes(5).ToString("o"), ["mock"] = true };
                }
                var a = Get(approvals, seg[1]);
                if (seg.Length > 2 && seg[2] == "cancel") { a.status = "cancelled"; return new JObject { ["status"] = a.status }; }
                // ponytail: offline approval auto-approves after 6 s so the flow can be clicked through without a phone.
                if (a.status == "pending" && Time.realtimeSinceStartup - a.at > 6f) a.status = "approved";
                return new JObject { ["status"] = a.status, ["mock"] = true };
        }
        throw new Exception($"offline: no route for {method} {path}");
    }

    static T Get<T>(Dictionary<string, T> d, string k) => d.TryGetValue(k, out var v) ? v : throw new Exception("not found: " + k);

    static JObject FightView(Fight f)
    {
        foreach (JObject p in f.payments)
            if ((string)p["status"] == "pending" && Time.realtimeSinceStartup - (float)p["_at"] > 1.2f) p["status"] = "settled";
        var pays = new JArray(f.payments.Select(p => { var c = (JObject)p.DeepClone(); c.Remove("_at"); return c; }));
        return new JObject
        {
            ["status"] = f.winner != null ? "over" : "live", ["budgets"] = JObject.FromObject(f.budgets), ["winner"] = f.winner,
            ["payments"] = pays, ["ledgerTopic"] = "offline", ["decisions"] = f.decisions, ["mock"] = true,
        };
    }

    static JObject Hit(Fight f, JToken body)
    {
        string att = (string)body["attacker"], def = (string)body["defender"], move = (string)body["move"], result = (string)body["result"];
        string ko = null;
        if (f.winner == null && result == "clean")
        {
            int dmg = Fighter.Damage(move);
            if (f.budgets[def] < dmg) { ko = def; f.winner = att; if (def == "bot_a") bots[def]["alive"] = false; } // house bot_b revives itself
            else
            {
                f.budgets[def] -= dmg;
                f.payments.Add(new JObject
                {
                    ["hitNo"] = ++f.hitNo, ["from"] = def, ["to"] = att, ["cents"] = dmg, ["status"] = "pending",
                    ["txId"] = $"offline-{f.hitNo:000}", ["hashscan"] = null, ["_at"] = Time.realtimeSinceStartup,
                });
            }
        }
        return new JObject { ["budgets"] = JObject.FromObject(f.budgets), ["ko"] = ko, ["mock"] = true };
    }

    // Phrase -> tactic table. Unknown text falls back to any move names it contains.
    static readonly (string key, string means, string[] moves)[] Tactics =
    {
        ("light", "fast jab combos", new[] { "jab", "jab", "cross" }),
        ("body", "go to the body", new[] { "body", "body", "hook_l" }),
        ("upper", "uppercut finisher", new[] { "jab", "uppercut" }),
        ("hook", "hooks both sides", new[] { "hook_l", "hook_r" }),
        ("cover", "cover up", new[] { "block", "block" }),
        ("guard", "cover up", new[] { "block", "block" }),
        ("slip", "slip and counter", new[] { "dodge_l", "cross" }),
        ("dodge", "slip and counter", new[] { "dodge_r", "hook_l" }),
        ("pressure", "walk him down", new[] { "step_in", "jab", "cross" }),
        ("back", "reset distance", new[] { "step_back" }),
    };

    static JObject Coach(string botId, string text, bool learn)
    {
        var low = text.ToLowerInvariant();
        var t = Tactics.FirstOrDefault(x => low.Contains(x.key));
        JObject learned = null; string command = null;
        if (t.moves != null)
        {
            command = t.moves[0];
            bool known = phrases[botId].Any(p => (string)p["say"] == low);
            if (learn && !known)
            {
                learned = new JObject { ["say"] = low, ["means"] = t.means, ["moves"] = new JArray(t.moves) };
                phrases[botId].Add(learned.DeepClone());
            }
        }
        else command = Fighter.Moves.FirstOrDefault(m => low.Contains(m.Replace('_', ' ')) || low.Contains(m));
        return new JObject { ["text"] = text, ["requestId"] = "offline-" + Hex(8).Substring(2), ["learned"] = learned, ["command"] = command, ["mock"] = true };
    }

    // Heuristic plan with the same shape as the 0G decision. Also drives the sparring dummy.
    public static JObject Plan(JToken state, float aggression)
    {
        float dist = (float?)state?["distance"] ?? 1.2f;
        int self = (int?)state?["selfBudget"] ?? 50;
        string stance; JArray combo; string trigger = "now";
        if (self < 10 || Random.value > aggression) { stance = "defensive"; combo = new JArray(Pick(new[] { "block", "dodge_l", "dodge_r" }), "jab"); }
        else if (Random.value < 0.3f) { stance = "counter"; combo = new JArray("block", Pick(new[] { "cross", "hook_r", "uppercut" })); trigger = "after_block"; }
        else
        {
            stance = "aggressive";
            var opts = new[] { new[] { "jab", "jab", "cross" }, new[] { "jab", "hook_l" }, new[] { "body", "hook_r" }, new[] { "cross", "uppercut" }, new[] { "jab", "body", "hook_l" } };
            combo = new JArray(opts[Random.Range(0, opts.Length)]);
        }
        if (dist > 1.6f && stance != "defensive") combo.Insert(0, "step_in");
        return new JObject
        {
            ["stance"] = stance, ["combo"] = combo, ["trigger"] = trigger, ["say"] = Lines[Random.Range(0, Lines.Length)],
            ["requestId"] = "offline-" + Hex(8).Substring(2), ["teeVerified"] = false, ["mock"] = true,
        };
    }

    static string Pick(string[] a) => a[Random.Range(0, a.Length)];

    // Not a real QR: a labelled placeholder pattern so the modal layout can be checked offline.
    public static Texture2D FakeQr(string seed)
    {
        const int n = 29;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        var rng = new System.Random(seed.GetHashCode());
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            bool finder = (x < 7 && y < 7) || (x >= n - 7 && y < 7) || (x < 7 && y >= n - 7);
            bool on = finder ? (Mathf.Max(Mathf.Abs((x % (n - 7)) - 3), Mathf.Abs((y % (n - 7)) - 3)) is 3 or <= 1) : rng.NextDouble() < 0.45;
            tex.SetPixel(x, y, on ? Color.black : Color.white);
        }
        tex.Apply();
        return tex;
    }
}
