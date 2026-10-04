using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Assets.ClientApiObjects;
using Assets.ClientApiObjects.Components;
using HeavyMetalMachines;
using HeavyMetalMachines.Combat;
using HeavyMetalMachines.Combat.Gadget;
using HeavyMetalMachines.DataTransferObjects.Battlepass;
using HeavyMetalMachines.Items.DataTransferObjects;
using HeavyMetalMachines.Match;
using Hoplon.Unity.Loading;
using Pocketverse;
using UnityEngine;

namespace HmmRevive
{
    /// <summary>
    /// Emotes (the in-match emote wheel, held on the emote key). Every car still carries the four emote gadgets
    /// (EmoteGadget0..3), but which emote each one shows comes from the player's customization slots (Emote,
    /// ExtraEmote1..3), which Swordfish filled from the player's inventory. Offline they are empty, so the wheel was empty
    /// and sent nothing. Now: the client picks four (--hmmrevive-emotes=N,N,N,N, numbers in <see cref="List"/>; default the
    /// first four), sends them with the login like the skin, fills its own inventory slots (the wheel and the sending side
    /// read those), and the server fills the player's slots (every client reads those to show someone's emote).
    /// </summary>
    public static class Emotes
    {
        public static readonly PlayerCustomizationSlot[] Slots =
            { PlayerCustomizationSlot.Emote, PlayerCustomizationSlot.ExtraEmote1, PlayerCustomizationSlot.ExtraEmote2, PlayerCustomizationSlot.ExtraEmote3 };
        private static readonly Dictionary<string, string> WishByName = new Dictionary<string, string>();
        private static List<ItemTypeScriptableObject> _list;
        private static bool _probed, _clientApplied;
        private static float _autoEmoteAt, _nextUses;
        private static int _autoEmotes;
        private static string _lastUses = "";

        /// <summary>Emotes whose sprite sheet ships with the game, in a stable order (by item name). Numbers are positions.</summary>
        public static List<ItemTypeScriptableObject> List()
        {
            if (_list != null) return _list;
            var collection = GameHubBehaviour.Hub?.InventoryColletion;
            if (collection == null || collection.AllItemTypes.Count == 0) return new List<ItemTypeScriptableObject>();
            var shipped = new HashSet<string>(Loading.Content.content.Select(c => c.AssetName.ToLowerInvariant()));
            _list = collection.AllItemTypes.Values.Where(i =>
            {
                EmoteItemTypeComponent e = Component(i);
                return e != null && !string.IsNullOrEmpty(e.spriteSheetName) && shipped.Contains(e.spriteSheetName.ToLowerInvariant());
            }).OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
            Log.Info($"emotes: {_list.Count} shipped: " + string.Join(", ", _list.Select((e, n) => n + "=" + e.Name).ToArray()));
            Write(_list);
            return _list;
        }

        private static EmoteItemTypeComponent Component(ItemTypeScriptableObject item)
        {
            try { return item.GetComponent<EmoteItemTypeComponent>(); }
            catch { return null; }
        }

        // hmmrevive-emotes.txt next to HMM.exe ("number<TAB>item name"), like hmmrevive-skins.txt.
        private static void Write(List<ItemTypeScriptableObject> list)
        {
            try
            {
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "hmmrevive-emotes.txt"),
                    string.Join("\r\n", list.Select((e, n) => n + "\t" + e.Name).ToArray()) + "\r\n");
            }
            catch (Exception e) { Log.Error("emote list failed: " + e.Message); }
        }

        /// <summary>"3,7,0,12" → the four emotes (wrong or missing numbers fall back to the defaults 0..3).</summary>
        public static Guid[] Resolve(string wish)
        {
            List<ItemTypeScriptableObject> list = List();
            string[] parts = (wish ?? "").Split(new[] { ',', '.', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var picked = new Guid[Slots.Length];
            for (int i = 0; i < Slots.Length; i++)
            {
                int n;
                if (!(i < parts.Length && int.TryParse(parts[i], out n) && n >= 0 && n < list.Count)) n = i;
                picked[i] = n < list.Count ? list[n].Id : Guid.Empty;
            }
            return picked;
        }

        private static void Fill(CustomizationContent c, Guid[] emotes)
        {
            for (int i = 0; i < Slots.Length; i++) c.SetGuidAndSlot(Slots[i], emotes[i]);
        }

        // Client: the wish that goes into the login ("name#car#skin#team#emotes"), dots so it survives the login's commas.
        public static string LoginField => Entry.Emotes == null ? null : Entry.Emotes.Replace(',', '.');

        // Server: from the login (CarChoice.TakeFromLogin).
        public static void Wish(string name, string emotes) { WishByName[name] = emotes; }

        // Server, during character selection (CarChoice.CharacterIndex): the player's emote slots, sent to every client
        // with the player's data.
        public static void ApplyToPlayer(PlayerData player)
        {
            if (player == null || player.IsBot || player.IsNarrator) return;
            try
            {
                string wish;
                WishByName.TryGetValue(player.Name, out wish);
                Guid[] emotes = Resolve(wish);
                Fill(player.Customizations, emotes);
                Log.Info($"player {player.Name} emotes {wish ?? "default"} = {string.Join(", ", emotes.Select(Name).ToArray())}");
            }
            catch (Exception e) { Log.Error("emotes for " + player.Name + " failed: " + e); }
        }

        private static string Name(Guid id)
        {
            ItemTypeScriptableObject item;
            return id != Guid.Empty && GameHubBehaviour.Hub.InventoryColletion.AllItemTypes.TryGetValue(id, out item) ? item.Name : "-";
        }

        // Once a second (Watchdog): the client's own slots (read by the emote wheel), once the inventory exists; the server
        // logs the cars' emote gadgets once per match.
        public static void Tick(HMMHub hub)
        {
            try
            {
                if (!Entry.ServerMode && !Entry.Spectate && !_clientApplied)
                {
                    var inv = hub.User?.Inventory;
                    if (inv == null || hub.InventoryColletion == null || hub.InventoryColletion.AllItemTypes.Count == 0) return;
                    if (inv.Customizations == null) inv.Customizations = new CustomizationContent();
                    Guid[] emotes = Resolve(Entry.Emotes);
                    Fill(inv.Customizations, emotes);
                    _clientApplied = true;
                    Log.Info("my emotes: " + string.Join(", ", emotes.Select(Name).ToArray()));
                }
                if (Entry.AutoEmote && _clientApplied && Time.unscaledTime >= _autoEmoteAt && hub.Match?.State == MatchData.MatchState.MatchStarted)
                {
                    var me = hub.Players?.CurrentPlayerData;
                    var controller = me?.CharacterInstance?.GetBitComponent<PlayerController>();
                    if (controller != null)
                    {
                        if (_autoEmoteAt == 0f) _autoEmoteAt = Time.unscaledTime + 5f; // a few seconds into the match
                        else
                        {
                            controller.AddGadgetCommand(GadgetSlot.EmoteGadget0 + _autoEmotes % Slots.Length);
                            Log.Info("autoemote sent " + (GadgetSlot.EmoteGadget0 + _autoEmotes % Slots.Length));
                            _autoEmoteAt = ++_autoEmotes < 4 ? Time.unscaledTime + 4f : float.MaxValue;
                        }
                    }
                }
                if (Entry.ServerMode && _probed && Time.unscaledTime >= _nextUses)
                {
                    _nextUses = Time.unscaledTime + 5f;
                    string uses = string.Join(" ", hub.Players.Players.Where(p => p.CharacterInstance != null).Select(p =>
                    {
                        var stats = p.CharacterInstance.GetBitComponent<HeavyMetalMachines.Bank.PlayerStats>();
                        return p.Name + "=" + (stats == null ? "?" : string.Join("/", Slots.Select((s, i) => stats.GetGadgetUses(GadgetSlot.EmoteGadget0 + i).ToString()).ToArray()));
                    }).ToArray());
                    if (uses != _lastUses) Log.Info("emote uses " + (_lastUses = uses));
                }
                if (Entry.ServerMode && !_probed)
                {
                    var all = hub.Players?.PlayersAndBots;
                    if (all == null || all.Count == 0 || all.Any(p => p.CharacterInstance == null)) return;
                    _probed = true;
                    foreach (var p in all.Where(x => !x.IsBot))
                    {
                        var combat = p.CharacterInstance.GetBitComponent<CombatObject>();
                        Log.Info($"emote gadgets of {p.Name} ({p.Character?.name}): " + string.Join(", ", Slots.Select((s, i) =>
                            (GadgetSlot.EmoteGadget0 + i) + "=" + (combat.HasGadgetContext((int)(GadgetSlot.EmoteGadget0 + i)) ? Name(p.Customizations.GetGuidBySlot(s)) : "none")).ToArray()));
                    }
                }
            }
            catch (Exception e)
            {
                _clientApplied = _probed = true;
                Log.Error("emotes failed: " + e);
            }
        }
    }
}
