using System.Collections.Generic;
using System.Reflection;
using HeavyMetalMachines;
using HeavyMetalMachines.Combat;
using HeavyMetalMachines.Combat.Gadget;
using HeavyMetalMachines.Combat.GadgetScript;
using HeavyMetalMachines.Match;
using UnityEngine;

namespace HmmRevive
{
    /// <summary>
    /// The lobby's "Ability cooldowns" (--hmmrevive-cooldown=SECONDS, server and clients): every car's weapons, special
    /// and boost recharge in that many seconds. The game has two kinds of gadgets:
    /// - classic ones (GadgetBehaviour) keep their cooldown per car in _cooldown, set from GadgetInfo.Cooldown in SetInfo.
    ///   It's changed there, never in the GadgetInfo, which every car of that kind shares; checked twice a second from
    ///   CarSwap's Update, so cars rebuilt by a /car swap get it too.
    /// - scripted ones (CombatGadget) read theirs in StartCooldownBlock.GetCooldown, which asks ScriptCooldown (IL hook).
    /// The game's cooldown reductions still apply on top, as they do to the gadgets' own values.
    /// </summary>
    public static class Cooldowns
    {
        private static readonly string[] AbilitySlots = { "CustomGadget0", "CustomGadget1", "CustomGadget2", "BoostGadget" };
        private static readonly FieldInfo CooldownField = typeof(GadgetBehaviour).GetField("_cooldown", BindingFlags.Instance | BindingFlags.NonPublic);
        // Object ids of the match's cars (players and bots): scripted gadgets of anything else keep their cooldown.
        private static readonly HashSet<int> Cars = new HashSet<int>();
        private static float _next;

        // CarSwap.Update, during the match (server and clients).
        public static void Enforce(HMMHub hub)
        {
            float target = Entry.Cooldown;
            if (target <= 0f || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.5f;
            foreach (PlayerData p in hub.Players.PlayersAndBots)
            {
                if (p.CharacterInstance == null) continue;
                var combat = p.CharacterInstance.GetBitComponent<CombatObject>();
                if (combat == null || combat.Id == null) continue;
                if (Cars.Add(combat.Id.ObjId)) Log.Info($"cooldowns {target}s for {p.Name} (car {combat.Id.ObjId})");
                foreach (string slot in AbilitySlots)
                {
                    var gadget = typeof(CombatObject).GetField(slot).GetValue(combat) as GadgetBehaviour;
                    var cooldown = gadget != null ? CooldownField.GetValue(gadget) as Upgradeable : null;
                    if (cooldown == null || (cooldown.Value == target && cooldown.Level == 0)) continue;
                    cooldown.Value = target;
                    cooldown.Level = 0; // Get() returns Value at level 0
                }
            }
        }

        // Start of StartCooldownBlock.GetCooldown(baseCooldown, context): baseCooldown = ScriptCooldown(baseCooldown, context).
        // context.Id is the gadget's slot; only the abilities change, not emotes or quick chat.
        public static float ScriptCooldown(float cooldown, object context)
        {
            if (Entry.Cooldown <= 0f || Cars.Count == 0) return cooldown;
            var ctx = context as IHMMGadgetContext;
            var combat = ctx != null ? ctx.Owner as CombatObject : null;
            if (combat == null || combat.Id == null || !Cars.Contains(combat.Id.ObjId)) return cooldown;
            int slot = ctx.Id;
            return slot == (int)GadgetSlot.CustomGadget0 || slot == (int)GadgetSlot.CustomGadget1 || slot == (int)GadgetSlot.CustomGadget2
                || slot == (int)GadgetSlot.BoostGadget ? Entry.Cooldown : cooldown;
        }
    }
}
