using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Mixamo FBX import rules: humanoid everywhere, clips renamed from "mixamo.com" to the file name.
// XZ root motion is extracted (not baked into the pose) for everything but the idles: Fighter.OnAnimatorMove applies it
// through the ring/spacing constraint, so a lunge or knockback moves the fighter instead of sliding the mesh into the other one.
public class MixamoImport : AssetPostprocessor
{
    public override uint GetVersion() => 2; // bump to force a reimport when these rules change

    bool Ours => assetPath.Contains("ThirdParty/Mixamo/") || assetPath.Contains("ThirdParty/Robots/");
    bool IsClip => assetPath.Contains("/Clips/");

    void OnPreprocessModel()
    {
        if (!Ours) return;
        var mi = (ModelImporter)assetImporter;
        mi.animationType = ModelImporterAnimationType.Human;
        mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        mi.importAnimation = IsClip;
        mi.importBlendShapes = false; mi.importCameras = false; mi.importLights = false;
        if (IsClip) mi.materialImportMode = ModelImporterMaterialImportMode.None;
    }

    void OnPreprocessAnimation()
    {
        if (!Ours || !IsClip) return;
        var mi = (ModelImporter)assetImporter;
        var defs = mi.defaultClipAnimations;
        if (defs.Length == 0) return;
        var c = defs[0];
        var name = Path.GetFileNameWithoutExtension(assetPath);
        c.name = name;
        c.loopTime = name.StartsWith("idle");
        c.lockRootRotation = c.keepOriginalOrientation = true;
        c.lockRootHeightY = c.keepOriginalPositionY = true;
        // Only clips that end somewhere else (measured: steps 0.6-0.9 m, hit_upper 1.1 m back, KO falls, advance/retreat).
        // Punches lean ~0.7 m and come back, so they stay baked and keep the arm geometry Fighter.Strikes was measured on.
        c.lockRootPositionXZ = !(name.StartsWith("step_") || name.StartsWith("ko_") || name is "hit_upper" or "dodge_advance" or "dodge_retreat");
        c.keepOriginalPositionXZ = true;
        mi.clipAnimations = new[] { c };
    }

    void OnPreprocessTexture()
    {
        if (!Ours) return;
        var ti = (TextureImporter)assetImporter;
        if (assetPath.Contains("_normal")) ti.textureType = TextureImporterType.NormalMap;
        ti.maxTextureSize = 2048;
    }
}

public static class Setup
{
    const string Mixamo = "Assets/ThirdParty/Mixamo";
    const string Out = "Assets/Game";

    [MenuItem("Iron Corner/Build Scene")]
    public static void Build()
    {
        foreach (var d in new[] { Out, Out + "/Materials", "Assets/Scenes" }) Directory.CreateDirectory(d);
        ReimportIfNeeded();
        Player();
        var ctrl = Controller();
        var a = Bot("bot_a", "Brass Knuckle", ctrl, new Color(0.95f, 0.72f, 0.38f), new Color(0.45f, 0.3f, 0.12f), new Color(1f, 0.75f, 0.25f) * 6f);
        var b = Bot("bot_b", "Rustbucket", ctrl, new Color(1.0f, 1.0f, 1.05f), new Color(0.95f, 0.38f, 0.1f), new Color(1f, 0.15f, 0.05f) * 6f);
        Scene(a, b);
        AssetDatabase.SaveAssets();
        Debug.Log("[setup] done");
    }

    static void ReimportIfNeeded()
    {
        foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { Mixamo }))
        {
            var p = AssetDatabase.GUIDToAssetPath(guid);
            if (AssetImporter.GetAtPath(p) is ModelImporter mi && mi.animationType != ModelImporterAnimationType.Human)
                AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate);
        }
    }

    static void Player()
    {
        PlayerSettings.companyName = "Keron Labs";
        PlayerSettings.productName = "Iron Corner";
        foreach (var t in new[] { NamedBuildTarget.Standalone, NamedBuildTarget.iOS, NamedBuildTarget.Android })
            PlayerSettings.SetApplicationIdentifier(t, "com.keronlabs.ironcorner");
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
        PlayerSettings.allowedAutorotateToPortrait = false;
        PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
        PlayerSettings.allowedAutorotateToLandscapeLeft = true;
        PlayerSettings.allowedAutorotateToLandscapeRight = true;
        PlayerSettings.runInBackground = true;
        // Dev: the phone talks to the laptop over plain HTTP. Tighten to HTTPS before any store build.
        PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
        PlayerSettings.iOS.microphoneUsageDescription = "Hold to talk: coach your robot by voice.";
        // Android: RECORD_AUDIO is added automatically because the code uses Microphone; Mic.cs asks at runtime.
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 1920;
        PlayerSettings.defaultScreenHeight = 1080;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.SplashScreen.show = false;
    }

    static AnimationClip Clip(string name)
    {
        var path = $"{Mixamo}/Clips/{name}.fbx";
        var clip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview__"));
        if (clip == null) Debug.LogError("[setup] missing clip " + path);
        return clip;
    }

    static AnimatorController Controller()
    {
        var path = Out + "/Fighter.controller";
        AssetDatabase.DeleteAsset(path);
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(path);
        ctrl.AddParameter("spd", AnimatorControllerParameterType.Float);
        var sm = ctrl.layers[0].stateMachine;
        AnimatorState Add(string state, string clip, bool mirror = false)
        {
            var s = sm.AddState(state);
            s.motion = Clip(clip); s.mirror = mirror;
            s.speedParameter = "spd"; s.speedParameterActive = true;
            return s;
        }
        var names = Directory.GetFiles($"{Mixamo}/Clips", "*.fbx").Select(p => Path.GetFileNameWithoutExtension(p)).OrderBy(n => n).ToList();
        foreach (var n in names) Add(n, n);
        Add("dodge_left", "dodge_right", mirror: true);
        Add("hit_head_left_m", "hit_head_left", mirror: true); // head snaps to the defender's left: what a left hook does
        Add("block_hold", "block_high");
        sm.defaultState = sm.states.First(s => s.state.name == "idle_boxing_bounce").state;
        // spd defaults to 0 otherwise; idle must play before any script sets it.
        var ps = ctrl.parameters; ps[0].defaultFloat = 1f; ctrl.parameters = ps;
        var layers = ctrl.layers; layers[0].iKPass = true; ctrl.layers = layers; // Fighter plants the feet while it slips and weaves
        EditorUtility.SetDirty(ctrl);
        return ctrl;
    }

    static Material Lit(string name, Color c, float metal, float smooth, Texture baseMap = null, Texture normal = null, Color? emission = null)
    {
        var m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        m.SetColor("_BaseColor", c);
        if (baseMap != null) m.SetTexture("_BaseMap", baseMap);
        m.SetFloat("_Metallic", metal);
        m.SetFloat("_Smoothness", smooth);
        if (normal != null) { m.SetTexture("_BumpMap", normal); m.EnableKeyword("_NORMALMAP"); }
        // Emission is always on (black by default) so Fighter can flash a bot on impact with a property block.
        m.SetColor("_EmissionColor", emission ?? Color.black); m.EnableKeyword("_EMISSION");
        m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        var path = $"{Out}/Materials/{name}.mat";
        AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(m, path);
        return m;
    }

    // One prefab per bot. To swap in another robot mesh later, change `model` (any humanoid rig works).
    // A bot uses its own robot model (Assets/ThirdParty/Robots/<id>.fbx, keeps its textures, tinted) when present,
    // otherwise the metal-skinned Mixamo Vanguard.
    static GameObject Bot(string id, string label, AnimatorController ctrl, Color body, Color accent, Color eye)
    {
        var robotPath = $"Assets/ThirdParty/Robots/{id}.fbx";
        var robot = AssetDatabase.LoadAssetAtPath<GameObject>(robotPath);
        if (robot != null) return RobotBot(id, label, ctrl, robot, body, eye);
        var model = AssetDatabase.LoadAssetAtPath<GameObject>($"{Mixamo}/Chars/vanguard.fbx");
        var diffuse = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Mixamo}/Chars/vanguard_tex/vanguard_diffuse1.png");
        var normal = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Mixamo}/Chars/vanguard_tex/vanguard_normal.png");
        var bodyMat = Lit(id + "_body", body, 1f, 0.65f, diffuse, normal);
        var accentMat = Lit(id + "_accent", accent, 1f, 0.6f, diffuse, normal);
        var eyeMat = Lit(id + "_eye", Color.black, 0f, 0.9f, null, null, eye);

        var root = new GameObject(id);
        var body3d = (GameObject)Object.Instantiate(model, root.transform);
        body3d.name = "model";
        foreach (var r in body3d.GetComponentsInChildren<Renderer>())
            r.sharedMaterials = r.sharedMaterials.Select(m => m != null && m.name.ToLower().Contains("visor") ? accentMat : bodyMat).ToArray();

        if (!body3d.TryGetComponent<Animator>(out var anim)) anim = body3d.AddComponent<Animator>();
        anim.runtimeAnimatorController = ctrl;
        anim.applyRootMotion = false;
        anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        var head = anim.GetBoneTransform(HumanBodyBones.Head);
        if (head != null)
        {
            foreach (var side in new[] { -1f, 1f })
            {
                var e = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Object.DestroyImmediate(e.GetComponent<Collider>());
                e.name = "eye";
                e.GetComponent<Renderer>().sharedMaterial = eyeMat;
                e.transform.position = head.position + new Vector3(side * 0.035f, 0.075f, 0.105f);
                e.transform.localScale = new Vector3(0.035f, 0.022f, 0.02f);
                e.transform.SetParent(head, true);
            }
        }
        else Debug.LogError("[setup] no head bone on " + id);

        var f = root.AddComponent<Fighter>();
        f.botId = id; f.displayName = label; f.anim = anim;
        var path = $"{Out}/{id}.prefab";
        Debug.Log($"[setup] {id}: renderers={body3d.GetComponentsInChildren<Renderer>().Length} head={(head != null ? head.position.ToString("F2") : "none")}");
        var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
        Object.DestroyImmediate(root);
        return prefab;
    }

    static GameObject RobotBot(string id, string label, AnimatorController ctrl, GameObject model, Color tint, Color eye)
    {
        var root = new GameObject(id);
        var body3d = (GameObject)Object.Instantiate(model, root.transform);
        body3d.name = "model";
        foreach (var r in body3d.GetComponentsInChildren<Renderer>())
            r.sharedMaterials = r.sharedMaterials.Select((m, i) =>
            {
                var path = $"{Out}/{id}_robot_{i}.mat";
                var mat = new Material(m != null ? m : new Material(Shader.Find("Universal Render Pipeline/Lit")));
                mat.shader = Shader.Find("Universal Render Pipeline/Lit");
                var tex = m == null ? null : m.HasProperty("_BaseMap") && m.GetTexture("_BaseMap") != null ? m.GetTexture("_BaseMap") : m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
                var baseTex = AssetDatabase.LoadAssetAtPath<Texture2D>($"Assets/ThirdParty/Robots/{id}_base.png");
                var normTex = AssetDatabase.LoadAssetAtPath<Texture2D>($"Assets/ThirdParty/Robots/{id}_normal.png");
                if (baseTex != null) tex = baseTex;
                if (tex != null) mat.SetTexture("_BaseMap", tex);
                if (normTex != null) { mat.SetTexture("_BumpMap", normTex); mat.EnableKeyword("_NORMALMAP"); }
                Debug.Log($"[setup] {id} material {i}: {(m != null ? m.name : "none")} tex={(tex != null ? tex.name : "none")}");
                mat.SetColor("_BaseColor", tint);
                mat.SetFloat("_Metallic", 0.85f); mat.SetFloat("_Smoothness", 0.55f);
                if (mat.IsKeywordEnabled("_EMISSION") == false) { mat.EnableKeyword("_EMISSION"); mat.SetColor("_EmissionColor", Color.black); }
                AssetDatabase.CreateAsset(mat, path);
                return mat;
            }).ToArray();
        if (!body3d.TryGetComponent<Animator>(out var anim)) anim = body3d.AddComponent<Animator>();
        anim.runtimeAnimatorController = ctrl;
        anim.applyRootMotion = false;
        anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        var head = anim.GetBoneTransform(HumanBodyBones.Head);
        if (head != null)
        {
            var e = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.DestroyImmediate(e.GetComponent<Collider>());
            e.name = "eye";
            e.GetComponent<Renderer>().sharedMaterial = Lit(id + "_eye", Color.black, 0f, 0.9f, null, null, eye);
            e.transform.position = head.position + new Vector3(0, 0.05f, 0.12f);
            e.transform.localScale = new Vector3(0.09f, 0.03f, 0.03f);
            e.transform.SetParent(head, true);
        }
        else Debug.LogError("[setup] no head bone on robot " + id);
        var f = root.AddComponent<Fighter>();
        f.botId = id; f.displayName = label; f.anim = anim;
        Debug.Log($"[setup] {id}: robot model {model.name}, renderers={body3d.GetComponentsInChildren<Renderer>().Length}");
        var prefab = PrefabUtility.SaveAsPrefabAsset(root, $"{Out}/{id}.prefab");
        Object.DestroyImmediate(root);
        return prefab;
    }

    static GameObject Cube(string name, Vector3 pos, Vector3 scale, Material m, Transform parent, PrimitiveType type = PrimitiveType.Cube, Vector3? euler = null)
    {
        var g = GameObject.CreatePrimitive(type);
        Object.DestroyImmediate(g.GetComponent<Collider>());
        g.name = name;
        g.transform.SetParent(parent, false);
        g.transform.localPosition = pos; g.transform.localScale = scale;
        g.transform.localEulerAngles = euler ?? Vector3.zero;
        g.GetComponent<Renderer>().sharedMaterial = m;
        return g;
    }

    static Light Spot(string name, Vector3 pos, Vector3 target, Color c, float intensity, float angle, bool shadows)
    {
        var g = new GameObject(name);
        g.transform.position = pos;
        g.transform.LookAt(target);
        var l = g.AddComponent<Light>();
        l.type = LightType.Spot; l.color = c; l.intensity = intensity; l.spotAngle = angle; l.innerSpotAngle = angle * 0.55f; l.range = 30f;
        l.shadows = shadows ? LightShadows.Soft : LightShadows.None;
        return l;
    }

    const float Top = 1.0f; // canvas height

    // URP particle material: additive for sparks/glow/flashes, alpha-blended for smoke/debris/confetti.
    static Material Particle(string name, bool additive)
    {
        var m = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit"));
        m.SetFloat("_Surface", 1); m.SetFloat("_Blend", additive ? 2 : 0);
        m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha); m.SetFloat("_DstBlend", (float)(additive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
        m.SetFloat("_ZWrite", 0); m.SetOverrideTag("RenderType", "Transparent");
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.renderQueue = (int)RenderQueue.Transparent;
        var path = $"{Out}/Materials/{name}.mat";
        AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(m, path);
        return m;
    }

    static void Scene(GameObject botA, GameObject botB)
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var canvasMat = Lit("ring_canvas", new Color(0.78f, 0.78f, 0.75f), 0f, 0.15f);
        var apronMat = Lit("ring_apron", new Color(0.09f, 0.1f, 0.13f), 0.2f, 0.35f);
        var postMat = Lit("ring_post", new Color(0.75f, 0.75f, 0.78f), 1f, 0.75f);
        var padA = Lit("pad_brass", new Color(0.95f, 0.55f, 0.12f), 0.3f, 0.5f);
        var padB = Lit("pad_steel", new Color(0.22f, 0.25f, 0.3f), 0.3f, 0.5f);
        var ropeMats = new[] { Lit("rope_low", new Color(0.95f, 0.95f, 0.95f), 0f, 0.5f), Lit("rope_mid", new Color(0.9f, 0.5f, 0.12f), 0f, 0.5f), Lit("rope_top", new Color(0.95f, 0.95f, 0.95f), 0f, 0.5f) };
        var floorMat = Lit("arena_floor", new Color(0.035f, 0.037f, 0.045f), 0.4f, 0.55f);
        var standMat = Lit("arena_stands", new Color(0.05f, 0.055f, 0.07f), 0f, 0.2f);
        var logoMat = Lit("ring_logo", new Color(1f, 0.55f, 0.12f), 0f, 0.3f);

        var ring = new GameObject("Ring").transform;
        Cube("platform", new Vector3(0, Top * 0.5f - 0.01f, 0), new Vector3(6.8f, Top, 6.8f), apronMat, ring);
        Cube("canvas", new Vector3(0, Top - 0.005f, 0), new Vector3(6.6f, 0.02f, 6.6f), canvasMat, ring);
        Cube("logo", new Vector3(0, Top + 0.008f, 0), new Vector3(1.6f, 0.002f, 1.6f), logoMat, ring, PrimitiveType.Cylinder);
        const float c = 3.1f;
        int i = 0;
        foreach (var (x, z) in new[] { (-c, -c), (c, -c), (c, c), (-c, c) })
        {
            Cube("post", new Vector3(x, Top + 0.75f, z), new Vector3(0.14f, 0.75f, 0.14f), postMat, ring, PrimitiveType.Cylinder);
            Cube("pad", new Vector3(x * 0.985f, Top + 0.85f, z * 0.985f), new Vector3(0.24f, 1.0f, 0.24f), (i == 0 || i == 2) ? padA : padB, ring);
            i++;
        }
        float[] h = { 0.42f, 0.82f, 1.22f };
        for (int k = 0; k < 3; k++)
        {
            float y = Top + h[k];
            Cube("rope", new Vector3(0, y, -c), new Vector3(0.045f, c, 0.045f), ropeMats[k], ring, PrimitiveType.Cylinder, new Vector3(0, 0, 90));
            Cube("rope", new Vector3(0, y, c), new Vector3(0.045f, c, 0.045f), ropeMats[k], ring, PrimitiveType.Cylinder, new Vector3(0, 0, 90));
            Cube("rope", new Vector3(-c, y, 0), new Vector3(0.045f, c, 0.045f), ropeMats[k], ring, PrimitiveType.Cylinder, new Vector3(90, 0, 0));
            Cube("rope", new Vector3(c, y, 0), new Vector3(0.045f, c, 0.045f), ropeMats[k], ring, PrimitiveType.Cylinder, new Vector3(90, 0, 0));
        }
        Cube("steps", new Vector3(-3.9f, 0.25f, -3.9f), new Vector3(1f, 0.5f, 1f), apronMat, ring, PrimitiveType.Cube, new Vector3(0, 45, 0));

        var arena = new GameObject("Arena").transform;
        Cube("floor", new Vector3(0, 0, 0), new Vector3(8, 1, 8), floorMat, arena, PrimitiveType.Plane);
        for (int s = 0; s < 4; s++)
        {
            var rot = Quaternion.Euler(0, s * 90, 0);
            for (int tier = 0; tier < 4; tier++)
            {
                var g = Cube("stand", rot * new Vector3(0, 0.5f + tier * 1f, 11f + tier * 1.6f), new Vector3(24f + tier * 3, 1f + tier * 2f, 1.6f), standMat, arena);
                g.transform.localRotation = rot;
            }
        }

        // LED ribbons along the front of each stand and an overhead truss with glowing panels (bloom picks them up).
        var ledA = Lit("led_brass", Color.black, 0f, 0.5f, null, null, new Color(1f, 0.5f, 0.12f) * 3.5f);
        var ledB = Lit("led_steel", Color.black, 0f, 0.5f, null, null, new Color(0.35f, 0.55f, 1f) * 3.5f);
        var panel = Lit("light_panel", Color.black, 0f, 0.5f, null, null, new Color(1f, 0.95f, 0.85f) * 6f);
        var truss = Lit("truss", new Color(0.12f, 0.12f, 0.14f), 0.8f, 0.4f);
        for (int s = 0; s < 4; s++)
        {
            var rot = Quaternion.Euler(0, s * 90, 0);
            var g = Cube("led", rot * new Vector3(0, 1.02f, 10.18f), new Vector3(24f, 0.08f, 0.04f), s % 2 == 0 ? ledA : ledB, arena);
            g.transform.localRotation = rot;
            var t = Cube("truss", rot * new Vector3(0, Top + 6.2f, 2.6f), new Vector3(5.6f, 0.18f, 0.18f), truss, arena);
            t.transform.localRotation = rot;
            for (int k = -1; k <= 1; k++)
            {
                var pn = Cube("panel", rot * new Vector3(k * 1.6f, Top + 6.05f, 2.6f), new Vector3(0.9f, 0.04f, 0.5f), panel, arena);
                pn.transform.localRotation = rot;
            }
        }

        // Lights: warm key with shadows, brass and steel rims, low cool fill.
        Spot("Key", new Vector3(0, 9f, -4f), new Vector3(0, Top, 0), new Color(1f, 0.95f, 0.86f), 140f, 62f, true);
        Spot("RimBrass", new Vector3(-7f, 7f, 5f), new Vector3(0, Top + 1f, 0), new Color(1f, 0.62f, 0.25f), 90f, 40f, false);
        Spot("RimSteel", new Vector3(7f, 7f, 5f), new Vector3(0, Top + 1f, 0), new Color(0.55f, 0.7f, 1f), 90f, 40f, false);
        var fill = new GameObject("Fill").AddComponent<Light>();
        fill.type = LightType.Directional; fill.intensity = 0.35f; fill.color = new Color(0.6f, 0.68f, 0.85f);
        fill.transform.rotation = Quaternion.Euler(35, 150, 0);
        fill.shadows = LightShadows.None;

        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.16f, 0.17f, 0.21f);
        RenderSettings.skybox = null;
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogDensity = 0.04f;
        RenderSettings.fogColor = new Color(0.045f, 0.05f, 0.065f);

        var probe = new GameObject("Reflections").AddComponent<ReflectionProbe>();
        probe.transform.position = new Vector3(0, Top + 1.5f, 0);
        probe.mode = ReflectionProbeMode.Realtime;
        probe.refreshMode = ReflectionProbeRefreshMode.OnAwake;
        probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.NoTimeSlicing;
        probe.size = new Vector3(40, 16, 40);
        probe.resolution = 128;
        probe.clearFlags = ReflectionProbeClearFlags.SolidColor;
        probe.backgroundColor = new Color(0.08f, 0.085f, 0.1f);

        var vol = new GameObject("PostFX").AddComponent<Volume>();
        vol.isGlobal = true;
        var profilePath = Out + "/PostFX.asset";
        AssetDatabase.DeleteAsset(profilePath);
        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        AssetDatabase.CreateAsset(profile, profilePath);
        var bloom = profile.Add<Bloom>(true); bloom.threshold.Override(1.0f); bloom.intensity.Override(0.7f); bloom.scatter.Override(0.6f);
        var tone = profile.Add<Tonemapping>(true); tone.mode.Override(TonemappingMode.ACES);
        var vig = profile.Add<Vignette>(true); vig.intensity.Override(0.32f);
        var ca = profile.Add<ColorAdjustments>(true); ca.postExposure.Override(0.35f); ca.contrast.Override(12f);
        profile.Add<ChromaticAberration>(true).intensity.Override(0f); // Game pulses it on heavy hits
        foreach (var comp in profile.components) AssetDatabase.AddObjectToAsset(comp, profile);
        EditorUtility.SetDirty(profile);
        vol.sharedProfile = profile;

        var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
        var cam = camGo.AddComponent<Camera>();
        cam.fieldOfView = 36f; cam.nearClipPlane = 0.1f; cam.farClipPlane = 80f;
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.03f, 0.033f, 0.045f);
        cam.allowMSAA = false; cam.allowHDR = true;
        camGo.transform.position = new Vector3(5, 3, 0);
        var urp = cam.GetUniversalAdditionalCameraData();
        urp.renderPostProcessing = true;
        urp.antialiasing = AntialiasingMode.FastApproximateAntialiasing;
        camGo.AddComponent<AudioListener>();

        var fa = (GameObject)PrefabUtility.InstantiatePrefab(botA);
        fa.transform.position = new Vector3(0, Top, -1.15f);
        var fb = (GameObject)PrefabUtility.InstantiatePrefab(botB);
        fb.transform.SetPositionAndRotation(new Vector3(0, Top, 1.15f), Quaternion.Euler(0, 180, 0));

        new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

        var gameGo = new GameObject("Game");
        var fx = gameGo.AddComponent<Fx>();
        fx.add = Particle("fx_add", true); fx.alpha = Particle("fx_alpha", false); fx.ringTop = Top;
        fx.crowdMat = Lit("crowd", Color.gray, 0f, 0.25f); fx.crowdMat.enableInstancing = true;
        var game = gameGo.AddComponent<Game>();
        game.a = fa.GetComponent<Fighter>(); game.b = fb.GetComponent<Fighter>(); game.cam = cam;

        var scenePath = "Assets/Scenes/Main.unity";
        EditorSceneManager.SaveScene(scene, scenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(scenePath, true) };
    }

    [MenuItem("Iron Corner/Build macOS Player")]
    public static void BuildMac()
    {
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/Main.unity" },
            locationPathName = "Builds/IronCorner.app",
            target = BuildTarget.StandaloneOSX,
            options = BuildOptions.None,
        });
        Debug.Log($"[setup] mac build: {report.summary.result} {report.summary.totalSize / (1024 * 1024)} MB");
        if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
    }

    // APK to share with judges (debug-signed, sideloaded; not for Play). IL2CPP + ARM64: newer phones are 64-bit only.
    [MenuItem("Iron Corner/Build Android APK")]
    public static void BuildAndroid()
    {
        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        EditorUserBuildSettings.buildAppBundle = false;
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/Main.unity" },
            locationPathName = "Builds/IronCorner.apk",
            target = BuildTarget.Android,
            options = BuildOptions.None,
        });
        Debug.Log($"[setup] android build: {report.summary.result} {report.summary.totalSize / (1024 * 1024)} MB");
        if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
    }

    // Xcode project for a personal-device install (automatic signing, team J99B88X7CK). Not for the App Store.
    [MenuItem("Iron Corner/Build iOS Xcode Project")]
    public static void BuildIOS()
    {
        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.iOS, BuildTarget.iOS);
        PlayerSettings.iOS.appleDeveloperTeamID = "J99B88X7CK";
        PlayerSettings.iOS.appleEnableAutomaticSigning = true;
        if (AssetImporter.GetAtPath("Assets/Plugins/iOS/WorldAuth.mm") is PluginImporter wa) // in-app World sheet
        {
            wa.SetCompatibleWithPlatform(BuildTarget.iOS, true);
            wa.SetPlatformData(BuildTarget.iOS, "FrameworkDependencies", "AuthenticationServices;");
            wa.SaveAndReimport();
        }
        PlayerSettings.iOS.iOSUrlSchemes = new[] { "ironcorner" }; // the 0G credits page hands the player back here
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/Main.unity" },
            locationPathName = "Builds/iOS",
            target = BuildTarget.iOS,
            options = BuildOptions.None,
        });
        Debug.Log($"[setup] ios build: {report.summary.result}");
        if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
    }
}
