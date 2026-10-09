using System.Linq;
using HeavyMetalMachines;
using HeavyMetalMachines.Combat;
using HeavyMetalMachines.Match;
using UnityEngine;

namespace HmmRevive.Legacy
{
    /// <summary>
    /// Client: on Metal God Arena (Arena_Test) the red/blue ball droppers (BombTargetTrigger boxes that make an enemy car
    /// drop the ball) are only partly drawn: the game's glow (BombDropper/GFX_bombDropper_repulse_*.hps, two "mark"
    /// emitters per post in the September 2017 copy) covers part of each box, and some boxes along the rails have no
    /// look at all, so cars lose the ball on bare floor. Every box already carries a cube with its renderer switched off;
    /// this draws it as a soft glow in the owner team's colour, pulsing a little, exactly where the ball gets dropped.
    /// The droppers around the goals are left alone (the game draws those zones).
    /// Found with the test aid --hmmrevive-dropper-scan (DropperScan.cs).
    /// </summary>
    public static class DropperLook
    {
        private static string _doneFor;
        private static Material _red, _blue;

        // Watch.Update (clients).
        public static void Tick(HMMHub hub)
        {
            if (hub.Match == null || hub.Match.State != MatchData.MatchState.MatchStarted) { _doneFor = null; return; }
            string level = Application.loadedLevelName;
            if (level == _doneFor) { Pulse(); return; }
            _doneFor = level;
            if (level != "Arena_Test") return;
            int n = 0;
            BombTargetTrigger[] all = Resources.FindObjectsOfTypeAll(typeof(BombTargetTrigger)).Cast<BombTargetTrigger>().Where(t => t.gameObject.activeInHierarchy).ToArray();
            Vector3[] goals = all.Where(t => t.ExplodeOnDrop).Select(t => t.transform.position).ToArray();
            foreach (BombTargetTrigger t in all)
            {
                if (t.ExplodeOnDrop) continue; // a goal
                // The droppers around each goal are big boxes under the game's own drop-zone art: leave them.
                if (goals.Any(g => (g - t.transform.position).magnitude < 80f)) continue;
                var r = t.GetComponent<MeshRenderer>();
                if (r == null || t.GetComponent<MeshFilter>() == null) continue;
                Material m = Look(t.TeamOwner);
                if (m == null) { Log.Error("dropper look: no glow shader in this build"); return; }
                r.sharedMaterial = m;
                r.castShadows = false;
                r.receiveShadows = false;
                r.enabled = true;
                n++;
            }
            Log.Info("dropper look: " + n + " ball droppers drawn on " + level);
        }

        private static Material Look(TeamKind team)
        {
            if (_red == null)
            {
                Shader s = Shader.Find("Particles/Additive") ?? Shader.Find("Particles/Alpha Blended");
                if (s == null) return null;
                _red = new Material(s) { name = "HmmRevive dropper red" };
                _blue = new Material(s) { name = "HmmRevive dropper blue" };
            }
            return team == TeamKind.Blue ? _blue : _red;
        }

        private static void Pulse()
        {
            if (_red == null) return;
            float a = 0.22f + 0.08f * Mathf.Sin(Time.time * 3f);
            _red.SetColor("_TintColor", new Color(1f, 0.12f, 0.08f, a));
            _blue.SetColor("_TintColor", new Color(0.1f, 0.35f, 1f, a));
        }
    }
}
