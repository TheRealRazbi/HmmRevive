using System;
using System.Globalization;
using System.Reflection;
using HeavyMetalMachines;
using HeavyMetalMachines.Combat;
using HeavyMetalMachines.Combat.Gadget;
using HeavyMetalMachines.Match;
using Pocketverse;
using EffectKind = HeavyMetalMachines.Combat.EffectKind;

namespace HmmRevive
{
    /// <summary>Lobby damage% on outgoing HP hits (passive damage buffs did not replicate reliably).</summary>
    internal static class CarDamageOverlay
    {
        private static FieldInfo _dataAmount;

        private struct PendingLog
        {
            public bool Active;
            public CombatController VictimCtrl;
            public string LinePrefix;
            public float HpBefore, HpMax, ExpectLoss;
            public bool Suspended;
        }

        [ThreadStatic] private static PendingLog _log;

        public static void BeforeApplyInstant(CombatController victimCtrl, ModifierInstance mod, CombatObject causer)
        {
            _log.Active = false;
            if (mod?.Info == null || !IsHpWeapon(mod.Info.Effect)) return;

            HMMHub hub = GameHubBehaviour.Hub;
            PlayerData attacker = ResolvePlayer(hub, causer);
            bool suspended = hub != null && causer != null && CarStats.ShouldSuspendMoveDamage(hub, causer);
            float mult = attacker != null ? CarStats.MultipliersForPlayer(attacker).Damage : 1f;
            float baseAmt = mod.Amount;
            float finalAmt = baseAmt;

            if (!suspended && Math.Abs(mult - 1f) > 0.001f)
            {
                finalAmt = baseAmt * mult;
                SetAmount(mod.Data, finalAmt);
            }

            if (!Entry.LogDamage) return;

            CombatObject victim = victimCtrl?.Combat;
            CombatData vd = victim?.Data;
            string who = attacker?.Name ?? causer?.name ?? "?";
            string tgt = victim?.Player?.Name ?? victim?.name ?? "?";
            int pct = attacker != null ? CarStats.MultipliersForPlayer(attacker).DamagePct : 100;
            string prefix = suspended
                ? "DAMAGE " + who + " -> " + tgt + " " + mod.Info.Effect + " base=" + F(baseAmt) + " (lobby dmg suspended: bomb)"
                : "DAMAGE " + who + " -> " + tgt + " " + mod.Info.Effect + " base=" + F(baseAmt) + " lobby=" + pct + "% final=" + F(finalAmt);

            _log = new PendingLog
            {
                Active = true,
                VictimCtrl = victimCtrl,
                LinePrefix = prefix,
                HpBefore = vd != null ? vd.HP : -1f,
                HpMax = vd != null ? vd.HPMax : -1f,
                ExpectLoss = finalAmt,
                Suspended = suspended,
            };
        }

        public static void AfterApplyInstant(CombatController victimCtrl)
        {
            if (!Entry.LogDamage || !_log.Active || _log.VictimCtrl != victimCtrl) return;
            PendingLog p = _log;
            _log.Active = false;

            float hpAfter = victimCtrl?.Combat?.Data?.HP ?? -1f;
            Log.Info(p.LinePrefix + " " + HpPart(p.HpBefore, hpAfter, p.HpMax, p.ExpectLoss, p.Suspended));
        }

        /// <summary>ApplyInstant uses Upgradeable.Get(): Value, or Upgrades.Values[Level-1].</summary>
        private static void SetAmount(ModifierData data, float amount)
        {
            if (data == null) return;
            EnsureFields();
            var upg = _dataAmount?.GetValue(data) as Upgradeable;
            if (upg == null) return;
            upg.Value = amount;
            float[] vals = upg.Upgrades?.Values;
            if (upg.Level > 0 && vals != null && upg.Level <= vals.Length)
                vals[upg.Level - 1] = amount;
        }

        private static string HpPart(float before, float after, float max, float expect, bool suspended)
        {
            if (before < 0f || after < 0f || max <= 0f) return "victimHp=?";
            float delta = before - after;
            string s = "victimHp=" + F(before) + "->" + F(after) + "/" + F(max) + " (-" + F(delta) + ")";
            if (!suspended && expect > 0.01f)
                s += Math.Abs(delta - expect) < 1.5f ? " applied=ok" : " applied=MISS want(-" + F(expect) + ")";
            return s;
        }

        private static bool IsHpWeapon(EffectKind e) =>
            e == EffectKind.HPPureDamage || e == EffectKind.HPPureDamageNL
            || e == EffectKind.HPLightDamage || e == EffectKind.HPHeavyDamage;

        private static PlayerData ResolvePlayer(HMMHub hub, CombatObject combat)
        {
            if (combat?.Player != null) return combat.Player;
            if (hub?.Players?.PlayersAndBots == null || combat == null) return null;
            foreach (PlayerData p in hub.Players.PlayersAndBots)
            {
                if (p?.CharacterInstance == null) continue;
                if (p.CharacterInstance.GetBitComponent<CombatObject>() == combat) return p;
            }
            return null;
        }

        private static void EnsureFields()
        {
            if (_dataAmount != null) return;
            _dataAmount = typeof(ModifierData).GetField("_amount", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
