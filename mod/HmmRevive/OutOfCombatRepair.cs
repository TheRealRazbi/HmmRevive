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
    /// <see cref="Entry.RepairPercentPerSecond"/>% of its max HP per second until it is full or gets hit again.
    ///
    /// Hoplon's own version (MartyrModifiersOutOfCombat in the OutOfCombatGadget slot) is still in the code, but every
    /// car's slot now holds Dummy_GenericGadget and its effect assets are gone, so this redoes it by hand. "Took damage"
    /// is HP or temp HP going down between frames, so hazards and every damage kind count. HP is written the same way
    /// CombatData's own regen does it; the CombatData stream sends it to the clients, so they need nothing.
    /// </summary>
    public class OutOfCombatRepair : MonoBehaviour
    {
        private class CarState
        {
            public float LastHealth; // HP + temp HP seen at the end of the previous frame
            public int LastHitTime;  // playback ms of the last damage (or death/respawn/round change)
            public bool Repairing;
        }

        private static readonly Dictionary<int, CarState> Cars = new Dictionary<int, CarState>();
        private static int _lastTime;

        public static void Attach(HMMHub hub)
        {
            if (!Entry.ServerMode || Entry.RepairDelay < 0) return;
            if (hub.GetComponent<OutOfCombatRepair>() == null) hub.gameObject.AddComponent<OutOfCombatRepair>();
            Log.Info($"out-of-combat repair: {Entry.RepairPercentPerSecond}% max HP/s after {Entry.RepairDelay}s without damage");
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
                if (!Cars.TryGetValue(p.PlayerCarId, out s)) Cars[p.PlayerCarId] = s = new CarState { LastHitTime = now };
                float health = data.HP + data.HPTemp;
                bool usable = delivery && combat.IsAlive() && data.HP > 0f
                              && combat.SpawnController != null && combat.SpawnController.State == SpawnStateKind.Spawned;
                if (!usable || health < s.LastHealth - 0.01f)
                {
                    if (s.Repairing && usable) Log.Info($"OOC stop {p.Name} hp={data.HP:0}/{data.HPMax} (hit)");
                    s.Repairing = false;
                    s.LastHitTime = now;
                    s.LastHealth = health;
                    continue;
                }
                if (now - s.LastHitTime >= delayMs && data.HP < data.HPMax
                    && !combat.Attributes.CurrentStatus.HasFlag(StatusKind.HpUnhealable))
                {
                    if (!s.Repairing) Log.Info($"OOC start {p.Name} hp={data.HP:0}/{data.HPMax}");
                    s.Repairing = true;
                    data.HP = Mathf.Min(data.HPMax, data.HP + data.HPMax * Entry.RepairPercentPerSecond / 100f * dt);
                    if (data.HP >= data.HPMax) Log.Info($"OOC full {p.Name} hp={data.HPMax}");
                }
                else s.Repairing = false;
                s.LastHealth = data.HP + data.HPTemp;
            }
        }
    }
}
