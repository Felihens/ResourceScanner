using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ResourceScanner
{
    // X-ray window: press the X-ray key (right mouse by default) while aiming at an ore deposit or a
    // big rock with a pickaxe. A small window under the minimap shows a live 3D view of the whole
    // deposit without the ground, buried pieces included. A see-through plane marks ground level.
    // It closes when you put the pickaxe away or don't aim at the deposit for a few seconds.
    public class XRay : MonoBehaviour
    {
        private class Piece
        {
            public int Index;
            public Transform T;
            public Mesh Mesh;
            public Material[] Mats;
        }

        private const float BelowMinimap = 40f, Pad = 10f, CountH = 24f;

        private UnityEngine.Object _object;
        private float _lastAim;
        private int _aimed = -1, _total;
        private readonly List<Piece> _pieces = new List<Piece>();
        private Vector3 _center;
        private float _radius = 1f, _ground;
        private bool _hasGround;
        private string _lastLog = "";

        private RectTransform _win;
        private RawImage _raw;
        private TMP_Text _count;
        private Camera _cam;
        private Light _light;
        private RenderTexture _rt;
        private int _layer = -1;
        private Mesh _quad;
        private Material _planeMat, _aimMat;

        private static FieldInfo _m5Areas, _haHealth, _haMesh, _haRenderer, _mrAreas, _mrMeshes;

        private void Update()
        {
            if (!Plugin.Enabled.Value || !Plugin.XRay.Value) { Close(); return; }
            Player p = Player.m_localPlayer;
            ItemDrop.ItemData w = p != null ? p.GetCurrentWeapon() : null;
            if (p == null || p.IsDead() || w == null || w.m_shared.m_skillType != Skills.SkillType.Pickaxes) { Close(); return; }

            Target t = HealthHud.Current;
            bool onDeposit = t != null && t.Kind == Kind.Deposit && t.Key != null;
            if (onDeposit && Input.GetKeyDown(Plugin.XRayKey.Value) && (_object == null || _object != t.Key)) Open(t.Key);
            if (_object == null) { Close(); return; }

            if (onDeposit && t.Key == _object) { _lastAim = Time.time; _aimed = t.Area; }
            else _aimed = -1;
            if (Time.time - _lastAim > Plugin.XRayCloseAfter.Value) { Close(); return; }

            try
            {
                Collect();
                if (_pieces.Count == 0) { Close(); return; }
                Render();
                _count.text = _pieces.Count + "/" + _total + " left";
                Position();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("X-ray window failed: " + e);
                Close();
            }
        }

        // ------------------------------------------------------------ open / close

        private void Open(UnityEngine.Object obj)
        {
            if (_layer < 0) _layer = FreeLayer();
            if (_layer < 0) { Plugin.Log.LogWarning("No free layer for the X-ray camera; X-ray is off."); Plugin.XRay.Value = false; return; }
            if (_win == null) BuildWindow();
            if (_cam == null) BuildCamera();

            _object = obj;
            _lastAim = Time.time;
            Collect();
            if (_pieces.Count == 0) { _object = null; return; }

            // Frame the deposit as it is now; the framing then stays put while you mine.
            Bounds b = new Bounds();
            bool first = true;
            foreach (Piece pc in _pieces)
            {
                Bounds mb = pc.Mesh.bounds;
                Vector3 mn = mb.min, mx = mb.max;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = pc.T.TransformPoint(new Vector3((i & 1) == 0 ? mn.x : mx.x, (i & 2) == 0 ? mn.y : mx.y, (i & 4) == 0 ? mn.z : mx.z));
                    if (first) { b = new Bounds(c, Vector3.zero); first = false; }
                    else b.Encapsulate(c);
                }
            }
            _center = b.center;
            _radius = Mathf.Max(0.5f, b.extents.magnitude);
            float g = 0f;
            _hasGround = ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(_center, out g);
            _ground = _hasGround ? g : 0f;

            _win.gameObject.SetActive(true);
            _cam.enabled = true;
            Plugin.Debug("X-ray window opened on " + ((Component)obj).gameObject.name + ": " + _pieces.Count + "/" + _total +
                " pieces, size " + (b.size.x.ToString("0.0") + " x " + b.size.y.ToString("0.0") + " x " + b.size.z.ToString("0.0")) +
                " m, ground " + (_hasGround ? (_ground - b.min.y).ToString("0.0") + " m above the bottom" : "unknown") + ", layer " + _layer + ".");
        }

        private void Close()
        {
            if (_object == null && (_win == null || !_win.gameObject.activeSelf)) return;
            if (_object != null || _win != null) Plugin.Debug("X-ray window closed.");
            _object = null;
            _pieces.Clear();
            if (_win != null) _win.gameObject.SetActive(false);
            if (_cam != null) _cam.enabled = false;
        }

        private static int FreeLayer()
        {
            for (int i = 31; i >= 8; i--)
                if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) return i;
            return -1;
        }

        // ------------------------------------------------------------ pieces

        private void Collect()
        {
            _pieces.Clear();
            MineRock5 m5 = _object as MineRock5;
            if (m5 != null) { Collect5(m5); return; }
            MineRock mr = _object as MineRock;
            if (mr != null) CollectOld(mr);
        }

        private void Collect5(MineRock5 m5)
        {
            if (_m5Areas == null)
            {
                _m5Areas = AccessTools.Field(typeof(MineRock5), "m_hitAreas");
                Type ha = typeof(MineRock5).GetNestedType("HitArea", BindingFlags.Public | BindingFlags.NonPublic);
                _haHealth = AccessTools.Field(ha, "m_health");
                _haMesh = AccessTools.Field(ha, "m_meshFilter");
                _haRenderer = AccessTools.Field(ha, "m_meshRenderer");
            }
            IList areas = _m5Areas.GetValue(m5) as IList;
            if (areas == null) return;
            _total = areas.Count;
            for (int i = 0; i < areas.Count; i++)
            {
                object a = areas[i];
                if ((float)_haHealth.GetValue(a) <= 0f) continue;
                MeshFilter mf = _haMesh.GetValue(a) as MeshFilter;
                MeshRenderer mr = _haRenderer.GetValue(a) as MeshRenderer;
                Add(i, mf, mr);
            }
        }

        private void CollectOld(MineRock mr)
        {
            if (_mrAreas == null)
            {
                _mrAreas = AccessTools.Field(typeof(MineRock), "m_hitAreas");
                _mrMeshes = AccessTools.Field(typeof(MineRock), "m_areaMeshes");
            }
            Collider[] areas = _mrAreas.GetValue(mr) as Collider[];
            MeshRenderer[][] meshes = _mrMeshes.GetValue(mr) as MeshRenderer[][];
            if (areas == null) return;
            _total = areas.Length;
            for (int i = 0; i < areas.Length; i++)
            {
                if (areas[i] == null || !areas[i].gameObject.activeInHierarchy) continue;   // hidden = broken
                if (meshes != null && i < meshes.Length && meshes[i] != null)
                    foreach (MeshRenderer r in meshes[i]) Add(i, r != null ? r.GetComponent<MeshFilter>() : null, r);
            }
        }

        private void Add(int index, MeshFilter mf, MeshRenderer mr)
        {
            if (mf == null || mf.sharedMesh == null || mr == null) return;
            Piece p = new Piece();
            p.Index = index;
            p.T = mf.transform;
            p.Mesh = mf.sharedMesh;
            p.Mats = mr.sharedMaterials;
            if (p.Mats == null || p.Mats.Length == 0) return;
            _pieces.Add(p);
        }

        // ------------------------------------------------------------ 3D view

        private void Render()
        {
            // Look at the deposit from the same side you're facing, slightly from above.
            Transform view = Camera.main != null ? Camera.main.transform : null;
            float yaw = view != null ? view.eulerAngles.y : 0f;
            Quaternion rot = Quaternion.Euler(35f, yaw, 0f);
            float dist = _radius * 3f + 5f;
            _cam.transform.position = _center - rot * Vector3.forward * dist;
            _cam.transform.rotation = rot;
            _cam.orthographicSize = _radius * 1.05f;
            _cam.nearClipPlane = 0.1f;
            _cam.farClipPlane = dist + _radius * 2f + 5f;
            _light.transform.rotation = rot * Quaternion.Euler(25f, -35f, 0f);

            // Unity draws these into our camera only, so nothing else in the world sees them.
            foreach (Piece p in _pieces)
            {
                Matrix4x4 m = p.T.localToWorldMatrix;
                int subs = p.Mesh.subMeshCount;
                for (int s = 0; s < subs; s++)
                {
                    Material mat = p.Mats[Mathf.Min(s, p.Mats.Length - 1)];
                    if (mat != null) Graphics.DrawMesh(p.Mesh, m, mat, _layer, _cam, s);
                }
                if (p.Index == _aimed)
                    for (int s = 0; s < subs; s++) Graphics.DrawMesh(p.Mesh, m, _aimMat, _layer, _cam, s);
            }
            if (Plugin.XRayGroundPlane.Value && _hasGround)
            {
                float size = _radius * 2.4f;
                Matrix4x4 pm = Matrix4x4.TRS(new Vector3(_center.x, _ground, _center.z), Quaternion.identity, new Vector3(size, 1f, size));
                Graphics.DrawMesh(_quad, pm, _planeMat, _layer, _cam);
            }
        }

        private void BuildCamera()
        {
            int sz = Mathf.RoundToInt(Plugin.XRaySize.Value);
            _rt = new RenderTexture(sz, sz, 24, RenderTextureFormat.ARGB32);
            _rt.name = "ResourceScanner_XRay";
            GameObject go = new GameObject("ResourceScanner_XRayCamera");
            DontDestroyOnLoad(go);
            _cam = go.AddComponent<Camera>();
            _cam.enabled = false;
            _cam.orthographic = true;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _cam.cullingMask = 1 << _layer;
            _cam.targetTexture = _rt;
            _cam.renderingPath = RenderingPath.Forward;
            _cam.allowHDR = false;
            _cam.allowMSAA = false;
            _cam.useOcclusionCulling = false;
            _cam.depth = -50f;
            _raw.texture = _rt;

            GameObject lg = new GameObject("ResourceScanner_XRayLight");
            lg.transform.SetParent(go.transform, false);
            _light = lg.AddComponent<Light>();
            _light.type = LightType.Directional;
            _light.intensity = 1.1f;
            _light.color = new Color(1f, 0.97f, 0.9f);
            _light.shadows = LightShadows.None;
            _light.cullingMask = 1 << _layer;
            _light.enabled = false;
            // The light is on only while our camera renders. Left on, the game (deferred rendering
            // ignores light layers) would light the whole world with it.
            Camera.onPreCull += LightOn;
            Camera.onPostRender += LightOff;

            _quad = new Mesh();
            _quad.vertices = new[] { new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, 0.5f), new Vector3(-0.5f, 0f, 0.5f) };
            _quad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            _quad.RecalculateNormals();
            _quad.RecalculateBounds();

            Shader sh = Shader.Find("Hidden/Internal-Colored");
            _planeMat = Translucent(sh, new Color(0.45f, 0.33f, 0.2f, 0.45f));
            _aimMat = Translucent(sh, new Color(1f, 0.9f, 0.3f, 0.35f));
        }

        private void LightOn(Camera c)
        {
            if (c == _cam && _light != null) _light.enabled = true;
        }

        private void LightOff(Camera c)
        {
            if (c == _cam && _light != null) _light.enabled = false;
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= LightOn;
            Camera.onPostRender -= LightOff;
        }

        private static Material Translucent(Shader sh, Color c)
        {
            Material m = new Material(sh);
            m.hideFlags = HideFlags.HideAndDontSave;
            m.SetInt("_SrcBlend", 5);
            m.SetInt("_DstBlend", 10);
            m.SetInt("_Cull", 0);
            m.SetInt("_ZWrite", 0);
            m.SetInt("_ZTest", 4);   // LessEqual
            m.renderQueue = 3000;
            m.color = c;
            return m;
        }

        // ------------------------------------------------------------ window

        private void BuildWindow()
        {
            Transform root = Hud.instance != null && Hud.instance.m_rootObject != null ? Hud.instance.m_rootObject.transform : null;
            if (root == null) throw new Exception("HUD not ready");
            float sz = Plugin.XRaySize.Value;

            GameObject go = new GameObject("ResourceScanner_XRayWindow", typeof(RectTransform));
            go.transform.SetParent(root, false);
            _win = (RectTransform)go.transform;
            _win.anchorMin = _win.anchorMax = new Vector2(1f, 1f);
            _win.pivot = new Vector2(0.5f, 1f);
            _win.sizeDelta = new Vector2(sz + 2 * Pad, sz + 2 * Pad + CountH);
            CanvasGroup g = go.AddComponent<CanvasGroup>();
            g.blocksRaycasts = false;
            g.interactable = false;
            HealthHud.CopyInventoryBackground(_win);

            GameObject ig = new GameObject("View", typeof(RectTransform));
            ig.transform.SetParent(_win, false);
            _raw = ig.AddComponent<RawImage>();
            _raw.raycastTarget = false;
            RectTransform ir = _raw.rectTransform;
            ir.anchorMin = ir.anchorMax = new Vector2(0.5f, 1f);
            ir.pivot = new Vector2(0.5f, 1f);
            ir.sizeDelta = new Vector2(sz, sz);
            ir.anchoredPosition = new Vector2(0f, -Pad);

            GameObject tg = new GameObject("Count", typeof(RectTransform));
            tg.transform.SetParent(_win, false);
            TextMeshProUGUI t = tg.AddComponent<TextMeshProUGUI>();
            TMP_Text src = Hud.instance.m_hoverName;
            if (src != null) { t.font = src.font; t.fontSharedMaterial = src.fontSharedMaterial; }
            t.fontSize = 16f;
            t.color = Color.white;
            t.alignment = TextAlignmentOptions.Center;
            t.raycastTarget = false;
            RectTransform tr = t.rectTransform;
            tr.anchorMin = tr.anchorMax = new Vector2(0.5f, 0f);
            tr.pivot = new Vector2(0.5f, 0f);
            tr.sizeDelta = new Vector2(sz, CountH);
            tr.anchoredPosition = new Vector2(0f, Pad * 0.5f);
            _count = t;
            go.SetActive(false);
        }

        // Centered under the small minimap; top-right corner if the minimap isn't shown.
        private readonly Vector3[] _corners = new Vector3[4];

        private void Position()
        {
            RectTransform parent = _win.parent as RectTransform;
            Vector2 offset = new Vector2(Plugin.XRayOffsetX.Value, Plugin.XRayOffsetY.Value);
            RawImage map = Minimap.instance != null ? Minimap.instance.m_mapImageSmall : null;
            if (parent != null && map != null && map.gameObject.activeInHierarchy)
            {
                map.rectTransform.GetWorldCorners(_corners);
                Vector3 bottomCenter = (_corners[0] + _corners[3]) * 0.5f;
                Vector3 local = parent.InverseTransformPoint(bottomCenter);
                _win.anchorMin = _win.anchorMax = new Vector2(0.5f, 0.5f);
                _win.localPosition = new Vector3(local.x + offset.x, local.y - BelowMinimap - _win.sizeDelta.y * 0.1f + offset.y, 0f);
            }
            else
            {
                _win.anchorMin = _win.anchorMax = new Vector2(1f, 1f);
                _win.anchoredPosition = new Vector2(-_win.sizeDelta.x / 2f - 20f + offset.x, -260f + offset.y);
            }

            if (Plugin.DetailedLog.Value)
            {
                string line = "X-ray window at " + _win.localPosition.x.ToString("0") + ", " + _win.localPosition.y.ToString("0") + (map != null && map.gameObject.activeInHierarchy ? " (under minimap)" : " (no minimap)");
                if (line != _lastLog) { _lastLog = line; Plugin.Log.LogInfo(line); }
            }
        }
    }
}
