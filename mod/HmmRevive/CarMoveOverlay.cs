using System.Reflection;
using HeavyMetalMachines;
using HeavyMetalMachines.Car;
using HeavyMetalMachines.Combat;
using HeavyMetalMachines.Match;
using Pocketverse;

namespace HmmRevive
{
    /// <summary>Lobby move% on the accel local used for <see cref="Rigidbody.AddRelativeForce"/> (patched after _lastAccel).</summary>
    internal static class CarMoveOverlay
    {
        private static FieldInfo _lastAccel;

        public static float ScaleMovementScalar(float accel, CarMovement mov) =>
            accel * ResolveMoveMult(mov);

        public static void ScaleStoredLastAccel(CarMovement mov)
        {
            if (mov == null) return;
            float mult = ResolveMoveMult(mov);
            if (mult == 1f) return;
            EnsureFields();
            if (_lastAccel == null) return;
            float a = (float)_lastAccel.GetValue(mov);
            _lastAccel.SetValue(mov, a * mult);
        }

        public static void ClearObject(int objId) { }

        internal static float ResolveMoveMult(CarMovement mov)
        {
            if (mov == null) return 1f;
            CombatObject combat = mov.Combat;
            if (combat == null) return 1f;
            HMMHub hub = GameHubBehaviour.Hub;
            if (hub == null) return 1f;

            PlayerData player = combat.Player;
            if (player == null && hub.Players?.PlayersAndBots != null)
            {
                foreach (PlayerData p in hub.Players.PlayersAndBots)
                {
                    if (p?.CharacterInstance == null) continue;
                    if (p.CharacterInstance.GetBitComponent<CombatObject>() == combat)
                    {
                        player = p;
                        break;
                    }
                }
            }
            if (player == null) return 1f;
            return CarStats.MultipliersForPlayer(player).Move;
        }

        private static void EnsureFields()
        {
            if (_lastAccel != null) return;
            const BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            _lastAccel = typeof(CarMovement).GetField("_lastAccel", f)
                         ?? typeof(CombatMovement).GetField("_lastAccel", f);
        }
    }
}
