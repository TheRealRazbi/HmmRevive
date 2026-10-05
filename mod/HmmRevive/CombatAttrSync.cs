using HeavyMetalMachines;
using HeavyMetalMachines.Combat;
using Pocketverse;

namespace HmmRevive
{
    /// <summary>Replicate combat attributes + HP after lobby stat passives (clients read these streams).</summary>
    internal static class CombatAttrSync
    {
        /// <summary>HP only — do not push combat attributes (that overwrites client move/damage passives).</summary>
        public static void PushData(CombatObject combat)
        {
            if (!Entry.ServerMode || combat == null) return;
            HMMHub hub = GameHubBehaviour.Hub;
            if (hub?.Stream == null) return;
            try { hub.Stream.CombatDataStream.Changed(combat.Data); }
            catch (System.Exception e) { Log.Error("CAR STATS data push: " + e.Message); }
        }

        public static void Push(CombatObject combat)
        {
            if (!Entry.ServerMode || combat == null) return;
            HMMHub hub = GameHubBehaviour.Hub;
            if (hub?.Stream == null) return;
            try
            {
                hub.Stream.CombatAttStream.Changed(combat.Attributes);
                hub.Stream.CombatDataStream.Changed(combat.Data);
            }
            catch (System.Exception e)
            {
                Log.Error("CAR STATS stream push: " + e.Message);
            }
        }
    }
}
