using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Assets.ClientApiObjects;
using Assets.ClientApiObjects.Components;
using HeavyMetalMachines;
using HeavyMetalMachines.AI;
using HeavyMetalMachines.Audio;
using HeavyMetalMachines.BotAI;
using HeavyMetalMachines.Car;
using HeavyMetalMachines.Characters;
using HeavyMetalMachines.Combat;
using HeavyMetalMachines.Combat.Gadget;
using HeavyMetalMachines.Combat.GadgetScript;
using HeavyMetalMachines.DataTransferObjects.Battlepass;
using HeavyMetalMachines.Frontend;
using HeavyMetalMachines.HMMChat;
using HeavyMetalMachines.Infra.Context;
using HeavyMetalMachines.Match;
using HeavyMetalMachines.Pipeline;
using HeavyMetalMachines.Playback;
using HeavyMetalMachines.Render;
using HeavyMetalMachines.VFX;
using Hoplon.DependencyInjection;
using Hoplon.Unity.Loading;
using Pocketverse;
using SharedUtils.Loading;
using UnityEngine;
using Zenject;
using CharacterInfo = HeavyMetalMachines.Characters.CharacterInfo;
using Object = UnityEngine.Object;

namespace HmmRevive
{
    /// <summary>
    /// Swap your car mid-match: type "/car wildfire" (or a number from "/cars") in the match chat. The server rebuilds
    /// the car in place while it is dead (or during the between-rounds countdown) and tells every client to do the same.
    ///
    /// The car keeps its object (id, CombatObject, SpawnController, stats...) so the HUD, bots and scoreboard keep valid
    /// references; only what PlayerCarFactory.CreateCar derives from the CharacterInfo is replaced: gadgets, model,
    /// colliders, handling, HP and bot goals. Every car is preloaded during the loading screen (CarPreCache hook).
    /// "/skin 3" (or a name, or "random"; "/skins" lists them) changes the skin the same way, and a player's skin wish (from
    /// the launcher or /skin) carries over to the cars they swap to. Skin models load on demand; a swap waits until the
    /// server has the model, and each client applies it once it has loaded it too.
    /// Server and clients talk through the chat RPCs: commands are caught in ChatService.ReceiveMessage on the server,
    /// and replies/swaps are chat messages starting with <see cref="Marker"/> that ChatService.ClientReceiveMessage
    /// swallows on the clients.
    /// </summary>
    public class CarSwap : MonoBehaviour, IDynamicAssetListener<Object>
    {
        private const string Marker = "@@hmmrevive:";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static CarSwap _instance;

        private struct Want
        {
            public int Car;   // character id
            public Guid Skin; // skin item id, Guid.Empty = the car's default skin

            public Want(int car, Guid skin) { Car = car; Skin = skin; }
        }

        // Server: car object id -> wanted car and skin, applied at the next safe moment.
        private static readonly Dictionary<int, Want> Pending = new Dictionary<int, Want>();
        // Client: swaps received from the server (car object id -> car and skin), applied from Update (outside the network callback).
        private static readonly Queue<KeyValuePair<int, Want>> ClientQueue = new Queue<KeyValuePair<int, Want>>();
        // Server: skin wishes typed with /skin this match, by player name (they replace the launcher's --hmmrevive-skin).
        private static readonly Dictionary<string, string> SkinWishes = new Dictionary<string, string>();
        // Skin models being loaded mid-match (asset names).
        private static readonly HashSet<string> SkinLoads = new HashSet<string>();
        // Chaos test mode (--hmmrevive-chaos): the unspawn time we already rolled a random car for, per car.
        private static readonly Dictionary<int, int> ChaosSeen = new Dictionary<int, int>();
        private static BombScoreboardState _lastPhase = (BombScoreboardState)(-1);
        private static readonly System.Random Rng = new System.Random();

        // Preloading of every car during the loading screen.
        private static Future _preload;
        private static readonly HashSet<string> PreloadWaiting = new HashSet<string>();
        private static float _preloadDeadline;

        public static void Attach(HMMHub hub)
        {
            _instance = hub.GetComponent<CarSwap>() ?? hub.gameObject.AddComponent<CarSwap>();
        }

        // ---------------------------------------------------------------- preload

        // Hooks.StateEnabled: a new loading screen starts a new preload.
        public static void OnLoadingStarted()
        {
            _preload = null;
            PreloadWaiting.Clear();
            Pending.Clear();
            ChaosSeen.Clear();
            ClientQueue.Clear();
            SkinWishes.Clear();
            _lastPhase = (BombScoreboardState)(-1);
        }

        // Replaces PlayerCarFactory.CarPreCache inside LoadingState.LoadAssetsAsync (one call per player, each awaited
        // before the next and before the loading token runs). The first call also starts loading every other car, and
        // its future only completes when those are in too, so their gadget assets make it into the loading token.
        public static Future CarPreCache(Guid charItemTypeId, Guid skin, bool isCurrentPlayer)
        {
            Future original = PlayerCarFactory.CarPreCache(charItemTypeId, skin, isCurrentPlayer);
            try
            {
                if (_preload == null) StartPreload();
                if (!_preload.IsDone) original.DependsOn(_preload);
            }
            catch (Exception e)
            {
                Log.Error("car preload failed to start: " + e);
            }
            return original;
        }

        private static void StartPreload()
        {
            _preload = new Future();
            HMMHub hub = GameHubBehaviour.Hub;
            var known = CharacterInfos();
            foreach (var c in hub.InventoryColletion.AllCharactersByCharacterId.OrderBy(c => c.Key))
            {
                if (known.ContainsKey(c.Key)) continue;
                try
                {
                    string infoName = c.Value.GetComponent<CharacterItemTypeComponent>().AssetPrefix + "_MainAttributes";
                    hub.Resources.PreCachePrefab(SkinAssetName(c.Value.Id), 1);
                    if (Loading.GenericAssetManager.GetAssetAsync(infoName, _instance)) PreloadWaiting.Add(infoName);
                    else Log.Error($"car preload: no asset {infoName}");
                }
                catch (Exception e)
                {
                    Log.Error($"car preload: {c.Key} failed: {e.Message}");
                }
            }
            _preloadDeadline = Time.unscaledTime + 60f;
            Log.Info($"car preload: loading {PreloadWaiting.Count} extra cars");
            if (PreloadWaiting.Count == 0) _preload.Result = true;
        }

        public void OnAssetLoaded(string name, Object asset)
        {
            try
            {
                var info = asset as CharacterInfo;
                if (info != null)
                {
                    var factory = Factory();
                    factory.GetType().GetMethod("PreCacheCharacterInfoGadgets", Any).Invoke(factory, new object[] { info });
                    var infos = CharacterInfos();
                    if (!infos.ContainsKey(info.CharacterId)) infos[info.CharacterId] = info;
                }
                else
                {
                    Log.Error($"car preload: {name} is not a CharacterInfo");
                }
            }
            catch (Exception e)
            {
                Log.Error($"car preload: {name} failed: {e}");
            }
            PreloadWaiting.Remove(name);
            if (PreloadWaiting.Count == 0 && _preload != null && !_preload.IsDone)
            {
                _preload.Result = true;
                Log.Info("car preload: done, swappable cars: " + CharacterInfos().Count);
            }
        }

        private static Dictionary<int, CharacterInfo> CharacterInfos() =>
            (Dictionary<int, CharacterInfo>)typeof(PlayerCarFactory).GetField("_characterInfos", Any).GetValue(null);

        private static PlayerCarFactory Factory() => (PlayerCarFactory)typeof(PlayerCarFactory).GetField("_instance", Any).GetValue(null);

        private static string SkinAssetName(Guid charItemTypeId, Guid skin = default(Guid)) => GameHubBehaviour.Hub.InventoryColletion
            .GetSkinItemTypeScriptableObjectByGuid(charItemTypeId, skin).GetComponent<SkinPrefabItemTypeComponent>().SkinPrefabName;

        // True when the skin model is in memory; otherwise starts loading it (once) and returns false.
        private static bool SkinReady(string asset)
        {
            Content content = Loading.Content.GetAsset(asset);
            if (content == null) return true; // not shipped: the rebuild will fail and log it
            if (content.HasAsset) return true;
            if (SkinLoads.Add(asset))
            {
                var token = new LoadingToken(typeof(CarSwap));
                token.AddLoadable(Loading.GetResourceLoadable(content));
                Loading.Engine.LoadToken(token, result =>
                {
                    SkinLoads.Remove(asset);
                    Log.Info($"skin model {asset} loaded: {result}");
                });
                Log.Info($"loading skin model {asset}");
            }
            return false;
        }

        // ---------------------------------------------------------------- chat

        // Server, start of ChatService.ReceiveMessage(group, msg): true = handled, don't broadcast it.
        public static bool ServerChat(object chatService, string msg)
        {
            if (!Entry.ServerMode || msg == null) return false;
            string[] words = msg.Trim().Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return false;
            string cmd = words[0].ToLowerInvariant();
            if (cmd != "/car" && cmd != "/cars" && cmd != "/skin" && cmd != "/skins") return false;
            try
            {
                var chat = (ChatService)chatService;
                PlayerData player = GameHubBehaviour.Hub.Players.GetPlayerByAddress(chat.Sender);
                if (player == null || player.IsNarrator) return true;
                string arg = words.Length > 1 ? words[1].Trim() : "";
                if (cmd == "/skins" || (cmd == "/skin" && arg.Length == 0)) ListSkins(chat, player);
                else if (cmd == "/skin") RequestSkin(chat, player, arg);
                else if (cmd == "/cars" || arg.Length == 0) ListCars(chat, player);
                else RequestCar(chat, player, arg);
            }
            catch (Exception e)
            {
                Log.Error("car command failed: " + e);
            }
            return true;
        }

        private static void ListCars(ChatService chat, PlayerData player)
        {
            var items = GameHubBehaviour.Hub.InventoryColletion.AllCharactersByCharacterId;
            var infos = CharacterInfos();
            var cars = items.Where(c => infos.ContainsKey(c.Key)).OrderBy(c => CarChoice.DisplayName(c.Value))
                .Select(c => c.Key + " " + CarChoice.DisplayName(c.Value)).ToArray();
            Say(chat, player, $"You drive [ffcc00]{CarName(player.CharacterId)}[-]. Type /car NAME or /car NUMBER (or /car random) to swap when you next die or between rounds:");
            for (int i = 0; i < cars.Length; i += 6)
                Say(chat, player, string.Join(", ", cars.Skip(i).Take(6).ToArray()));
            Say(chat, player, "Skins: /skins lists your car's skins, /skin NUMBER (or /skin random) changes it.");
        }

        // The car the player will drive next: a pending swap's, else the current one.
        private static int NextCar(PlayerData player)
        {
            Want want;
            return Pending.TryGetValue(player.PlayerCarId, out want) ? want.Car : player.CharacterId;
        }

        private static void ListSkins(ChatService chat, PlayerData player)
        {
            int car = NextCar(player);
            List<IItemType> skins = CarChoice.SkinsOf(GameHubBehaviour.Hub.InventoryColletion.GetCharacterGuidId(car));
            Guid current = car == player.CharacterId ? SkinOrDefault(car, player.Customizations.GetGuidBySlot(PlayerCustomizationSlot.Skin)) : Guid.Empty;
            var names = skins.Select((s, i) =>
            {
                bool worn = SkinOrDefault(car, s.Id) == current;
                return (worn ? "[ffcc00]" : "") + i + " " + CarChoice.SkinName(s) + (worn ? "[-]" : "");
            }).ToArray();
            Say(chat, player, $"Skins for {CarName(car)} (type /skin NUMBER, /skin random, or /skin 0 for the original):");
            for (int i = 0; i < names.Length; i += 4)
                Say(chat, player, string.Join(", ", names.Skip(i).Take(4).ToArray()));
        }

        private static void RequestSkin(ChatService chat, PlayerData player, string wish)
        {
            int car = NextCar(player);
            IItemType skin = CarChoice.ResolveSkin(wish, GameHubBehaviour.Hub.InventoryColletion.GetCharacterGuidId(car));
            if (skin == null)
            {
                Say(chat, player, $"No skin of {CarName(car)} matches '{wish}'. Type /skins for the list.");
                return;
            }
            SkinWishes[player.Name] = wish;
            Guid id = SkinOrDefault(car, skin.Id);
            if (car == player.CharacterId && id == SkinOrDefault(car, player.Customizations.GetGuidBySlot(PlayerCustomizationSlot.Skin)))
            {
                Pending.Remove(player.PlayerCarId);
                Say(chat, player, $"You already wear {CarChoice.SkinName(skin)}.");
                return;
            }
            Queue(player, new Want(car, id));
            Log.Info($"SKIN request {player.Name} {CarName(car)} -> {skin.Name}");
            Say(chat, player, $"Your {CarName(car)} gets the [ffcc00]{CarChoice.SkinName(skin)}[-] skin when you next die or between rounds.");
        }

        // Guid.Empty for the car's default skin (what the game itself uses for bots).
        private static Guid SkinOrDefault(int car, Guid skin)
        {
            IItemType def = GameHubBehaviour.Hub.InventoryColletion.GetDefaultSkin(GameHubBehaviour.Hub.InventoryColletion.GetCharacterGuidId(car));
            return def != null && def.Id == skin ? Guid.Empty : skin;
        }

        // The skin a player gets on a car they swap to: their /skin wish, else the launcher's, resolved for that car.
        private static Guid SkinFor(PlayerData player, int car)
        {
            if (player.IsBot) return Guid.Empty;
            string wish;
            if (!SkinWishes.TryGetValue(player.Name, out wish)) wish = CarChoice.SkinWish(player);
            if (wish == null) return Guid.Empty;
            IItemType skin = CarChoice.ResolveSkin(wish, GameHubBehaviour.Hub.InventoryColletion.GetCharacterGuidId(car));
            return skin == null ? Guid.Empty : SkinOrDefault(car, skin.Id);
        }

        // Queues a swap and starts loading its skin model on the server and every client.
        private static void Queue(PlayerData player, Want want)
        {
            Pending[player.PlayerCarId] = want;
            if (want.Skin == Guid.Empty) return;
            HMMHub hub = GameHubBehaviour.Hub;
            string asset = SkinAssetName(hub.InventoryColletion.GetCharacterGuidId(want.Car), want.Skin);
            if (!SkinReady(asset))
                hub.Chat.DispatchReliable(hub.AddressGroups.GetGroup(0)).ClientReceiveMessage(false, $"{Marker}load:{asset}", 0);
        }

        private static void RequestCar(ChatService chat, PlayerData player, string want)
        {
            var infos = CharacterInfos();
            var items = GameHubBehaviour.Hub.InventoryColletion.AllCharactersByCharacterId;
            var swappable = items.Where(c => infos.ContainsKey(c.Key)).ToDictionary(c => c.Key, c => c.Value);
            int id = want.Equals("random", StringComparison.OrdinalIgnoreCase) ? RandomCar(player.CharacterId) : CarChoice.Resolve(want, swappable);
            if (id < 0)
            {
                Say(chat, player, $"No car matches '{want}'. Type /cars for the list.");
                return;
            }
            if (id == player.CharacterId)
            {
                Pending.Remove(player.PlayerCarId);
                Say(chat, player, $"You already drive {CarName(id)}.");
                return;
            }
            Queue(player, new Want(id, SkinFor(player, id)));
            Log.Info($"SWAP request {player.Name} {CarName(player.CharacterId)} -> {CarName(id)}");
            Say(chat, player, $"You'll switch to [ffcc00]{CarName(id)}[-] when you next die or between rounds.");
        }

        private static int RandomCar(int except)
        {
            var ids = CharacterInfos().Keys.Where(k => k != except && GameHubBehaviour.Hub.InventoryColletion.AllCharactersByCharacterId.ContainsKey(k)).ToArray();
            return ids.Length == 0 ? -1 : ids[Rng.Next(ids.Length)];
        }

        private static string CarName(int charId)
        {
            IItemType item;
            return GameHubBehaviour.Hub.InventoryColletion.AllCharactersByCharacterId.TryGetValue(charId, out item) ? CarChoice.DisplayName(item) : "#" + charId;
        }

        private static void Say(ChatService chat, PlayerData to, string text) =>
            chat.DispatchReliable(to.PlayerAddress).ClientReceiveMessage(false, Marker + "say:" + text, 0);

        private static void SayAll(ChatService chat, string text) =>
            chat.DispatchReliable(GameHubBehaviour.Hub.AddressGroups.GetGroup(0)).ClientReceiveMessage(false, Marker + "say:" + text, 0);

        // Client, start of ChatService.ClientReceiveMessage(group, msg, address): true = ours, don't show it as chat.
        public static bool ClientChat(object chatService, string msg)
        {
            if (msg == null || !msg.StartsWith(Marker, StringComparison.Ordinal)) return false;
            try
            {
                string body = msg.Substring(Marker.Length);
                if (body.StartsWith("say:", StringComparison.Ordinal))
                {
                    Log.Info("chat> " + body.Substring(4));
                    ((ChatService)chatService).ClientReceiveLogMessage(body.Substring(4));
                }
                else if (body.StartsWith("swap:", StringComparison.Ordinal))
                {
                    // swap:<car object id>:<character id>[:<skin id>] (servers before skins sent no skin)
                    string[] p = body.Substring(5).Split(':');
                    Guid skin = p.Length > 2 ? new Guid(p[2]) : Guid.Empty;
                    ClientQueue.Enqueue(new KeyValuePair<int, Want>(int.Parse(p[0]), new Want(int.Parse(p[1]), skin)));
                }
                else if (body.StartsWith("load:", StringComparison.Ordinal))
                {
                    SkinReady(body.Substring(5)); // a skin swap is coming: start loading its model now
                }
            }
            catch (Exception e)
            {
                Log.Error($"bad car message '{msg}': {e}");
            }
            return true;
        }

        // ---------------------------------------------------------------- when to swap

        private void Update()
        {
            HMMHub hub = GameHubBehaviour.Hub;
            if (hub == null || hub.Players == null) return;
            if (_preload != null && !_preload.IsDone && Time.unscaledTime > _preloadDeadline)
            {
                Log.Error("car preload: timed out waiting for " + string.Join(", ", PreloadWaiting.ToArray()));
                PreloadWaiting.Clear();
                _preload.Result = false;
            }
            if (hub.State.Current == null || hub.State.Current.StateKind != GameState.GameStateKind.Game) return; // Game / ServerGame
            try
            {
                if (Entry.ServerMode) ServerUpdate(hub);
                else ClientUpdate(hub);
            }
            catch (Exception e)
            {
                Log.Error("car swap update failed: " + e);
                Pending.Clear();
                ClientQueue.Clear();
            }
        }

        private static void ServerUpdate(HMMHub hub)
        {
            BombScoreboardState phase = hub.BombManager.CurrentBombGameState;
            if (Entry.Chaos) Chaos(hub, phase);
            _lastPhase = phase;
            if (Pending.Count == 0) return;
            int now = hub.GameTime.GetPlaybackTime();
            foreach (var request in Pending.ToArray())
            {
                PlayerData player = hub.Players.PlayersAndBots.FirstOrDefault(p => p.PlayerCarId == request.Key);
                if (player == null)
                {
                    Pending.Remove(request.Key);
                    continue;
                }
                SpawnController spawn = player.CharacterInstance?.GetBitComponent<SpawnController>();
                if (spawn == null) continue; // asked during loading: the car isn't built yet
                // Dead: the car is hidden and has no gadgets running; leave a moment after the death so kill/explosion
                // effects finish, and don't start when the respawn is about to begin.
                bool dead = spawn.State == SpawnStateKind.Unspawned && now - spawn.UnspawnTime >= 500 && spawn.GetDeathTimeRemainingMillis() > 500;
                // Between rounds: every car sits locked at its start position until the countdown ends.
                bool betweenRounds = phase == BombScoreboardState.Shop && spawn.State == SpawnStateKind.Spawned;
                if (!dead && !betweenRounds) continue;
                Want want = request.Value;
                if (want.Skin != Guid.Empty && !SkinReady(SkinAssetName(hub.InventoryColletion.GetCharacterGuidId(want.Car), want.Skin))) continue;
                Pending.Remove(request.Key);
                int fromCar = player.CharacterId;
                string from = CarName(fromCar);
                if (!Rebuild(player, want.Car, want.Skin)) continue;
                if (fromCar != want.Car) ReorderGrid(hub, player.Team);
                hub.Chat.DispatchReliable(hub.AddressGroups.GetGroup(0)).ClientReceiveMessage(false, $"{Marker}swap:{request.Key}:{want.Car}:{want.Skin}", 0);
                if (player.IsBot) continue;
                if (fromCar != want.Car) SayAll(hub.Chat, $"{player.Name} swapped {from} for [ffcc00]{CarName(want.Car)}[-]");
                else Say(hub.Chat, player, $"Skin changed to [ffcc00]{SkinLabel(want.Skin)}[-].");
            }
        }

        // Test mode: every death rolls a random car, and so does every car when a between-rounds countdown starts.
        private static void Chaos(HMMHub hub, BombScoreboardState phase)
        {
            bool roundStart = phase == BombScoreboardState.Shop && _lastPhase != BombScoreboardState.Shop;
            // Check that the cars lined up on their role slots (ReorderGrid) when the countdown ends.
            if (phase != BombScoreboardState.Shop && _lastPhase == BombScoreboardState.Shop)
            {
                var level = Object.FindObjectOfType<LevelSpawn>();
                if (level != null)
                    Log.Info("CHAOS start line: " + string.Join(" ", hub.Players.PlayersAndBots.Where(p => p.CharacterInstance != null).OrderBy(p => p.Team).ThenBy(p => p.GridIndex)
                        .Select(p => $"{p.Team}{p.GridIndex}={p.Name}({p.GetCharacterRole()},off={Vector3.Distance(p.CharacterInstance.transform.position, level.GetStart(p).position):0.0})").ToArray()));
            }
            foreach (PlayerData p in hub.Players.PlayersAndBots)
            {
                SpawnController spawn = p.CharacterInstance?.GetBitComponent<SpawnController>();
                if (spawn == null) continue;
                int seen;
                bool newDeath = spawn.State == SpawnStateKind.Unspawned && (!ChaosSeen.TryGetValue(p.PlayerCarId, out seen) || seen != spawn.UnspawnTime);
                if (!newDeath && !roundStart) continue;
                if (newDeath) ChaosSeen[p.PlayerCarId] = spawn.UnspawnTime;
                int id = Entry.ChaosCars == null ? RandomCar(p.CharacterId) : ChaosCar(p.CharacterId);
                if (id >= 0 && !Pending.ContainsKey(p.PlayerCarId)) Queue(p, new Want(id, SkinFor(p, id)));
            }
        }

        private static int ChaosCar(int except)
        {
            int[] ids = Entry.ChaosCars.Where(k => k != except && CharacterInfos().ContainsKey(k)).ToArray();
            return ids.Length == 0 ? -1 : ids[Rng.Next(ids.Length)];
        }

        private static float _autoChatAt = -1f;

        private static void ClientUpdate(HMMHub hub)
        {
            if (Entry.AutoChat != null)
            {
                if (_autoChatAt < 0f) _autoChatAt = Time.unscaledTime + 3f;
                else if (Time.unscaledTime >= _autoChatAt)
                {
                    foreach (string line in Entry.AutoChat) hub.Chat.ClientSendMessage(false, line);
                    Log.Info("autochat sent: " + string.Join(" | ", Entry.AutoChat));
                    _autoChatAt = float.MaxValue;
                }
            }
            for (int n = ClientQueue.Count; n > 0; n--)
            {
                var swap = ClientQueue.Dequeue();
                PlayerData player = hub.Players.PlayersAndBots.FirstOrDefault(p => p.PlayerCarId == swap.Key);
                if (player == null)
                {
                    Log.Error($"car swap: no player with car {swap.Key}");
                    continue;
                }
                Want want = swap.Value;
                if (player.CharacterId == want.Car && player.Character != null && player.Character.CharacterId == want.Car
                    && player.Customizations.GetGuidBySlot(PlayerCustomizationSlot.Skin) == want.Skin) continue;
                // Wait for the skin model; requeued behind any later swap, which is fine as the server only sends a car's
                // next swap after its previous one (at least one death or round apart).
                if (want.Skin != Guid.Empty && !SkinReady(SkinAssetName(hub.InventoryColletion.GetCharacterGuidId(want.Car), want.Skin)))
                {
                    ClientQueue.Enqueue(swap);
                    continue;
                }
                if (!Rebuild(player, want.Car, want.Skin)) continue;
                RefreshIcons(hub, player);
                if (player.IsCurrentPlayer) RefreshOwnHud(hub, player);
            }
        }

        // ---------------------------------------------------------------- the rebuild

        private sealed class Slot
        {
            public readonly string Name;
            public readonly GadgetSlot Kind;
            public readonly FieldInfo CombatField, InfoField;

            public Slot(string name, GadgetSlot kind)
            {
                Name = name;
                Kind = kind;
                CombatField = typeof(CombatObject).GetField(name);
                InfoField = typeof(CharacterInfo).GetField(name); // null for TakeOffGadget (per arena)
            }
        }

        // PlayerCarFactory.CreateCar's creation order.
        private static readonly Slot[] Slots =
        {
            new Slot("CustomGadget0", GadgetSlot.CustomGadget0), new Slot("CustomGadget1", GadgetSlot.CustomGadget1),
            new Slot("CustomGadget2", GadgetSlot.CustomGadget2), new Slot("GenericGadget", GadgetSlot.GenericGadget),
            new Slot("BoostGadget", GadgetSlot.BoostGadget), new Slot("PassiveGadget", GadgetSlot.PassiveGadget),
            new Slot("TrailGadget", GadgetSlot.TrailGadget), new Slot("OutOfCombatGadget", GadgetSlot.OutOfCombatGadget),
            new Slot("DmgUpgrade", GadgetSlot.DmgUpgrade), new Slot("HPUpgrade", GadgetSlot.HPUpgrade),
            new Slot("EPUpgrade", GadgetSlot.EPUpgrade), new Slot("BombGadget", GadgetSlot.BombGadget),
            new Slot("RespawnGadget", GadgetSlot.RespawnGadget), new Slot("TakeOffGadget", GadgetSlot.TakeoffGadget),
            new Slot("KillGadget", GadgetSlot.KillGadget), new Slot("BombExplosionGadget", GadgetSlot.BombExplosionGadget),
            new Slot("SprayGadget", GadgetSlot.SprayGadget), new Slot("GridHighlightGadget", GadgetSlot.GridHighlightGadget),
        };

        private static GadgetInfo InfoFor(Slot slot, CharacterInfo info)
        {
            if (slot.InfoField != null) return (GadgetInfo)slot.InfoField.GetValue(info);
            int arena = GameHubBehaviour.Hub.Match.ArenaIndex;
            return info.TakeoffGadgets != null && arena >= 0 && arena < info.TakeoffGadgets.Length ? info.TakeoffGadgets[arena] : null;
        }

        // Gadget bodies that follow a dummy of the car (AttachToDummyBodyMovement, e.g. one Black Lotus leaves running after
        // death) read the old model's dummy every frame; once it is destroyed that throws until the body ends (~1 min).
        // Point them at the same dummy on the new model.
        private static readonly Type AttachToDummy = typeof(HeavyMetalMachines.Combat.GadgetScript.Body.GadgetBody).Assembly.GetType("HeavyMetalMachines.Combat.GadgetScript.Body.AttachToDummyBodyMovement");

        private static void RetargetDummyBodies(CombatObject combat)
        {
            if (AttachToDummy == null || combat.Dummy == null) return;
            FieldInfo target = AttachToDummy.GetField("_dummyTransform", Any), owner = AttachToDummy.GetField("_combatObject", Any);
            FieldInfo kind = AttachToDummy.GetField("_dummyKind", Any), custom = AttachToDummy.GetField("_customDummyName", Any);
            foreach (Object body in Object.FindObjectsOfType(AttachToDummy))
            {
                if (!ReferenceEquals(owner.GetValue(body), combat)) continue;
                var dummy = target.GetValue(body) as Transform;
                if (dummy != null || ReferenceEquals(dummy, null)) continue; // still alive, or never initialized
                target.SetValue(body, combat.Dummy.GetDummy((CDummy.DummyKind)kind.GetValue(body), (string)custom.GetValue(body)));
                Log.Info($"car swap: gadget body {body.name} follows the new model");
            }
        }

        // Overlay effects (SurfaceEffectVFX, e.g. Zephyr's, still running on a dead car) keep the renderers of the model
        // they were started on and draw them every LateUpdate; after the model is destroyed that throws every frame
        // until the effect ends. Drop the destroyed renderers from every running overlay.
        private static void PruneSurfaceEffects()
        {
            FieldInfo holdersField = typeof(SurfaceEffectVFX).GetField("_rendererHolders", Any);
            foreach (SurfaceEffectVFX vfx in Object.FindObjectsOfType<SurfaceEffectVFX>())
            {
                var holders = holdersField.GetValue(vfx) as System.Collections.IList;
                if (holders == null) continue;
                for (int i = holders.Count - 1; i >= 0; i--)
                {
                    object h = holders[i];
                    var renderer = h.GetType().GetField("Renderer").GetValue(h) as Renderer;
                    if (renderer == null) holders.RemoveAt(i);
                }
            }
        }

        // Re-runs the character-dependent half of PlayerCarFactory.CreateCar on an existing car.
        private static string SkinLabel(Guid skin)
        {
            IItemType item;
            if (skin == Guid.Empty || !GameHubBehaviour.Hub.InventoryColletion.TryGet(skin, out item)) return "Original";
            return CarChoice.SkinName(item);
        }

        private static bool Rebuild(PlayerData player, int charId, Guid skinId)
        {
            var started = DateTime.Now;
            HMMHub hub = GameHubBehaviour.Hub;
            bool server = hub.Net.IsServer(), client = hub.Net.IsClient();
            CharacterInfo info;
            IItemType item;
            if (!CharacterInfos().TryGetValue(charId, out info) || info == null || !hub.InventoryColletion.AllCharactersByCharacterId.TryGetValue(charId, out item))
            {
                Log.Error($"car swap: car {charId} is not loaded, {player.Name} keeps {player.Character?.name}");
                return false;
            }
            try
            {
                PlayerCarFactory factory = Factory();
                var container = Field<DiContainer>(factory, "_container");
                Identifiable car = player.CharacterInstance;
                var carHub = car.GetComponent<CarComponentHub>();
                var combat = car.GetBitComponent<CombatObject>();
                var movement = car.GetBitComponent<CarMovement>();
                var turret = car.GetBitComponent<TurretMovement>();
                var spawn = car.GetBitComponent<SpawnController>();
                var feedback = car.GetBitComponent<CombatFeedback>();
                CharacterInfo old = player.Character;

                player.SetCharacter(item.Id, hub.InventoryColletion);
                player.Character = info;
                player.Customizations.SetGuidAndSlot(PlayerCustomizationSlot.Skin, skinId); // Guid.Empty = the car's default skin

                RemoveGadgets(combat, server);
                ReplaceColliders(car, old, info);

                // Model: swap the "Res[...]" child that holds the skin, dummies, wheels and animations.
                Transform oldRender = spawn.Renderer;
                bool wasActive = oldRender == null || oldRender.gameObject.activeSelf;
                bool wasHidden = oldRender != null && oldRender.localPosition.y > 5000f; // client-side unspawn (y += 10000)
                if (carHub.carGenerator != null && car.IsOwner) feedback.OnCollisionEvent -= carHub.carGenerator.OnCarCollision;
                if (carHub.carAudioController != null) Call(carHub.carAudioController, "DeactivateListeners");
                if (carHub.VoiceOverController != null) Call(carHub.VoiceOverController, "DeactivateListeners");
                // CarSuspensionGroup only leaves the bomb phase event in OnDestroy on clients (servers never destroyed a
                // car mid-match); a stale one throws on every round change and cuts off the listeners after it.
                if (server && oldRender != null)
                {
                    MethodInfo onPhase = typeof(CarSuspensionGroup).GetMethod("OnPhaseChanged", Any);
                    foreach (CarSuspensionGroup g in oldRender.GetComponentsInChildren<CarSuspensionGroup>(true))
                        hub.BombManager.ListenToPhaseChange -= (Action<BombScoreboardState>)Delegate.CreateDelegate(typeof(Action<BombScoreboardState>), g, onPhase);
                }
                if (oldRender != null) Object.DestroyImmediate(oldRender.gameObject);
                if (client) PruneSurfaceEffects();

                string asset = SkinAssetName(item.Id, skinId);
                var prefab = (Transform)Loading.Content.GetAsset(asset).Asset;
                Transform render = container.InstantiatePrefab(prefab, car.transform).transform;
                CarSkin skin = render.GetComponent<CarSkin>();
                if (skin != null) skin.SetSkin(asset, hub.Net.IsTest() || (client && hub.Players.CurrentPlayerTeam == player.Team));
                render.localPosition = Vector3.zero;
                render.localRotation = Quaternion.identity;
                LayerManager.SetLayerRecursively(car, player.Team != TeamKind.Red ? LayerManager.Layer.PlayerBlu : LayerManager.Layer.PlayerRed);
                car.name = string.Format("[{1}]{0}", player.GetCharacter(), player.PlayerAddress);
                render.name = string.Format("Res[{1}]{0}", player.GetCharacterAssetPrefix(), player.PlayerAddress);
                spawn.Renderer = render;
                Field<Dictionary<Type, Component>>(car, "__components").Clear(); // GetBitComponentInChildren cache

                var generator = render.GetComponent<CarGenerator>();
                var dummy = render.GetComponentInChildren<CDummy>();
                carHub.carGenerator = generator;
                carHub.dummy = dummy;
                combat.Dummy = dummy;
                RetargetDummyBodies(combat);
                if (generator != null)
                {
                    generator.carComponentHub = carHub;
                    generator.suspensionGroup.CarMovement = movement;
                    generator.transform.localPosition = Vector3.zero;
                    generator.SetParentCarTemplate(carHub.transform);
                    if (client)
                    {
                        var movementFeedback = carHub.carMovementFeedback = generator.GetComponent<CarMovementFeedback>();
                        if (movementFeedback != null)
                        {
                            movementFeedback._carMovement = movement;
                            movementFeedback._carAudioController = car.GetBitComponent<CarAudioController>();
                        }
                        if (car.IsOwner) feedback.OnCollisionEvent += generator.OnCarCollision;
                        generator.carWheelsController.CarMovement = movement;
                    }
                }

                movement.Char = info;
                turret.TurretConfiguration = info.TurretMovementConfiguration;
                combat.Data.SetInfo(info.Combat);
                AddGadgets(factory, container, combat, info);
                // Rebuilt while dead: the new gadgets missed the unspawn the old ones got. Without it a toggle gadget
                // (GadgetSwitchUpdater, e.g. Full Metal Judge) thinks the car is alive and re-fires an effect that
                // PerkDestroyOnDeath kills at once, forever, inside one server frame.
                if (spawn.State == SpawnStateKind.Unspawned)
                {
                    var unspawn = new UnspawnEvent(car.transform.position, SpawnReason.Death, -1, car.ObjId);
                    foreach (Slot slot in Slots) ((GadgetBehaviour)slot.CombatField.GetValue(combat))?.OnObjectUnspawned(unspawn);
                }

                if (server)
                {
                    Call(movement, "Start"); // center of mass and mesh validator depth come from the car info
                    foreach (Animator a in render.GetComponentsInChildren<Animator>()) Object.Destroy(a); // as Identifiable.Start does
                    UpdateAI(factory, carHub, player);
                    if (combat.IsAlive()) combat.Data.HP = combat.Data.HPMax;
                }
                if (client)
                {
                    carHub.RenderTransform = render;
                    carHub.ArtReference = render.GetComponent<ArtReference>();
                    carHub.carAudioController = car.GetBitComponent<CarAudioController>();
                    carHub.carAudioController.Initialize(carHub);
                    carHub.VoiceOverController = car.GetBitComponent<VoiceOverController>();
                    carHub.VoiceOverController.Initialize(carHub);
                    if (generator != null) generator.CreateCarAnimation(combat);
                }

                // Keep the dead/alive look the old model had (SpawnController hides it by deactivating on the server and
                // by moving it 10000 up on clients).
                if (!wasActive) render.gameObject.SetActive(false);
                if (wasHidden)
                {
                    Vector3 p = render.localPosition;
                    p.y = 10000f;
                    render.localPosition = p;
                }
                Log.Info($"SWAP {player.Name} car={car.ObjId} {old?.name} -> {info.name} skin={asset} alive={combat.IsAlive()} state={spawn.State} in {(DateTime.Now - started).TotalMilliseconds:0}ms");
                return true;
            }
            catch (Exception e)
            {
                Log.Error($"car swap {player.Name} -> {info.name} failed: {e}");
                return false;
            }
        }

        private static void RemoveGadgets(CombatObject combat, bool server)
        {
            foreach (Slot slot in Slots)
            {
                var gadget = (GadgetBehaviour)slot.CombatField.GetValue(combat);
                if (gadget != null) Object.DestroyImmediate(gadget); // OnDestroy unhooks its combat listeners
                slot.CombatField.SetValue(combat, null);
            }
            Field<Dictionary<int, GadgetSlot>>(combat, "gadgetSlotDictionary").Clear();

            // Scripted gadgets (CharacterInfo.CustomGadgets); the character-independent EffectBehaviour context stays.
            var contexts = Field<Dictionary<int, IHMMGadgetContext>>(combat, "_customGadgets");
            IHMMGadgetContext effects = null;
            foreach (var entry in contexts)
            {
                if (entry.Key == (int)GadgetSlot.EffectBehaviourGadget)
                {
                    effects = entry.Value;
                    continue;
                }
                var gadget = entry.Value as BaseGadget;
                if (gadget == null) continue;
                if (server && gadget is CombatGadget) Call(gadget, "UnsubscribeEvents", ((CombatGadget)gadget).Events);
                gadget.CleanUp();
                var timer = Field<GadgetTimer>(gadget, "_timer");
                if (timer != null) Object.Destroy(timer.gameObject);
            }
            contexts.Clear();
            combat.CustomGadgets.Clear();
            if (effects != null) combat.AddGadget(GadgetSlot.EffectBehaviourGadget, effects);
        }

        private static void AddGadgets(PlayerCarFactory factory, DiContainer container, CombatObject combat, CharacterInfo info)
        {
            var created = new List<KeyValuePair<GadgetBehaviour, GadgetInfo>>();
            foreach (Slot slot in Slots)
            {
                GadgetInfo gadgetInfo = InfoFor(slot, info);
                if (gadgetInfo == null) continue;
                var gadget = (GadgetBehaviour)container.InstantiateComponent(gadgetInfo.GadgetType(), combat.Gadgets);
                gadget.Parent = combat.Id;
                gadget.Combat = combat;
                gadget.Slot = slot.Kind;
                slot.CombatField.SetValue(combat, gadget);
                created.Add(new KeyValuePair<GadgetBehaviour, GadgetInfo>(gadget, gadgetInfo));
            }

            // The EffectBehaviour context was created last originally; keep it last.
            IHMMGadgetContext effects = combat.GetGadgetContext((int)GadgetSlot.EffectBehaviourGadget);
            Field<Dictionary<int, IHMMGadgetContext>>(combat, "_customGadgets").Clear();
            combat.CustomGadgets.Clear();
            if (info.CustomGadgets != null)
            {
                HMMHub hub = GameHubBehaviour.Hub;
                var dispatcher = Field<IGadgetEventDispatcher>(factory, "_eventDispatcher");
                var playback = Field<IServerPlaybackDispatcher>(factory, "_server");
                var resolver = Field<IInjectionResolver>(factory, "_injectionResolver");
                foreach (CombatGadget g in info.CustomGadgets)
                {
                    if (combat.HasGadgetContext((int)g.Slot)) Log.Error($"car swap: slot {g.Slot} used twice in {info.name}");
                    else combat.AddGadget(g.Slot, (CombatGadget)g.CreateGadgetContext((int)g.Slot, combat, dispatcher, hub.GetContext(), playback, resolver));
                }
                foreach (CombatGadget g in info.CustomGadgets)
                    ((CombatGadget)combat.GetGadgetContext((int)g.Slot)).RouteParametersGadgets();
            }
            if (effects != null) combat.AddGadget(GadgetSlot.EffectBehaviourGadget, effects);

            foreach (var entry in created) entry.Key.SetInfo(entry.Value);
        }

        private static void ReplaceColliders(Identifiable car, CharacterInfo old, CharacterInfo info)
        {
            var box = car.GetComponent<BoxCollider>();
            box.size = info.Collider.Size;
            box.center = info.Collider.Center;
            if (old != null && old.ExtraColliders != null)
            {
                foreach (CarCollider c in old.ExtraColliders)
                {
                    Transform t = car.transform.Find(c.Name);
                    if (t != null && t.GetComponents<Component>().Length == 2 && t.GetComponent<BoxCollider>() != null) Object.DestroyImmediate(t.gameObject);
                }
            }
            if (info.ExtraColliders == null) return;
            foreach (CarCollider c in info.ExtraColliders)
            {
                var go = new GameObject(c.Name);
                go.transform.parent = car.transform;
                go.transform.localPosition = Vector3.zero;
                go.transform.rotation = car.transform.rotation;
                var extra = go.AddComponent<BoxCollider>();
                extra.center = c.Center;
                extra.size = c.Size;
                go.layer = car.gameObject.layer;
            }
        }

        // Bots (and humans' disconnect autopilot) pick goals per car and cache their gadgets once initialized.
        private static void UpdateAI(PlayerCarFactory factory, CarComponentHub carHub, PlayerData player)
        {
            var agent = carHub.AIAgent as HMMAgent;
            if (agent == null) return;
            object agentFactory = Field<object>(factory, "_agentFactory");
            var goals = (BotAIGoal)agentFactory.GetType().GetMethod("GetGoals", Any).Invoke(agentFactory, new object[] { player });
            agent.Goals = goals;
            agent.BotContext.BotParameters = goals;
            BotAIGoalManager manager = agent.GoalManager;
            manager.Goals = goals;
            if (Field<bool>(manager, "initialized"))
            {
                CombatObject combat = carHub.combatObject;
                MethodInfo make = typeof(BotAIGoalManager).GetMethod("CreateGadgetAIState", Any);
                Func<GadgetBehaviour, object, object, object> state = (g, use, s) => make.Invoke(manager, new[] { g, use, s });
                var gadgets = Field<System.Collections.IList>(manager, "_gadgets");
                gadgets.Clear();
                gadgets.Add(state(combat.CustomGadget2, goals.Gadget2, combat.GadgetStates.G2StateObject));
                gadgets.Add(state(combat.CustomGadget1, goals.Gadget1, combat.GadgetStates.G1StateObject));
                gadgets.Add(state(combat.CustomGadget0, goals.Gadget0, combat.GadgetStates.G0StateObject));
                gadgets.Add(state(combat.BoostGadget, goals.BoostGadget, combat.GadgetStates.GBoostStateObject));
                SetField(manager, "_bombGadget", state(combat.BombGadget, goals.BombGadget, combat.GadgetStates.BombStateObject));
                SetField(manager, "_myRole", player.GetCharacterRole());
            }
            if (agent.Controller != null) SetField(agent.Controller, "_maxDistanceSqr", 0f);
        }

        // Your own gadget bar and driver tips are built once, for the car you loaded with.
        private static void RefreshOwnHud(HMMHub hub, PlayerData player)
        {
            try
            {
                UIGadgetConstructor gadgets;
                if (UIGadgetConstructor.TryToGetUiGadgetConstructor(out gadgets))
                {
                    gadgets.CombatData = null;
                    gadgets.SetCombatDataAndPopulateUI(player.CharacterInstance.GetBitComponent<CombatData>());
                }
                hub.GuiScripts.DriverHelper.Setup(hub.InventoryColletion.AllItemTypes[player.Character.CharacterItemTypeGuid], hub.State.Current);
                // Weapon details window (ESC menu / help shortcut): GameGui gives it the car once, when the match starts.
                var gui = Object.FindObjectOfType<GameGui>();
                var help = gui != null ? Field<HeavyMetalMachines.CharacterHelp.Presenting.ICharacterHelpPresenter>(gui, "_characterHelpPresenter") : null;
                if (help != null)
                {
                    help.Set(player.CharacterItemType.Id);
                    Log.Info("weapon details window now shows " + CarName(player.CharacterId));
                }
            }
            catch (Exception e)
            {
                Log.Error("car swap: HUD refresh failed: " + e);
            }
        }

        // Start and respawn slots (LevelSpawn by PlayerData.GridIndex) are ordered by role once, at car selection
        // (LegacySetMatchPlayersPicks: Transporters, then Supports, then Interceptors, humans before bots within a role),
        // and each car's SpawnController keeps the slot it got when it was created. Redo both for the team after a swap
        // so the new car's role decides where it spawns. Server only: positions come from the server.
        private static void ReorderGrid(HMMHub hub, TeamKind team)
        {
            var level = Object.FindObjectOfType<LevelSpawn>();
            List<PlayerData> mates = hub.Players.PlayersAndBots.Where(p => p.Team == team && p.GridIndex >= 0)
                .OrderByDescending(p => RoleOrder(p.GetCharacterRole())).ThenBy(p => p.IsBot ? 1 : 0).ThenBy(p => p.GridIndex).ToList();
            var order = new List<string>();
            for (int i = 0; i < mates.Count; i++)
            {
                PlayerData p = mates[i];
                order.Add($"{i}={p.Name}({p.GetCharacterRole()})");
                if (p.GridIndex == i) continue;
                p.GridIndex = i;
                SpawnController spawn = p.CharacterInstance?.GetBitComponent<SpawnController>();
                if (spawn == null || level == null) continue;
                spawn.StartPosition = level.GetStart(p);
                spawn.SpawnPosition = level.GetSpawn(p);
                // During the between-rounds countdown the cars already stand on the start line: move to the new slot the
                // way BombScoreController.RepositionPlayers does at the round start.
                if (hub.BombManager.CurrentBombGameState == BombScoreboardState.Shop && spawn.State == SpawnStateKind.Spawned)
                    p.CharacterInstance.GetBitComponent<CombatObject>().Movement.ForcePositionAndRotation(spawn.StartPosition.position, spawn.StartPosition.rotation);
            }
            Log.Info($"GRID {team}: " + string.Join(" ", order.ToArray()));
        }

        private static int RoleOrder(DriverRoleKind role) =>
            role == DriverRoleKind.Carrier ? 100 : role == DriverRoleKind.Support ? 10 : role == DriverRoleKind.Tackler ? 1 : 0;

        // Car portraits: top bar (HudPlayersObject), Tab scoreboard (HudTabPlayer) and minimap (HudMinimapPlayerObject).
        private static void RefreshIcons(HMMHub hub, PlayerData player)
        {
            try
            {
                CombatObject combat = player.CharacterInstance.GetBitComponent<CombatObject>();
                Guid car = player.Character.CharacterItemTypeGuid;
                string icon = HudUtils.GetPlayerIconName(hub, car, HudUtils.PlayerIconSize.Size64);
                int n = 0;
                foreach (var o in Resources.FindObjectsOfTypeAll<HudPlayersObject>())
                    if (o.gameObject.scene.IsValid() && Field<CombatObject>(o, "_combatObject") == combat) { o.Thumb.SpriteName = icon; n++; }
                foreach (var o in Resources.FindObjectsOfTypeAll<HudTabPlayer>())
                    if (o.gameObject.scene.IsValid() && o.CombatObject == combat)
                    {
                        o.CharacterTexture.SpriteName = icon;
                        o.CharacterLabel.text = player.GetCharacterLocalizedName();
                        n++;
                    }
                foreach (var o in Resources.FindObjectsOfTypeAll<HudMinimapPlayerObject>())
                    if (o.gameObject.scene.IsValid() && Field<int>(o, "_playerCarId") == player.PlayerCarId)
                    {
                        Field<HmmUiImage>(o, "_iconImage").TryToLoadAsset(HudUtils.GetPlayerPixelArtIconName(hub, car));
                        n++;
                    }
                if (n < 3) Log.Info($"car swap: refreshed {n}/3 portraits of {player.Name}");
            }
            catch (Exception e)
            {
                Log.Error("car swap: portrait refresh failed: " + e);
            }
        }

        // ---------------------------------------------------------------- reflection helpers

        private static FieldInfo FindField(Type t, string name)
        {
            for (; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, Any | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            throw new MissingFieldException(name);
        }

        private static T Field<T>(object o, string name) => (T)FindField(o.GetType(), name).GetValue(o);

        private static void SetField(object o, string name, object value) => FindField(o.GetType(), name).SetValue(o, value);

        private static object Call(object o, string name, params object[] args)
        {
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                MethodInfo m = t.GetMethod(name, Any | BindingFlags.DeclaredOnly, null, args.Select(a => a.GetType()).ToArray(), null)
                               ?? t.GetMethods(Any | BindingFlags.DeclaredOnly).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == args.Length);
                if (m != null) return m.Invoke(o, args);
            }
            return null; // not every class has it (e.g. DeactivateListeners)
        }
    }
}
