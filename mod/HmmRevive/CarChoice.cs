using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Assets.ClientApiObjects;
using HeavyMetalMachines.Configuring.Instances;
using HeavyMetalMachines.Match;
using Pocketverse;

namespace HmmRevive
{
    /// <summary>
    /// Pick your car at launch (--hmmrevive-car=NAME|ID) instead of in a pick screen. SkipSwordfish mode assigns cars
    /// from [Debug] Team{1,2}Character{1..4} per slot, so the client appends "#car" to the login name and the server
    /// strips it off and uses it for that player.
    /// </summary>
    public static class CarChoice
    {
        private const char Separator = '#';
        private static readonly Dictionary<string, string> ChoiceByName = new Dictionary<string, string>();
        private static bool _listed;

        // Client, AuthenticationSerializer.SerializeAuthenticationRequest: transforms the login name that is written.
        public static string LoginName(string name)
        {
            return Entry.Car == null ? name : name + Separator + Entry.Car;
        }

        // Server, start of AuthenticationManager.FakeAuthentication: username = TakeFromLogin(username).
        public static string TakeFromLogin(string login)
        {
            int i = login?.LastIndexOf(Separator) ?? -1;
            if (i < 0) return login;
            string name = login.Substring(0, i), car = login.Substring(i + 1);
            ChoiceByName[name] = car;
            Log.Info($"player {name} wants car '{car}'");
            return name;
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
            }
            string want;
            if (player.IsBot || !ChoiceByName.TryGetValue(player.Name, out want)) return fallback;
            int id = Resolve(want, characters);
            if (id >= 0) return id;
            Log.Error($"unknown car '{want}' for {player.Name}; using slot default {fallback}");
            return fallback;
        }

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
            try { display = item.GetComponent<Assets.ClientApiObjects.Components.CharacterItemTypeComponent>().GetCharacterLocalizedName(); }
            catch { }
            return string.IsNullOrEmpty(display) || display == item.Name || display.StartsWith("#") ? item.Name : item.Name + "/" + display;
        }

        private static string Normalize(string s) => s.Replace(" ", "").Replace("_", "").ToLowerInvariant();
    }
}
