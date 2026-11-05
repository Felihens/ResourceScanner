using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ResourceScanner
{
    public enum Kind { Tree, Log, Stump, Rock, Deposit, Nest }

    public class Target
    {
        public Kind Kind;
        public UnityEngine.Object Key;   // object + area, to notice when the aim moves
        public int Area = -1;
        public float Health, Max;
        public int Left = -1, Total = -1;  // pieces left in a big deposit
    }

    // Finds what the crosshair is on and reads its health from the synced ZDO.
    public static class Targets
    {
        private static MethodInfo _mr5AreaIndex, _mrAreaIndex, _mrHealth;
        private static FieldInfo _mr5Areas, _mrAreas;

        public static Target Find(Player player)
        {
            Camera cam = Camera.main;
            if (cam == null) return null;
            Vector3 origin = cam.transform.position, dir = cam.transform.forward;
            float reach = Vector3.Distance(origin, player.GetEyePoint()) + Plugin.Reach.Value;
            bool blocked;
            Target t = First(player, Physics.RaycastAll(origin, dir, reach, ~0, QueryTriggerInteraction.Ignore), out blocked);
            if (t == null && !blocked)
                t = First(player, Physics.SphereCastAll(origin, 0.2f, dir, reach, ~0, QueryTriggerInteraction.Ignore), out blocked);
            return t;
        }

        private static Target First(Player player, RaycastHit[] hits, out bool blocked)
        {
            blocked = false;
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            for (int i = 0; i < hits.Length; i++)
            {
                Collider c = hits[i].collider;
                if (c == null || c.transform.IsChildOf(player.transform)) continue;
                bool known;
                Target t = Read(c, out known);
                if (known)
                {
                    if (t != null && !ToolFits(player, t.Kind)) t = null;
                    blocked = true;
                    return t;
                }
                // The ground or a building in the way: nothing behind it counts.
                if (c.GetComponentInParent<Heightmap>() != null || c.GetComponentInParent<WearNTear>() != null)
                {
                    blocked = true;
                    return null;
                }
            }
            return null;
        }

        private static bool ToolFits(Player player, Kind kind)
        {
            if (!Plugin.RequireTool.Value || kind == Kind.Nest) return true;
            ItemDrop.ItemData w = player.GetCurrentWeapon();
            if (w == null) return false;
            Skills.SkillType s = w.m_shared.m_skillType;
            if (kind == Kind.Tree || kind == Kind.Log || kind == Kind.Stump) return s == Skills.SkillType.Axes;
            return s == Skills.SkillType.Pickaxes;
        }

        // known = the collider belongs to something this mod handles (even if it can't be read now).
        private static Target Read(Collider c, out bool known)
        {
            known = false;
            MineRock5 m5 = c.GetComponentInParent<MineRock5>();
            if (m5 != null) { known = true; return Deposit5(m5, c); }
            MineRock mr = c.GetComponentInParent<MineRock>();
            if (mr != null) { known = true; return Deposit(mr, c); }
            TreeLog log = c.GetComponentInParent<TreeLog>();
            if (log != null) { known = true; return Simple(log, Kind.Log, log.m_health); }
            TreeBase tree = c.GetComponentInParent<TreeBase>();
            if (tree != null) { known = true; return Simple(tree, Kind.Tree, tree.m_health); }
            Destructible d = c.GetComponentInParent<Destructible>();
            if (d != null)
            {
                Kind k;
                if (!Classify(d, out k)) return null;
                known = true;
                return Simple(d, k, d.m_health);
            }
            return null;
        }

        private static bool Classify(Destructible d, out Kind kind)
        {
            kind = Kind.Rock;
            string n = d.gameObject.name.ToLowerInvariant();
            if (d.GetComponent<SpawnArea>() != null || n.Contains("nest") || n.Contains("spawner") || n.Contains("bonepile") || n.Contains("bodypile"))
            { kind = Kind.Nest; return true; }
            // Only real stumps: bushes and saplings are also "tree" destructibles.
            if (n.Contains("stub") || n.Contains("stump"))
            { kind = Kind.Stump; return true; }
            if (n.Contains("rock") || n.Contains("deposit") || n.Contains("tin") || n.Contains("ore") || n.Contains("marble") || n.Contains("stone"))
            { kind = Kind.Rock; return true; }
            return false;
        }

        // Same max health the game uses: base health raised by the world level.
        private static float WorldMax(float baseHp)
        {
            if (Game.instance == null) return baseHp;
            return baseHp + Game.m_worldLevel * baseHp * Game.instance.m_worldLevelMineHPMultiplier;
        }

        private static ZDO Zdo(Component c)
        {
            ZNetView nv = c.GetComponent<ZNetView>();
            if (nv == null) nv = c.GetComponentInParent<ZNetView>();
            return nv != null && nv.IsValid() ? nv.GetZDO() : null;
        }

        private static Target Simple(MonoBehaviour obj, Kind kind, float baseHp)
        {
            ZDO z = Zdo(obj);
            if (z == null) return null;
            float max = WorldMax(baseHp);
            Target t = new Target();
            t.Kind = kind;
            t.Key = obj;
            t.Max = max;
            t.Health = Mathf.Clamp(z.GetFloat(ZDOVars.s_health, max), 0f, max);
            return t;
        }

        // Big deposits (copper, silver, muddy scrap piles...): one health per piece, packed in one string.
        private static Target Deposit5(MineRock5 m5, Collider c)
        {
            ZDO z = Zdo(m5);
            if (z == null) return null;
            if (_mr5AreaIndex == null)
            {
                _mr5AreaIndex = AccessTools.Method(typeof(MineRock5), "GetAreaIndex", new Type[] { typeof(Collider) });
                _mr5Areas = AccessTools.Field(typeof(MineRock5), "m_hitAreas");
            }
            IList areas = _mr5Areas.GetValue(m5) as IList;
            int idx = (int)_mr5AreaIndex.Invoke(m5, new object[] { c });
            if (areas == null || idx < 0) return null;

            float max = WorldMax(m5.m_health);
            float[] hp = new float[areas.Count];
            for (int i = 0; i < hp.Length; i++) hp[i] = max;
            string s = z.GetString(ZDOVars.s_health, "");
            if (s.Length > 0)
            {
                ZPackage p = new ZPackage(Convert.FromBase64String(s));
                int n = p.ReadInt();
                for (int i = 0; i < n; i++)
                {
                    float h = p.ReadSingle();
                    if (i < hp.Length) hp[i] = h;
                }
            }
            Target t = new Target();
            t.Kind = Kind.Deposit;
            t.Key = m5;
            t.Area = idx;
            t.Max = max;
            t.Health = idx < hp.Length ? Mathf.Clamp(hp[idx], 0f, max) : max;
            t.Total = hp.Length;
            t.Left = 0;
            for (int i = 0; i < hp.Length; i++) if (hp[i] > 0f) t.Left++;
            return t;
        }

        // Older multi-piece rocks: one "Health<i>" value per piece.
        private static Target Deposit(MineRock mr, Collider c)
        {
            ZDO z = Zdo(mr);
            if (z == null) return null;
            if (_mrAreaIndex == null)
            {
                _mrAreaIndex = AccessTools.Method(typeof(MineRock), "GetAreaIndex", new Type[] { typeof(Collider) });
                _mrHealth = AccessTools.Method(typeof(MineRock), "GetHealth");
                _mrAreas = AccessTools.Field(typeof(MineRock), "m_hitAreas");
            }
            Collider[] areas = _mrAreas.GetValue(mr) as Collider[];
            int idx = (int)_mrAreaIndex.Invoke(mr, new object[] { c });
            if (areas == null || idx < 0) return null;
            float max = (float)_mrHealth.Invoke(mr, null);

            Target t = new Target();
            t.Kind = Kind.Deposit;
            t.Key = mr;
            t.Area = idx;
            t.Max = max;
            t.Total = areas.Length;
            t.Left = 0;
            for (int i = 0; i < areas.Length; i++)
            {
                float h = z.GetFloat("Health" + i, max);
                if (h > 0f) t.Left++;
                if (i == idx) t.Health = Mathf.Clamp(h, 0f, max);
            }
            return t;
        }
    }
}
