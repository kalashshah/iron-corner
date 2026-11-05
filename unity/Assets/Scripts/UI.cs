using System;
using UnityEngine;
using UnityEngine.UI;

// Tiny runtime UGUI builder. Screens are built in code so there are no prefabs to keep in sync.
public static class UI
{
    public static Font Font;
    public static Sprite Round;

    public static readonly Color Bg = new(0.05f, 0.055f, 0.07f, 0.94f);
    public static readonly Color Card = new(0.09f, 0.1f, 0.12f, 0.9f);
    public static readonly Color Line = new(1f, 1f, 1f, 0.08f);
    public static readonly Color Accent = new(1f, 0.58f, 0.13f);
    public static readonly Color Brass = new(0.88f, 0.68f, 0.34f);
    public static readonly Color Ink = new(0.95f, 0.95f, 0.96f);
    public static readonly Color Dim = new(0.62f, 0.65f, 0.7f);
    public static readonly Color Green = new(0.36f, 0.86f, 0.52f);
    public static readonly Color Red = new(0.95f, 0.32f, 0.26f);
    public static readonly Color Steel = new(0.2f, 0.22f, 0.26f, 0.95f);

    public static void Init()
    {
        Font = Font.CreateDynamicFontFromOSFont(new[] { "Avenir Next Condensed", "DIN Condensed", "Roboto Condensed", "Helvetica Neue", "Arial", "Arial Unicode MS" }, 48);
        if (Font == null) Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        const int s = 64, r = 18;
        var t = new Texture2D(s, s, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < s; y++)
        for (int x = 0; x < s; x++)
        {
            float cx = Mathf.Clamp(x + 0.5f, r, s - r), cy = Mathf.Clamp(y + 0.5f, r, s - r);
            float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
            t.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(r - d + 0.5f)));
        }
        t.Apply();
        Round = Sprite.Create(t, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 100, 0, SpriteMeshType.FullRect, new Vector4(r, r, r, r));
    }

    public static GameObject Go(Transform parent, string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go;
    }

    public static RectTransform Rt(Transform parent, string name) => (RectTransform)Go(parent, name).transform;

    // anchors (ax,ay)-(bx,by) in parent space, then pixel offsets left/bottom/right/top from those anchors.
    public static T At<T>(this T c, float ax, float ay, float bx, float by, float l = 0, float b = 0, float r = 0, float t = 0) where T : Component
    {
        var rt = (RectTransform)c.transform;
        rt.anchorMin = new Vector2(ax, ay); rt.anchorMax = new Vector2(bx, by);
        rt.offsetMin = new Vector2(l, b); rt.offsetMax = new Vector2(r, t);
        return c;
    }

    public static Image Box(Transform p, Color c, bool rounded = true, string name = "box")
    {
        var img = Go(p, name).AddComponent<Image>();
        img.color = c;
        if (rounded) { img.sprite = Round; img.type = Image.Type.Sliced; img.pixelsPerUnitMultiplier = 2f; }
        return img;
    }

    public static Text Label(Transform p, string s, int size, Color c, TextAnchor a = TextAnchor.MiddleLeft, FontStyle st = FontStyle.Normal)
    {
        var t = Go(p, "text").AddComponent<Text>();
        t.font = Font; t.text = s; t.fontSize = size; t.color = c; t.alignment = a; t.fontStyle = st;
        t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false; t.supportRichText = true;
        return t.At(0, 0, 1, 1);
    }

    public static Button Btn(Transform p, string label, Color bg, Color fg, Action click, int size = 34)
    {
        var img = Box(p, bg, true, "btn:" + label);
        var b = img.gameObject.AddComponent<Button>();
        var cb = b.colors; cb.highlightedColor = new Color(1.1f, 1.1f, 1.1f); cb.pressedColor = new Color(0.8f, 0.8f, 0.8f); b.colors = cb;
        if (click != null) b.onClick.AddListener(() => click());
        Label(img.transform, label, size, fg, TextAnchor.MiddleCenter, FontStyle.Bold).At(0, 0, 1, 1, 12, 0, -12, 0);
        return b;
    }

    public static void SetLabel(this Button b, string s) => b.GetComponentInChildren<Text>().text = s;

    public static InputField Field(Transform p, string placeholder, string value, int size = 28)
    {
        var img = Box(p, new Color(0, 0, 0, 0.55f), true, "field");
        var f = img.gameObject.AddComponent<InputField>();
        var txt = Label(img.transform, value, size, Ink).At(0, 0, 1, 1, 18, 0, -18, 0);
        txt.supportRichText = false;
        txt.horizontalOverflow = HorizontalWrapMode.Overflow;
        var ph = Label(img.transform, placeholder, size, Dim, TextAnchor.MiddleLeft, FontStyle.Italic).At(0, 0, 1, 1, 18, 0, -18, 0);
        f.textComponent = txt; f.placeholder = ph; f.text = value;
        return f;
    }

    public static VerticalLayoutGroup Column(Transform p, float spacing, string name = "column")
    {
        var v = Go(p, name).AddComponent<VerticalLayoutGroup>();
        v.spacing = spacing; v.childControlHeight = true; v.childControlWidth = true; v.childForceExpandHeight = false; v.childForceExpandWidth = true;
        return v;
    }

    public static T H<T>(this T c, float height) where T : Component
    {
        if (!c.TryGetComponent<LayoutElement>(out var le)) le = c.gameObject.AddComponent<LayoutElement>();
        le.preferredHeight = height; le.minHeight = height;
        return c;
    }

    // Move id → what a coach would say ("dodge_r" → "slip right").
    public static string Nice(string m) => m switch
    {
        "hook_l" => "left hook", "hook_r" => "right hook", "body" => "body shot", "dodge_l" => "slip left", "dodge_r" => "slip right",
        "step_in" => "step in", "step_back" => "step back", null => "", _ => m,
    };

    public static string Usd(double cents) => (cents < 0 ? "-$" : "$") + Math.Abs(cents / 100.0).ToString("0.00");
    public static string Short(string h, int keep = 6) => string.IsNullOrEmpty(h) || h.Length <= keep * 2 + 1 ? h : h.Substring(0, keep) + "…" + h.Substring(h.Length - 4);
}
