using System;
using System.Collections.Generic;
using UnityEngine;

// Plays a queue of spec moves on a Mixamo humanoid. Never decides strategy: plans come from the backend
// (or OfflineSim). Resolves its own hits from the real fist bone during each attack's strike window and reports them via OnHit.
public class Fighter : MonoBehaviour
{
    public string botId = "bot_a";
    public string displayName = "Brass Knuckle";
    public Fighter opp;
    public Animator anim;
    public Color flashColor = new(1f, 0.6f, 0.2f);

    [NonSerialized] public int budget, startBudget;
    [NonSerialized] public float guard = 1f;
    [NonSerialized] public bool active;      // false = idle on the spot (home screen)
    [NonSerialized] public bool holdBlock;   // safe_stop: cover up and hold
    [NonSerialized] public bool down, won;
    [NonSerialized] public string lastHitBy;
    public readonly List<string> lastMoves = new();
    public event Action<Fighter, string, string> OnHit; // attacker, move, result
    public event Action<Fighter, string> OnSwing;       // attacker, move: every punch at its strike frame, landed or not
    public event Action<Fighter, string, int> OnBegin;  // a move actually starts (after any queue wait / step-in), with its throw id
    public int ThrowId { get; private set; }            // id of the move in progress; hits resolve during it

    public static readonly string[] Moves = { "jab", "cross", "hook_l", "hook_r", "uppercut", "body", "block", "dodge_l", "dodge_r", "step_in", "step_back" };

    // spec move -> animator state (states are named after the clip files; *_left/_m states are mirrors).
    // circle_l/circle_r are local footwork, never sent to or expected from the backend.
    static readonly Dictionary<string, string> State = new()
    {
        ["jab"] = "jab", ["cross"] = "cross", ["hook_l"] = "hook_lead", ["hook_r"] = "hook_rear", ["uppercut"] = "upper_rear",
        ["body"] = "body_hook", ["block"] = "block_high", ["dodge_l"] = "dodge_left", ["dodge_r"] = "dodge_right",
        ["step_in"] = "step_forward", ["step_back"] = "step_back", ["circle_l"] = "step_left", ["circle_r"] = "step_right",
    };
    // A left hook lands on the defender's right cheek and turns the head to their left (mirrored clip), and so on.
    static readonly Dictionary<string, string> HitState = new()
    {
        ["jab"] = "hit_head_straight", ["cross"] = "hit_head_straight", ["hook_l"] = "hit_head_left_m", ["hook_r"] = "hit_head_right",
        ["uppercut"] = "hit_upper", ["body"] = "hit_body",
    };
    public static int Damage(string move) => move switch { "jab" => 1, "cross" => 2, "hook_l" or "hook_r" => 3, "uppercut" => 4, "body" => 2, _ => 0 };
    public static bool IsAttack(string move) => Damage(move) > 0;

    // Measured from the clips with Editor/Measure.cs (samples every clip, tracks both hands):
    // which fist throws it, when it is fully out (normalized clip time), and the fighter distance at which that fist meets the target
    // (ranges tuned from the capture-mode [strike] log: clean hits land at 0.95-1.1 m).
    // radius: how close the knuckles must pass to the target for a clean hit. Straights drive into the face; hooks and
    // uppercuts arc past it, so they get a wider radius (measured with -record -strikes: at range a clean cross passes at
    // ~0.5, a hook at ~0.6, an uppercut at ~0.65 knuckles-to-face).
    readonly struct Strike
    {
        public readonly HumanBodyBones fist; public readonly float peak, range, radius; public readonly bool body;
        public Strike(HumanBodyBones f, float p, float r, float rad, bool b = false) { fist = f; peak = p; range = r; radius = rad; body = b; }
    }
    static readonly Dictionary<string, Strike> Strikes = new()
    {
        ["jab"] = new(HumanBodyBones.LeftHand, 0.28f, 1.05f, 0.6f),
        ["cross"] = new(HumanBodyBones.RightHand, 0.32f, 1.05f, 0.68f),
        ["hook_l"] = new(HumanBodyBones.LeftHand, 0.27f, 0.98f, 0.72f),
        ["hook_r"] = new(HumanBodyBones.RightHand, 0.35f, 0.98f, 0.72f),
        ["uppercut"] = new(HumanBodyBones.RightHand, 0.33f, 1.0f, 0.82f),
        ["body"] = new(HumanBodyBones.LeftHand, 0.42f, 1.05f, 0.72f, true),
    };
    public static float RangeOf(string move) => Strikes.TryGetValue(move, out var s) ? s.range : Reach;

    // MinGap: chest-to-chest with the punch lean baked in, bodies never touch. Reach: longest punch plus a little slack.
    public const float RingHalf = 2.35f, MinGap = 0.95f, Reach = 1.4f;


    readonly Queue<string> queue = new();
    readonly Dictionary<string, float> lengths = new();
    string current; float t, dur; bool hitDone, idle;
    float stepTarget, minFist, nextFootwork;
    Vector3 knock;
    float baseY;

    // Procedural layer on top of the clips (LateUpdate): a spring that jolts spine/chest/head when hit, slow breathing
    // and weight shift in the guard, and a head that keeps tracking the opponent. World-space axes, so bone orientation doesn't matter.
    Vector3 jolt, joltVel;  // degrees: x = pitch (back +), y = yaw, z = roll
    float phase;            // per-bot offset so the two never breathe in sync
    Transform spine, chest, head, hips;

    // Boxing dodges, drawn procedurally over the guard clip with the feet pinned by IK: a slip (knees bend, head goes
    // off the centre line, torso leans into it) against straights, a bob-and-weave (U under the punch) against hooks.
    float dodgeSide; bool weave;

    // impact flash on the body (emission via property block)
    Renderer[] rends; MaterialPropertyBlock mpb; float flash; Color flashTint;
    static readonly int Emission = Shader.PropertyToID("_EmissionColor");

    static readonly int Spd = Animator.StringToHash("spd");
    void Play(string state, float speed, float fade) { anim.SetFloat(Spd, speed); anim.CrossFadeInFixedTime(state, fade); }

    public string Current => current;
    public bool Busy => current != null || queue.Count > 0;
    public float Health01 => startBudget > 0 ? Mathf.Clamp01((float)budget / startBudget) : 1f;

    void Awake()
    {
        if (anim == null) anim = GetComponentInChildren<Animator>();
        baseY = transform.position.y;
        foreach (var c in anim.runtimeAnimatorController.animationClips) lengths[c.name] = c.length;
        anim.gameObject.AddComponent<RootRelay>().f = this;
        rends = GetComponentsInChildren<Renderer>();
        mpb = new MaterialPropertyBlock();
        spine = Bone(HumanBodyBones.Spine); chest = Bone(HumanBodyBones.UpperChest) ?? Bone(HumanBodyBones.Chest); head = Bone(HumanBodyBones.Head); hips = Bone(HumanBodyBones.Hips);
        phase = UnityEngine.Random.value * 10f;
    }

    float Len(string state, float speed)
    {
        var clip = state switch { "dodge_left" => "dodge_right", "hit_head_left_m" => "hit_head_left", _ => state };
        return (lengths.TryGetValue(clip, out var l) ? l : 0.8f) / speed;
    }

    static float Speed(string state) => state switch
    {
        "jab" or "cross" => 1.45f,
        "hook_lead" or "hook_rear" or "upper_rear" or "body_hook" => 1.3f,
        "step_forward" or "step_back" => 1.6f,
        "step_left" or "step_right" => 1.3f,
        "dodge_left" or "dodge_right" or "block_high" => 1.4f,
        _ => 1f,
    };

    public void ResetFor(int cents)
    {
        budget = startBudget = cents; guard = 1f; down = won = holdBlock = false; lastHitBy = null; jolt = joltVel = Vector3.zero;
        queue.Clear(); lastMoves.Clear(); current = null; idle = false; knock = Vector3.zero; flash = 0; ApplyFlash();
        nextFootwork = Time.time + UnityEngine.Random.Range(0.6f, 1.6f);
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
    Vector3 ToOpp => Flat(opp.transform.position - transform.position).normalized;

    public Transform Bone(HumanBodyBones b) => anim.GetBoneTransform(b);
    public Vector3 Head => Bone(HumanBodyBones.Head)?.position ?? transform.position + Vector3.up * 1.6f;
    public Vector3 Chest => (Bone(HumanBodyBones.UpperChest) ?? Bone(HumanBodyBones.Chest) ?? Bone(HumanBodyBones.Spine))?.position ?? transform.position + Vector3.up * 1.25f;
    // The knuckles, not the wrist: the hand bone pushed ~9 cm further along the forearm.
    public Vector3 Fist(string move)
    {
        if (!Strikes.TryGetValue(move, out var s) || Bone(s.fist) == null) return Head;
        var hand = Bone(s.fist).position;
        var fore = Bone(s.fist == HumanBodyBones.LeftHand ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
        return fore != null ? hand + (hand - fore.position).normalized * 0.09f : hand;
    }

    void Update()
    {
        UpdateFlash();
        if (opp == null || down) return;
        Face();
        if (won || !active) return;

        if (knock.sqrMagnitude > 1e-4f) { Move(knock * Time.deltaTime); knock *= Mathf.Exp(-9f * Time.deltaTime); }
        if (current != null) { Tick(); if (current != null) return; }

        if (holdBlock)
        {
            // Covering up (no fresh plan): a slowed guard bounce, hands up. IsBlocking stays true via holdBlock.
            // (Freezing block_hold mid-clip left an upright, arms-down pose.)
            if (!idle) { Play("idle_boxing_bounce", 0.6f, 0.25f); idle = true; }
            guard = Mathf.Min(1f, guard + Time.deltaTime * 0.1f);
            return;
        }
        float d = Distance;
        if (queue.Count > 0)
        {
            var next = queue.Peek();
            // Close to this punch's range before throwing it, otherwise it whiffs in front of his face.
            if (IsAttack(next) && d > RangeOf(next) + 0.12f) { BeginStep(RangeOf(next)); return; }
            Begin(queue.Dequeue()); return;
        }
        if (d > Reach + 0.25f) { BeginStep(Reach); return; }
        if (d < MinGap + 0.05f) { Begin("step_back", record: false); return; }
        if (Time.time >= nextFootwork) { Footwork(); return; }
        if (!idle) { Play("idle_boxing_bounce", 1f, 0.2f); idle = true; }
        guard = Mathf.Min(1f, guard + Time.deltaTime * 0.08f);
    }

    // Cosmetic: circle the other fighter between plans, and always circle away from the ropes.
    void Footwork()
    {
        nextFootwork = Time.time + UnityEngine.Random.Range(0.9f, 2.2f);
        var p = Flat(transform.position);
        bool nearRope = Mathf.Max(Mathf.Abs(p.x), Mathf.Abs(p.z)) > RingHalf - 0.55f;
        if (!nearRope && UnityEngine.Random.value < 0.35f) return;
        // step_right moves along +transform.right; pick the side that heads for the centre when cornered
        bool right = nearRope ? Vector3.Dot(transform.right, -p) > 0 : UnityEngine.Random.value < 0.5f;
        Begin(right ? "circle_r" : "circle_l", record: false);
    }

    void Face()
    {
        var dir = Flat(opp.transform.position - transform.position);
        if (dir.sqrMagnitude < 0.001f) return;
        transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(dir), 1f - Mathf.Exp(-10f * Time.deltaTime));
    }

    // Automatic spacing (closing to punch range, backing off when too close) isn't a decision, so it isn't recorded in
    // lastMoves: the state other code reads should show real moves (punches, defence, steps that were asked for).
    void BeginStep(float target) { Begin("step_in", record: false); stepTarget = target; }

    void Begin(string move, bool record = true)
    {
        var state = State[move];
        float speed = Speed(state) * UnityEngine.Random.Range(0.94f, 1.06f); // no two jabs identical
        if (move is "dodge_l" or "dodge_r")
        {
            dodgeSide = move == "dodge_l" ? -1 : 1;
            var incoming = opp != null ? opp.current : null;
            weave = incoming is "hook_l" or "hook_r";
            anim.SetFloat(Spd, 1f); anim.CrossFadeInFixedTime("idle_boxing_bounce", 0.1f); // the normal guard; the dodge is layered on top
            current = move; t = 0; dur = Len(state, speed); hitDone = false; idle = false; minFist = float.MaxValue; // same timing window as before
            OnBegin?.Invoke(this, move, ++ThrowId); // before it's recorded: listeners snapshot the state this move answers
            lastMoves.Add(move); if (lastMoves.Count > 6) lastMoves.RemoveAt(0);
            return;
        }
        // Attacks out of another attack chain quickly; everything else blends a touch longer so it never pops.
        bool chain = IsAttack(move) && lastMoves.Count > 0 && IsAttack(lastMoves[^1]) && t < dur + 0.2f;
        Play(state, speed, IsAttack(move) ? (chain ? 0.14f : 0.11f) : 0.18f);
        current = move; t = 0; dur = Len(state, speed); hitDone = false; idle = false; minFist = float.MaxValue;
        stepTarget = MinGap + 0.1f;
        if (!record) return;
        OnBegin?.Invoke(this, move, ++ThrowId); // before it's recorded: listeners snapshot the state this move answers
        lastMoves.Add(move);
        if (lastMoves.Count > 6) lastMoves.RemoveAt(0);
    }

    void Tick()
    {
        t += Time.deltaTime;
        float n = t / dur;
        // Reached the distance we stepped for: cut the step short and let the queued punch start from here.
        if (current == "step_in" && Distance <= stepTarget && n > 0.25f) { current = null; return; }
        if (Strikes.TryGetValue(current, out var s) && !hitDone)
        {
            // Wind-up: settle to this punch's range so the fist actually lands on the target instead of passing through it.
            if (n < s.peak) Move(ToOpp * Mathf.Clamp(Distance - s.range, -1.6f * Time.deltaTime, 1.8f * Time.deltaTime));
            if (n >= s.peak - 0.15f) // the whole closest pass of the arc, not a thin slice
            {
                var target = s.body ? opp.Chest : opp.Head + Vector3.up * 0.08f; // head bone sits at the neck; aim at the face
                minFist = Mathf.Min(minFist, Vector3.Distance(Fist(current), target));
            }
            if (n >= s.peak + 0.1f) { hitDone = true; OnSwing?.Invoke(this, current); Resolve(current); }
        }
        // Flow: once a punch has landed and is recovering, a queued punch comes straight out of it (a combo);
        // with nothing queued, blend back to guard before the clip's dead tail instead of snapping at the end.
        bool attack = IsAttack(current);
        bool resolved = !attack || hitDone;
        if (resolved && n >= 0.7f && queue.Count > 0 && IsAttack(queue.Peek()) && Distance <= RangeOf(queue.Peek()) + 0.12f) { current = null; return; }
        // (block's guard window runs to 0.85, so it blends out a little later)
        if (resolved && n >= (current == "block" ? 0.86f : 0.82f) && queue.Count == 0 && current != "_hit") { current = null; Play("idle_boxing_bounce", 1f, 0.3f); idle = true; return; }
        if (n >= 1f) current = null;
    }

    // 0 → 1 → 0 over the dodge, peaking inside the window where IsDodging counts (12-70%).
    float DodgeK => current is "dodge_l" or "dodge_r" ? Mathf.Sin(Mathf.PI * Mathf.Clamp01(N / 0.85f)) : 0f;

    // Called from RootRelay.OnAnimatorIK: move the body, then pin both feet where the clip had them so the knees bend.
    public void OnIK()
    {
        float k = DodgeK;
        if (k <= 0.001f) { anim.SetIKPositionWeight(AvatarIKGoal.LeftFoot, 0); anim.SetIKPositionWeight(AvatarIKGoal.RightFoot, 0); return; }
        var lf = anim.GetIKPosition(AvatarIKGoal.LeftFoot); var rf = anim.GetIKPosition(AvatarIKGoal.RightFoot);
        var lr = anim.GetIKRotation(AvatarIKGoal.LeftFoot); var rr = anim.GetIKRotation(AvatarIKGoal.RightFoot);
        Vector3 side = transform.right * dodgeSide, offset;
        if (weave)
        {
            float n = Mathf.Clamp01(N / 0.85f);
            offset = Vector3.down * 0.24f * Mathf.Sin(Mathf.PI * n) + side * 0.18f * Mathf.Sin(Mathf.PI * (n - 0.5f)) + transform.forward * 0.06f * Mathf.Sin(Mathf.PI * n); // down, under, up the other side
        }
        else offset = Vector3.down * 0.1f * k + side * 0.2f * k + transform.forward * 0.05f * k; // head goes forward and outside the punch
        anim.bodyPosition += offset;
        foreach (var (g, pos, rot) in new[] { (AvatarIKGoal.LeftFoot, lf, lr), (AvatarIKGoal.RightFoot, rf, rr) })
        {
            anim.SetIKPositionWeight(g, 1); anim.SetIKRotationWeight(g, 1);
            anim.SetIKPosition(g, pos); anim.SetIKRotation(g, rot);
        }
    }

    void LateUpdate()
    {
        if (anim == null || head == null) return;
        float dt = Time.deltaTime; // follows hit-stop and slow-mo
        // critically-ish damped spring back to zero
        joltVel += (-jolt * 260f - joltVel * 20f) * dt;
        jolt += joltVel * dt;
        if (down || opp == null) return;
        float tt = Time.time + phase;
        bool settled = current == null && !won;
        float breathe = settled ? Mathf.Sin(tt * 2.1f) * 1.6f : 0f, sway = settled ? Mathf.Sin(tt * 0.9f) * 3f : 0f;
        Vector3 right = transform.right, up = Vector3.up, fwd = transform.forward;
        Quaternion J(Vector3 deg) => Quaternion.AngleAxis(-deg.x, right) * Quaternion.AngleAxis(deg.y, up) * Quaternion.AngleAxis(deg.z, fwd);
        if (hips != null) hips.rotation = Quaternion.AngleAxis(sway + 10f * dodgeSide * DodgeK * (weave ? 0.5f : 1f), up) * hips.rotation; // hips turn with the slip
        // dodge lean: slip rolls the torso into the slip and turns the shoulders; the weave folds forward at the bottom of the U
        float dk = DodgeK;
        // x < 0 folds forward at the waist (never back), y turns the shoulders into the slip, z is a small side tilt
        var lean = weave ? new Vector3(-30f * dk, 8f * dodgeSide * dk, 6f * dodgeSide * Mathf.Sin(Mathf.PI * N)) : new Vector3(-12f * dk, 18f * dodgeSide * dk, 9f * dodgeSide * dk);
        if (spine != null) spine.rotation = J(jolt * 0.35f + new Vector3(breathe * 0.5f, 0, 0) + lean * 0.45f) * spine.rotation;
        if (chest != null) chest.rotation = J(jolt * 0.3f + new Vector3(breathe, -sway * 0.6f, 0) + lean * 0.55f) * chest.rotation;
        // eyes on the opponent: a small yaw toward his head, clamped so the clip's own head work still shows
        var toOpp = Vector3.ProjectOnPlane(opp.Head - head.position, up);
        float look = Mathf.Clamp(Vector3.SignedAngle(Vector3.ProjectOnPlane(fwd, up), toOpp, up), -18f, 18f) * (settled ? 0.6f : 0.25f);
        head.rotation = J(jolt * 0.6f + new Vector3(0, look, 0)) * head.rotation;
    }

    // Every position change goes through here: ring bounds plus a hard minimum gap, so the bodies can never overlap
    // whatever the animation, the knockback or the other fighter does.
    public void Move(Vector3 delta)
    {
        var p = transform.position + new Vector3(delta.x, 0, delta.z);
        p.x = Mathf.Clamp(p.x, -RingHalf, RingHalf);
        p.z = Mathf.Clamp(p.z, -RingHalf, RingHalf);
        p.y = baseY;
        if (opp != null && !down && !opp.down)
        {
            var away = Flat(p - opp.transform.position);
            if (away.magnitude < MinGap)
            {
                if (away.sqrMagnitude < 1e-6f) away = -transform.forward;
                var fixedP = Flat(opp.transform.position) + away.normalized * MinGap;
                p.x = fixedP.x; p.z = fixedP.z;
            }
        }
        transform.position = p;
    }

    // Root motion from the clips that travel (steps, knockback, KO falls); the stepping fighter stops at its target distance.
    public void ApplyRoot(Vector3 delta)
    {
        if (!active && !down) return;
        if (current == "step_in" && Distance <= stepTarget) delta -= Vector3.Project(delta, ToOpp);
        Move(delta);
    }

    public static bool logStrikes; // capture mode: log every punch to tune ranges against the clips
    public bool scripted; // replay: results come from the 0G episode log, not from local hit detection

    void Resolve(string move)
    {
        if (scripted) return;
        if (opp.down || opp.won) return;
        var toMe = Flat(transform.position - opp.transform.position).normalized;
        float facing = Vector3.Angle(opp.transform.forward, toMe);
        if (facing > 75f) { if (logStrikes) Debug.Log($"[strike] {botId} {move} d={Distance:F2} fist={minFist:F2} -> turned away ({facing:F0}°)"); return; } // defender turned away: miss
        // Block and dodge win whenever the punch was in range; a clean hit needs the fist to have reached the target.
        float radius = Strikes[move].radius;
        bool inRange = minFist < radius + 0.2f;
        string result = opp.IsBlocking && inRange ? "blocked" : opp.IsDodging && inRange ? "dodged" : minFist < radius ? "clean" : null;
        if (logStrikes) Debug.Log($"[strike] {botId} {move} d={Distance:F2} fist={minFist:F2} -> {result ?? "whiff"}");
        if (result == null) return; // whiff
        if (result == "clean") opp.TakeHit(move);
        else if (result == "blocked") { opp.guard = Mathf.Max(0f, opp.guard - 0.08f * Damage(move)); opp.Flash(new Color(0.4f, 0.65f, 1f), 0.5f); opp.knock = -opp.ToOpp * 0.35f; opp.joltVel += new Vector3(140 * Damage(move), 0, 0); }
        OnHit?.Invoke(this, move, result);
    }

    // Only while the guard is actually up / the head is actually off the line (dodge_right's head shift peaks at 25-60% of the clip).
    float N => dur > 0 ? t / dur : 0;
    public bool IsBlocking => holdBlock || (current == "block" && N > 0.12f && N < 0.85f);
    public bool IsDodging => (current == "dodge_l" || current == "dodge_r") && N > 0.12f && N < 0.7f;

    public void TakeHit(string move)
    {
        lastHitBy = move;
        var state = HitState.TryGetValue(move, out var s) ? s : "hit_body";
        Play(state, 1.25f * UnityEngine.Random.Range(0.95f, 1.05f), 0.07f);
        current = "_hit"; t = 0; dur = Len(state, 1.25f) * 0.78f; idle = false;
        // hit_upper carries its own 1.1 m root-motion knockback; the rest get a short shove that bleeds off
        if (move != "uppercut" && opp != null) knock = -ToOpp * (0.45f + 0.18f * Damage(move));
        // The body answers the punch: head snaps back from straights, turns away from hooks, lifts on an uppercut, folds on a body shot.
        float k = 0.8f + 0.2f * Damage(move);
        joltVel += move switch
        {
            "jab" => new Vector3(260, 0, 0), "cross" => new Vector3(380, 40, 0),
            "hook_l" => new Vector3(80, -420, 160), "hook_r" => new Vector3(80, 420, -160),
            "uppercut" => new Vector3(520, 0, 0), "body" => new Vector3(-420, 0, 0), _ => new Vector3(200, 0, 0),
        } * k;
        Flash(flashColor, 1f);
    }

    public void Flash(Color c, float k) { flashTint = c; flash = Mathf.Max(flash, k); }

    void UpdateFlash()
    {
        if (flash <= 0f) return;
        flash = Mathf.Max(0f, flash - Time.unscaledDeltaTime * 5f);
        ApplyFlash();
    }

    void ApplyFlash()
    {
        if (rends == null) return;
        var c = flashTint * (flash * flash * 3f);
        foreach (var r in rends)
        {
            if (r == null) continue;
            r.GetPropertyBlock(mpb);
            mpb.SetColor(Emission, c);
            r.SetPropertyBlock(mpb);
        }
    }

    public void KnockOut()
    {
        down = true; queue.Clear(); current = null; knock = Vector3.zero;
        Play("ko_back", 1f, 0.05f);
        Flash(Color.white, 1f);
    }

    public void Victory()
    {
        won = true; queue.Clear(); current = null; holdBlock = false;
        Play("victory", 1f, 0.25f);
    }

    public void PlayIdle() { queue.Clear(); current = null; holdBlock = false; idle = false; Play("idle_boxing_bounce", 1f, 0.2f); idle = true; }
}

// OnAnimatorMove must live next to the Animator; it hands the clip's root motion to the Fighter instead of applying it raw.
public class RootRelay : MonoBehaviour
{
    public Fighter f;
    Animator anim;
    void Awake() => anim = GetComponent<Animator>();
    void OnAnimatorMove() { if (f != null) f.ApplyRoot(anim.deltaPosition); }
    void OnAnimatorIK(int layer) { if (f != null) f.OnIK(); }
}
