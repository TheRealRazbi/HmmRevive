using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HeavyMetalMachines;
using HeavyMetalMachines.Combat;
using HeavyMetalMachines.Infra.Context;
using HeavyMetalMachines.Match;
using Pocketverse;
using UnityEngine;

namespace HmmRevive
{
    /// <summary>
    /// Server, test flag --hmmrevive-scan-heal: finds everything loaded in the match that can heal. Walks every loaded
    /// MonoBehaviour and ScriptableObject (scene objects, prefabs, gadget infos) by reflection and logs each
    /// ModifierInfo that repairs HP (EffectKind.HPRepair) or buffs HP regen (AttributeBuffKind.HPRegen), with the object,
    /// its type, where it lives and the modifier's numbers. Also lists the arena's own scene scripts (not cars, not UI).
    /// Runs 20 s into the match and again at 3 min (for things spawned later).
    /// </summary>
    public static class HealScan
    {
        private static int _runs;
        private static float _next = -1f;
        private static readonly HashSet<string> Seen = new HashSet<string>();

        // Called once a second from Watchdog.Update.
        public static void Tick(HMMHub hub)
        {
            if (!Entry.ServerMode || !Entry.ScanHeal || _runs >= 2) return;
            if (hub.State.Current == null || hub.State.Current.StateKind != GameState.GameStateKind.Game) return;
            if (_next < 0f) _next = Time.unscaledTime + 20f;
            if (Time.unscaledTime < _next) return;
            _runs++;
            _next = Time.unscaledTime + 160f;
            try { Scan(); }
            catch (Exception e) { Log.Error("HealScan failed: " + e); }
        }

        private static void Scan()
        {
            int objects = 0, hits = 0;
            var census = new Dictionary<string, int>();
            foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(typeof(MonoBehaviour)).Concat(Resources.FindObjectsOfTypeAll(typeof(ScriptableObject))))
            {
                if (o == null) continue;
                objects++;
                string where = Where(o);
                if (o is MonoBehaviour mb && mb.gameObject.scene.IsValid() && mb.transform.root.GetComponentInChildren<CombatObject>() == null)
                {
                    string ns = o.GetType().Namespace ?? "";
                    if (!ns.StartsWith("UnityEngine") && !o.GetType().Name.StartsWith("UI") && !o.GetType().Name.StartsWith("Hud"))
                    {
                        string key = o.GetType().FullName;
                        census[key] = census.TryGetValue(key, out int n) ? n + 1 : 1;
                    }
                }
                var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
                Walk(o, o.GetType().Name, 0, visited, (path, mod) =>
                {
                    string line = $"HEALSCAN {o.GetType().FullName} \"{o.name}\" {where} .{path}: {Describe(mod)}";
                    if (Seen.Add(line)) { Log.Info(line); hits++; }
                });
            }
            Log.Info($"HEALSCAN run {_runs}: {objects} objects, {hits} new heal modifiers");
            if (_runs == 1) ListHealingAreas();
            if (_runs == 1)
                foreach (var kv in census.OrderBy(k => k.Key))
                    Log.Info($"HEALSCAN scene script {kv.Key} x{kv.Value}");
        }

        // Arena hazard areas whose modifiers heal: where they are, how big, which team they skip, bot repair points.
        private static void ListHealingAreas()
        {
            int all = 0;
            foreach (HazardArea h in Resources.FindObjectsOfTypeAll(typeof(HazardArea)))
            {
                if (h == null || !h.gameObject.scene.IsValid()) continue;
                all++;
                HazardModifiers[] mods = { h.Modifiers, h.OnEnterModifiers, h.OnExitModifiers };
                if (!mods.Any(m => m != null && m.ModsInfo != null && m.ModsInfo.Any(Heals))) continue;
                Bounds b = default(Bounds);
                bool hasBounds = false;
                foreach (Collider c in h.GetComponentsInChildren<Collider>(true))
                {
                    if (!hasBounds) { b = c.bounds; hasBounds = true; } else b.Encapsulate(c.bounds);
                }
                Log.Info($"HEALSCAN area \"{h.name}\" {Where(h)} pos={h.transform.position} size={(hasBounds ? b.size.ToString() : "?")} " +
                         $"skipsTeam={h.Team} botRepairPoint={h.AiRepairPoint} onlyBombCarrier={h.OnlyWhenCarryingBomb} enabled={h.enabled} " +
                         $"stay={h.Modifiers?.name} enter={h.OnEnterModifiers?.name} exit={h.OnExitModifiers?.name}");
            }
            Log.Info($"HEALSCAN {all} hazard areas in the scene");
            var dumped = new HashSet<HazardModifiers>();
            foreach (HazardArea h in Resources.FindObjectsOfTypeAll(typeof(HazardArea)))
                foreach (HazardModifiers m in new[] { h.Modifiers, h.OnEnterModifiers, h.OnExitModifiers })
                    if (m != null && m.ModsInfo != null && (m.ModsInfo.Any(Heals) || m.name.IndexOf("Repair", StringComparison.OrdinalIgnoreCase) >= 0))
                        if (dumped.Add(m))
                            for (int i = 0; i < m.ModsInfo.Length; i++) Log.Info($"HEALSCAN mods \"{m.name}\"[{i}]: {Describe(m.ModsInfo[i])}");
        }

        private static string Where(UnityEngine.Object o)
        {
            if (!(o is Component c)) return "(asset)";
            var sb = new StringBuilder(c.gameObject.scene.IsValid() ? "scene=" + c.gameObject.scene.name + " " : "(prefab) ");
            var parts = new List<string>();
            for (Transform t = c.transform; t != null; t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return sb.Append("path=").Append(string.Join("/", parts.ToArray())).Append(c.gameObject.activeInHierarchy ? "" : " (inactive)").ToString();
        }

        private static string Describe(ModifierInfo m) =>
            $"effect={m.Effect} attr={m.Attribute} amount={m.Amount} percent={m.IsPercent} perSecond={m.AmountPerSecond} tick={m.TickDelta} " +
            $"life={m.LifeTime} status={m.Status} newModifier={(m.NewModifier == null ? "-" : m.NewModifier.GetType().Name)} target={m.TargetGadget} " +
            $"unstable={m.Unstable} purgeable={m.IsPurgeable} dispellable={m.IsDispellable} tag={m.Tag} targetTag={m.TargetTag} notForPlayers={m.NotForPlayers} notForEnemies={m.NotForEnemies} friendlyFire={m.FriendlyFire} hitOwner={m.HitOwner}";

        private static bool Heals(ModifierInfo m) => m.Effect == EffectKind.HPRepair || m.Attribute == AttributeBuffKind.HPRegen;

        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // Follows plain fields, arrays/lists and serializable nested classes; stops at other Unity objects (they're
        // scanned on their own).
        private static void Walk(object obj, string path, int depth, HashSet<object> visited, Action<string, ModifierInfo> hit)
        {
            if (obj == null || depth > 6 || !visited.Add(obj)) return;
            if (obj is ModifierInfo mi)
            {
                if (Heals(mi)) hit(path, mi);
                if (mi.NewModifier != null) Walk(mi.NewModifier, path + ".NewModifier", depth + 1, visited, hit);
                return;
            }
            Type type = obj.GetType();
            for (Type t = type; t != null && t != typeof(MonoBehaviour) && t != typeof(ScriptableObject) && t != typeof(object); t = t.BaseType)
                foreach (FieldInfo f in t.GetFields(Fields | BindingFlags.DeclaredOnly))
                {
                    Type ft = f.FieldType;
                    if (ft.IsPrimitive || ft.IsEnum || ft == typeof(string) || ft.IsPointer) continue;
                    object v;
                    try { v = f.GetValue(obj); } catch { continue; }
                    if (v == null) continue;
                    if (v is UnityEngine.Object && !(v is ModifierInfo)) continue;
                    if (v is IEnumerable list && !(v is string))
                    {
                        int i = 0;
                        foreach (object item in list)
                        {
                            if (item != null && !(item is UnityEngine.Object) && !item.GetType().IsPrimitive && !(item is string))
                                Walk(item, $"{path}.{f.Name}[{i}]", depth + 1, visited, hit);
                            if (++i > 200) break;
                        }
                        continue;
                    }
                    if (ft.IsValueType && !(v is ModifierInfo)) { if (ft.Namespace != null && ft.Namespace.StartsWith("UnityEngine")) continue; }
                    Walk(v, path + "." + f.Name, depth + 1, visited, hit);
                }
        }

        private class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }
    }
}
