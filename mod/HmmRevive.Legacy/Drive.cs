using HeavyMetalMachines;
using HeavyMetalMachines.Car;
using HeavyMetalMachines.Options;
using Pocketverse;

namespace HmmRevive.Legacy
{
    /// <summary>
    /// Client: the launcher's "Drive with WASD" (--hmmrevive-wasd). The old builds have three driving modes in their own
    /// options (Esc > Controls): Simulator (W/S speed, A/D steer, like the Steam game), FollowMouse (W/S speed, the car
    /// turns towards the mouse) and Controller (gamepad). The game's default is FollowMouse with speed on the mouse
    /// buttons (forward = left click, back = right click). It's the lobby host's choice (all players alike). With the flag
    /// the game is set to Simulator whenever it changes screen (menu, pick, match), so a mode picked in the Esc menu during
    /// a match holds until the next screen, and forward/back get W/S (the mouse buttons stay as second keys). The game
    /// saves key bindings itself, so without the flag our W/S bindings go back to the game's defaults.
    /// </summary>
    public static class Drive
    {
        private static GameState _seen;

        // Watch.Update (clients).
        public static void Tick(HMMHub hub, GameState cur)
        {
            if (cur == _seen || hub.Options == null || hub.Options.Game == null) return;
            _seen = cur;
            GameOptions game = hub.Options.Game;
            var mode = (CarInput.DrivingStyleKind)game.MovementModeIndex;
            string line = "driving: " + (cur ? cur.name : "-") + " mode=" + mode + " keys W=" + Key("MovementForward") + " S=" + Key("MovementBackward")
                          + " A=" + Key("MovementLeft") + " D=" + Key("MovementRight");
            if (Entry.Wasd && mode != CarInput.DrivingStyleKind.Simulator)
            {
                game.MovementModeIndex = (int)CarInput.DrivingStyleKind.Simulator;
                line += " -> Simulator (WASD)";
            }
            line += Bind("MovementForward", "Mouse0", "None", "W") + Bind("MovementBackward", "Mouse1", "Joy1 Axis 9+", "S");
            Log.Info(line);
        }

        // The game's default (primary/secondary) <-> ours (key/the default's mouse button). Anything else is the player's own.
        private static string Bind(string action, string mouse, string secondary, string key)
        {
            try
            {
                string p = cInput.GetText(action, 1), s = cInput.GetText(action, 2);
                if (Entry.Wasd && p == mouse && s == secondary) cInput.ChangeKey(action, key, mouse);
                else if (!Entry.Wasd && p == key && s == mouse) cInput.ChangeKey(action, mouse, secondary);
                else return "";
                return " " + action + "=" + cInput.GetText(action, 1) + "/" + cInput.GetText(action, 2);
            }
            catch { return ""; } // keys not loaded yet: next screen
        }

        private static string Key(string action)
        {
            try { return cInput.GetText(action, 1) + "/" + cInput.GetText(action, 2); }
            catch { return "?"; }
        }
    }
}
