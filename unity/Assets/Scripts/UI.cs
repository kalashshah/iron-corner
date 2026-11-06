using System;
using UnityEngine;
using UnityEngine.UI;

// Tiny runtime UGUI builder. Screens are built in code so there are no prefabs to keep in sync.
public static class UI
{
    public static Font Font, Display; // Display: heavy face for titles and game buttons
    public static Sprite Round;

    // Monochrome: near-black glass, white type, grey secondary. Colour only for meaning (green = good, red = damage).
    // Linear-space blending washes out translucent darks, so panels stay near-opaque.
    public static readonly Color Bg = new(0.035f, 0.035f, 0.04f, 0.95f);
    public static readonly Color Card = new(0.06f, 0.06f, 0.066f, 0.9f);
    public static readonly Color Line = new(1f, 1f, 1f, 0.09f);
    public static readonly Color Accent = new(0.96f, 0.96f, 0.97f);      // primary: solid white pill, dark text
    public static readonly Color Brass = new(0.93f, 0.93f, 0.94f);       // money, meter fills
    public static readonly Color Ink = new(0.96f, 0.96f, 0.97f);
    public static readonly Color Dim = new(0.56f, 0.56f, 0.6f);
    public static readonly Color Green = new(0.42f, 0.84f, 0.58f);
    public static readonly Color Red = new(0.93f, 0.36f, 0.32f);
    public static readonly Color Steel = new(0.11f, 0.11f, 0.12f, 0.88f); // secondary: dark filled pill
    public static readonly Color Outline = new(1f, 1f, 1f, 0.04f);        // outline pill: hairline white rim, glass fill
    public static readonly Color Dark = new(0.05f, 0.05f, 0.06f);         // text on white
    public static readonly Color Row = new(1f, 1f, 1f, 0.04f);            // list rows inside cards

    public static Sprite Gloss, Ring, Glow, Circle, CircleRing;
    public static Action Click; // UI tick sound, hooked to Fx by Game

    public static void Init()
    {
        Font = Font.CreateDynamicFontFromOSFont(new[] { "Avenir Next", "Roboto", "Helvetica Neue", "Arial", "Arial Unicode MS" }, 48);
        if (Font == null) Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        Display = Font.CreateDynamicFontFromOSFont(new[] { "Avenir Next Heavy", "Avenir Next Condensed Heavy", "Roboto Black", "Arial Black", "Roboto", "Arial" }, 64) ?? Font;
        const int s = 64, r = 18;
        // One rounded-rect distance field, four looks: solid fill, top sheen, 2px rim, soft outer glow.
        Sprite Make(Func<float, float, float> alpha, int size, int border)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++) t.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(alpha(x + 0.5f, y + 0.5f))));
            t.Apply();
            return Sprite.Create(t, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100, 0, SpriteMeshType.FullRect, new Vector4(border, border, border, border));
        }
        float Sd(float x, float y, int size, float rad, float inset) // signed distance to a rounded rect inset from the texture edge
        {
            float lo = inset + rad, hi = size - inset - rad;
            float dx = Mathf.Max(lo - x, 0, x - hi), dy = Mathf.Max(lo - y, 0, y - hi);
            return Mathf.Sqrt(dx * dx + dy * dy) - rad;
        }
        Round = Make((x, y) => 0.5f - Sd(x, y, s, r, 0), s, r);
        Gloss = Make((x, y) => Mathf.Clamp01(0.5f - Sd(x, y, s, r, 0)) * Mathf.InverseLerp(s * 0.4f, s, y), s, r);
        Ring = Make((x, y) => { float d = Sd(x, y, s, r, 0); return Mathf.Min(0.5f - d, d + 2f); }, s, r);
        const int g = 128, pad = 40;
        Glow = Make((x, y) => { float d = Sd(x, y, g, 20, pad); return d <= 0 ? 1 : Mathf.Pow(1 - Mathf.Clamp01(d / pad), 2.2f); }, g, pad + 20);
        const int c = 128;
        float Cd(float x, float y) => Mathf.Sqrt((x - c / 2f) * (x - c / 2f) + (y - c / 2f) * (y - c / 2f)) - (c / 2f - 1);
        Circle = Make((x, y) => 0.5f - Cd(x, y), c, 0);
        CircleRing = Make((x, y) => { float d = Cd(x, y); return Mathf.Min(0.5f - d, d + 3.5f); }, c, 0);
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

    // Sliced-sprite scale (texture radius 18px): cards get ~24px corners, buttons are full pills.
    const float CardCorner = 0.75f, PillCorner = 0.4f;

    public static Image Box(Transform p, Color c, bool rounded = true, string name = "box", float corner = CardCorner)
    {
        var img = Go(p, name).AddComponent<Image>();
        img.color = c;
        if (rounded) { img.sprite = Round; img.type = Image.Type.Sliced; img.pixelsPerUnitMultiplier = corner; }
        if (rounded && (c == Card || c == Bg)) Panel(img);
        return img;
    }

    static Image Layer(Image parent, Sprite sp, Color c, string name, float grow = 0)
    {
        var img = Go(parent.transform, name).AddComponent<Image>();
        img.sprite = sp; img.type = Image.Type.Sliced; img.pixelsPerUnitMultiplier = sp == Glow ? 2f : parent.pixelsPerUnitMultiplier; img.color = c; img.raycastTarget = false;
        img.At(0, 0, 1, 1, -grow, -grow, grow, grow);
        return img;
    }

    // Glass card: soft drop shadow, faint top sheen, hairline rim.
    public static Image Panel(Image img, Color? rim = null)
    {
        img.gameObject.AddComponent<SoftShadow>();
        Layer(img, Gloss, new Color(1, 1, 1, 0.035f), "sheen");
        Layer(img, Ring, rim ?? new Color(1, 1, 1, 0.1f), "rim");
        return img;
    }

    // Sheen on a bar fill so meters read as lit tubes rather than flat strips.
    public static Image Shine(Image img) { Layer(img, Gloss, new Color(1, 1, 1, 0.25f), "shine"); return img; }

    // Soft halo around an element. The glow's 40px falloff in the texture is scaled to fit the 26px it grows past the edge.
    public static Image Halo(Image img, Color c, float alpha = 0.35f) { var h = Layer(img, Glow, new Color(c.r, c.g, c.b, alpha), "halo", 26); h.transform.SetAsFirstSibling(); h.pixelsPerUnitMultiplier = 40f / 26f; return h; }

    public static Text Label(Transform p, string s, int size, Color c, TextAnchor a = TextAnchor.MiddleLeft, FontStyle st = FontStyle.Normal)
    {
        var t = Go(p, "text").AddComponent<Text>();
        t.font = Font; t.text = s; t.fontSize = size; t.color = c; t.alignment = a; t.fontStyle = st;
        t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false; t.supportRichText = true;
        // Shrink-to-fit instead of spilling out of the box: long names, ids and translated copy stay inside their rect.
        t.verticalOverflow = VerticalWrapMode.Truncate;
        t.resizeTextForBestFit = true; t.resizeTextMaxSize = size; t.resizeTextMinSize = Mathf.Max(10, Mathf.RoundToInt(size * 0.5f));
        if (c.r + c.g + c.b > 1.2f) t.gameObject.AddComponent<Shadow>().effectColor = new Color(0, 0, 0, 0.45f); // light text stays legible over the 3D ring
        return t.At(0, 0, 1, 1);
    }

    // Pill button. bg picks the style: Accent = solid white, Outline = hairline rim, Steel = dark fill, clear = text only.
    public static Button Btn(Transform p, string label, Color bg, Color fg, Action click, int size = 28)
    {
        var img = Box(p, bg, true, "btn:" + label, PillCorner);
        var b = img.gameObject.AddComponent<Button>();
        var cb = b.colors; cb.highlightedColor = new Color(1.1f, 1.1f, 1.1f); cb.pressedColor = new Color(0.75f, 0.75f, 0.75f); cb.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.55f); cb.fadeDuration = 0.08f; b.colors = cb;
        b.navigation = new Navigation { mode = Navigation.Mode.None };
        if (bg == Outline) Layer(img, Ring, new Color(1, 1, 1, 0.7f), "rim");
        else if (bg.a > 0.05f)
        {
            img.gameObject.AddComponent<SoftShadow>();
            Layer(img, Gloss, new Color(1, 1, 1, bg.grayscale > 0.5f ? 0.18f : 0.04f), "sheen");
            Layer(img, Ring, new Color(1, 1, 1, bg.grayscale > 0.5f ? 0f : 0.09f), "rim");
        }
        img.gameObject.AddComponent<Press>();
        if (click != null) b.onClick.AddListener(() => click());
        Label(img.transform, label, size, fg, TextAnchor.MiddleCenter, FontStyle.Bold).At(0, 0, 1, 1, 14, 0, -14, 0);
        return b;
    }

    public static void SetLabel(this Button b, string s) => b.GetComponentInChildren<Text>().text = s;

    // Heavy italic display text for titles, banners and numbers.
    public static Text Title(Transform p, string s, int size, Color c, TextAnchor a = TextAnchor.MiddleLeft)
    {
        var t = Label(p, s, size, c, a, FontStyle.Italic);
        t.font = Display;
        return t;
    }

    // Chunky game button: a face sitting on a darker slab, so it reads as a physical key. Primary = white, else graphite.
    public static Button Big(Transform p, string label, bool primary, Action click, int size = 44, bool pulse = false)
    {
        var root = Go(p, "big:" + label).AddComponent<Image>(); root.color = new Color(0, 0, 0, 0);
        var slab = Box(root.transform, primary ? new Color(0.5f, 0.5f, 0.53f) : new Color(0.015f, 0.015f, 0.02f, 0.96f), true, "slab", 0.5f).At(0, 0, 1, 1, 0, 0, 0, -10);
        slab.raycastTarget = false; slab.gameObject.AddComponent<SoftShadow>().alpha = 0.3f;
        var face = Box(root.transform, primary ? Accent : new Color(0.12f, 0.12f, 0.13f, 0.97f), true, "face", 0.5f).At(0, 0, 1, 1, 0, 10, 0, 0);
        face.raycastTarget = false;
        Layer(face, Gloss, new Color(1, 1, 1, primary ? 0.22f : 0.07f), "sheen");
        Layer(face, Ring, new Color(1, 1, 1, primary ? 0f : 0.16f), "rim");
        var b = root.gameObject.AddComponent<Button>(); b.targetGraphic = face;
        var cb = b.colors; cb.pressedColor = new Color(0.8f, 0.8f, 0.8f); cb.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.6f); cb.fadeDuration = 0.06f; b.colors = cb;
        b.navigation = new Navigation { mode = Navigation.Mode.None };
        root.gameObject.AddComponent<Press>().pulse = pulse;
        if (click != null) b.onClick.AddListener(() => click());
        Title(face.transform, label, size, primary ? Dark : Ink, TextAnchor.MiddleCenter).At(0, 0, 1, 1, 12, 2, -12, 0);
        return b;
    }

    // Red notification dot on a button's top-right corner.
    public static GameObject Badge(Component c)
    {
        var d = Go(c.transform, "badge").AddComponent<Image>(); d.sprite = Circle; d.color = Red; d.raycastTarget = false;
        d.At(1, 1, 1, 1, -26, -26, 4, 4);
        return d.gameObject;
    }

    // Dark rounded chip with a hairline rim (currency / energy readouts).
    public static Text Pill(Transform p, string s, int size = 26)
    {
        var img = Box(p, new Color(0.05f, 0.05f, 0.056f, 0.88f), true, "pill", PillCorner).At(0, 0, 1, 1);
        Layer(img, Ring, new Color(1, 1, 1, 0.14f), "rim");
        return Label(img.transform, s, size, Ink, TextAnchor.MiddleCenter, FontStyle.Bold).At(0, 0, 1, 1, 22, 0, -22, 0);
    }

    // Round icon button (back arrow, menu).
    public static Button Round1(Transform p, string glyph, Action click, int size = 34)
    {
        var b = Btn(p, glyph, Steel, Ink, click, size);
        var img = b.GetComponent<Image>(); img.sprite = Circle; img.type = Image.Type.Simple;
        foreach (var l in img.GetComponentsInChildren<Image>()) if (l != img) l.gameObject.SetActive(false);
        b.GetComponentInChildren<Text>().At(0, 0, 1, 1, 0, 3, 0, 0);
        return b;
    }

    // Small × in a card's top-right corner; the card also closes on a tap outside it (see Dismiss).
    public static Button Close(Transform card, Action click)
    {
        var b = Btn(card, "×", new Color(0, 0, 0, 0), Dim, click, 52);
        b.At(1, 1, 1, 1, -92, -92, -16, -16);
        return b;
    }

    // Full-screen invisible catcher behind a pop-up card: tapping the scene closes it, like a game menu.
    public static void Dismiss(Transform panel, Action click)
    {
        var bg = Go(panel, "dismiss").AddComponent<Image>(); bg.color = new Color(0, 0, 0, 0);
        bg.At(0, 0, 1, 1); bg.transform.SetAsFirstSibling();
        bg.gameObject.AddComponent<Button>().onClick.AddListener(() => click());
    }

    // Card heading: bold title, dim one-line subtitle.
    public static void Header(Transform col, string title, string sub = null, int size = 38)
    {
        Label(col, title, size, Ink, TextAnchor.LowerLeft, FontStyle.Bold).H(size + 14);
        if (!string.IsNullOrEmpty(sub)) Label(col, sub, 23, Dim, TextAnchor.UpperLeft).H(34);
    }

    // Small uppercase section caption in grey.
    public static Text Caption(Transform p, string s) => Label(p, s, 18, Dim, TextAnchor.LowerLeft, FontStyle.Bold);

    // Thin meter: dark track, white fill. Returns the fill (SetFill drives its right anchor).
    public static RectTransform Meter(Transform p)
    {
        var bg = Box(p, new Color(1, 1, 1, 0.12f), true, "track", PillCorner).At(0, 0, 1, 1);
        var f = Box(bg.transform, Brass, true, "fill", PillCorner).At(0, 0, 1, 1);
        return f.rectTransform;
    }

    // Left-edge scrim: black fading to clear over `width` px.
    public static void Fade(Transform p, float width)
    {
        var t = new Texture2D(64, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        for (int x = 0; x < 64; x++) t.SetPixel(x, 0, new Color(0, 0, 0, 0.9f * Mathf.Pow(1 - x / 63f, 1.1f)));
        t.Apply();
        var img = Go(p, "fade").AddComponent<RawImage>(); img.texture = t; img.raycastTarget = false;
        img.At(0, 0, 0, 1, -200, -200, width, 200); img.transform.SetAsFirstSibling();
    }

    // Disc portrait with a ring and an initial (fight health bars).
    public static Text Portrait(Transform p, Color ring, string initial)
    {
        var disc = Go(p, "portrait").AddComponent<Image>(); disc.sprite = Circle; disc.color = new Color(0.07f, 0.07f, 0.075f, 0.95f); disc.raycastTarget = false;
        disc.gameObject.AddComponent<SoftShadow>();
        var rim = Go(disc.transform, "rim").AddComponent<Image>(); rim.sprite = CircleRing; rim.color = ring; rim.raycastTarget = false; rim.At(0, 0, 1, 1);
        return Label(disc.transform, initial, 40, ring, TextAnchor.MiddleCenter, FontStyle.Bold).At(0, 0, 1, 1, 0, 2, 0, 0);
    }

    public static InputField Field(Transform p, string placeholder, string value, int size = 26)
    {
        var img = Box(p, new Color(0, 0, 0, 0.5f), true, "field", PillCorner);
        Layer(img, Ring, new Color(1, 1, 1, 0.14f), "rim");
        var f = img.gameObject.AddComponent<InputField>();
        var txt = Label(img.transform, value, size, Ink).At(0, 0, 1, 1, 24, 0, -24, 0);
        txt.supportRichText = false; txt.resizeTextForBestFit = false;
        txt.horizontalOverflow = HorizontalWrapMode.Overflow;
        var ph = Label(img.transform, placeholder, size, Dim, TextAnchor.MiddleLeft, FontStyle.Italic).At(0, 0, 1, 1, 24, 0, -24, 0);
        f.textComponent = txt; f.placeholder = ph; f.text = value;
        f.selectionColor = new Color(1, 1, 1, 0.25f); f.caretColor = Ink; f.customCaretColor = true; f.caretWidth = 3;
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

// Layered drop shadow drawn under the element's own mesh: four expanding, fading copies fake a blur.
public class SoftShadow : BaseMeshEffect
{
    public float spread = 3f, drop = 5f, alpha = 0.16f;
    static readonly System.Collections.Generic.List<UIVertex> src = new(), dst = new();
    public override void ModifyMesh(VertexHelper vh)
    {
        if (!IsActive()) return;
        src.Clear(); dst.Clear();
        vh.GetUIVertexStream(src);
        if (src.Count == 0) return;
        var r = ((RectTransform)transform).rect; var c = r.center;
        for (int k = 4; k >= 1; k--)
        {
            float e = spread * k;
            foreach (var v0 in src)
            {
                var v = v0; var p = v.position;
                p.x += p.x < c.x ? -e : e; p.y += (p.y < c.y ? -e : e) - drop;
                v.position = p; v.color = new Color32(0, 0, 0, (byte)(255 * alpha * v0.color.a / 255f * (1.2f - k * 0.2f)));
                dst.Add(v);
            }
        }
        dst.AddRange(src);
        vh.Clear(); vh.AddUIVertexTriangleStream(dst);
    }
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
    void Upd() => target = !Live ? 1f : down ? 0.93f : over ? 1.03f : 1f;
    void OnDisable() { over = down = false; s = target = 1f; vel = 0; transform.localScale = Vector3.one; }
    void Update()
    {
        if (!pulse && Mathf.Abs(s - target) < 0.0005f && Mathf.Abs(vel) < 0.001f) return;
        s = Mathf.SmoothDamp(s, target, ref vel, down ? 0.03f : 0.09f, Mathf.Infinity, Time.unscaledDeltaTime);
        float k = pulse && !down ? s * (1f + 0.022f * Mathf.Sin(Time.unscaledTime * 3.2f)) : s;
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
