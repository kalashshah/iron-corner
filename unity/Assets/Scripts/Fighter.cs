using System;
using System.Collections.Generic;
using UnityEngine;

// Plays a queue of spec moves on a Mixamo humanoid. Never decides strategy: plans come from the backend
// (or OfflineSim). Resolves its own hits during each attack's active window and reports them via OnHit.
public class Fighter : MonoBehaviour
{
    public string botId = "bot_a";
    public string displayName = "Brass Knuckle";
    public Fighter opp;
    public Animator anim;

    [NonSerialized] public int budget, startBudget;
    [NonSerialized] public float guard = 1f;
    [NonSerialized] public bool active;      // false = idle on the spot (home screen)
    [NonSerialized] public bool holdBlock;   // safe_stop: cover up and hold
    [NonSerialized] public bool down, won;
    [NonSerialized] public string lastHitBy;
    public readonly List<string> lastMoves = new();
    public event Action<Fighter, string, string> OnHit; // attacker, move, result

    public static readonly string[] Moves = { "jab", "cross", "hook_l", "hook_r", "uppercut", "body", "block", "dodge_l", "dodge_r", "step_in", "step_back" };

    // spec move -> animator state (states are named after the clip files; dodge_left is a mirrored dodge_right)
    static readonly Dictionary<string, string> State = new()
    {
        ["jab"] = "jab", ["cross"] = "cross", ["hook_l"] = "hook_lead", ["hook_r"] = "hook_rear", ["uppercut"] = "upper_rear",
        ["body"] = "body_hook", ["block"] = "block_high", ["dodge_l"] = "dodge_left", ["dodge_r"] = "dodge_right",
        ["step_in"] = "step_forward", ["step_back"] = "step_back",
    };
    static readonly Dictionary<string, string> HitState = new()
    {
        ["jab"] = "hit_head_straight", ["cross"] = "hit_head_straight", ["hook_l"] = "hit_head_left", ["hook_r"] = "hit_head_right",
        ["uppercut"] = "hit_upper", ["body"] = "hit_body",
    };
    public static int Damage(string move) => move switch { "jab" => 1, "cross" => 2, "hook_l" or "hook_r" => 3, "uppercut" => 4, "body" => 2, _ => 0 };
    public static bool IsAttack(string move) => Damage(move) > 0;

    public const float RingHalf = 2.35f, MinGap = 0.85f, Reach = 1.45f;
    const float ActiveFrom = 0.4f, ActiveTo = 0.6f;

    readonly Queue<string> queue = new();
    readonly Dictionary<string, float> lengths = new();
    string current; float t, dur; bool hitDone, idle;
    float baseY;

    static readonly int Spd = Animator.StringToHash("spd");
    void Play(string state, float speed, float fade) { anim.SetFloat(Spd, speed); anim.CrossFadeInFixedTime(state, fade); }

    public string Current => current;
    public bool Busy => current != null || queue.Count > 0;

    void Awake()
    {
        if (anim == null) anim = GetComponentInChildren<Animator>();
        baseY = transform.position.y;
            foreach (var c in anim.runtimeAnimatorController.animationClips) lengths[c.name] = c.length;
    }

    float Len(string state, float speed)
    {
        var clip = state == "dodge_left" ? "dodge_right" : state;
        return (lengths.TryGetValue(clip, out var l) ? l : 0.8f) / speed;
    }

    static float Speed(string state) => state switch
    {
        "jab" or "cross" or "hook_lead" or "hook_rear" or "upper_rear" or "body_hook" => 1.35f,
        "step_forward" or "step_back" => 1.6f,
        "dodge_left" or "dodge_right" or "block_high" => 1.4f,
        _ => 1f,
    };

    public void ResetFor(int cents)
    {
        budget = startBudget = cents; guard = 1f; down = won = holdBlock = false; lastHitBy = null;
        queue.Clear(); lastMoves.Clear(); current = null; idle = false;
        anim.SetFloat(Spd, 1f);
        anim.Play("idle_boxing_bounce", 0, UnityEngine.Random.value);
    }

    public void SetQueue(IEnumerable<string> moves, bool front = false)
    {
        var list = new List<string>();
        foreach (var m in moves) if (State.ContainsKey(m)) list.Add(m);
        if (front) { list.AddRange(queue); }
        queue.Clear();
        foreach (var m in list) queue.Enqueue(m);
    }

    public float Distance => opp == null ? 0 : Vector3.Distance(Flat(transform.position), Flat(opp.transform.position));
    static Vector3 Flat(Vector3 v) => new(v.x, 0, v.z);

    void Update()
    {
        if (opp == null) return;
        if (down) return;
        if (won) { Face(); return; }
        if (!active) { Face(); return; }
        Face();
        // Hard spacing: both fighters can step in at once, so each pushes itself back half of any overlap.
        float gap = Distance;
        if (gap < MinGap && !opp.down)
            Move(Flat(transform.position - opp.transform.position).normalized * Mathf.Min((MinGap - gap) * 0.5f, 2f * Time.deltaTime));
        if (current != null) { Tick(); if (current != null) return; }

        if (holdBlock)
        {
            if (!idle) { anim.SetFloat(Spd, 0f); anim.CrossFade("block_hold", 0.15f, 0, 0.45f); idle = true; }
            guard = Mathf.Min(1f, guard + Time.deltaTime * 0.1f);
            return;
        }
        float d = Distance;
        // Close the gap before throwing, otherwise punches queued at 1.45-1.7 m all whiff.
        if (queue.Count > 0 && IsAttack(queue.Peek()) && d > Reach - 0.15f) { Begin("step_in"); return; }
        if (queue.Count > 0) { Begin(queue.Dequeue()); return; }
        if (d > Reach + 0.25f) { Begin("step_in"); return; }
        if (d < MinGap) { Begin("step_back"); return; }
        if (!idle) { Play("idle_boxing_bounce", 1f, 0.2f); idle = true; }
        guard = Mathf.Min(1f, guard + Time.deltaTime * 0.08f);
    }

    void Face()
    {
        var dir = Flat(opp.transform.position - transform.position);
        if (dir.sqrMagnitude < 0.001f) return;
        transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(dir), 1f - Mathf.Exp(-10f * Time.deltaTime));
    }

    void Begin(string move)
    {
        var state = State[move];
        float speed = Speed(state);
        Play(state, speed, 0.08f);
        current = move; t = 0; dur = Len(state, speed); hitDone = false; idle = false;
        lastMoves.Add(move);
        if (lastMoves.Count > 6) lastMoves.RemoveAt(0);
    }

    void Tick()
    {
        t += Time.deltaTime;
        float n = t / dur;
        if (current == "step_in" || current == "step_back")
        {
            var toOpp = Flat(opp.transform.position - transform.position).normalized;
            float sign = current == "step_in" ? 1f : -1f;
            if (current == "step_in" && Distance <= MinGap + 0.15f) sign = 0;
            Move(toOpp * sign * 0.75f * Time.deltaTime / dur);
        }
        if (IsAttack(current) && !hitDone && n >= ActiveFrom && n <= ActiveTo) { hitDone = true; Resolve(current); }
        if (n >= 1f) current = null;
    }

    void Move(Vector3 delta)
    {
        var p = transform.position + delta;
        p.x = Mathf.Clamp(p.x, -RingHalf, RingHalf);
        p.z = Mathf.Clamp(p.z, -RingHalf, RingHalf);
        p.y = baseY;
        transform.position = p;
    }

    public bool scripted; // replay: results come from the 0G episode log, not from local hit detection

    void Resolve(string move)
    {
        if (scripted) return;
        if (opp.down || opp.won) return;
        float reach = move == "body" ? Reach - 0.1f : Reach;
        if (Distance > reach) return; // whiff
        var toMe = Flat(transform.position - opp.transform.position).normalized;
        if (Vector3.Angle(opp.transform.forward, toMe) > 75f) return; // defender turned away: miss
        string result = opp.IsBlocking ? "blocked" : opp.IsDodging ? "dodged" : "clean";
        if (result == "clean") opp.TakeHit(move);
        else if (result == "blocked") opp.guard = Mathf.Max(0f, opp.guard - 0.08f * Damage(move));
        OnHit?.Invoke(this, move, result);
    }

    public bool IsBlocking => holdBlock || current == "block";
    public bool IsDodging => current == "dodge_l" || current == "dodge_r";

    public void TakeHit(string move)
    {
        lastHitBy = move;
        var state = HitState.TryGetValue(move, out var s) ? s : "hit_body";
        Play(state, 1.3f, 0.05f);
        current = "_hit"; t = 0; dur = Len(state, 1.3f) * 0.8f; idle = false;
        Move(Flat(transform.position - opp.transform.position).normalized * 0.12f);
    }

    public void KnockOut()
    {
        down = true; queue.Clear(); current = null;
        Play("ko_back", 1f, 0.05f);
    }

    public void Victory()
    {
        won = true; queue.Clear(); current = null; holdBlock = false;
        Play("victory", 1f, 0.25f);
    }

    public void PlayIdle() { queue.Clear(); current = null; holdBlock = false; idle = false; Play("idle_boxing_bounce", 1f, 0.2f); idle = true; }
}
