using System;
using System.Collections.Generic;
using HeavyMetalMachines;
using HeavyMetalMachines.Combat;
using HeavyMetalMachines.Infra.Context;
using HeavyMetalMachines.Match;
using Pocketverse;
using UnityEngine;

namespace HmmRevive
{
    /// <summary>
    /// Server: out-of-combat repair. A car that took no damage for <see cref="Entry.RepairDelay"/> seconds repairs
    /// <see cref="Entry.RepairHpPerSecond"/> HP per second (or <see cref="Entry.RepairPercentPerSecond"/>% of its max HP per
    /// second when set) until it is full or gets hit again. The default, 100 HP/s flat, is what the arenas' repair areas
    /// give (HazardArea with the Repair_Hazard modifiers, e.g. Metal God Arena; players remember regen matching them).
    ///
    /// Hoplon's own version (MartyrModifiersOutOfCombat in the OutOfCombatGadget slot) is still in the code, but every
    /// car's slot now holds Dummy_GenericGadget and its effect assets are gone, so this redoes it by hand. "Took damage"
    /// is the game's damage event (CombatObject.ListenToPosDamageTaken, every HP damage kind incl. hazards, damage the
    /// shield absorbed and damage a car does to itself, e.g. the HP cost of Full Metal Judge's aggressive mode, 0.15 per
    /// tick). Temp HP draining by itself (CombatData's HPTempDegradation, e.g. Full Metal Judge's shield) is not a hit;
    /// watching HP + temp HP for drops made that shield stop the repair in the defensive mode.
    /// HP is written the same way CombatData's own regen does it; the CombatData stream sends it to the clients.
    /// </summary>
    public class OutOfCombatRepair : MonoBehaviour
    {
        private class CarState
        {
            public int LastHitTime;  // playback ms of the last damage (or death/respawn/round change)
            public bool Repairing;
            public bool Full; // "OOC full" logged for this repair
        }

        // Per car (the CombatObject survives car swaps), subscribed to its damage event when first seen.
        private static readonly Dictionary<CombatObject, CarState> Cars = new Dictionary<CombatObject, CarState>();
        private static int _lastTime;

        public static void Attach(HMMHub hub)
        {
            if (!Entry.ServerMode || Entry.RepairDelay < 0) return;
            if (hub.GetComponent<OutOfCombatRepair>() == null) hub.gameObject.AddComponent<OutOfCombatRepair>();
            Log.Info($"out-of-combat repair: {Entry.RepairRate} after {Entry.RepairDelay}s without damage");
        }

        private void Update()
        {
            HMMHub hub = GameHubBehaviour.Hub;
            if (hub == null || hub.Players == null || hub.State.Current == null) return;
            if (hub.State.Current.StateKind != GameState.GameStateKind.Game)
            {
                Cars.Clear();
                return;
            }
            try
            {
                Tick(hub);
            }
            catch (Exception e)
            {
                Log.Error("out-of-combat repair failed: " + e);
                Cars.Clear();
            }
        }

        private static void Tick(HMMHub hub)
        {
            int now = hub.GameTime.GetPlaybackTime();
            float dt = Mathf.Max(0, now - _lastTime) / 1000f;
            _lastTime = now;
            bool delivery = hub.BombManager.CurrentBombGameState == BombScoreboardState.BombDelivery;
            int delayMs = (int)(Entry.RepairDelay * 1000f);
            foreach (PlayerData p in hub.Players.PlayersAndBots)
            {
                CombatObject combat = p.CharacterInstance == null ? null : p.CharacterInstance.GetBitComponent<CombatObject>();
                if (combat == null || combat.Data == null) continue;
                CombatData data = combat.Data;
                CarState s;
                if (!Cars.TryGetValue(combat, out s))
                {
                    Cars[combat] = s = new CarState { LastHitTime = now };
                    combat.ListenToPosDamageTaken += OnDamageTaken;
                }
                bool usable = delivery && combat.IsAlive() && data.HP > 0f
                              && combat.SpawnController != null && combat.SpawnController.State == SpawnStateKind.Spawned;
                if (!usable)
                {
                    s.Repairing = false;
                    s.LastHitTime = now;
                    continue;
                }
                if (now - s.LastHitTime >= delayMs && data.HP < data.HPMax
                    && !combat.Attributes.CurrentStatus.HasFlag(StatusKind.HpUnhealable))
                {
                    if (!s.Repairing) Log.Info($"OOC start {p.Name} hp={data.HP:0}/{data.HPMax}");
                    if (!s.Repairing) s.Full = false;
                    s.Repairing = true;
                    data.HP = Mathf.Min(data.HPMax, data.HP + (Entry.RepairPercentPerSecond > 0f ? data.HPMax * Entry.RepairPercentPerSecond / 100f : Entry.RepairHpPerSecond) * dt);
                    if (data.HP >= data.HPMax && !s.Full) Log.Info($"OOC full {p.Name} hp={data.HPMax}");
                    if (data.HP >= data.HPMax) s.Full = true;
                }
                else if (data.HP < data.HPMax) s.Repairing = false; // full: stay on (no second "start" log)
            }
        }

        // Server damage event (CombatController, after the damage is applied).
        private static void OnDamageTaken(CombatObject causer, CombatObject taker, ModifierData mod, float amount, int eventId)
        {
            CarState s;
            if (taker == null || !Cars.TryGetValue(taker, out s)) return;
            if (s.Repairing) Log.Info($"OOC stop {taker.Player?.Name} hp={taker.Data.HP:0}/{taker.Data.HPMax} (hit by {(causer == null ? "?" : causer == taker ? "itself" : causer.name)})");
            s.Repairing = false;
            s.LastHitTime = GameHubBehaviour.Hub.GameTime.GetPlaybackTime();
        }
    }
}
