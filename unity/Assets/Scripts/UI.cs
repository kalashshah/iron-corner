using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Tiny runtime UGUI builder. Screens are built in code so there are no prefabs to keep in sync.
// Look: a shooter-style lobby. Sharp translucent black panels, condensed type (Barlow Condensed), amber for the
// one thing to press and the active tab, Material Icons for every glyph. TextMesh Pro for all text.
public static class UI
{
    // Fonts are built at startup from the TTFs in Resources/Fonts (dynamic SDF atlases).
    public static TMP_FontAsset Font, Bold, Heavy, Italic, Icons;
    static readonly Dictionary<TMP_FontAsset, Material> shadowed = new();

    public static readonly Color Bg = new(0.03f, 0.03f, 0.032f, 0.88f);
    public static readonly Color Card = new(0.02f, 0.02f, 0.022f, 0.74f);    // standard translucent panel
    public static readonly Color Line = new(1f, 1f, 1f, 0.12f);
    public static readonly Color Accent = new(1f, 0.64f, 0.24f);           // amber: primary blocks, active tab, selection
    public static readonly Color Amber = Accent;
    public static readonly Color Brass = new(0.98f, 0.9f, 0.78f);          // meter fills, money
    public static readonly Color Ink = new(0.97f, 0.96f, 0.94f);
    public static readonly Color Dim = new(0.66f, 0.64f, 0.6f);
    public static readonly Color Green = new(0.45f, 0.86f, 0.5f);
    public static readonly Color Red = new(0.94f, 0.33f, 0.28f);
    public static readonly Color Steel = new(0.04f, 0.04f, 0.045f, 0.72f);  // secondary button / row
    public static readonly Color Outline = new(0, 0, 0, 0.35f);             // secondary with a hairline frame
    public static readonly Color Dark = new(0.08f, 0.05f, 0.02f);           // text on amber
    public static readonly Color Row = new(1f, 1f, 1f, 0.05f);

    public static Sprite Circle, CircleRing, Ramp, Slant, Glow;
    public static Action Click; // UI tick sound, hooked to Fx by Game

    public static void Init()
    {
        TMP_FontAsset Load(string file)
        {
            var src = Resources.Load<Font>("Fonts/" + file);
            var fa = TMP_FontAsset.CreateFontAsset(src, 90, 9, UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic);
            fa.name = file;
            var sh = Shader.Find("TextMeshPro/Mobile/Distance Field");
            if (sh != null) fa.material.shader = sh;
            return fa;
        }
        Font = Load("BarlowCondensed-SemiBold");
        Bold = Load("BarlowCondensed-Bold");
        Heavy = Load("BarlowCondensed-ExtraBold");
        Italic = Load("BarlowCondensed-ExtraBoldItalic");
        Icons = Load("MaterialIcons-Regular");
        foreach (var f in new[] { Font, Bold, Heavy, Italic, Icons }) if (f != Icons) f.fallbackFontAssetTable = new List<TMP_FontAsset> { Icons };

        Sprite Make(Func<float, float, float> alpha, int w, int h, Vector4 border = default)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) t.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(alpha(x + 0.5f, y + 0.5f))));
            t.Apply();
            return Sprite.Create(t, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100, 0, SpriteMeshType.FullRect, border);
        }
        const int c = 128;
        float Cd(float x, float y) => Mathf.Sqrt((x - c / 2f) * (x - c / 2f) + (y - c / 2f) * (y - c / 2f)) - (c / 2f - 1);
        Circle = Make((x, y) => 0.5f - Cd(x, y), c, c);
        CircleRing = Make((x, y) => { float d = Cd(x, y); return Mathf.Min(0.5f - d, d + 3.5f); }, c, c);
        // Horizontal ramp: solid on the left fading out to the right (list rows, the bottom strip).
        Ramp = Make((x, y) => Mathf.Lerp(1f, 0f, Mathf.Pow(x / 256f, 1.4f)), 256, 4);
        // Block with a slanted right edge (the big amber action blocks).
        Slant = Make((x, y) => (256 - 28 * (y / 64f)) - x + 0.5f, 256, 64, new Vector4(0, 0, 40, 0));
        // Soft square glow for selection and the main action.
        Glow = Make((x, y) => { float dx = Mathf.Max(0, Mathf.Abs(x - 64) - 24), dy = Mathf.Max(0, Mathf.Abs(y - 64) - 24); return Mathf.Pow(1 - Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy) / 40f), 2.2f); }, 128, 128, new Vector4(60, 60, 60, 60));
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

    // Sharp panel. `rounded` is kept for call sites; panels are square-cornered in this style.
    public static Image Box(Transform p, Color c, bool rounded = true, string name = "box", float corner = 0)
    {
        var img = Go(p, name).AddComponent<Image>();
        img.color = c;
        if (c == Card || c == Bg) Panel(img);
        return img;
    }

    // Hairline frame on a panel (1px, faint), drawn as four thin edges so the fill stays one quad.
    public static Image Panel(Image img, Color? rim = null)
    {
        var col = rim ?? Line;
        foreach (var (ax, ay, bx, by, l, b, r, t) in new[] { (0f, 1f, 1f, 1f, 0f, -1f, 0f, 0f), (0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f), (0f, 0f, 0f, 1f, 0f, 0f, 1f, 0f), (1f, 0f, 1f, 1f, -1f, 0f, 0f, 0f) })
        {
            var e = Go(img.transform, "edge").AddComponent<Image>(); e.color = col; e.raycastTarget = false;
            e.At(ax, ay, bx, by, l, b, r, t);
        }
        return img;
    }

    // Soft glow behind an element (selection, main action).
    public static Image Halo(Image img, Color c, float alpha = 0.35f)
    {
        var h = Go(img.transform, "halo").AddComponent<Image>(); h.sprite = Glow; h.type = Image.Type.Sliced; h.color = new Color(c.r, c.g, c.b, alpha); h.raycastTarget = false;
        h.At(0, 0, 1, 1, -30, -30, 30, 30); h.transform.SetAsFirstSibling();
        return h;
    }

    static TextAlignmentOptions Align(TextAnchor a) => a switch
    {
        TextAnchor.UpperLeft => TextAlignmentOptions.TopLeft, TextAnchor.UpperCenter => TextAlignmentOptions.Top, TextAnchor.UpperRight => TextAlignmentOptions.TopRight,
        TextAnchor.MiddleLeft => TextAlignmentOptions.MidlineLeft, TextAnchor.MiddleCenter => TextAlignmentOptions.Midline, TextAnchor.MiddleRight => TextAlignmentOptions.MidlineRight,
        TextAnchor.LowerLeft => TextAlignmentOptions.BottomLeft, TextAnchor.LowerCenter => TextAlignmentOptions.Bottom, _ => TextAlignmentOptions.BottomRight,
    };

    public static void SetAlign(this TMP_Text t, TextAnchor a) => t.alignment = Align(a);

    // Light text gets a soft drop shadow (TMP underlay) so it reads over the 3D ring.
    static Material Shadowed(TMP_FontAsset f)
    {
        if (shadowed.TryGetValue(f, out var m)) return m;
        m = new Material(f.material);
        m.EnableKeyword("UNDERLAY_ON");
        m.SetColor("_UnderlayColor", new Color(0, 0, 0, 0.55f));
        m.SetFloat("_UnderlayOffsetX", 0.25f); m.SetFloat("_UnderlayOffsetY", -0.35f); m.SetFloat("_UnderlaySoftness", 0.35f); m.SetFloat("_UnderlayDilate", 0.1f);
        return shadowed[f] = m;
    }

    // FontStyle picks the weight: Normal = SemiBold, Bold = Bold, Italic = ExtraBold Italic, BoldAndItalic = ExtraBold.
    public static TMP_Text Label(Transform p, string s, int size, Color c, TextAnchor a = TextAnchor.MiddleLeft, FontStyle st = FontStyle.Normal)
    {
        var t = Go(p, "text").AddComponent<TextMeshProUGUI>();
        t.font = st switch { FontStyle.Bold => Bold, FontStyle.Italic => Italic, FontStyle.BoldAndItalic => Heavy, _ => Font };
        if (c.r + c.g + c.b > 1.5f) t.fontSharedMaterial = Shadowed(t.font);
        t.text = s; t.fontSize = size; t.color = c; t.alignment = Align(a);
        t.richText = true; t.raycastTarget = false; t.textWrappingMode = TextWrappingModes.Normal;
        t.overflowMode = TextOverflowModes.Ellipsis;
        // Shrink-to-fit instead of spilling out of the box.
        t.enableAutoSizing = true; t.fontSizeMax = size; t.fontSizeMin = Mathf.Max(10, size * 0.55f);
        return t.At(0, 0, 1, 1);
    }

    // Heavy italic display text for titles, banners and big numbers.
    public static TMP_Text Title(Transform p, string s, int size, Color c, TextAnchor a = TextAnchor.MiddleLeft) => Label(p, s, size, c, a, FontStyle.Italic);

    // One Material Icons glyph ("home", "bolt", …).
    public static readonly Dictionary<string, string> Glyph = new()
    {
        ["home"] = "", ["brain"] = "", ["fight"] = "", ["camp"] = "", ["wallet"] = "", ["bolt"] = "",
        ["gear"] = "", ["stats"] = "", ["mic"] = "", ["send"] = "", ["info"] = "", ["back"] = "",
        ["play"] = "", ["refresh"] = "", ["close"] = "", ["shield"] = "", ["trophy"] = "", ["globe"] = "",
        ["check"] = "", ["up"] = "", ["down"] = "", ["left"] = "", ["right"] = "", ["glove"] = "", ["chevron"] = "", ["medal"] = "", ["fire"] = "", ["menu"] = "", ["rank"] = "", ["diamond"] = "",
    };

    public static TMP_Text IconImg(Transform p, string name, Color c, int size = 44)
    {
        var t = Go(p, "icon:" + name).AddComponent<TextMeshProUGUI>();
        t.font = Icons; t.text = Glyph[name]; t.fontSize = size; t.color = c; t.alignment = TextAlignmentOptions.Center; t.raycastTarget = false;
        t.enableAutoSizing = true; t.fontSizeMax = size; t.fontSizeMin = 10; t.textWrappingMode = TextWrappingModes.NoWrap;
        return t.At(0, 0, 1, 1);
    }

    // Inline icon inside a label string (the icon font is every text font's fallback).
    public static string I(string name) => Glyph[name];

    // Rectangular button. bg picks the style: Accent = amber block, Outline = dark with a frame, Steel = dark row, clear = text only.
    public static Button Btn(Transform p, string label, Color bg, Color fg, Action click, int size = 28)
    {
        var img = Box(p, bg, true, "btn:" + label);
        if (bg == Outline) Panel(img, new Color(1, 1, 1, 0.28f));
        if (bg == Accent) { Slanted(img); Highlight(img); }
        var b = img.gameObject.AddComponent<Button>();
        var cb = b.colors; cb.highlightedColor = new Color(1.08f, 1.08f, 1.08f); cb.pressedColor = new Color(0.78f, 0.78f, 0.78f); cb.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.55f); cb.fadeDuration = 0.06f; b.colors = cb;
        b.navigation = new Navigation { mode = Navigation.Mode.None };
        img.gameObject.AddComponent<Press>();
        if (click != null) b.onClick.AddListener(() => click());
        Label(img.transform, label, size, fg, TextAnchor.MiddleCenter, FontStyle.Bold).At(0, 0, 1, 1, 14, 0, -14, 0);
        return b;
    }

    static void Slanted(Image img) { img.sprite = Slant; img.type = Image.Type.Sliced; img.pixelsPerUnitMultiplier = 1; }

    // Lighter top half and a darker bottom lip on amber blocks, so they read as lit rather than flat.
    static void Highlight(Image img)
    {
        var h = Go(img.transform, "hi").AddComponent<Image>(); h.color = new Color(1, 0.95f, 0.8f, 0.1f); h.raycastTarget = false;
        Slanted(h); h.At(0, 0.5f, 1, 1);
        var lip = Go(img.transform, "lip").AddComponent<Image>(); lip.color = new Color(0.5f, 0.24f, 0.02f, 0.85f); lip.raycastTarget = false;
        Slanted(lip); lip.At(0, 0, 1, 0, 0, 0, 0, 6);
    }

    public static void SetLabel(this Button b, string s) => b.GetComponentInChildren<TMP_Text>().text = s;

    // Main action. primary: amber block with a slanted right edge and a soft glow; secondary: dark framed block.
    public static Button Big(Transform p, string label, bool primary, Action click, int size = 34, bool pulse = false)
    {
        var b = Btn(p, label, primary ? Accent : Outline, primary ? Dark : Ink, click, size);
        b.GetComponent<Press>().pulse = pulse;
        if (primary) Halo(b.GetComponent<Image>(), Accent, 0.18f);
        return b;
    }

    // Round dark button with an icon (stats, settings, info).
    public static Button IconBtn(Transform p, string icon, Action click, Color? bg = null, Color? fg = null)
    {
        var img = Go(p, "ibtn:" + icon).AddComponent<Image>(); img.sprite = Circle; img.color = bg ?? new Color(0, 0, 0, 0.55f);
        var ring = Go(img.transform, "ring").AddComponent<Image>(); ring.sprite = CircleRing; ring.color = new Color(1, 1, 1, 0.3f); ring.raycastTarget = false; ring.At(0, 0, 1, 1);
        var b = img.gameObject.AddComponent<Button>(); b.navigation = new Navigation { mode = Navigation.Mode.None };
        img.gameObject.AddComponent<Press>();
        if (click != null) b.onClick.AddListener(() => click());
        IconImg(img.transform, icon, fg ?? Ink, 40).At(0.18f, 0.18f, 0.82f, 0.82f);
        return b;
    }

    // Sub-screen header: back icon and the title, top left.
    public static Button TopBar(Transform p, string title, Action back)
    {
        var g = Rt(p, "topbar").At(0, 1, 0, 1, 36, -118, 900, -38);
        var b = IconBtn(g, "back", back).At(0, 0, 0, 1, 0, 0, 80, 0);
        if (!string.IsNullOrEmpty(title)) Label(g, title, 44, Ink, TextAnchor.MiddleLeft, FontStyle.BoldAndItalic).At(0, 0, 1, 1, 104, 0, 0, 0);
        return b;
    }

    // Red notification dot on an element's top-right corner.
    public static GameObject Badge(Component c)
    {
        var d = Go(c.transform, "badge").AddComponent<Image>(); d.sprite = Circle; d.color = Red; d.raycastTarget = false;
        d.At(1, 1, 1, 1, -20, -20, 6, 6);
        return d.gameObject;
    }

    // Small uppercase section caption.
    public static TMP_Text Caption(Transform p, string s) { var t = Label(p, s, 20, Dim, TextAnchor.LowerLeft, FontStyle.Bold); t.characterSpacing = 6; return t; }

    // Thin meter: dark track, light fill. Returns the fill (SetFill drives its right anchor).
    public static RectTransform Meter(Transform p)
    {
        var bg = Box(p, new Color(1, 1, 1, 0.14f), false, "track").At(0, 0, 1, 1);
        var f = Box(bg.transform, Brass, false, "fill").At(0, 0, 1, 1);
        return f.rectTransform;
    }

    // Edge scrim: black fading to clear over `width` px, from the left (or right) edge.
    public static void Fade(Transform p, float width, bool right = false)
    {
        var t = new Texture2D(64, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        for (int x = 0; x < 64; x++) { float k = right ? x / 63f : 1 - x / 63f; t.SetPixel(x, 0, new Color(0, 0, 0, 0.8f * Mathf.Pow(k, 1.3f))); }
        t.Apply();
        var img = Go(p, "fade").AddComponent<RawImage>(); img.texture = t; img.raycastTarget = false;
        if (right) img.At(1, 0, 1, 1, -width, -200, 200, 200); else img.At(0, 0, 0, 1, -200, -200, width, 200);
        img.transform.SetAsFirstSibling();
    }

    // Bottom scrim: black at the bottom fading up over `height` px (under the lobby strip).
    public static void FadeUp(Transform p, float height)
    {
        var t = new Texture2D(1, 64, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < 64; y++) t.SetPixel(0, y, new Color(0, 0, 0, 0.85f * Mathf.Pow(1 - y / 63f, 1.4f)));
        t.Apply();
        var img = Go(p, "fadeUp").AddComponent<RawImage>(); img.texture = t; img.raycastTarget = false;
        img.At(0, 0, 1, 0, -200, -200, 200, height); img.transform.SetAsFirstSibling();
    }

    // Disc portrait with a ring and an initial (fight health bars).
    public static TMP_Text Portrait(Transform p, Color ring, string initial)
    {
        var disc = Go(p, "portrait").AddComponent<Image>(); disc.sprite = Circle; disc.color = new Color(0.05f, 0.05f, 0.055f, 0.9f); disc.raycastTarget = false;
        var rim = Go(disc.transform, "rim").AddComponent<Image>(); rim.sprite = CircleRing; rim.color = ring; rim.raycastTarget = false; rim.At(0, 0, 1, 1);
        return Label(disc.transform, initial, 44, ring, TextAnchor.MiddleCenter, FontStyle.BoldAndItalic).At(0, 0, 1, 1, 0, 2, 0, 0);
    }

    public static TMP_InputField Field(Transform p, string placeholder, string value, int size = 26)
    {
        var img = Box(p, new Color(0, 0, 0, 0.6f), false, "field");
        Panel(img, new Color(1, 1, 1, 0.2f));
        var area = Rt(img.transform, "area").At(0, 0, 1, 1, 22, 0, -22, 0);
        area.gameObject.AddComponent<RectMask2D>();
        var txt = (TextMeshProUGUI)Label(area, "", size, Ink);
        txt.enableAutoSizing = false; txt.fontSize = size; txt.textWrappingMode = TextWrappingModes.NoWrap; txt.overflowMode = TextOverflowModes.Overflow; txt.richText = false;
        var ph = Label(area, placeholder, size, Dim);
        ph.enableAutoSizing = false; ph.fontSize = size; ph.fontStyle = FontStyles.Italic;
        var f = img.gameObject.AddComponent<TMP_InputField>();
        f.textViewport = area; f.textComponent = txt; f.placeholder = ph; f.fontAsset = Font; f.pointSize = size;
        f.text = value;
        f.selectionColor = new Color(1, 0.64f, 0.24f, 0.35f); f.caretColor = Accent; f.customCaretColor = true; f.caretWidth = 3;
        return f;
    }

    // Screens and pop-ups ease in (fade + slight zoom) every time they're shown.
    public static GameObject Appear(this GameObject go, float from = 0.97f) { go.AddComponent<Appear>().from = from; return go; }

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

// Tactile buttons: grow a touch on hover, squash on press, spring back. Plays the UI tick.
public class Press : MonoBehaviour, UnityEngine.EventSystems.IPointerEnterHandler, UnityEngine.EventSystems.IPointerExitHandler, UnityEngine.EventSystems.IPointerDownHandler, UnityEngine.EventSystems.IPointerUpHandler
{
    public bool pulse; // primary call to action breathes so the eye finds it
    float target = 1f, s = 1f, vel;
    bool over, down;
    Selectable sel;
    void Awake() => sel = GetComponent<Selectable>();
    bool Live => sel == null || sel.IsInteractable();
    public void OnPointerEnter(UnityEngine.EventSystems.PointerEventData e) { over = true; Upd(); }
    public void OnPointerExit(UnityEngine.EventSystems.PointerEventData e) { over = false; Upd(); }
    public void OnPointerDown(UnityEngine.EventSystems.PointerEventData e) { down = true; Upd(); if (Live) UI.Click?.Invoke(); }
    public void OnPointerUp(UnityEngine.EventSystems.PointerEventData e) { down = false; Upd(); }
    void Upd() => target = !Live ? 1f : down ? 0.95f : over ? 1.02f : 1f;
    void OnDisable() { over = down = false; s = target = 1f; vel = 0; transform.localScale = Vector3.one; }
    void Update()
    {
        if (!pulse && Mathf.Abs(s - target) < 0.0005f && Mathf.Abs(vel) < 0.001f) return;
        s = Mathf.SmoothDamp(s, target, ref vel, down ? 0.03f : 0.09f, Mathf.Infinity, Time.unscaledDeltaTime);
        float k = pulse && !down ? s * (1f + 0.015f * Mathf.Sin(Time.unscaledTime * 3f)) : s;
        transform.localScale = new Vector3(k, k, 1);
    }
}

public class Appear : MonoBehaviour
{
    public float from = 0.97f, dur = 0.22f;
    CanvasGroup g; float t0;
    void OnEnable()
    {
        if (g == null && !TryGetComponent(out g)) g = gameObject.AddComponent<CanvasGroup>();
        t0 = Time.unscaledTime; g.alpha = 0; transform.localScale = Vector3.one * from;
    }
    void Update()
    {
        float k = Mathf.Clamp01((Time.unscaledTime - t0) / dur);
        if (g.alpha >= 1f && k >= 1f) return;
        float e = 1 - Mathf.Pow(1 - k, 3);
        g.alpha = e; transform.localScale = Vector3.one * Mathf.LerpUnclamped(from, 1f, e);
    }
}
