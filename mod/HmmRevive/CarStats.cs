using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HeavyMetalMachines;
using HeavyMetalMachines.Combat;
using HeavyMetalMachines.Combat.Gadget;
using HeavyMetalMachines.Match;
using UnityEngine;
namespace HmmRevive
{
    /// <summary>
    /// Lobby car stat multipliers (HP / move / damage). HP on server; move/damage via movement/damage overlays.
    /// Config: --hmmrevive-car-stats=default:100,100,100;8:150,120,110 (percent 0–300, 100 = 1×).
    /// </summary>
    public static class CarStats
    {
        public struct Multipliers
        {
            public int HpPct, MovePct, DamagePct;

            public float Hp => HpPct / 100f;
            public float Move => MovePct / 100f;
            public float Damage => DamagePct / 100f;

            public static Multipliers Vanilla => new Multipliers { HpPct = 100, MovePct = 100, DamagePct = 100 };

            public bool IsVanilla => HpPct == 100 && MovePct == 100 && DamagePct == 100;

            public int Hash => HpPct * 1000000 + MovePct * 1000 + DamagePct;
        }

        private static readonly Dictionary<string, Multipliers> ByKey = new Dictionary<string, Multipliers>(StringComparer.OrdinalIgnoreCase);
        private static Multipliers _default = Multipliers.Vanilla;
        /// <summary>Our additive slice of CombatData._levelHPMax (passive HPMax mods do not apply on dedicated server).</summary>
        private static readonly Dictionary<int, float> AppliedHpLevelBonus = new Dictionary<int, float>();
        private static FieldInfo _levelHpMaxField;

        private static FieldInfo LevelHpMaxField => _levelHpMaxField ?? (_levelHpMaxField =
            typeof(CombatData).GetField("_levelHPMax", BindingFlags.Instance | BindingFlags.NonPublic));

        public static void Configure(string compact)
        {
            ByKey.Clear();
            _default = Multipliers.Vanilla;
            AppliedHpLevelBonus.Clear();
            if (string.IsNullOrEmpty(compact)) return;

            foreach (string part in compact.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string p = part.Trim();
                int colon = p.IndexOf(':');
                if (colon <= 0) continue;
                string key = p.Substring(0, colon).Trim();
                string[] nums = p.Substring(colon + 1).Split(',');
                if (nums.Length < 3) continue;
                var m = new Multipliers
                {
                    HpPct = ClampPct(nums[0]),
                    MovePct = ClampPct(nums[1]),
                    DamagePct = ClampPct(nums[2]),
                };
                if (key.Equals("default", StringComparison.OrdinalIgnoreCase)) _default = m;
                else ByKey[key] = m;
            }
            Log.Info("CAR STATS config default=" + Format(_default) + " overrides=" + ByKey.Count);
        }

        public static void Tick(HMMHub hub)
        {
            if (hub?.Players?.PlayersAndBots == null) return;
            if (string.IsNullOrEmpty(Entry.CarStatsConfig) && _default.IsVanilla && ByKey.Count == 0) return;
            if (!Entry.ServerMode && string.IsNullOrEmpty(Entry.CarStatsConfig)) return;

            foreach (var player in hub.Players.PlayersAndBots)
            {
                if (player?.CharacterInstance == null) continue;
                var combat = player.CharacterInstance.GetBitComponent<CombatObject>();
                if (combat == null) continue;

                int objId = combat.Identifiable.ObjId;
                Multipliers m = For(player);
                ApplyHpLevel(combat, objId, m);
                ClampHp(combat.Data);
            }
        }

        public static void ClearObject(int objId)
        {
            AppliedHpLevelBonus.Remove(objId);
            CarMoveOverlay.ClearObject(objId);
        }

        internal static Multipliers MultipliersForPlayer(PlayerData player) => For(player);

        internal static bool ShouldSuspendMoveDamage(HMMHub hub, CombatObject combat) =>
            ShouldSuspendMoveDamageInternal(hub, combat);

        public static void ClearObject(CombatObject combat)
        {
            if (combat == null) return;
            ClearHpLevel(combat.Data, combat.Identifiable.ObjId);
            ClearObject(combat.Identifiable.ObjId);
        }

        /// <summary>Bomb chain, grab/spin, and power-shot must not stack with lobby move/damage passives.</summary>
        private static void ClampHp(CombatData data)
        {
            if (data == null) return;
            if (data.HP > data.HPMax) data.HP = data.HPMax;
            if (data.HP < 0f) data.HP = 0f;
        }

        private static bool ShouldSuspendMoveDamageInternal(HMMHub hub, CombatObject combat)
        {
            if (combat == null) return false;
            if (combat.IsCarryingBomb) return true;
            int objId = combat.Identifiable.ObjId;
            try
            {
                BombInstance active = hub?.BombManager?.ActiveBomb;
                if (active != null && active.IsSpawned && active.BombCarriersIds != null
                    && active.BombCarriersIds.Contains(objId))
                    return true;
            }
            catch { /* replicated bomb state can be briefly null */ }
            GadgetBehaviour g = combat.BombGadget;
            var bombGadget = g as BombGadget;
            if (bombGadget == null) return false;
            return bombGadget.CurrentState != BombGadget.State.Disabled;
        }

        private static Multipliers For(PlayerData player)
        {
            string id = player.CharacterId.ToString(CultureInfo.InvariantCulture);
            Multipliers m;
            if (ByKey.TryGetValue(id, out m)) return m;
            if (player.Character != null)
            {
                if (ByKey.TryGetValue(player.Character.name, out m)) return m;
                string lower = player.Character.name.ToLowerInvariant();
                if (ByKey.TryGetValue(lower, out m)) return m;
            }
            return _default;
        }

        /// <summary>HP via _levelHPMax (same term the game uses for level scaling); refreshed every tick.</summary>
        private static void ApplyHpLevel(CombatObject combat, int objId, Multipliers m)
        {
            FieldInfo f = LevelHpMaxField;
            if (f == null) return;
            CombatData data = combat.Data;
            float infoMax = data.Info.HPMax;
            float wantBonus = infoMax * (m.Hp - 1f);
            float prevBonus;
            AppliedHpLevelBonus.TryGetValue(objId, out prevBonus);
            if (Math.Abs(wantBonus - prevBonus) < 0.01f) return;

            float hpBefore = data.HPMax;
            float hpNow = data.HP;
            float level = (float)f.GetValue(data);
            f.SetValue(data, level - prevBonus + wantBonus);
            AppliedHpLevelBonus[objId] = wantBonus;

            float hpAfter = data.HPMax;
            if (hpBefore > 0.01f && Math.Abs(hpAfter - hpBefore) > 0.01f)
                data.HP = Mathf.Min(hpAfter, hpNow * (hpAfter / hpBefore));
            ClampHp(data);
            Log.Info("CAR STATS HP level bonus obj=" + objId + " " + hpBefore.ToString("0") + "->" + hpAfter.ToString("0")
                     + " (pct=" + (m.HpPct) + " bonus=" + wantBonus.ToString("0") + ")");
            CombatAttrSync.PushData(combat);
        }

        /// <summary>Reserved hook at bomb link (move overlay already off during bomb).</summary>
        public static void StripBeforeBombLink(CombatObject combat) { }

        private static void ClearHpLevel(CombatData data, int objId)
        {
            FieldInfo f = LevelHpMaxField;
            if (f == null) return;
            float prevBonus;
            if (!AppliedHpLevelBonus.TryGetValue(objId, out prevBonus)) return;
            float level = (float)f.GetValue(data);
            f.SetValue(data, level - prevBonus);
            AppliedHpLevelBonus.Remove(objId);
        }

        private static void RemovePassives(CombatObject combat, int objId) { }

        private static int ClampPct(string s)
        {
            int v;
            if (!int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) v = 100;
            if (v < 0) v = 0;
            if (v > 300) v = 300;
            return v;
        }

        private static string Format(Multipliers m) =>
            "hp=" + m.HpPct + "% move=" + m.MovePct + "% dmg=" + m.DamagePct + "%";
    }
}
