using System;
using UnityEngine;
using Random = UnityEngine.Random;

// Everything you see and hear that isn't gameplay: impact sparks and light, smoke, crowd (instanced, cheers on big moments),
// camera-flash glints in the stands, KO confetti, and every sound, synthesized at startup so there are no audio files to ship.
public class Fx : MonoBehaviour
{
    public Material add, alpha, crowdMat; // assigned by Setup (URP Particles/Unlit additive + alpha, URP Lit instanced)
    public float ringTop = 1f;

    [NonSerialized] public float shake;  // camera trauma 0..1, Game reads and decays it
    [NonSerialized] public float excite; // crowd energy 0..1, decays here
    float crowdBase = 0.2f;

    ParticleSystem sparks, glow, blockSparks, debris, smoke, flashes, confetti;
    Light[] lights; float[] lightLife, lightPeak; int li;
    AudioSource[] srcs; int si; AudioSource crowdLoop;
    // Music: two sources so screens crossfade; loops render on a worker thread at startup.
    AudioSource musicA, musicB; Music.Kind musicWant = Music.Kind.Lobby; bool musicOn;
    readonly AudioClip[] music = new AudioClip[3]; readonly float[][] musicData = new float[3][];
    public float musicVolume = 0.5f;
    AudioClip punch, punchHeavy, body, clank, whoosh, bell, cheer, boom, murmur, tick, dodge, slam, ooh, ding;
    // Recorded impacts (Kenney "Impact Sounds", CC0) in Resources/Sfx; the synthesized ones above are the fallback.
    AudioClip[] sfxBasic, sfxHeavy, sfxMajor; // jab/cross/body · hooks/uppercut · slow-mo specials and K.O.
    AudioClip[] sfxMeat, sfxBlock;           // punch body layered under every hit · forearm-guard clank
    AudioClip jingle, coin, whoomp;          // round complete · test funds arrive · slow-mo starts
    float duck;                              // music dip after hits (side-chain feel), decays here

    void Awake()
    {
        var dot = Dot();
        Material M(Material src, Color tint) { var m = new Material(src); m.SetTexture("_BaseMap", dot); m.SetColor("_BaseColor", tint); return m; }
        var hot = M(add, new Color(3.2f, 3.2f, 3.2f, 1f));
        var soft = M(alpha, Color.white);

        sparks = PS("sparks", hot, 0.22f, 0.5f, 3f, 9f, 0.018f, 0.04f, 2.2f, new Color(1f, 0.62f, 0.22f), new Color(1f, 0.9f, 0.55f), stretch: true);
        glow = PS("glow", hot, 0.07f, 0.12f, 0f, 0f, 0.28f, 0.42f, 0f, new Color(1f, 0.7f, 0.35f, 0.7f), new Color(1f, 0.85f, 0.6f, 0.9f));
        blockSparks = PS("blockSparks", hot, 0.15f, 0.35f, 2f, 5f, 0.015f, 0.03f, 1.2f, new Color(0.45f, 0.7f, 1f), new Color(0.85f, 0.95f, 1f), stretch: true);
        debris = PS("debris", soft, 0.6f, 1.1f, 1.5f, 4f, 0.025f, 0.05f, 5f, new Color(0.22f, 0.22f, 0.25f), new Color(0.55f, 0.5f, 0.45f));
        var col = debris.collision; col.enabled = true; col.type = ParticleSystemCollisionType.Planes;
        var floor = new GameObject("floorPlane").transform; floor.SetParent(transform); floor.position = new Vector3(0, ringTop + 0.01f, 0);
        col.SetPlane(0, floor); col.bounce = 0.35f; col.dampen = 0.4f;
        smoke = PS("smoke", soft, 1.2f, 2f, 0.2f, 0.6f, 0.18f, 0.32f, -0.25f, new Color(0.2f, 0.2f, 0.22f, 0.5f), new Color(0.35f, 0.33f, 0.32f, 0.4f), grow: 2.6f);
        flashes = PS("flashes", hot, 0.05f, 0.1f, 0f, 0f, 0.25f, 0.45f, 0f, new Color(0.9f, 0.95f, 1f), Color.white);
        confetti = PS("confetti", M(alpha, new Color(1.6f, 1.6f, 1.6f, 1f)), 2.5f, 4f, 2f, 6f, 0.03f, 0.06f, 0.6f, new Color(1f, 0.55f, 0.1f), new Color(1f, 0.95f, 0.8f));
        var cn = confetti.noise; cn.enabled = true; cn.strength = 1.2f; cn.frequency = 0.6f;

        lights = new Light[3]; lightLife = new float[3]; lightPeak = new float[3];
        for (int i = 0; i < lights.Length; i++)
        {
            var l = new GameObject("impactLight").AddComponent<Light>();
            l.transform.SetParent(transform); l.type = LightType.Point; l.range = 3.5f; l.intensity = 0; l.shadows = LightShadows.None;
            lights[i] = l;
        }

        srcs = new AudioSource[12];
        for (int i = 0; i < srcs.Length; i++) { srcs[i] = gameObject.AddComponent<AudioSource>(); srcs[i].playOnAwake = false; srcs[i].spatialBlend = 0; }
        BuildSounds();
        AudioClip[] Set(string prefix) { var s = Array.FindAll(Resources.LoadAll<AudioClip>("Sfx"), c => c.name.StartsWith(prefix)); return s.Length > 0 ? s : null; }
        sfxBasic = Set("impactPlate_light"); sfxHeavy = Set("impactSoft_heavy"); sfxMajor = Set("impactMining");
        sfxMeat = Set("impactPunch_medium"); sfxBlock = Set("impactMetal_light");
        Debug.Log($"[sfx] basic {sfxBasic?.Length ?? 0}, heavy {sfxHeavy?.Length ?? 0}, major {sfxMajor?.Length ?? 0}");
        crowdLoop = gameObject.AddComponent<AudioSource>();
        crowdLoop.clip = murmur; crowdLoop.loop = true; crowdLoop.volume = 0f; crowdLoop.Play();
        musicA = gameObject.AddComponent<AudioSource>(); musicB = gameObject.AddComponent<AudioSource>();
        foreach (var m in new[] { musicA, musicB }) { m.loop = true; m.volume = 0; m.playOnAwake = false; }
        foreach (Music.Kind k in Enum.GetValues(typeof(Music.Kind)))
        {
            var kk = k;
            System.Threading.Tasks.Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { musicData[(int)kk] = Music.Render(kk); Debug.Log($"[music] {kk} rendered in {sw.ElapsedMilliseconds} ms"); }
                catch (Exception e) { Debug.LogException(e); } // a task would swallow it otherwise
            });
        }
        BuildCrowd();
    }

    // ---------------------------------------------------------------- public hooks
    public void SetCrowd(float baseLevel) => crowdBase = baseLevel;

    // Lobby / Train / Fight music; crossfades over ~1 s.
    public void SetMusic(Music.Kind k) { musicWant = k; musicOn = true; }

    void UpdateMusic(float dt)
    {
        for (int i = 0; i < 3; i++)
            if (music[i] == null && musicData[i] != null) { var c = AudioClip.Create("music" + i, musicData[i].Length, 1, Music.SR, false); c.SetData(musicData[i], 0); music[i] = c; musicData[i] = null; }
        var want = music[(int)musicWant];
        if (musicOn && want != null && musicA.clip != want)
        {
            (musicA, musicB) = (musicB, musicA); // A is always the one fading in
            musicA.clip = want; musicA.volume = 0; musicA.Play();
        }
        duck = Mathf.Max(0, duck - dt * 2.2f); // the dip lasts about a quarter second
        float target = musicVolume * (musicWant == Music.Kind.Fight ? 0.75f : musicWant == Music.Kind.Train ? 0.55f : 0.45f) * (1f - duck);
        musicA.volume = duck > 0.01f ? Mathf.Lerp(musicA.volume, musicOn ? target : 0, dt * 25f) : Mathf.MoveTowards(musicA.volume, musicOn ? target : 0, dt * 0.8f);
        musicB.volume = Mathf.MoveTowards(musicB.volume, 0, dt * 0.8f);
        if (musicB.isPlaying && musicB.volume <= 0) musicB.Stop();
    }

    public void Swing(Vector3 at, bool heavy) => Play(whoosh, heavy ? 0.55f : 0.4f, heavy ? 0.85f : 1.1f);

    // result: clean | blocked | dodged
    public void Impact(Vector3 at, string move, string result)
    {
        int dmg = Fighter.Damage(move);
        bool heavy = dmg >= 3;
        if (result == "clean")
        {
            Emit(sparks, at, 14 + dmg * 7); Emit(glow, at, 1);
            if (heavy) Emit(debris, at, 4 + dmg);
            Light(at, new Color(1f, 0.65f, 0.3f), 9f + dmg * 5f);
            float pan = PanAt(at), pr = Random.Range(0.92f, 1.08f);
            var top = Pick(heavy ? sfxHeavy : sfxBasic);
            if (top != null) Play(top, 0.85f + dmg * 0.04f, pr, pan);
            else Play(move == "body" ? body : heavy ? punchHeavy : punch, 0.75f + dmg * 0.06f, pr, pan);
            // layers: a meaty punch body under every hit, and a sub thump plus a crowd "ooh" under the heavy ones
            Play(Pick(sfxMeat), heavy ? 0.6f : 0.45f, heavy ? 0.8f * pr : pr, pan);
            if (heavy) { Play(boom, 0.35f + dmg * 0.05f, 1.6f, pan * 0.5f); Play(ooh, 0.3f + dmg * 0.04f, Random.Range(0.95f, 1.06f)); }
            Duck(heavy ? 0.55f : 0.3f);
            shake = Mathf.Min(1f, shake + 0.22f + dmg * 0.09f);
            excite = Mathf.Min(1f, excite + 0.1f + dmg * 0.06f);
            if (dmg >= 4) Play(cheer, 0.45f, Random.Range(0.95f, 1.05f));
        }
        else if (result == "blocked")
        {
            Emit(blockSparks, at, 10); Light(at, new Color(0.5f, 0.7f, 1f), 6f);
            float pan = PanAt(at);
            if (sfxBlock != null) { Play(Pick(sfxBlock), 0.7f, Random.Range(0.95f, 1.1f), pan); Play(clank, 0.25f, Random.Range(0.95f, 1.12f), pan); }
            else Play(clank, 0.55f, Random.Range(0.95f, 1.12f), pan);
            Duck(0.2f);
            shake = Mathf.Min(1f, shake + 0.1f);
        }
        else { Play(dodge, 0.6f, Random.Range(0.95f, 1.1f), PanAt(at)); Play(ooh, 0.3f, Random.Range(0.95f, 1.08f)); }
    }

    // Combo counter: a ding that climbs a step with every hit in the run.
    public void Combo(int n)
    {
        Play(ding, 0.35f, Mathf.Pow(1.122f, Mathf.Min(n, 8) - 2));
        if (n == 3) Play(cheer, 0.45f, Random.Range(0.98f, 1.06f)); // the crowd gets into a run
        if (n >= 4) Play(cheer, 0.6f, Random.Range(1.0f, 1.08f));
    }

    public void Jingle() => Play(jingle, 0.55f, 1f); // round complete
    public void Coin() => Play(coin, 0.6f, 1f);     // test funds arrived
    public void FightStart() { Bell(1); Play(cheer, 0.55f, 1f); }

    // Special impact (slow-mo close-up): a big spark burst, a white-hot flash, the slam and the crowd's "ooh".
    public void Special(Vector3 at)
    {
        Emit(sparks, at, 110); Emit(glow, at, 3); Emit(debris, at, 18); Emit(flashes, at, 2);
        Light(at, new Color(1f, 0.75f, 0.45f), 45f);
        Play(whoomp, 0.9f, 1f); // a deep swell as time slows (played before Play() starts pitching things down)
        if (sfxMajor != null) Play(Pick(sfxMajor), 1f, Random.Range(0.92f, 1.02f), PanAt(at)); else Play(slam, 1f, 1f);
        Play(Pick(sfxMeat), 0.7f, 0.7f, PanAt(at));
        Play(ooh, 0.8f, 1f); Play(cheer, 0.7f, 1f); // the crowd roars
        Duck(0.8f);
        shake = 1f; excite = 1f;
    }

    public void Tick() => Play(tick, 0.3f, Random.Range(0.96f, 1.04f)); // UI buttons

    public void Bell(int times) { for (int i = 0; i < times; i++) Invoke(nameof(Ding), i * 0.38f); }
    void Ding() => Play(bell, 0.6f, 1f);

    public void KO(Vector3 at)
    {
        Emit(sparks, at, 60); Emit(glow, at, 2); Emit(debris, at, 14);
        Light(at, new Color(1f, 0.75f, 0.45f), 30f);
        Play(boom, 0.9f, 1f);
        if (sfxMajor != null) Play(sfxMajor[Random.Range(0, sfxMajor.Length)], 1f, 0.9f); else Play(punchHeavy, 0.9f, 0.85f);
        Play(cheer, 0.8f, 1f); Invoke(nameof(Cheer2), 0.7f);
        shake = 1f; excite = 1f;
        Bell(3);
        Invoke(nameof(Confetti), 0.9f);
    }
    void Cheer2() => Play(cheer, 0.6f, 0.92f);
    void Confetti() { for (int i = 0; i < 6; i++) Emit(confetti, new Vector3(Random.Range(-2.5f, 2.5f), ringTop + 4.5f, Random.Range(-2.5f, 2.5f)), 25); }

    public void Cheer(float vol) => Play(cheer, vol, Random.Range(0.95f, 1.05f));

    // Damaged bots smoke; called every frame with the fighter's chest and how hurt it is (0..1).
    public void Smoke(Vector3 chest, float hurt)
    {
        if (hurt <= 0 || Random.value > hurt * Time.deltaTime * 22f) return;
        Emit(smoke, chest + new Vector3(Random.Range(-0.15f, 0.15f), Random.Range(0f, 0.25f), Random.Range(-0.15f, 0.15f)), 1);
        if (hurt > 0.6f && Random.value < 0.08f) Emit(sparks, chest, 4);
    }

    // ---------------------------------------------------------------- per-frame
    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        excite = Mathf.Max(0f, excite - dt * 0.18f);
        for (int i = 0; i < lights.Length; i++)
        {
            lightLife[i] = Mathf.Max(0, lightLife[i] - dt * 9f);
            lights[i].intensity = lightPeak[i] * lightLife[i] * lightLife[i];
        }
        float level = Mathf.Max(crowdBase, excite);
        crowdLoop.volume = Mathf.Lerp(crowdLoop.volume, level * 0.5f, dt * 3f);
        UpdateMusic(dt);
        // slow-mo bends the music down like tape slowing, and snaps back after
        float mp = Mathf.Lerp(musicA.pitch, Time.timeScale < 0.5f ? 0.8f : 1f, dt * (Time.timeScale < 0.5f ? 10f : 4f));
        musicA.pitch = musicB.pitch = mp;
        // camera-flash glints from the stands, more when the crowd is up
        if (Random.value < dt * (1.5f + level * 14f))
        {
            int side = Random.Range(0, 4), tier = Random.Range(0, 4);
            var p = Quaternion.Euler(0, side * 90, 0) * new Vector3(Random.Range(-10f, 10f), 1.6f + tier * 2f, 10.6f + tier * 1.6f);
            Emit(flashes, p, 1);
        }
        DrawCrowd(level);
    }

    // ---------------------------------------------------------------- crowd (GPU instanced)
    Mesh bodyMesh, headMesh; Matrix4x4[][] bodies, heads; Vector4[] seeds; Material[] crowdMats; RenderParams[] rp;
    const int Groups = 4;

    void BuildCrowd()
    {
        if (crowdMat == null) return;
        bodyMesh = PrimMesh(PrimitiveType.Capsule); headMesh = PrimMesh(PrimitiveType.Sphere);
        var spots = new System.Collections.Generic.List<Vector4>(); // x, y, z, yaw
        for (int s = 0; s < 4; s++)
        for (int tier = 0; tier < 4; tier++)
        {
            float half = 12f + tier * 1.5f - 0.8f, z = 10.6f + tier * 1.6f, y = 1f + tier * 2f;
            for (float x = -half; x <= half; x += Random.Range(0.5f, 0.75f))
            {
                if (Random.value < 0.12f) continue; // empty seats
                var p = Quaternion.Euler(0, s * 90, 0) * new Vector3(x, y, z + Random.Range(-0.3f, 0.3f));
                spots.Add(new Vector4(p.x, p.y, p.z, s * 90 + 180));
            }
        }
        var per = new System.Collections.Generic.List<Vector4>[Groups];
        for (int g = 0; g < Groups; g++) per[g] = new();
        foreach (var sp in spots) per[Random.Range(0, Groups)].Add(sp);
        bodies = new Matrix4x4[Groups][]; heads = new Matrix4x4[Groups][];
        crowdMats = new Material[Groups]; rp = new RenderParams[Groups];
        var tints = new[] { new Color(0.16f, 0.17f, 0.2f), new Color(0.3f, 0.16f, 0.1f), new Color(0.12f, 0.16f, 0.24f), new Color(0.24f, 0.24f, 0.26f) };
        seeds = new Vector4[spots.Count];
        int k = 0;
        for (int g = 0; g < Groups; g++)
        {
            bodies[g] = new Matrix4x4[per[g].Count]; heads[g] = new Matrix4x4[per[g].Count];
            crowdMats[g] = new Material(crowdMat) { enableInstancing = true };
            crowdMats[g].SetColor("_BaseColor", tints[g]);
            rp[g] = new RenderParams(crowdMats[g]) { shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off, receiveShadows = false };
            foreach (var sp in per[g]) seeds[k++] = new Vector4(sp.x, sp.y, sp.z, Random.value * 10f);
        }
        crowdSpots = per;
    }
    System.Collections.Generic.List<Vector4>[] crowdSpots;

    void DrawCrowd(float level)
    {
        if (bodies == null) return;
        float tt = Time.unscaledTime;
        int k = 0;
        for (int g = 0; g < Groups; g++)
        {
            var list = crowdSpots[g];
            for (int i = 0; i < list.Count; i++, k++)
            {
                var sp = list[i]; float seed = seeds[k].w;
                float hop = Mathf.Max(0, Mathf.Sin(tt * (5f + seed * 0.4f) + seed * 7f)) * level * 0.35f + Mathf.Sin(tt * 1.3f + seed) * 0.02f;
                var basePos = new Vector3(sp.x, sp.y + hop, sp.z);
                var rot = Quaternion.Euler(0, sp.w, 0);
                float h = 0.85f + (seed % 1f) * 0.3f;
                bodies[g][i] = Matrix4x4.TRS(basePos + Vector3.up * 0.5f * h, rot, new Vector3(0.45f, 0.5f * h, 0.32f));
                heads[g][i] = Matrix4x4.TRS(basePos + Vector3.up * (1.08f * h + 0.12f), rot, Vector3.one * 0.24f);
            }
            if (list.Count == 0) continue;
            Graphics.RenderMeshInstanced(rp[g], bodyMesh, 0, bodies[g]);
            Graphics.RenderMeshInstanced(rp[g], headMesh, 0, heads[g]);
        }
    }

    static Mesh PrimMesh(PrimitiveType t)
    {
        var g = GameObject.CreatePrimitive(t);
        var m = g.GetComponent<MeshFilter>().sharedMesh;
        Destroy(g);
        return m;
    }

    // ---------------------------------------------------------------- particles & lights
    ParticleSystem PS(string name, Material mat, float life0, float life1, float spd0, float spd1, float size0, float size1, float gravity,
        Color c0, Color c1, bool stretch = false, float grow = 0.3f)
    {
        var go = new GameObject(name); go.transform.SetParent(transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var m = ps.main;
        m.playOnAwake = false; m.loop = true; m.duration = 1f; m.maxParticles = 600; // always running, emits only via Emit()
        m.simulationSpace = ParticleSystemSimulationSpace.World;
        m.startLifetime = new ParticleSystem.MinMaxCurve(life0, life1);
        m.startSpeed = new ParticleSystem.MinMaxCurve(spd0, spd1);
        m.startSize = new ParticleSystem.MinMaxCurve(size0, size1);
        m.startColor = new ParticleSystem.MinMaxGradient(c0, c1);
        m.startRotation = new ParticleSystem.MinMaxCurve(0, Mathf.PI * 2);
        m.gravityModifier = gravity;
        var em = ps.emission; em.rateOverTime = 0;
        var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Sphere; sh.radius = 0.04f;
        var col = ps.colorOverLifetime; col.enabled = true;
        var grad = new Gradient();
        grad.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
            new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(0.8f, 0.5f), new GradientAlphaKey(0, 1) });
        col.color = grad;
        var sz = ps.sizeOverLifetime; sz.enabled = true;
        sz.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0, 1, 1, grow));
        var r = go.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        if (stretch) { r.renderMode = ParticleSystemRenderMode.Stretch; r.velocityScale = 0.035f; r.lengthScale = 1.2f; }
        ps.Play();
        return ps;
    }

    static void Emit(ParticleSystem ps, Vector3 at, int n)
    {
        var ep = new ParticleSystem.EmitParams { position = at, applyShapeToPosition = true };
        ps.Emit(ep, n);
    }

    void Light(Vector3 at, Color c, float intensity)
    {
        var l = lights[li]; lightLife[li] = 1f; lightPeak[li] = intensity; li = (li + 1) % lights.Length;
        l.transform.position = at; l.color = c; l.intensity = intensity;
    }

    static Texture2D Dot()
    {
        const int s = 64;
        var t = new Texture2D(s, s, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < s; y++)
        for (int x = 0; x < s; x++)
        {
            float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(s / 2f, s / 2f)) / (s / 2f);
            float a = Mathf.Clamp01(1f - d); a = a * a * (3 - 2 * a);
            t.SetPixel(x, y, new Color(1, 1, 1, a));
        }
        t.Apply();
        return t;
    }

    // ---------------------------------------------------------------- audio
    // pan: -1 left .. 1 right. In slow motion every effect drops in pitch with it.
    void Play(AudioClip c, float vol, float pitch, float pan = 0f)
    {
        if (c == null) return;
        var s = srcs[si]; si = (si + 1) % srcs.Length;
        s.pitch = pitch * (Time.timeScale < 0.5f ? 0.72f : 1f); s.panStereo = pan; s.PlayOneShot(c, vol);
    }

    static AudioClip Pick(AudioClip[] set) => set == null || set.Length == 0 ? null : set[Random.Range(0, set.Length)];
    static float PanAt(Vector3 world) { var cam = Camera.main; if (cam == null) return 0; return Mathf.Clamp((cam.WorldToViewportPoint(world).x - 0.5f) * 1.4f, -0.8f, 0.8f); }
    public void Duck(float amount) => duck = Mathf.Max(duck, amount);

    const int SR = 44100;
    static readonly System.Random Rng = new(7);
    static float Noise() => (float)(Rng.NextDouble() * 2 - 1);
    static float LpA(float fc) => 1f - Mathf.Exp(-2f * Mathf.PI * fc / SR);

    // Sum of decaying partials: the metallic ring of a robot getting hit.
    static float Ring(float t, float[] f, float[] decay, float amp)
    {
        float v = 0;
        for (int i = 0; i < f.Length; i++) v += Mathf.Sin(2 * Mathf.PI * f[i] * t) * Mathf.Exp(-t * decay[i]) / (i + 1);
        return v * amp;
    }

    public static float[] Synth(float secs, Func<float, float> f)
    {
        var d = new float[(int)(secs * SR)];
        float peak = 1e-4f;
        for (int i = 0; i < d.Length; i++) { d[i] = f(i / (float)SR); peak = Mathf.Max(peak, Mathf.Abs(d[i])); }
        for (int i = 0; i < d.Length; i++) d[i] *= 0.9f / peak;
        int fade = Mathf.Min(220, d.Length); // no click at the tail
        for (int i = 0; i < fade; i++) d[d.Length - 1 - i] *= i / (float)fade;
        return d;
    }

    static AudioClip Clip(string name, float[] d)
    {
        var c = AudioClip.Create(name, d.Length, 1, SR, false);
        c.SetData(d, 0);
        return c;
    }

    // A thump whose pitch drops from f0 to f1: the body of every punch.
    static Func<float, float> Thump(float f0, float f1, float decay)
    {
        float ph = 0;
        return t => { float f = f1 + (f0 - f1) * Mathf.Exp(-t * 30f); ph += 2 * Mathf.PI * f / SR; return Mathf.Sin(ph) * Mathf.Exp(-t * decay); };
    }

    static Func<float, float> Hiss(float fc, float decay)
    {
        float y = 0, a = LpA(fc);
        return t => { y += a * (Noise() - y); return y * Mathf.Exp(-t * decay); };
    }

    // Short bright noise burst: the "crack" on top of a punch.
    static Func<float, float> Crack(float hp, float decay)
    {
        float lo = 0, a = LpA(hp);
        return t => { float x = Noise(); lo += a * (x - lo); return (x - lo) * Mathf.Exp(-t * decay); };
    }

    // Resonant band-pass (two-pole): formants for the crowd "ooh" and the glove smack.
    static Func<float, float, float> Reso(float q)
    {
        float y1 = 0, y2 = 0;
        return (x, f) => { float w = 2 * Mathf.PI * f / SR, r = 1 - w / (2 * q); float y = (1 - r) * x + 2 * r * Mathf.Cos(w) * y1 - r * r * y2; y2 = y1; y1 = y; return y; };
    }

    // A small room: four feedback combs in parallel, mixed under the dry signal (and a tail added to the clip).
    static float[] Room(float[] d, float mix, float tailSecs)
    {
        int tail = (int)(tailSecs * SR);
        var o = new float[d.Length + tail];
        Array.Copy(d, o, d.Length);
        int[] taps = { 1557, 1617, 1491, 1422 };
        var wet = new float[o.Length];
        foreach (var tap in taps)
        {
            var buf = new float[tap]; int bi = 0;
            for (int i = 0; i < o.Length; i++) { float x = i < d.Length ? d[i] : 0; float y = buf[bi]; buf[bi] = x + y * 0.72f; bi = (bi + 1) % tap; wet[i] += y * 0.25f; }
        }
        float peak = 1e-4f;
        for (int i = 0; i < o.Length; i++) { o[i] = o[i] + wet[i] * mix; peak = Mathf.Max(peak, Mathf.Abs(o[i])); }
        for (int i = 0; i < o.Length; i++) o[i] *= 0.92f / peak;
        int fade = Mathf.Min(SR / 50, o.Length); for (int i = 0; i < fade; i++) o[o.Length - 1 - i] *= i / (float)fade;
        return o;
    }

    void BuildSounds()
    {
        // jab / cross: glove smack (resonant body ~1.1 kHz), a bright crack, a pitch-drop thump, a light metal ring
        {
            var th = Thump(160, 55, 18); var cr = Crack(5000, 140); var bp = Reso(3f);
            float[] f = { 610, 1430, 2270, 3310 }, dk = { 18, 24, 30, 38 };
            punch = Clip("punch", Room(Synth(0.3f, t => th(t) * 1.1f + cr(t) * 1.4f + bp(Noise() * Mathf.Exp(-t * 55), 1100) * 14f + Ring(t, f, dk, 0.16f)), 0.16f, 0.25f));
        }
        // heavy (hooks / uppercut): deeper thump, sub boom, crunch, longer ring
        {
            var th = Thump(120, 36, 9); var cr = Crack(3500, 90); var sub = Thump(60, 30, 5); var cn = Hiss(900, 22);
            float[] f = { 340, 810, 1390, 2110 }, dk = { 8, 11, 15, 20 };
            punchHeavy = Clip("punchHeavy", Room(Synth(0.7f, t => th(t) * 1.1f + sub(t) * 0.7f + cr(t) * 1.6f + cn(t) * 2.2f + Ring(t, f, dk, 0.26f)), 0.24f, 0.45f));
        }
        // body shot: dull thud, a muffled whump, a quick armour rattle
        {
            var th = Thump(95, 40, 12); var hs = Hiss(700, 30);
            float[] f = { 2400, 3700, 5100 }, dk = { 45, 60, 70 };
            body = Clip("body", Room(Synth(0.42f, t => th(t) * 1.2f + hs(t) * 2.4f + Ring(t, f, dk, 0.12f)), 0.14f, 0.25f));
        }
        // block: metal clank on the forearm guards
        {
            var th = Thump(230, 120, 30); var hs = Hiss(6000, 90);
            float[] f = { 1240, 2870, 4130, 5390 }, dk = { 11, 14, 19, 25 };
            clank = Clip("clank", Room(Synth(0.45f, t => th(t) * 0.5f + hs(t) * 1.4f + Ring(t, f, dk, 0.45f)), 0.2f, 0.3f));
        }
        // punch swing
        {
            float lo = 0, hi = 0; const float T = 0.26f;
            whoosh = Clip("whoosh", Synth(T, t =>
            {
                float k = t / T, fc = 500 + 2600 * Mathf.Sin(Mathf.PI * k);
                float x = Noise(); lo += LpA(fc) * (x - lo); hi += LpA(fc * 0.35f) * (lo - hi);
                float env = Mathf.Sin(Mathf.PI * k); return (lo - hi) * env * env;
            }));
        }
        // dodge: a shorter, brighter whoosh (the head slipping past the glove)
        {
            float lo = 0, hi = 0; const float T = 0.18f;
            dodge = Clip("dodge", Synth(T, t =>
            {
                float k = t / T, fc = 1400 + 4200 * Mathf.Sin(Mathf.PI * k);
                float x = Noise(); lo += LpA(fc) * (x - lo); hi += LpA(fc * 0.4f) * (lo - hi);
                float env = Mathf.Sin(Mathf.PI * k); return (lo - hi) * env;
            }));
        }
        {
            float[] f = { 1180, 2380, 3240, 4420, 6370 }, dk = { 1.6f, 2.4f, 3.4f, 5f, 7f };
            var hs = Hiss(8000, 120);
            bell = Clip("bell", Synth(2.2f, t => Ring(t, f, dk, 1f) + hs(t) * 0.5f));
        }
        {
            var th = Thump(70, 28, 3.5f); var hs = Hiss(600, 5);
            boom = Clip("boom", Synth(1.4f, t => th(t) + hs(t) * 1.5f));
        }
        // special impact: a long sub drop, a crack, a big metallic ring, lots of room
        {
            var sub = Thump(90, 24, 2.6f); var cr = Crack(3000, 40); var hs = Hiss(1800, 9);
            float[] f = { 290, 690, 1180, 1870, 2730 }, dk = { 3, 4, 6, 8, 11 };
            slam = Clip("slam", Room(Synth(1.6f, t => sub(t) * 1.2f + cr(t) * 1.8f + hs(t) * 1.4f + Ring(t, f, dk, 0.35f)), 0.35f, 0.9f));
        }
        // crowd "ooh": noise through two gliding vowel formants, quick swell, slow fall
        {
            var f1 = Reso(5f); var f2 = Reso(6f); float lo = 0, a = LpA(2000);
            ooh = Clip("ooh", Synth(1.5f, t =>
            {
                float x = Noise(); lo += a * (x - lo);
                float glide = 1f - 0.12f * Mathf.Clamp01(t / 1.2f);
                float env = Mathf.Clamp01(t / 0.12f) * Mathf.Exp(-t * 1.7f);
                return (f1(lo, 420 * glide) + f2(lo, 780 * glide) * 0.7f) * env;
            }));
        }
        // combo ding: two bright partials with a quick tick on the front (pitched up per hit in the run)
        { var th = Thump(900, 700, 60); ding = Clip("ding", Synth(0.5f, t => Mathf.Sin(2 * Mathf.PI * 1320 * t) * Mathf.Exp(-t * 7) + Mathf.Sin(2 * Mathf.PI * 1980 * t) * 0.4f * Mathf.Exp(-t * 11) + th(t) * 0.4f)); }
        cheer = Clip("cheer", Synth(2.6f, CrowdWash(2.6f, true)));
        // round-complete jingle: a quick rising arpeggio (A C E A) of bell-ish tones
        {
            float[] notes = { 440f, 523.25f, 659.25f, 880f };
            jingle = Clip("jingle", Synth(1.1f, t =>
            {
                float v = 0;
                for (int i = 0; i < notes.Length; i++)
                {
                    float st = i * 0.09f; if (t < st) continue; float u = t - st;
                    v += (Mathf.Sin(2 * Mathf.PI * notes[i] * u) + 0.35f * Mathf.Sin(2 * Mathf.PI * notes[i] * 2.01f * u)) * Mathf.Exp(-u * (i == 3 ? 3.5f : 9f));
                }
                return v;
            }));
        }
        // coin: two bright partials, the second a fourth up
        coin = Clip("coin", Synth(0.5f, t => (t < 0.07f ? Mathf.Sin(2 * Mathf.PI * 1568 * t) : Mathf.Sin(2 * Mathf.PI * 2093 * t)) * Mathf.Exp(-(t < 0.07f ? t : t - 0.07f) * 9f)));
        // whoomp: a low sine dropping in pitch under a darkening noise swell
        { var sub = Thump(140, 38, 2.2f); var hs = Hiss(500, 3f); whoomp = Clip("whoomp", Synth(1.2f, t => (sub(t) + hs(t) * 1.2f) * Mathf.Clamp01(t / 0.05f))); }
        { float[] f = { 1850, 3120, 4700 }, dk = { 60, 80, 110 }; var th = Thump(420, 180, 70); tick = Clip("tick", Synth(0.09f, t => th(t) * 0.6f + Ring(t, f, dk, 0.8f))); }
        murmur = Clip("murmur", Loop(Synth(8f, CrowdWash(8f, false)), 1f));
    }

    // A crowd as shaped noise (no oscillators, so no drone): a band-passed wash that swells slowly, plus scattered
    // hand claps. cheer=true rises fast, gets brighter and clappier, then fades; false is a steady low murmur.
    static Func<float, float> CrowdWash(float secs, bool cheer)
    {
        float lo = 0, lo2 = 0, hp = 0, clapEnv = 0;
        float aN = LpA(cheer ? 2400 : 1200), aN2 = LpA(cheer ? 300 : 220), aH = LpA(2500);
        var sw = new float[4]; var ph = new float[4];
        for (int i = 0; i < 4; i++) { sw[i] = 0.12f + (float)Rng.NextDouble() * 0.5f; ph[i] = (float)Rng.NextDouble() * 6; }
        float clapRate = cheer ? 38f : 3f; // claps per second
        return t =>
        {
            float x = Noise(); lo += aN * (x - lo); lo2 += aN2 * (lo - lo2);
            float wash = (lo - lo2) * 2.2f;
            float mod = 0.7f; for (int i = 0; i < 4; i++) mod += 0.08f * Mathf.Sin(2 * Mathf.PI * sw[i] * t + ph[i]);
            if (Rng.NextDouble() < clapRate / SR) clapEnv = 0.5f + (float)Rng.NextDouble() * 0.5f;
            clapEnv *= 0.9985f; hp += aH * (x - hp);
            float clap = (x - hp) * clapEnv * clapEnv * (cheer ? 0.9f : 0.35f);
            float env = cheer ? Mathf.Clamp01(t / 0.18f) * Mathf.Clamp01((secs - t) / 1.6f) : 1f;
            return (wash * mod + clap) * env;
        };
    }

    // Crossfade the tail into the head so the murmur loops without a seam.
    static float[] Loop(float[] d, float xfadeSecs)
    {
        int x = (int)(xfadeSecs * SR), n = d.Length - x;
        var o = new float[n];
        Array.Copy(d, o, n);
        for (int i = 0; i < x; i++) { float k = i / (float)x; o[i] = d[i] * k + d[n + i] * (1 - k); }
        return o;
    }
}
