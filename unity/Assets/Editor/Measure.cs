using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

// Run: Unity -batchmode -quit -projectPath . -executeMethod Measure.Run (writes Logs/measure.txt). Samples every clip on the bot and logs hand/head motion so Fighter timing and reach match the animations.
public static class Measure
{
    public static void Run()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Game/bot_a.prefab");
        var go = (GameObject)Object.Instantiate(prefab);
        var anim = go.GetComponentInChildren<Animator>();
        var root = anim.transform;
        Transform B(HumanBodyBones b) => anim.GetBoneTransform(b);
        var lh = B(HumanBodyBones.LeftHand); var rh = B(HumanBodyBones.RightHand); var head = B(HumanBodyBones.Head); var hips = B(HumanBodyBones.Hips);
        var sb = new StringBuilder();
        foreach (var path in Directory.GetFiles("Assets/ThirdParty/Mixamo/Clips", "*.fbx").OrderBy(p => p))
        {
            var clip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview__"));
            if (clip == null) continue;
            float bestL = -9, bestR = -9, tL = 0, tR = 0; Vector3 head0 = Vector3.zero, headMax = Vector3.zero; float headD = 0;
            Vector3 l0 = Vector3.zero, r0 = Vector3.zero;
            int n = 60;
            for (int i = 0; i <= n; i++)
            {
                float t = clip.length * i / n;
                clip.SampleAnimation(go.transform.GetChild(0).gameObject, t);
                var L = root.InverseTransformPoint(lh.position); var R = root.InverseTransformPoint(rh.position); var H = root.InverseTransformPoint(head.position);
                if (i == 0) { head0 = H; l0 = L; r0 = R; }
                if (L.z > bestL) { bestL = L.z; tL = (float)i / n; }
                if (R.z > bestR) { bestR = R.z; tR = (float)i / n; }
                var d = H - head0; if (d.magnitude > headD) { headD = d.magnitude; headMax = d; }
            }
            var disp = clip.averageSpeed * clip.length;
            sb.AppendLine($"{clip.name,-20} len={clip.length:F2} rootDisp=({disp.x:F2},{disp.z:F2}) model=({root.localPosition.x:F2},{root.localPosition.z:F2}) L: z0={l0.z:F2} max={bestL:F2}@{tL:F2} (x{l0.x:F2},y{l0.y:F2})  R: z0={r0.z:F2} max={bestR:F2}@{tR:F2} (x{r0.x:F2},y{r0.y:F2})  headΔ=({headMax.x:F2},{headMax.y:F2},{headMax.z:F2}) head0=({head0.x:F2},{head0.y:F2},{head0.z:F2})");
        }
        File.WriteAllText("Logs/measure.txt", sb.ToString());
        Debug.Log("[measure]\n" + sb);
    }
}
