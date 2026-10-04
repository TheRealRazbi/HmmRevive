using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Assets.ClientApiObjects;
using Assets.ClientApiObjects.Components;
using HeavyMetalMachines.Configuring.Instances;
using HeavyMetalMachines.Localization;
using HeavyMetalMachines.Match;
using Hoplon.Unity.Loading;
using Pocketverse;
using UnityEngine;

namespace HmmRevive
{
    /// <summary>
    /// Pick your car and skin at launch (--hmmrevive-car=NAME|ID, --hmmrevive-skin=NAME|NUMBER|random) instead of in a
    /// pick screen. SkipSwordfish mode assigns cars and skins from [Debug] Team{1,2}Character{1..4} / Team{1,2}Skin{1..4}
    /// per slot, so the client appends "#car#skin" to the login name and the server strips it off and uses it for that
    /// player. Bots get the cars the host picked (--hmmrevive-bot-cars-red/blue=6,7,random) and always the default skin.
    /// </summary>
    public static class CarChoice
    {
        private const char Separator = '#';
        private static readonly Dictionary<string, string> ChoiceByName = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> SkinByName = new Dictionary<string, string>();
        private static readonly Dictionary<string, bool> RedByName = new Dictionary<string, bool>();
        private static readonly System.Random Rng = new System.Random();
        private static bool _listed;

        // Client, AuthenticationSerializer.SerializeAuthenticationRequest: transforms the login name that is written.
        // "name", "name#car", "name#car#skin", "name#car#skin#team" or "name#car#skin#team#emotes" (any may be empty).
        public static string LoginName(string name)
        {
            string[] fields = { Entry.Car, Entry.Skin, Entry.Team, Emotes.LoginField };
            int last = Array.FindLastIndex(fields, f => f != null);
            if (last < 0) return name;
            return name + Separator + string.Join(Separator.ToString(), fields.Take(last + 1).Select(f => f ?? "").ToArray());
        }

        // Client, start of UserInfo.InternalConnectToServer(narrator, ...): narrator = Narrator(narrator). A spectator
        // logs in as a narrator; the server (SkipSwordfish) then makes it one in FakeAuthentication → FakeNarrator.
        public static bool Narrator(bool narrator)
        {
            if (Entry.Spectate && !narrator) Log.Info("connecting as a spectator (narrator)");
            return narrator || Entry.Spectate;
        }

        // Server, start of AuthenticationManager.FakeNarrator: the game allows 2 narrators by counting logins
        // (_narratorCount, addresses 100 + count) and never counts down, though it drops a narrator who disconnects. So
        // after two logins nobody could watch, not even a spectator coming back. Count the narrators still there instead,
        // and hand out the lowest free address. (A narrator still listed under the same name reconnects before this check.)
        public static void FreeNarratorSeats(object authManager)
        {
            List<PlayerData> narrators = GameHubBehaviour.Hub.Players.Narrators;
            int free = 0;
            while (free < 2 && narrators.Exists(n => n.PlayerAddress == 100 + free)) free++;
            FieldInfo count = authManager.GetType().GetField("_narratorCount", BindingFlags.Instance | BindingFlags.NonPublic);
            if ((int)count.GetValue(authManager) != free) Log.Info($"spectator seats: {narrators.Count} watching, next seat {(free < 2 ? (100 + free).ToString() : "none")}");
            count.SetValue(authManager, free);
        }

        // Server, start of AuthenticationManager.FakeAuthentication: username = TakeFromLogin(username).
        public static string TakeFromLogin(string login)
        {
            int i = login?.IndexOf(Separator) ?? -1;
            if (i < 0) return login;
            string name = login.Substring(0, i);
            string[] parts = login.Substring(i + 1).Split(Separator);
            if (parts[0].Length > 0) ChoiceByName[name] = parts[0];
            if (parts.Length > 1 && parts[1].Length > 0) SkinByName[name] = parts[1];
            if (parts.Length > 2 && (parts[2] == "red" || parts[2] == "blue")) RedByName[name] = parts[2] == "red";
            if (parts.Length > 3 && parts[3].Length > 0) Emotes.Wish(name, parts[3]);
            Log.Info($"player {name} wants car '{parts[0]}' skin '{(parts.Length > 1 ? parts[1] : "")}' team '{(parts.Length > 2 ? parts[2] : "")}'");
            return name;
        }

        // Server, start of AuthenticationManager.FakeRequest(username, ...): a player who asked for a team (the launcher's
        // lobby) gets it. FakeRequest puts the player on Red when _nextPlayerOnRedTeam is set (and the config doesn't force
        // everyone on Blue), then flips that flag; without a wish the game keeps alternating.
        public static void ChooseTeam(object authManager, string username)
        {
            bool red;
            if (username == null || !RedByName.TryGetValue(username, out red)) return;
            authManager.GetType().GetField("_nextPlayerOnRedTeam", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(authManager, red);
        }

        /// <summary>Skin the player asked for at login (a number, a name or "random"), or null.</summary>
        public static string SkinWish(PlayerData player)
        {
            string skin;
            return player != null && !player.IsBot && SkinByName.TryGetValue(player.Name, out skin) ? skin : null;
        }

        // Server, SkipSwordfishServerExecuteCharacterSelection.GetCharacterId: replaces configLoader.GetIntValue(inst).
        public static int CharacterIndex(IConfigLoader config, ConfigInstance inst, PlayerData player, object selection)
        {
            int fallback = config.GetIntValue(inst);
            var characters = ((CollectionScriptableObject)selection.GetType()
                .GetField("_collectionScriptableObject", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(selection)).AllCharactersByCharacterId;
            if (!_listed)
            {
                _listed = true;
                Log.Info("cars: " + string.Join(", ", characters.OrderBy(c => c.Key).Select(c => c.Key + "=" + Names(c.Value)).ToArray()));
                DumpSkins(characters);
            }
            if (player.IsBot) return BotCar(player, characters, fallback);
            Log.Info($"player {player.Name} is {player.Team} #{player.TeamSlot + 1}");
            Emotes.ApplyToPlayer(player);
            string want;
            if (!ChoiceByName.TryGetValue(player.Name, out want)) return fallback;
            int id = Resolve(want, characters);
            if (id >= 0) return id;
            Log.Error($"unknown car '{want}' for {player.Name}; using slot default {fallback}");
            return fallback;
        }

        // The host's list for the bot's team (--hmmrevive-bot-cars-red/blue), in slot order: "6,wildfire,random". A single
        // "random" makes every bot of the team random; bots past the end of the list keep the game's slot default.
        private static int BotCar(PlayerData bot, Dictionary<int, IItemType> characters, int fallback)
        {
            string[] list = bot.Team == TeamKind.Red ? Entry.RedBotCars : bot.Team == TeamKind.Blue ? Entry.BluBotCars : null;
            if (list == null || list.Length == 0) return fallback;
            List<PlayerData> mates = GameHubBehaviour.Hub.Players.Bots.Where(b => b.Team == bot.Team).OrderBy(b => b.TeamSlot).ToList();
            int index = mates.IndexOf(bot);
            string want = list.Length == 1 && IsRandom(list[0]) ? list[0] : index >= 0 && index < list.Length ? list[index] : null;
            if (want == null || want.Equals("default", StringComparison.OrdinalIgnoreCase)) return fallback;
            int id = IsRandom(want) ? RandomBotCar(bot, characters) : Resolve(want, characters);
            if (id < 0)
            {
                Log.Error($"unknown bot car '{want}' for {bot.Name}; using slot default {fallback}");
                return fallback;
            }
            BotPicks[bot] = id;
            Log.Info($"bot {bot.Name} ({bot.Team} #{index + 1}) gets car {id}");
            return id;
        }

        // Random bot cars differ from each other within a team where possible.
        private static readonly Dictionary<PlayerData, int> BotPicks = new Dictionary<PlayerData, int>();

        private static int RandomBotCar(PlayerData bot, Dictionary<int, IItemType> characters)
        {
            var taken = new HashSet<int>(GameHubBehaviour.Hub.Players.Bots
                .Where(b => b.Team == bot.Team && b != bot && BotPicks.ContainsKey(b)).Select(b => BotPicks[b]));
            int[] free = characters.Keys.Where(k => !taken.Contains(k)).ToArray();
            if (free.Length == 0) free = characters.Keys.ToArray();
            return free[Rng.Next(free.Length)];
        }

        public static bool IsRandom(string s) => s != null && (s.Equals("random", StringComparison.OrdinalIgnoreCase) || s == "?" || s.Equals("r", StringComparison.OrdinalIgnoreCase));

        // Server, SkipSwordfishServerExecuteCharacterSelection.GetSkinId: replaces configLoader.GetValue(inst). The game
        // then uses the car's skin whose item name equals the returned string (default skin when none does).
        public static string SkinItemName(IConfigLoader config, ConfigInstance inst, PlayerData player, Guid characterId)
        {
            string fallback = config.GetValue(inst);
            string want = SkinWish(player);
            if (want == null) return fallback;
            IItemType skin = ResolveSkin(want, characterId);
            if (skin == null)
            {
                Log.Error($"unknown skin '{want}' for {player.Name}; using the default skin");
                return fallback;
            }
            Log.Info($"player {player.Name} gets skin {skin.Name} ({SkinName(skin)})");
            return skin.Name;
        }

        // ---------------------------------------------------------------- skins

        private static HashSet<string> _shippedAssets;

        // Skins of a car that ship a model, default first, then in the game's own order. "/skins", the launcher's skins.txt
        // and a numeric --hmmrevive-skin use the position in this list (0 = default).
        public static List<IItemType> SkinsOf(Guid characterId)
        {
            var collection = GameHubBehaviour.Hub.InventoryColletion;
            IItemType def = collection.GetDefaultSkin(characterId);
            var list = new List<IItemType>();
            if (def != null) list.Add(def);
            List<Guid> ids;
            if (!collection.CharacterToSkinGuids.TryGetValue(characterId, out ids)) return list;
            var others = new List<IItemType>();
            foreach (Guid id in ids)
            {
                IItemType item;
                if (!collection.TryGet(id, out item) || item == def || !Shipped(item)) continue;
                if (item.Name.IndexOf("Tutorial", StringComparison.OrdinalIgnoreCase) >= 0) continue; // copy of the default
                others.Add(item);
            }
            list.AddRange(others.OrderBy(s => Prefab(s).Index).ThenBy(s => s.Name, StringComparer.Ordinal));
            return list;
        }

        private static SkinPrefabItemTypeComponent Prefab(IItemType skin)
        {
            try { return skin.GetComponent<SkinPrefabItemTypeComponent>(); }
            catch { return null; }
        }

        // The skin's model is in the shipped content index (some skins lost their bundle when the game closed).
        private static bool Shipped(IItemType skin)
        {
            var prefab = Prefab(skin);
            if (prefab == null || string.IsNullOrEmpty(prefab.SkinPrefabName)) return false;
            if (_shippedAssets == null)
                _shippedAssets = new HashSet<string>(Loading.Content.content.Select(c => c.AssetName.ToLowerInvariant()));
            return _shippedAssets.Contains(prefab.SkinPrefabName.ToLowerInvariant());
        }

        public static string SkinName(IItemType skin)
        {
            string name = null;
            try { name = Language.Get(Prefab(skin).CardSkinDraft, TranslationContext.Items); }
            catch { }
            return string.IsNullOrEmpty(name) || name.StartsWith("#") ? skin.Name : name;
        }

        // A number from the car's skin list, a skin name (exact, then substring, ignoring case and spaces) or "random"
        // (any skin but the default). null when nothing matches.
        public static IItemType ResolveSkin(string want, Guid characterId)
        {
            List<IItemType> skins = SkinsOf(characterId);
            if (skins.Count == 0) return null;
            if (IsRandom(want)) return skins.Count > 1 ? skins[1 + Rng.Next(skins.Count - 1)] : skins[0];
            int n;
            if (int.TryParse(want, out n)) return n >= 0 && n < skins.Count ? skins[n] : null;
            string key = Normalize(want);
            if (key.Length == 0) return null;
            return skins.FirstOrDefault(s => Normalize(SkinName(s)) == key || Normalize(s.Name) == key)
                ?? skins.FirstOrDefault(s => Normalize(SkinName(s)).Contains(key));
        }

        // Log every car's skins and write them to hmmrevive-skins.txt next to HMM.exe ("carId<TAB>car<TAB>number<TAB>skin"), the
        // source of the launcher's skins.txt.
        private static void DumpSkins(Dictionary<int, IItemType> characters)
        {
            try
            {
                var lines = new List<string>();
                foreach (var c in characters.OrderBy(c => c.Key))
                {
                    List<IItemType> skins = SkinsOf(c.Value.Id);
                    int missing = GameHubBehaviour.Hub.InventoryColletion.CharacterToSkinGuids.TryGetValue(c.Value.Id, out var all) ? all.Count(g => !skins.Any(s => s.Id == g)) : 0;
                    Log.Info($"skins {c.Key} {DisplayName(c.Value)}: " + string.Join(", ", skins.Select((s, i) =>
                        $"{i}={SkinName(s)} [{s.Name} {Prefab(s)?.SkinPrefabName} {Prefab(s)?.Tier}]").ToArray()) + (missing > 0 ? $" (+{missing} without a model)" : ""));
                    for (int i = 0; i < skins.Count; i++) lines.Add($"{c.Key}\t{DisplayName(c.Value)}\t{i}\t{SkinName(skins[i]).Replace('’', '\'')}"); // ASCII for the Windows console
                }
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "hmmrevive-skins.txt"),
                    string.Join("\r\n", lines.ToArray()) + "\r\n");
            }
            catch (Exception e)
            {
                Log.Error("skin list failed: " + e);
            }
        }

        // ---------------------------------------------------------------- cars

        // Character id for a number, codename (HotRod) or display name (Wildfire): exact, then substring, ignoring case
        // and spaces. -1 when nothing matches.
        public static int Resolve(string want, Dictionary<int, IItemType> characters)
        {
            int id;
            if (int.TryParse(want, out id)) return characters.ContainsKey(id) ? id : -1;
            string key = Normalize(want);
            if (key.Length == 0) return -1;
            var match = characters.FirstOrDefault(c => Normalize(Names(c.Value)).Split('/').Contains(key));
            if (match.Value == null) match = characters.FirstOrDefault(c => Normalize(Names(c.Value)).Contains(key));
            return match.Value != null ? match.Key : -1;
        }

        // Display name when translations are loaded, else the codename.
        public static string DisplayName(IItemType item)
        {
            string names = Names(item);
            int slash = names.IndexOf('/');
            return slash < 0 ? names : names.Substring(slash + 1);
        }

        // "HotRod/Wildfire": codename, plus the localized display name when translations are loaded.
        public static string Names(IItemType item)
        {
            string display = null;
            try { display = item.GetComponent<CharacterItemTypeComponent>().GetCharacterLocalizedName(); }
            catch { }
            return string.IsNullOrEmpty(display) || display == item.Name || display.StartsWith("#") ? item.Name : item.Name + "/" + display;
        }

        private static string Normalize(string s) =>
            s.Replace(" ", "").Replace("_", "").Replace("'", "").Replace("’", "").Replace(".", "").ToLowerInvariant();
    }
}
