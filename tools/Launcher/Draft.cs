using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace HmmRevive.Launcher
{
    /// <summary>
    /// Tournament draft, run in the lobby before the match: the teams take turns banning cars, then take turns picking
    /// them, in the host's order. A = the team that goes first (drawn at random), B = the other one; "A1 B2" means A bans
    /// one car, then B bans two. The same order is used for the bans and then for the picks. A turn's choices stay hidden
    /// from the other team until the team locks them in. Picked cars are the team's cars: after the draft each player
    /// drives one of them, and bots get the ones left. Nobody can pick a banned car or a car the other team picked.
    /// </summary>
    public class Draft
    {
        public const string DefaultOrder = "A1 B2 A2 B1 B1 A1";

        public class Turn { public bool Pick; public string Team; public int Count; }
        public class Choice { public string Car, Team; }

        public readonly string First, Second; // "blue"/"red": A and B
        public readonly List<Turn> Turns = new List<Turn>();
        public readonly List<Choice> Bans = new List<Choice>(), Picks = new List<Choice>();
        public readonly Dictionary<string, List<string>> Pending = new Dictionary<string, List<string>> { ["blue"] = new List<string>(), ["red"] = new List<string>() };
        public int Step;
        private readonly List<string> _cars;

        public bool Done => Step >= Turns.Count;
        public Turn Current => Done ? null : Turns[Step];

        /// <summary>"A1 B2 A2" → (team A?, count) per step, or null when the text isn't a valid order.</summary>
        public static List<(bool a, int n)> Parse(string order)
        {
            if (string.IsNullOrWhiteSpace(order)) return null;
            var steps = new List<(bool, int)>();
            foreach (string tok in order.ToUpperInvariant().Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                Match m = Regex.Match(tok, @"^([AB])([1-9])$");
                if (!m.Success) return null;
                steps.Add((m.Groups[1].Value == "A", int.Parse(m.Groups[2].Value)));
            }
            return steps.Count > 0 && steps.Count <= 20 ? steps : null;
        }

        /// <param name="slots">cars each team needs (players + bots)</param>
        /// <param name="cars">every car id</param>
        public Draft(string order, string first, Dictionary<string, int> slots, IEnumerable<string> cars)
        {
            First = first;
            Second = first == "blue" ? "red" : "blue";
            _cars = cars.ToList();
            var steps = Parse(order) ?? Parse(DefaultOrder);
            // Bans may not leave too few cars for the picks.
            int bansLeft = Math.Max(0, _cars.Count - slots.Values.Sum());
            foreach (var (a, n) in steps)
            {
                int k = Math.Min(n, bansLeft);
                bansLeft -= k;
                if (k > 0) Turns.Add(new Turn { Pick = false, Team = a ? First : Second, Count = k });
            }
            // Picks follow the same order, but a team never picks more cars than it has drivers.
            var given = new Dictionary<string, int> { ["blue"] = 0, ["red"] = 0 };
            foreach (var (a, n) in steps)
            {
                string team = a ? First : Second;
                int k = Math.Min(n, slots[team] - given[team]);
                given[team] += Math.Max(0, k);
                if (k > 0) Turns.Add(new Turn { Pick = true, Team = team, Count = k });
            }
            foreach (string team in new[] { First, Second }) // an order too short for the teams: the rest at the end
                if (given[team] < slots[team]) Turns.Add(new Turn { Pick = true, Team = team, Count = slots[team] - given[team] });
        }

        public bool Available(string car) => _cars.Contains(car) && !Bans.Any(c => c.Car == car) && !Picks.Any(c => c.Car == car);

        public List<string> Pool(string team) => Picks.Where(c => c.Team == team).Select(c => c.Car).ToList();

        /// <summary>A player of <paramref name="team"/> clicks a car: select it for this turn, or unselect it.</summary>
        public string Select(string team, string car)
        {
            Turn t = Current;
            if (t == null) return "The draft is over.";
            if (t.Team != team) return "It's the other team's turn.";
            if (!Available(car)) return "That car is already banned or picked.";
            List<string> p = Pending[team];
            if (p.Remove(car)) return null;
            if (p.Count >= t.Count) p.RemoveAt(0); // full: the newest click replaces the oldest
            p.Add(car);
            return null;
        }

        public string Lock(string team)
        {
            Turn t = Current;
            if (t == null) return "The draft is over.";
            if (t.Team != team) return "It's the other team's turn.";
            List<string> p = Pending[team];
            if (p.Count != t.Count) return t.Count == 1 ? "Choose 1 car first." : $"Choose {t.Count} cars first.";
            Commit();
            return null;
        }

        /// <summary>Random choices for the current turn (a team with no players in the lobby).</summary>
        public void Auto(Random rng)
        {
            Turn t = Current;
            List<string> p = Pending[t.Team];
            p.Clear();
            List<string> free = _cars.Where(Available).OrderBy(_ => rng.Next()).ToList();
            p.AddRange(free.Take(t.Count));
            Commit();
        }

        private void Commit()
        {
            Turn t = Current;
            List<Choice> into = t.Pick ? Picks : Bans;
            foreach (string car in Pending[t.Team]) into.Add(new Choice { Car = car, Team = t.Team });
            Pending[t.Team].Clear();
            Step++;
        }

        /// <summary>The draft as a member of <paramref name="viewerTeam"/> sees it: the other team's selection stays hidden.</summary>
        public Dictionary<string, object> Json(string viewerTeam)
        {
            Turn cur = Current;
            object Choices(List<Choice> list) => list.Select(c => new Dictionary<string, object> { ["car"] = c.Car, ["team"] = c.Team }).ToArray();
            return new Dictionary<string, object>
            {
                ["first"] = First,
                ["step"] = Step,
                ["done"] = Done,
                ["turns"] = Turns.Select(t => new Dictionary<string, object> { ["pick"] = t.Pick, ["team"] = t.Team, ["count"] = t.Count }).ToArray(),
                ["bans"] = Choices(Bans),
                ["picks"] = Choices(Picks),
                ["pending"] = cur != null && cur.Team == viewerTeam ? Pending[viewerTeam].ToArray() : new string[0],
            };
        }
    }
}
