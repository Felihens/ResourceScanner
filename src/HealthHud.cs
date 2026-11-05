using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ResourceScanner
{
    // The box at the bottom of the screen. Built from the game's own UI: the inventory window's
    // background and border, the game's font, and a copy of the enemy health bar.
    public class HealthHud : MonoBehaviour
    {
        private const float Width = 260f, PadX = 14f, PadY = 10f, BarRowH = 24f, CountH = 22f;
        private const float BarW = 160f, BarH = 12f, Gap = 10f, BottomMargin = 14f;

        // What the crosshair is on (refreshed every 0.1 s), shared with the X-ray.
        public static Target Current;

        private RectTransform _panel;
        private CanvasGroup _group;
        private TMP_Text _value, _count;
        private RectTransform _barRoot;
        private List<GuiBar> _bars = new List<GuiBar>();
        private GuiBar _mainBar;
        private Image _fallbackFill;

        private Target _shown;
        private float _lastSeen = -99f, _nextScan;
        private string _lastLog = "";

        private void Update()
        {
            if (!Plugin.Enabled.Value || Hud.instance == null)
            {
                if (_group != null) _group.alpha = 0f;
                return;
            }
            if (_panel == null)   // also after logout, when the Hud is rebuilt
            {
                try { Build(); }
                catch (Exception e) { Plugin.Log.LogWarning("Could not build the health box: " + e); enabled = false; return; }
            }

            if (Time.time >= _nextScan)
            {
                _nextScan = Time.time + 0.1f;
                Target t = null;
                Player p = Player.m_localPlayer;
                if (p != null && !p.IsDead() && !InventoryGui.IsVisible() && !Minimap.IsOpen())
                {
                    try { t = Targets.Find(p); }
                    catch (Exception e) { Plugin.Debug("Scan failed: " + e.Message); }
                }
                Current = t;
                if (t != null)
                {
                    _shown = t;
                    _lastSeen = Time.time;
                    Show(t);
                    LogTarget(t);
                }
            }

            bool visible = _shown != null && Time.time - _lastSeen <= Plugin.HideDelay.Value + 0.1f;
            _group.alpha = Mathf.MoveTowards(_group.alpha, visible ? 1f : 0f, Time.deltaTime * (visible ? 8f : 4f));
            if (!visible && _group.alpha <= 0f) _shown = null;

            float s = Plugin.Scale.Value;
            _panel.localScale = new Vector3(s, s, 1f);
            if (_group.alpha > 0f) Position();
        }

        // The box sits at the very bottom of the screen, under the stamina, eitr and adrenaline bars.
        // The wooden border sticks out 10 units past the box, so the box starts a bit higher.
        private void Position()
        {
            float s = Plugin.Scale.Value;
            float top = BottomMargin * s + _panel.sizeDelta.y * s + Plugin.PosY.Value;
            _panel.anchoredPosition = new Vector2(Plugin.PosX.Value, top);
        }

        private void LogTarget(Target t)
        {
            if (!Plugin.DetailedLog.Value) return;
            string line = t.Kind + " (" + ((Component)t.Key).gameObject.name + ")" +
                (t.Area >= 0 ? " piece " + t.Area : "") + ": " + t.Health.ToString("0.#") + " / " + t.Max.ToString("0.#") +
                (t.Total > 0 ? ", pieces left " + t.Left + "/" + t.Total : "");
            if (line != _lastLog) { _lastLog = line; Plugin.Log.LogInfo(line); }
        }

        // ------------------------------------------------------------ content

        private void Show(Target t)
        {
            float frac = t.Max > 0f ? Mathf.Clamp01(t.Health / t.Max) : 0f;
            bool showCount = t.Total > 1;

            _count.gameObject.SetActive(showCount);
            if (showCount) _count.text = t.Left + "/" + t.Total + " left";

            _value.text = Plugin.Format.Value == ValueFormat.Numbers
                ? Mathf.CeilToInt(t.Health) + " / " + Mathf.RoundToInt(t.Max)
                : (t.Health > 0f ? Mathf.Max(1, Mathf.FloorToInt(frac * 100f)) : 0) + "%";

            Color c = frac > 0.5f
                ? Color.Lerp(new Color(1f, 0.85f, 0.2f), new Color(0.35f, 0.85f, 0.3f), (frac - 0.5f) * 2f)
                : Color.Lerp(new Color(0.9f, 0.2f, 0.15f), new Color(1f, 0.85f, 0.2f), frac * 2f);

            if (_mainBar != null)
            {
                foreach (GuiBar b in _bars)
                {
                    b.SetMaxValue(1f);
                    b.SetValue(frac);
                }
                _mainBar.SetColor(c);
            }
            else if (_fallbackFill != null)
            {
                _fallbackFill.rectTransform.sizeDelta = new Vector2(BarW * frac, BarH);
                _fallbackFill.color = c;
            }
            Layout(showCount);
        }

        private void Layout(bool showCount)
        {
            float y = -PadY;
            float tw = Mathf.Max(30f, _value.preferredWidth);
            float total = BarW + Gap + tw;
            float rowMid = y - BarRowH / 2f;
            _barRoot.anchoredPosition = new Vector2(-total / 2f + BarW / 2f, rowMid);
            _value.rectTransform.anchoredPosition = new Vector2(-total / 2f + BarW + Gap, rowMid);
            y -= BarRowH;

            if (showCount) { _count.rectTransform.anchoredPosition = new Vector2(0f, y); y -= CountH; }
            _panel.sizeDelta = new Vector2(Width, -y + PadY);
        }

        // ------------------------------------------------------------ building

        private void Build()
        {
            Transform root = Hud.instance.m_rootObject != null ? Hud.instance.m_rootObject.transform : Hud.instance.transform;

            GameObject go = new GameObject("ResourceScanner_Box", typeof(RectTransform));
            go.transform.SetParent(root, false);
            _panel = (RectTransform)go.transform;
            _panel.anchorMin = _panel.anchorMax = new Vector2(0.5f, 0f);
            _panel.pivot = new Vector2(0.5f, 1f);
            _panel.sizeDelta = new Vector2(Width, 60f);
            _group = go.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.blocksRaycasts = false;
            _group.interactable = false;

            string bgInfo = CopyInventoryBackground(_panel);

            // Text styles from the inventory tooltip, font fallback from the hover text.
            GameObject tip = TooltipPrefab();
            TMP_Text srcText = null;
            if (tip != null)
                foreach (TMP_Text tx in tip.GetComponentsInChildren<TMP_Text>(true))
                {
                    string n = tx.gameObject.name.ToLowerInvariant();
                    if (n.Contains("text") && !n.Contains("topic")) { srcText = tx; break; }
                }
            if (srcText == null) srcText = Hud.instance.m_hoverName;

            BuildBar();
            _value = MakeText("Value", srcText, 18f, TextAlignmentOptions.MidlineLeft, new Vector2(0f, 0.5f), 120f, BarRowH);
            _count = MakeText("Count", srcText, 16f, TextAlignmentOptions.Top, new Vector2(0.5f, 1f), Width - 2 * PadX, CountH);
            _value.color = Color.white;
            Layout(true);

            Plugin.Debug("Box built. Background: " + bgInfo +
                ", text font: " + (srcText != null && srcText.font != null ? srcText.gameObject.name + "/" + srcText.font.name : "none") +
                ", bar: " + (_mainBar != null ? "enemy bar (" + _bars.Count + " parts, main " + _mainBar.gameObject.name + ")" : "plain"));
        }

        // Copies the inventory window's background and border: the Images on the window itself and
        // on its children that cover the whole window, in drawing order.
        internal static string CopyInventoryBackground(RectTransform panel)
        {
            string info = "";
            RectTransform win = InventoryGui.instance != null ? InventoryGui.instance.m_player : null;
            if (win != null)
            {
                List<Image> layers = new List<Image>();
                Image own = win.GetComponent<Image>();
                if (own != null && own.sprite != null) layers.Add(own);
                for (int i = 0; i < win.childCount; i++)
                {
                    RectTransform ch = win.GetChild(i) as RectTransform;
                    if (ch == null) continue;
                    Image im = ch.GetComponent<Image>();
                    if (im == null || im.sprite == null) continue;
                    bool covers = ch.anchorMin.x <= 0.01f && ch.anchorMin.y <= 0.01f && ch.anchorMax.x >= 0.99f && ch.anchorMax.y >= 0.99f;
                    if (Plugin.DetailedLog.Value)
                        Plugin.Log.LogInfo("Inventory window child '" + ch.name + "' sprite " + im.sprite.name + " type " + im.type +
                            (covers ? " (covers window, offsets " + ch.offsetMin + " " + ch.offsetMax + ")" : ""));
                    if (covers) layers.Add(im);
                }
                foreach (Image src in layers)
                {
                    GameObject lg = new GameObject("Bkg_" + src.gameObject.name, typeof(RectTransform));
                    lg.transform.SetParent(panel, false);
                    RectTransform r = (RectTransform)lg.transform;
                    r.anchorMin = Vector2.zero;
                    r.anchorMax = Vector2.one;
                    if (src.transform == win) { r.offsetMin = Vector2.zero; r.offsetMax = Vector2.zero; }
                    else { RectTransform sr = src.rectTransform; r.offsetMin = sr.offsetMin; r.offsetMax = sr.offsetMax; }
                    Image im = lg.AddComponent<Image>();
                    im.sprite = src.sprite;
                    im.type = src.type;
                    im.material = src.material;
                    im.color = src.color;
                    im.pixelsPerUnitMultiplier = src.pixelsPerUnitMultiplier;
                    im.raycastTarget = false;
                    info += (info.Length > 0 ? " + " : "") + src.gameObject.name + "/" + src.sprite.name;
                }
            }
            if (info.Length > 0) return "inventory " + info;

            Image plain = panel.gameObject.AddComponent<Image>();
            plain.color = new Color(0f, 0f, 0f, 0.65f);
            plain.raycastTarget = false;
            return "plain";
        }

        private TMP_Text MakeText(string name, TMP_Text src, float size, TextAlignmentOptions align, Vector2 pivot, float w, float h)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(_panel, false);
            TextMeshProUGUI t = go.AddComponent<TextMeshProUGUI>();
            if (src != null)
            {
                t.font = src.font;
                t.fontSharedMaterial = src.fontSharedMaterial;
                t.color = src.color;
                t.fontStyle = src.fontStyle;
            }
            t.fontSize = size;
            t.alignment = align;
            t.overflowMode = TextOverflowModes.Overflow;
            t.raycastTarget = false;
            RectTransform r = t.rectTransform;
            r.anchorMin = r.anchorMax = new Vector2(0.5f, 1f);
            r.pivot = pivot;
            r.sizeDelta = new Vector2(w, h);
            return t;
        }

        private void BuildBar()
        {
            _bars.Clear();
            _mainBar = null;
            GameObject source = null;
            try
            {
                if (EnemyHud.instance != null && EnemyHud.instance.m_baseHud != null)
                {
                    GuiBar[] found = EnemyHud.instance.m_baseHud.GetComponentsInChildren<GuiBar>(true);
                    if (found.Length > 0)
                    {
                        Transform t = found[0].transform.parent;
                        source = t != null && t != EnemyHud.instance.m_baseHud.transform ? t.gameObject : found[0].gameObject;
                    }
                }
            }
            catch (Exception e) { Plugin.Debug("Enemy bar lookup failed: " + e.Message); }

            if (source != null)
            {
                RectTransform srcRt = source.transform as RectTransform;
                float w0 = srcRt != null ? srcRt.rect.width : 0f, h0 = srcRt != null ? srcRt.rect.height : 0f;
                if (w0 < 1f && srcRt != null) { w0 = srcRt.sizeDelta.x; h0 = srcRt.sizeDelta.y; }
                if (w0 >= 1f && h0 >= 1f)
                {
                    GameObject clone = Instantiate(source, _panel, false);
                    clone.name = "Bar";
                    clone.SetActive(true);
                    foreach (TMP_Text tx in clone.GetComponentsInChildren<TMP_Text>(true)) tx.gameObject.SetActive(false);
                    float kx = BarW / w0, ky = BarH / h0;
                    foreach (RectTransform r in clone.GetComponentsInChildren<RectTransform>(true))
                    {
                        if (r == clone.transform) continue;
                        Vector2 sd = r.sizeDelta, ap = r.anchoredPosition;
                        if (Mathf.Approximately(r.anchorMin.x, r.anchorMax.x)) { sd.x *= kx; ap.x *= kx; }
                        if (Mathf.Approximately(r.anchorMin.y, r.anchorMax.y)) { sd.y *= ky; ap.y *= ky; }
                        r.sizeDelta = sd;
                        r.anchoredPosition = ap;
                    }
                    _barRoot = (RectTransform)clone.transform;
                    _barRoot.anchorMin = _barRoot.anchorMax = new Vector2(0.5f, 1f);
                    _barRoot.pivot = new Vector2(0.5f, 0.5f);
                    _barRoot.sizeDelta = new Vector2(BarW, BarH);
                    _barRoot.localScale = Vector3.one;
                    _bars.AddRange(clone.GetComponentsInChildren<GuiBar>(true));
                    // The bar drawn on top is the one that shows the current value.
                    foreach (GuiBar b in _bars) if (b.gameObject.name.ToLowerInvariant().Contains("fast")) _mainBar = b;
                    if (_mainBar == null && _bars.Count > 0) _mainBar = _bars[_bars.Count - 1];
                    if (Plugin.DetailedLog.Value)
                    {
                        string parts = "";
                        foreach (RectTransform r in clone.GetComponentsInChildren<RectTransform>(true)) parts += " " + r.name + "[" + r.sizeDelta.x.ToString("0") + "x" + r.sizeDelta.y.ToString("0") + "]";
                        Plugin.Log.LogInfo("Bar copied from '" + source.name + "' (" + w0.ToString("0") + "x" + h0.ToString("0") + "):" + parts);
                    }
                    if (_mainBar != null) return;
                    Destroy(clone);
                    _bars.Clear();
                }
            }

            // Plain bar if the enemy bar can't be copied.
            GameObject bgGo = new GameObject("Bar", typeof(RectTransform));
            bgGo.transform.SetParent(_panel, false);
            Image bgImg = bgGo.AddComponent<Image>();
            bgImg.color = new Color(0f, 0f, 0f, 0.6f);
            bgImg.raycastTarget = false;
            _barRoot = (RectTransform)bgGo.transform;
            _barRoot.anchorMin = _barRoot.anchorMax = new Vector2(0.5f, 1f);
            _barRoot.pivot = new Vector2(0.5f, 0.5f);
            _barRoot.sizeDelta = new Vector2(BarW, BarH);
            GameObject fillGo = new GameObject("Fill", typeof(RectTransform));
            fillGo.transform.SetParent(_barRoot, false);
            _fallbackFill = fillGo.AddComponent<Image>();
            _fallbackFill.raycastTarget = false;
            RectTransform fr = _fallbackFill.rectTransform;
            fr.anchorMin = fr.anchorMax = new Vector2(0f, 0.5f);
            fr.pivot = new Vector2(0f, 0.5f);
            fr.sizeDelta = new Vector2(BarW, BarH);
        }

        private static GameObject TooltipPrefab()
        {
            try
            {
                foreach (UITooltip t in Resources.FindObjectsOfTypeAll<UITooltip>())
                {
                    GameObject p = Traverse.Create(t).Field("m_tooltipPrefab").GetValue<GameObject>();
                    if (p != null) return p;
                }
            }
            catch (Exception e) { Plugin.Debug("Tooltip lookup failed: " + e.Message); }
            return null;
        }
    }
}
