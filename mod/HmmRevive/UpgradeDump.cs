using System;
using System.Linq;
using System.Text;
using HeavyMetalMachines;
using HeavyMetalMachines.Bank;
using HeavyMetalMachines.BotAI;
using HeavyMetalMachines.Combat;
using HeavyMetalMachines.Combat.Gadget;
using HeavyMetalMachines.Match;
using UnityEngine;

namespace HmmRevive
{
    /// <summary>
    /// Server: logs what the removed in-match shop would need. Once per match, every car's gadget upgrades (names,
    /// level names, prices, max level) and the bots' shopping lists; then every 30 s each player's metal (scrap).
    /// </summary>
    public static class UpgradeDump
    {
        private static bool _dumped;
        private static float _nextScrap;

        // Called once a second from Watchdog.Update.
        public static void Tick(HMMHub hub)
        {
            if (!Entry.ServerMode) return;
            var all = hub.Players?.PlayersAndBots;
            if (all == null || all.Count == 0 || all.Any(p => p.CharacterInstance == null)) return;
            try
            {
                if (!_dumped)
                {
                    _dumped = true;
                    DumpUpgrades(all.ToArray());
                    DumpBotLists();
                }
                if (Time.unscaledTime >= _nextScrap)
                {
                    _nextScrap = Time.unscaledTime + 30f;
                    Log.Info("SCRAP " + string.Join(" | ", all.Select(p =>
                    {
                        var s = p.CharacterInstance.GetBitComponent<PlayerStats>();
                        return s == null ? p.Name + "=?" : $"{p.Name}={s.Scrap} (collected {s.ScrapCollected}, spent {s.ScrapSpent})";
                    }).ToArray()));
                }
            }
            catch (Exception e)
            {
                Log.Error("UpgradeDump failed: " + e);
            }
        }

        private static void DumpUpgrades(PlayerData[] players)
        {
            foreach (var p in players)
            {
                var combat = p.CharacterInstance.GetBitComponent<CombatObject>();
                var sb = new StringBuilder();
                sb.Append($"UPGRADES {p.Name} car={p.Character?.name}");
                if (combat == null) { Log.Info(sb.Append(" (no CombatObject)").ToString()); continue; }
                foreach (GadgetSlot slot in Enum.GetValues(typeof(GadgetSlot)))
                {
                    GadgetBehaviour g;
                    try { g = combat.GetGadget(slot); } catch { continue; }
                    if (g == null || g.Info == null) continue;
                    var info = g.Info;
                    sb.Append($"\n  [{slot}] {info.Name} ({info.GetType().Name}) price={info.Price}" +
                              $" upgrades={g.Upgrades.Length} invisible={g.InvisibleUpgrades.Length} values={info.UpgradesValues?.Length ?? 0}");
                    foreach (var u in g.Upgrades)
                    {
                        var ui = u.Info;
                        sb.Append($"\n    {ui.Name} tag={ui.Tag} max={u.MaxLevel} available={u.Available}" +
                                  $" prices=[{Join(ui.LevelPrices)}] levels=[{Join(ui.LevelNames)}]");
                        if (ui.ExternalUpgrades != null && ui.ExternalUpgrades.Length > 0)
                            sb.Append(" external=[" + string.Join(",", ui.ExternalUpgrades.Select(x => x.GadgetSlot + ":" + x.UpgradeName).ToArray()) + "]");
                    }
                    if (info.UpgradesValues != null)
                        foreach (var v in info.UpgradesValues)
                            sb.Append($"\n    value {v.Name} = [{Join(v.Values)}]");
                }
                Log.Info(sb.ToString());
            }
        }

        private static void DumpBotLists()
        {
            var lists = Resources.FindObjectsOfTypeAll<BotAIGadgetList>();
            Log.Info($"BOTSHOP {lists.Length} lists");
            foreach (var l in lists)
                Log.Info($"BOTSHOP {l.name}: " + string.Join(", ", (l.GadgetList ?? new System.Collections.Generic.List<BotGadgetShopInfo>())
                    .Select(b => $"{b.GadgetSlot}:{b.UpgradeName}{(b.Recurring ? "*" : "")}").ToArray()));
        }

        private static string Join<T>(T[] a) => a == null ? "" : string.Join(",", a.Select(x => x?.ToString()).ToArray());
    }
}
