using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HmmRevive.Legacy
{
    /// <summary>
    /// Server-side value overrides from a text file (balance-&lt;build&gt;.txt next to the mod DLL), one per line:
    ///     AssetName  Field.Path[0].Sub = value      # comment
    /// AssetName is a loaded ScriptableObject's name (e.g. a GadgetInfo such as Troll0Beyblade), * matches any text
    /// (*_BombGadgetInfo = every car's bomb gadget); the path walks its fields,
    /// [n] indexes arrays and lists. Damage and push are computed by the server, so clients need nothing. Gadgets copy
    /// their numbers when a car is built, so this runs on every server state up to the match (the game data is loaded
    /// with the first scene) and rewrites the same values each time.
    /// </summary>
    public static class Balance
    {
        private class Entry { public string Asset, Path, Value, Text; public Regex Pattern; public bool Found; }

        private static List<Entry> _entries;

        private static List<Entry> Load()
        {
            var list = new List<Entry>();
            string file = HmmRevive.Legacy.Entry.BalanceFile;
            if (file == null) { Log.Info("BALANCE off"); return list; }
            if (!File.Exists(file)) { Log.Info("BALANCE no file at " + file); return list; }
            foreach (string raw in File.ReadAllLines(file))
            {
                string line = raw;
                int hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash);
                line = line.Trim();
                if (line.Length == 0) continue;
                Match m = Regex.Match(line, @"^(\S+)\s+([A-Za-z_][\w\.\[\]]*)\s*=\s*(\S+)$");
                if (!m.Success) { Log.Error("BALANCE can't read line: " + raw); continue; }
                string asset = m.Groups[1].Value;
                list.Add(new Entry
                {
                    Asset = asset, Path = m.Groups[2].Value, Value = m.Groups[3].Value, Text = line,
                    Pattern = asset.IndexOf('*') >= 0 ? new Regex("^" + Regex.Escape(asset).Replace(@"\*", ".*") + "$") : null,
                });
            }
            Log.Info("BALANCE " + list.Count + " override(s) from " + file);
            return list;
        }

        public static void Apply(string when)
        {
            if (_entries == null) _entries = Load();
            if (when == "ServerGame") Dump();
            if (_entries.Count == 0) return;
            var byName = new Dictionary<string, List<Object>>();
            foreach (Object o in Resources.FindObjectsOfTypeAll(typeof(ScriptableObject)))
            {
                if (o == null) continue;
                List<Object> same;
                if (!byName.TryGetValue(o.name, out same)) byName[o.name] = same = new List<Object>();
                same.Add(o);
            }
            foreach (Entry e in _entries)
            {
                var assets = new List<Object>();
                if (e.Pattern == null) { List<Object> same; if (byName.TryGetValue(e.Asset, out same)) assets = same; }
                else foreach (var kv in byName) if (e.Pattern.IsMatch(kv.Key)) assets.AddRange(kv.Value);
                foreach (Object asset in assets)
                {
                    try
                    {
                        string old = Set(asset, e.Path.Split('.'), 0, e.Value);
                        if (old == null) continue;
                        if (!e.Found || old != e.Value)
                            Log.Info("BALANCE " + asset.name + " (" + asset.GetType().Name + ") " + e.Path + ": " + old + " -> " + e.Value + " at " + when);
                        e.Found = true;
                    }
                    catch (Exception ex) { Log.Error("BALANCE " + e.Text + ": " + ex.Message); }
                }
            }
            if (when == "ServerGame")
                foreach (Entry e in _entries)
                    if (!e.Found) Log.Error("BALANCE never found: " + e.Text);
        }

        // Test aid (--hmmrevive-balance-dump=Asset1,TypeName2): logs every number and modifier list of those assets (by
        // name, or every asset of a type), to find the field path for an override.
        private static void Dump()
        {
            string arg = Array.Find(Environment.GetCommandLineArgs(), a => a.StartsWith("--hmmrevive-balance-dump=", StringComparison.OrdinalIgnoreCase));
            if (arg == null) return;
            var names = new List<string>(arg.Substring("--hmmrevive-balance-dump=".Length).Split(','));
            var hub = Pocketverse.GameHubBehaviour<HeavyMetalMachines.HMMHub>.Hub;
            var rules = hub != null && hub.BombManager != null ? hub.BombManager.Rules : null;
            if (rules != null)
            {
                Log.Info("BALANCE DUMP bomb rules: " + rules.name + ", bomb " + (rules.BombInfo ? rules.BombInfo.name : "-")
                         + ", weapon " + (rules.Weapon ? rules.Weapon.name + " (" + rules.Weapon.GetType().Name + ")" : "-"));
                names.Add(rules.name);
                if (rules.Weapon) names.Add(rules.Weapon.name);
            }
            foreach (Object o in Resources.FindObjectsOfTypeAll(typeof(ScriptableObject)))
                if (o != null && (names.Contains(o.name) || names.Contains(o.GetType().Name)))
                {
                    var sb = new System.Text.StringBuilder("BALANCE DUMP " + o.name + " (" + o.GetType().Name + ")");
                    DumpFields(o, "", sb, 0);
                    Log.Info(sb.ToString());
                }
        }

        private static void DumpFields(object obj, string prefix, System.Text.StringBuilder sb, int depth)
        {
            if (obj == null || depth > 3) return;
            for (Type t = obj.GetType(); t != null && t != typeof(ScriptableObject) && t != typeof(Object); t = t.BaseType)
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    object v = f.GetValue(obj);
                    Type ft = f.FieldType;
                    if (ft.IsPrimitive || ft.IsEnum) sb.Append("\n  ").Append(prefix).Append(f.Name).Append(" = ").Append(Text(v));
                    else if (v is IList && !(v is string) && ft.GetElementType() != null && !typeof(Object).IsAssignableFrom(ft.GetElementType()))
                    {
                        var items = (IList)v;
                        for (int k = 0; k < items.Count; k++) DumpFields(items[k], prefix + f.Name + "[" + k + "].", sb, depth + 1);
                    }
                    else if (v is Object && (Object)v != null) sb.Append("\n  ").Append(prefix).Append(f.Name).Append(" -> ").Append(((Object)v).name);
                    else if (ft.IsSerializable && !ft.IsArray && v != null && !(v is string) && ft.Namespace != null && ft.Namespace.StartsWith("HeavyMetalMachines"))
                        DumpFields(v, prefix + f.Name + ".", sb, depth + 1);
                }
        }

        // Sets obj.path[i..] = value; returns the old value as text, or null when the path doesn't fit this object's type.
        private static string Set(object obj, string[] path, int i, string value)
        {
            Match m = Regex.Match(path[i], @"^(\w+)(?:\[(\d+)\])?$");
            if (!m.Success) throw new Exception("bad path part '" + path[i] + "'");
            FieldInfo f = FindField(obj.GetType(), m.Groups[1].Value);
            if (f == null) return null;
            object cur = f.GetValue(obj);
            bool indexed = m.Groups[2].Success;
            int index = indexed ? int.Parse(m.Groups[2].Value) : -1;
            if (indexed)
            {
                var items = cur as IList;
                if (items == null) throw new Exception(f.Name + " is not a list");
                if (index >= items.Count) throw new Exception(f.Name + " has " + items.Count + " entries");
                object item = items[index];
                if (i == path.Length - 1)
                {
                    Type t = cur.GetType().IsArray ? cur.GetType().GetElementType() : item != null ? item.GetType() : typeof(string);
                    items[index] = Parse(value, t);
                    return Text(item);
                }
                string old = Set(item, path, i + 1, value);
                if (item != null && item.GetType().IsValueType) items[index] = item; // structs are copies
                return old;
            }
            if (i == path.Length - 1)
            {
                f.SetValue(obj, Parse(value, f.FieldType));
                return Text(cur);
            }
            if (cur == null) throw new Exception(f.Name + " is empty");
            string o = Set(cur, path, i + 1, value);
            if (f.FieldType.IsValueType) f.SetValue(obj, cur);
            return o;
        }

        private static FieldInfo FindField(Type t, string name)
        {
            for (; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        private static object Parse(string v, Type t)
        {
            if (t == typeof(float)) return float.Parse(v, CultureInfo.InvariantCulture);
            if (t == typeof(int)) return int.Parse(v, CultureInfo.InvariantCulture);
            if (t == typeof(bool)) return bool.Parse(v);
            if (t == typeof(string)) return v;
            if (t.IsEnum) return Enum.Parse(t, v, true);
            throw new Exception("can't set a " + t.Name);
        }

        private static string Text(object v)
        {
            return v is float ? ((float)v).ToString("R", CultureInfo.InvariantCulture) : Convert.ToString(v, CultureInfo.InvariantCulture);
        }
    }
}
