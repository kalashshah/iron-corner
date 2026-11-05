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
    class Fight { public string a, b; public Dictionary<string, int> budgets = new(); public JArray payments = new(); public JArray decisions = new(); public string winner; public int hitNo; public bool ended; }
    class Approval { public float at; public string status = "pending"; public string action; }

    static readonly Dictionary<string, JObject> bots = new()
    {
        ["bot_a"] = Bot("bot_a", "Brass Knuckle", "0.0.5001001", 5.00, 1),
        ["bot_b"] = Bot("bot_b", "Rustbucket", "0.0.5001002", 5.00, 1),
    };
    static readonly Dictionary<string, JArray> phrases = new() { ["bot_a"] = new JArray(), ["bot_b"] = new JArray() };
    static readonly Dictionary<string, JArray> counters = new() { ["bot_a"] = new JArray(), ["bot_b"] = new JArray() };
    static readonly Dictionary<string, JArray> combos = new() { ["bot_a"] = new JArray(), ["bot_b"] = new JArray() };
    static readonly Dictionary<string, JObject> styles = new() { ["bot_a"] = new JObject { ["aggression"] = 0.6, ["guard"] = 0.5 }, ["bot_b"] = new JObject { ["aggression"] = 0.4, ["guard"] = 0.6 } };
    // Daily goal, same rules as the backend: 3 drills a UTC day, consecutive days grow the streak.
    const int DailyGoal = 3;
    static string dailyDay, goalDay; static int drills, streak;
    static readonly Dictionary<string, JArray> history = new() { ["bot_a"] = new JArray(), ["bot_b"] = new JArray() };
    static readonly Dictionary<string, Fight> fights = new();
    // Shadow-sparring mirror of backend/src/demos.ts: same shapes, simpler recall (match on the opponent's last move).
    class Demo { public string a, outcome, o1, m1, round; }
    static readonly List<Demo> demos = new();
    const int EnergyMax = 5;
    static int xpTotal, roundsToday, roundCount; static string energyDay, roundId; static float roundStart;
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
                if (seg.Length == 1) return new JArray(bots.Values.Select(b => { var c = (JObject)b.DeepClone(); if ((string)b["botId"] == "bot_a") c["daily"] = Daily(false); c["progress"] = Progress((string)b["botId"]); return c; }));
                if (seg.Length == 3 && seg[2] == "revive") { Get(bots, seg[1])["alive"] = true; return new JObject { ["botId"] = seg[1], ["alive"] = true, ["mock"] = true }; }
                var bot = (JObject)Get(bots, seg[1]).DeepClone();
                bot["brain"] = new JObject
                {
                    ["botId"] = seg[1], ["version"] = bot["brainVersion"], ["phrases"] = phrases[seg[1]].DeepClone(), ["counters"] = counters[seg[1]].DeepClone(),
                    ["combos"] = combos[seg[1]].DeepClone(), ["lessons"] = new JArray("offline: no lessons yet"), ["style"] = styles[seg[1]].DeepClone(),
                };
                if (seg[1] == "bot_a") bot["daily"] = Daily(false);
                bot["progress"] = Progress(seg[1]);
                bot["brainHistory"] = history[seg[1]].DeepClone();
                return bot;

            case "train":
                var botId = seg[1];
                switch (seg[2])
                {
                    case "coach": return Coach(botId, (string)body["text"] ?? "when he throws a left hook, slip right and uppercut");
                    case "coach-text": return Coach(botId, (string)body["text"] ?? "");
                    case "spar": return PlanFor(botId, body["state"], 0.7f);
                    case "demos": return Demos(botId, body);
                    case "save":
                        var b = Get(bots, botId);
                        b["brainVersion"] = (int)b["brainVersion"] + 1;
                        b["brainRoot"] = Hex(64);
                        var pg = Progress(botId);
                        var row = new JObject { ["version"] = b["brainVersion"], ["root"] = b["brainRoot"], ["txHash"] = Hex(64), ["at"] = DateTime.UtcNow.ToString("o"),
                            ["level"] = pg["level"], ["stats"] = pg["stats"], ["demos"] = pg["demos"] };
                        history[botId].Add(row); // oldest first, like the backend
                        return new JObject { ["version"] = row["version"], ["root"] = row["root"], ["txHash"] = row["txHash"], ["level"] = row["level"], ["stats"] = row["stats"], ["demos"] = row["demos"], ["mock"] = true };
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
                        var plan = PlanFor((string)body["botId"], body["state"], (string)body["botId"] == "bot_a" ? 0.7f : 0.55f);
                        fight.decisions.Add(new JObject { ["botId"] = body["botId"], ["requestId"] = plan["requestId"], ["stance"] = plan["stance"], ["used"] = plan["used"], ["at"] = DateTime.UtcNow.ToString("o") });
                        return plan;
                    case "hit": return Hit(fight, body);
                    case "coach":
                    case "coach-text":
                        throw new Exception("fights are autonomous; coach in training");
                    case "end":
                        if (!fight.ended) { fight.ended = true; xpTotal += fight.winner == "bot_a" ? 25 : 12; }
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

    // Move words in the order they're said ("left hook" before "hook").
    static readonly (string word, string move)[] Words = new (string, string)[]
    {
        ("left hook", "hook_l"), ("right hook", "hook_r"), ("hook", "hook_l"), ("uppercut", "uppercut"), ("upper", "uppercut"), ("jab", "jab"),
        ("cross", "cross"), ("straight right", "cross"), ("body", "body"), ("block", "block"), ("guard up", "block"), ("cover", "block"),
        ("slip left", "dodge_l"), ("dodge left", "dodge_l"), ("slip right", "dodge_r"), ("dodge right", "dodge_r"), ("slip", "dodge_r"), ("dodge", "dodge_l"),
        ("step in", "step_in"), ("step back", "step_back"), ("back off", "step_back"),
    }.Concat(Fighter.Moves.Select(m => (m.Replace('_', ' '), m))).OrderByDescending(w => w.Item1.Length).ToArray();

    static List<string> MovesIn(string text)
    {
        var s = " " + new string(text.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray()) + " ";
        var found = new List<(int at, string move)>();
        foreach (var (w, m) in Words)
            for (int i; (i = s.IndexOf(" " + w + " ", StringComparison.Ordinal)) >= 0;)
            {
                found.Add((i, m));
                s = s.Substring(0, i + 1) + new string('#', w.Length) + s.Substring(i + 1 + w.Length);
            }
        return found.OrderBy(f => f.at).Select(f => f.move).ToList();
    }

    static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd");
    static JObject Daily(bool justDone)
    {
        string yest = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
        int d = dailyDay == Today ? drills : 0;
        return new JObject { ["goal"] = DailyGoal, ["drills"] = Math.Min(d, DailyGoal), ["done"] = d >= DailyGoal, ["streak"] = goalDay == Today || goalDay == yest ? streak : 0, ["justDone"] = justDone };
    }
    static bool AddDrill()
    {
        if (dailyDay != Today) { dailyDay = Today; drills = 0; }
        if (++drills != DailyGoal) return false;
        streak = goalDay == DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd") ? streak + 1 : 1; goalDay = Today;
        return true;
    }

    // Same response shape as POST /train/:id/coach-text: command word, or one learned rule with a readable diff.
    static JObject Coach(string botId, string text)
    {
        var low = text.ToLowerInvariant().Trim();
        var ms = MovesIn(low);
        bool has(params string[] k) => k.Any(x => low.Contains(x));
        JObject learned = null; string command = null, diff = null;
        string L(IEnumerable<string> m) => string.Join(", ", m);
        if ((has("when ", "if ", "every time")) && ms.Count >= 2 && Fighter.IsAttack(ms[0]))
        {
            var list = counters[botId];
            var old = list.FirstOrDefault(c => (string)c["when"] == ms[0]);
            var c = new JObject { ["when"] = ms[0], ["moves"] = new JArray(ms.Skip(1).Take(4)), ["said"] = text };
            if (old != null) old.Replace(c); else list.Add(c);
            int idx = list.ToList().FindIndex(x => (string)x["when"] == ms[0]) + 1;
            diff = $"Learned counter: {ms[0]} → {L(ms.Skip(1).Take(4))}";
            learned = new JObject { ["kind"] = "counter", ["id"] = $"counter#{idx}", ["when"] = ms[0], ["moves"] = c["moves"].DeepClone() };
        }
        else if (has("open", "start", "finish", "put him away") && ms.Count > 0)
        {
            string use = has("finish", "put him away") ? "finisher" : "opener";
            combos[botId].Add(new JObject { ["use"] = use, ["name"] = string.Join(" ", ms), ["moves"] = new JArray(ms.Take(4)), ["said"] = text });
            diff = $"Learned {use}: {L(ms.Take(4))}";
            learned = new JObject { ["kind"] = "combo", ["id"] = $"combo#{combos[botId].Count}", ["use"] = use, ["moves"] = new JArray(ms.Take(4)) };
        }
        else if (has("aggress", "brave", "attack more", "careful", "patient", "defen"))
        {
            var st = styles[botId];
            bool calm = has("careful", "patient", "defen");
            double a0 = (double)st["aggression"], g0 = (double)st["guard"];
            st["aggression"] = Math.Round(Mathf.Clamp01((float)(a0 + (calm ? -0.1 : 0.15))), 2);
            st["guard"] = Math.Round(Mathf.Clamp01((float)(g0 + (calm ? 0.15 : -0.05))), 2);
            diff = $"Style: aggression {a0:0.00} → {(double)st["aggression"]:0.00}, guard {g0:0.00} → {(double)st["guard"]:0.00}";
            learned = new JObject { ["kind"] = "style", ["id"] = "style", ["moves"] = new JArray() };
        }
        else if (Tactics.FirstOrDefault(x => low.Contains(x.key)) is var t && t.moves != null && !(ms.Count == 1 && Words.Any(w => w.word == low)))
        {
            var list = phrases[botId];
            var known = list.FirstOrDefault(p => (string)p["say"] == low);
            if (known == null) list.Add(new JObject { ["say"] = low, ["means"] = t.means, ["moves"] = new JArray(t.moves) });
            diff = (known == null ? "Learned phrase" : "Drilled phrase") + $": “{low}” = {L(t.moves)}";
            learned = new JObject { ["kind"] = "phrase", ["id"] = $"phrase#{list.ToList().FindIndex(p => (string)p["say"] == low) + 1}", ["moves"] = new JArray(t.moves) };
        }
        else command = ms.FirstOrDefault();
        if (learned != null) { learned["say"] = text; learned["means"] = diff; }
        bool goal = (command != null || learned != null) && AddDrill();
        return new JObject { ["text"] = text, ["requestId"] = "offline-" + Hex(8).Substring(2), ["learned"] = learned, ["command"] = command, ["diff"] = diff, ["daily"] = Daily(goal), ["mock"] = true };
    }

    // Taught rules first (counter → finisher → opener), else the heuristic. Mirrors the backend mock planner.
    static JObject PlanFor(string botId, JToken state, float aggression)
    {
        string last = (state?["oppLastMoves"] as JArray)?.LastOrDefault()?.ToString(), hitBy = (string)state?["lastHitBy"];
        float t = (float?)state?["t"] ?? 99f; int opp = (int?)state?["oppBudget"] ?? 50;
        JObject Rule(string id, string text, string stance, JToken moves, string say) => new()
        {
            ["stance"] = stance, ["combo"] = moves.DeepClone(), ["trigger"] = "now", ["say"] = say, ["used"] = id, ["usedText"] = text,
            ["requestId"] = "offline-" + Hex(8).Substring(2), ["teeVerified"] = false, ["mock"] = true,
        };
        string Ms(JToken m) => string.Join(", ", m.Select(x => (string)x));
        var cs = counters.TryGetValue(botId ?? "", out var cl) ? cl : new JArray();
        for (int i = 0; i < cs.Count; i++)
            if ((string)cs[i]["when"] == last || (string)cs[i]["when"] == hitBy)
                return Rule($"counter#{i + 1}", $"counter: {cs[i]["when"]} → {Ms(cs[i]["moves"])}", "counter", cs[i]["moves"], $"Saw the {cs[i]["when"]}. Drilled this.");
        var co = combos.TryGetValue(botId ?? "", out var col) ? col : new JArray();
        for (int i = 0; i < co.Count; i++)
        {
            string use = (string)co[i]["use"];
            if ((use == "finisher" && opp <= 12) || (use == "opener" && t < 4))
                return Rule($"combo#{i + 1}", $"{use}: {Ms(co[i]["moves"])}", "aggressive", co[i]["moves"], use == "finisher" ? "Finish it like we practised." : "Opener from training.");
        }
        if (botId == "bot_a" && last != null)
        {
            var best = demos.Where(d => d.o1 == last && Worked(d.a, d.outcome)).GroupBy(d => d.a).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (best != null)
            {
                int n = demos.Count(d => d.o1 == last && d.a == best.Key);
                string follow = Fighter.IsAttack(best.Key) ? null : demos.Where(d => d.m1 == best.Key && d.outcome == "landed").GroupBy(d => d.a).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
                var mv = follow != null ? new JArray(best.Key, follow) : new JArray(best.Key);
                return Rule("demo", $"Like your coach: {UI.Nice(best.Key)}{(follow != null ? " then " + UI.Nice(follow) : "")} (worked {best.Count()}/{n})", "counter", mv, "Doing what coach showed me.");
            }
        }
        return Plan(state, aggression);
    }

    static bool Worked(string a, string o) => Fighter.IsAttack(a) ? o == "landed" : o is "blocked" or "dodged";
    static int Pct(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 100);

    static JObject Stats(string botId)
    {
        var mine = botId == "bot_a" ? demos : new List<Demo>();
        var atk = mine.Where(d => Fighter.IsAttack(d.a)).ToList();
        var def = mine.Where(d => !Fighter.IsAttack(d.a) && Fighter.IsAttack(d.o1)).ToList();
        var cs = counters.TryGetValue(botId, out var cl) ? cl : new JArray();
        double reflex = Fighter.Moves.Where(Fighter.IsAttack).Average(x => cs.Any(c => (string)c["when"] == x) ? 1 : Math.Min(1, def.Count(d => d.o1 == x && Worked(d.a, d.outcome)) / 3.0));
        var decs = fights.Values.SelectMany(f => f.decisions).Where(d => (string)d["botId"] == botId).ToList();
        return new JObject
        {
            ["reflexes"] = Pct(reflex), ["accuracy"] = Pct(atk.Count(d => d.outcome == "landed") / (atk.Count + 5.0)),
            ["defense"] = Pct(def.Count(d => Worked(d.a, d.outcome)) / (def.Count + 5.0)),
            ["ring_iq"] = Pct(decs.Count(d => d["used"] != null && d["used"].Type == JTokenType.String) / (decs.Count + 5.0) * 0.5),
            ["experience"] = Pct(Math.Log(1 + mine.Count) / Math.Log(2001)),
        };
    }

    static JObject Progress(string botId)
    {
        if (energyDay != Today) { energyDay = Today; roundsToday = 0; }
        int xp = botId == "bot_a" ? xpTotal : 0, L = (int)Math.Floor((1 + Math.Sqrt(1 + 8 * xp / 100.0)) / 2), start = 100 * L * (L - 1) / 2;
        return new JObject
        {
            ["level"] = L, ["xp"] = xp, ["into"] = xp - start, ["next"] = 100 * L,
            ["energy"] = new JObject { ["left"] = Math.Max(0, EnergyMax - roundsToday), ["max"] = EnergyMax, ["resetsAt"] = DateTime.UtcNow.Date.AddDays(1).ToString("o") },
            ["stats"] = Stats(botId), ["demos"] = botId == "bot_a" ? demos.Count : 0, ["mock"] = true,
        };
    }

    // POST /train/:id/demos: same rules as the backend (15 s + 3 moves for XP, 5 rounds of energy a UTC day).
    static JObject Demos(string botId, JToken body)
    {
        string round = (string)body["round"];
        if (round != null && round != roundId) { roundId = round; roundStart = Time.realtimeSinceStartup; roundCount = 0; }
        int stored = 0;
        foreach (var d in body["demos"] as JArray ?? new JArray())
        {
            string a = (string)d["action"], o = (string)d["outcome"];
            if (!Fighter.Moves.Contains(a) || o == null) continue;
            demos.Add(new Demo { a = a, outcome = o, round = round,
                o1 = (string)(d["state"]?["oppLastMoves"] as JArray)?.LastOrDefault(), m1 = (string)(d["state"]?["selfLastMoves"] as JArray)?.LastOrDefault() });
            stored++;
        }
        if (demos.Count > 2000) demos.RemoveRange(0, demos.Count - 2000);
        roundCount += stored;
        var res = new JObject { ["stored"] = stored, ["total"] = demos.Count, ["mock"] = true };
        Progress(botId); // rolls the energy day
        if ((bool?)body["roundEnd"] == true && round != null && round == roundId)
        {
            float secs = Time.realtimeSinceStartup - roundStart; int xp = 0; string reason = null;
            if (roundCount < 3 || secs < 15) reason = "round too short for XP (15 s and 3 moves minimum)";
            else if (roundsToday >= EnergyMax) reason = "out of energy: still learning, XP is back at 00:00 UTC";
            else { xp = (int)Math.Round((10 + Math.Min(20, roundCount)) * new[] { 1, 0.85, 0.7, 0.6, 0.5 }[roundsToday]); roundsToday++; xpTotal += xp; AddDrill(); }
            var mine = demos.Where(d => d.round == round && !Fighter.IsAttack(d.a) && Fighter.IsAttack(d.o1)).ToList();
            string top = mine.GroupBy(d => d.o1).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key, hl = null;
            if (top != null) { var all = demos.Where(d => d.o1 == top && !Fighter.IsAttack(d.a)).ToList(); hl = $"{UI.Nice(top)} defense {Pct(all.Count(d => Worked(d.a, d.outcome)) / (double)all.Count)}%"; }
            res["round"] = new JObject { ["moves"] = roundCount, ["seconds"] = (int)secs, ["xpGained"] = xp, ["reason"] = reason, ["highlight"] = hl };
            roundId = null;
        }
        var p = Progress(botId);
        res["energy"] = p["energy"]; res["stats"] = p["stats"];
        res["xp"] = new JObject { ["level"] = p["level"], ["xp"] = p["xp"], ["into"] = p["into"], ["next"] = p["next"] };
        return res;
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
