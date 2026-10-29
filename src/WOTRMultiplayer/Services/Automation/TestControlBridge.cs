using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Kingmaker;
using Kingmaker.Controllers.Combat;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using WOTRMultiplayer.Abstractions;
using WOTRMultiplayer.Abstractions.GameInteraction;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat.RealTime;

namespace WOTRMultiplayer.Services.Automation
{
    /// <summary>
    /// Local, file-backed control bridge used by the external MCP test server.
    /// It is compiled into all builds so its policy can be unit tested, but it
    /// can only start in a presentation-prototype build and an isolated profile.
    /// </summary>
    public sealed class TestControlBridge
    {
        public const string EnabledEnvironmentVariable = "WOTR_RTWP_AUTOMATION";
        public const string TokenEnvironmentVariable = "WOTR_RTWP_AUTOMATION_TOKEN";
        public const string BridgeFolderName = "McpBridge";

#if RTWP_HOST_PRESENTATION_PROTOTYPE
        private readonly ILogger<TestControlBridge> _logger;
        private readonly IMultiplayerActorAccessor _actors;
        private readonly IGameInteractionService _gameInteraction;
        private readonly WOTRMultiplayer.Abstractions.Unity.IMainThreadAccessor _mainThreadAccessor;
        private readonly object _pendingLock = new();
        private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _completed = new(StringComparer.OrdinalIgnoreCase);
        private string _profile;
        private string _root;
        private string _requests;
        private string _responses;
        private string _sessionId;
        private string _token;
        private GameObject _pumpObject;
        private bool _startupAdvanced;
#endif

        public bool Enabled { get; private set; }

        public TestControlBridge(
            ILogger<TestControlBridge> logger,
            IMultiplayerActorAccessor actors,
            IGameInteractionService gameInteraction,
            WOTRMultiplayer.Abstractions.Unity.IMainThreadAccessor mainThreadAccessor)
        {
#if RTWP_HOST_PRESENTATION_PROTOTYPE
            _logger = logger;
            _actors = actors;
            _gameInteraction = gameInteraction;
            _mainThreadAccessor = mainThreadAccessor;
#endif
        }

        public void Start(string isolatedProfile)
        {
#if !RTWP_HOST_PRESENTATION_PROTOTYPE
            return;
#else
            if (!string.Equals(Environment.GetEnvironmentVariable(EnabledEnvironmentVariable), "1",
                    StringComparison.Ordinal))
                return;

            var token = Environment.GetEnvironmentVariable(TokenEnvironmentVariable);
            if (!TestControlBridgePolicy.TryValidateActivation(isolatedProfile, token, out var profile, out var error))
                throw new InvalidOperationException("MCP test bridge refused: " + error);

            _profile = profile;
            _root = Path.Combine(profile, BridgeFolderName);
            _requests = Path.Combine(_root, "requests");
            _responses = Path.Combine(_root, "responses");
            _token = token;
            _sessionId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(_requests);
            Directory.CreateDirectory(_responses);
            // Both local laboratory instances must keep ticking while only one
            // game window has focus, otherwise one side cannot answer probes or
            // process the multiplayer traffic the test is meant to observe.
            Application.runInBackground = true;
            _pumpObject = new GameObject("WOTRMultiplayer.TestControlBridgePump");
            UnityEngine.Object.DontDestroyOnLoad(_pumpObject);
            _pumpObject.hideFlags = HideFlags.HideAndDontSave;
            _pumpObject.AddComponent<TestControlBridgePump>().Initialize(this);
            WriteAtomic(Path.Combine(_root, "ready.json"), new JObject
            {
                ["schema"] = 1,
                ["sessionId"] = _sessionId,
                ["token"] = _token,
                ["processId"] = System.Diagnostics.Process.GetCurrentProcess().Id,
                ["profile"] = _profile,
                ["modVersion"] = Main.ModVersion?.ToString(),
                ["protocolVersion"] = NetworkRealTimeCombatState.ProtocolVersion,
                ["runInBackground"] = Application.runInBackground,
                ["startedUtc"] = DateTime.UtcNow.ToString("O")
            });
            Enabled = true;
            _logger.LogWarning("[MCP] Isolated test-control bridge enabled. Profile={Profile}, SessionId={SessionId}",
                _profile, _sessionId);
#endif
        }

        public void Tick()
        {
#if RTWP_HOST_PRESENTATION_PROTOTYPE
            if (!Enabled) return;
            // The normal UMM callback is not invoked on every startup/loading
            // screen. Drain the mod's main-thread queue from this Unity-owned
            // component as well so menu loads and network callbacks can finish.
            (_mainThreadAccessor as WOTRMultiplayer.UI.MainThreadAccessor)?.Drain();
            string[] files;
            try
            {
                files = Directory.GetFiles(_requests, "*.json")
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
            }
            catch (Exception failure)
            {
                _logger.LogError(failure, "[MCP] Unable to enumerate test-control requests");
                return;
            }

            foreach (var file in files) Process(file);
#endif
        }

#if RTWP_HOST_PRESENTATION_PROTOTYPE
        private void Process(string path)
        {
            string requestId = null;
            try
            {
                var info = new FileInfo(path);
                if (info.Length <= 0 || info.Length > 64 * 1024)
                    throw new InvalidDataException("request-size-invalid");
                var request = JObject.Parse(File.ReadAllText(path));
                requestId = request.Value<string>("id");
                if (!Guid.TryParseExact(requestId, "N", out _)
                    || !string.Equals(Path.GetFileNameWithoutExtension(path), requestId,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("request-id-invalid");
                if (!ConstantTimeEquals(request.Value<string>("token"), _token))
                    throw new UnauthorizedAccessException("request-token-invalid");
                if (!string.Equals(request.Value<string>("sessionId"), _sessionId, StringComparison.Ordinal))
                    throw new InvalidDataException("request-session-stale");
                var expiresUnixMs = request.Value<long?>("expiresUnixMs");
                if (!expiresUnixMs.HasValue
                    || expiresUnixMs.Value <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    throw new TimeoutException("request-expired");
                // Remove the durable queue entry before claiming the id. The
                // bridge can be ticked by both UMM and its startup pump; if a
                // transient Windows delete denial occurs, no pending claim is
                // retained and the exact same file is retried on the next frame.
                File.Delete(path);
                lock (_pendingLock)
                {
                    // A recreated request with the same id must never replace
                    // the response of an asynchronous command already running.
                    // Keep completed ids for the lifetime of this bridge
                    // session as well: the MCP producer may recreate the same
                    // durable request while consuming its response, after the
                    // synchronous dispatch has already left _pending.
                    if (_completed.Contains(requestId) || !_pending.Add(requestId)) return;
                }
                Dispatch(requestId, request.Value<string>("command"), request["arguments"] as JObject ?? new JObject());
            }
            catch (IOException)
            {
                // A rename is atomic, but Windows may still briefly deny a read
                // or delete while the producer closes the file. Leave it in the
                // queue and retry on the next frame instead of losing a command.
                return;
            }
            catch (Exception failure)
            {
                TryDelete(path);
                if (!string.IsNullOrWhiteSpace(requestId)) Complete(requestId, null, failure);
                else _logger.LogWarning(failure, "[MCP] Rejected malformed test-control request. Path={Path}", path);
            }
        }

        private void Dispatch(string requestId, string command, JObject arguments)
        {
            try
            {
                switch (command)
                {
                    case "get_state":
                        Complete(requestId, CaptureState(), null);
                        return;
                    case "advance_startup":
                        Complete(requestId, AdvanceStartup(), null);
                        return;
                    case "create_host":
                    {
                        if (_actors.Current?.IsActive == true)
                            throw new InvalidOperationException("multiplayer-session-already-active");
                        var fileName = arguments.Value<string>("fileName");
                        var path = TestControlBridgePolicy.ResolveSavePath(_profile, fileName);
                        if (!File.Exists(path)) throw new FileNotFoundException("save-not-found", path);
                        var startup = ReadStartup(path, out var gameId);
                        _actors.Host.Create(gameId, null, null, startup);
                        Complete(requestId, new JObject
                        {
                            ["created"] = true,
                            ["gameId"] = gameId,
                            ["fileName"] = Path.GetFileName(path),
                            ["characters"] = startup.Characters.Count
                        }, null);
                        return;
                    }
                    case "connect_client":
                    {
                        if (_actors.Current?.IsActive == true)
                            throw new InvalidOperationException("multiplayer-session-already-active");
                        var address = arguments.Value<string>("address");
                        var port = arguments.Value<int?>("port");
                        if (string.IsNullOrWhiteSpace(address))
                            throw new InvalidDataException("address-required");
                        if (!port.HasValue || port.Value < IPEndPoint.MinPort || port.Value > IPEndPoint.MaxPort)
                            throw new InvalidDataException("port-invalid");
                        _actors.Client.Connect(address, port.Value);
                        Complete(requestId, new JObject
                        {
                            ["connecting"] = true,
                            ["address"] = address,
                            ["port"] = port.Value
                        }, null);
                        return;
                    }
                    case "set_ready":
                    {
                        var actor = _actors.Current
                            ?? throw new InvalidOperationException("multiplayer-session-not-active");
                        var requested = RequireBoolean(arguments, "ready");
                        var local = actor.GetPlayers().FirstOrDefault(player =>
                            player.Id == (actor as MultiplayerActorBase)?.Game?.LocalPlayerId)
                            ?? throw new InvalidOperationException("local-player-not-found");
                        var actual = local.IsReady == requested ? local.IsReady : actor.ReadyChanged();
                        Complete(requestId, new JObject
                        {
                            ["requestedReady"] = requested,
                            ["isReady"] = actual
                        }, null);
                        return;
                    }
                    case "start_game":
                    {
                        var host = RequireHost();
                        Complete(requestId, new JObject
                        {
                            ["started"] = host.Start()
                        }, null);
                        return;
                    }
                    case "assign_character":
                    {
                        var host = RequireHost();
                        var index = arguments.Value<int?>("index")
                            ?? throw new InvalidDataException("index-required");
                        var character = host.GetCharacters().FirstOrDefault(value => value.Index == index)
                            ?? throw new InvalidDataException("character-index-not-found");
                        var player = host.GetPlayers().FirstOrDefault(value => !value.IsHost)
                            ?? throw new InvalidOperationException("client-player-not-found");
                        host.ChangeCharacterOwner(character, player);
                        Complete(requestId, new JObject
                        {
                            ["index"] = index,
                            ["unitId"] = character.UnitId,
                            ["playerId"] = player.Id
                        }, null);
                        return;
                    }
                    case "teleport_character":
                    {
                        var host = RequireHost();
                        var index = arguments.Value<int?>("index")
                            ?? throw new InvalidDataException("index-required");
                        var distance = arguments.Value<float?>("distance")
                            ?? throw new InvalidDataException("distance-required");
                        if (float.IsNaN(distance) || float.IsInfinity(distance) || distance < 0 || distance > 200)
                            throw new InvalidDataException("distance-invalid");
                        var character = host.GetCharacters().FirstOrDefault(value => value.Index == index)
                            ?? throw new InvalidDataException("character-index-not-found");
                        var unit = Game.Instance?.State?.Units?.All.FirstOrDefault(value =>
                            string.Equals(value.UniqueId, character.UnitId, StringComparison.Ordinal))
                            ?? throw new InvalidOperationException("character-unit-not-found");
                        var before = unit.Position;
                        unit.Position = before + new Vector3(distance, 0, 0);
                        Complete(requestId, new JObject
                        {
                            ["unitId"] = unit.UniqueId,
                            ["beforeX"] = before.x,
                            ["afterX"] = unit.Position.x,
                            ["distance"] = Vector3.Distance(before, unit.Position)
                        }, null);
                        return;
                    }
                    case "start_nearest_combat":
                    {
                        var actor = _actors.Current;
                        if (actor?.IsActive != true)
                            throw new InvalidOperationException("multiplayer-session-not-active");
                        var game = Game.Instance ?? throw new InvalidOperationException("native-game-not-ready");
                        var party = game.Player?.PartyAndPets?.Where(unit => unit != null && !unit.State.IsFinallyDead)
                            .ToList() ?? [];
                        if (party.Count == 0) throw new InvalidOperationException("party-not-found");
                        var requestedEnemyId = arguments.Value<string>("enemyId");
                        var locallyControlled = party.Where(unit => actor.IsControlledByLocalPlayer(unit.UniqueId)).ToList();
                        var referenceParty = locallyControlled.Count > 0 ? locallyControlled : party;
                        var hostiles = game.State.Units.All.Where(unit => unit != null && !unit.State.IsFinallyDead
                            && !unit.IsPlayerFaction && unit.IsPlayersEnemy && unit.View != null);
                        var enemy = string.IsNullOrWhiteSpace(requestedEnemyId)
                            ? hostiles.OrderBy(unit => referenceParty.Min(member =>
                                Vector3.Distance(member.Position, unit.Position))).FirstOrDefault()
                            : hostiles.FirstOrDefault(unit => string.Equals(unit.UniqueId, requestedEnemyId,
                                StringComparison.Ordinal));
                        if (enemy == null) throw new InvalidOperationException("hostile-unit-not-found");
                        var distance = referenceParty.Min(member => Vector3.Distance(member.Position, enemy.Position));
                        foreach (var member in party) game.UnitMemoryController.AddToMemory(enemy, member);
                        if (!enemy.IsInCombat)
                            enemy.JoinCombat(UnitCombatJoinController.CalculateIsNotSurprised(enemy));
                        foreach (var member in party)
                        {
                            if (!member.IsInCombat)
                                member.JoinCombat(UnitCombatJoinController.CalculateIsNotSurprised(member));
                        }
                        Complete(requestId, new JObject
                        {
                            ["enemyId"] = enemy.UniqueId,
                            ["enemyName"] = enemy.CharacterName,
                            ["distance"] = distance,
                            ["partyCount"] = party.Count,
                            ["role"] = ReferenceEquals(actor, _actors.Host) ? "host" : "client"
                        }, null);
                        return;
                    }
                    case "set_pause":
                    {
                        var paused = RequireBoolean(arguments, "paused");
                        bool accepted;
                        if (_actors.Host?.IsActive == true)
                            accepted = _actors.Host.RequestLocalRealTimePause(paused);
                        else if (_actors.Client?.IsActive == true)
                            accepted = _actors.Client.RequestRealTimePause(paused);
                        else
                            throw new InvalidOperationException("multiplayer-session-not-active");
                        Complete(requestId, new JObject
                        {
                            ["accepted"] = accepted,
                            ["requestedPaused"] = paused,
                            ["isPaused"] = _gameInteraction.IsPaused
                        }, null);
                        return;
                    }
                    case "force_unpause":
                    {
                        var actor = _actors.Current;
                        if (actor?.IsActive != true)
                            throw new InvalidOperationException("multiplayer-session-not-active");
                        actor.ForceUnpause();
                        Complete(requestId, new JObject
                        {
                            ["forced"] = true,
                            ["isPaused"] = _gameInteraction.IsPaused
                        }, null);
                        return;
                    }
                    case "set_combat_mode":
                    {
                        var host = RequireHost();
                        var value = arguments.Value<string>("mode");
                        var mode = string.Equals(value, "real_time", StringComparison.OrdinalIgnoreCase)
                            ? CombatMode.RealTimeWithPause
                            : string.Equals(value, "turn_based", StringComparison.OrdinalIgnoreCase)
                                ? CombatMode.TurnBased
                                : throw new InvalidDataException("mode-must-be-real_time-or-turn_based");
                        CompleteAsync(requestId, host.RequestRealTimeCombatModeAsync(mode), accepted => new JObject
                        {
                            ["accepted"] = accepted,
                            ["requestedMode"] = mode.ToString()
                        });
                        return;
                    }
                    case "force_sync":
                    {
                        var host = RequireHost();
                        CompleteAsync(requestId, host.ForceCombatSynchronizationAsync(), accepted => new JObject
                        {
                            ["accepted"] = accepted
                        });
                        return;
                    }
                    case "save_combat":
                    {
                        var host = RequireHost();
                        CompleteAsync(requestId, host.SaveCombatAsync(), savePath => new JObject
                        {
                            ["savePath"] = savePath,
                            ["fileName"] = string.IsNullOrWhiteSpace(savePath) ? null : Path.GetFileName(savePath)
                        });
                        return;
                    }
                    case "load_save":
                    {
                        var actor = _actors.Current as MultiplayerActorBase
                            ?? throw new InvalidOperationException("multiplayer-session-not-active");
                        if (!ReferenceEquals(actor, _actors.Host))
                            throw new InvalidOperationException("load-save-is-host-only");
                        var fileName = arguments.Value<string>("fileName");
                        var path = TestControlBridgePolicy.ResolveSavePath(_profile, fileName);
                        if (!File.Exists(path)) throw new FileNotFoundException("save-not-found", path);
                        // Exercise the same native Game.LoadGame entry point as
                        // the in-game menu.  Calling ForceLoadGame directly only
                        // resets/transfers the multiplayer session and would
                        // leave the host in its old world while the client loads.
                        if (_gameInteraction.QuickLoadGame(path) != actor.Game.Id)
                            throw new InvalidOperationException("host-native-load-not-started");
                        Complete(requestId, new JObject
                        {
                            ["initiated"] = true,
                            ["fileName"] = Path.GetFileName(path)
                        }, null);
                        return;
                    }
                    default:
                        throw new InvalidDataException("command-not-allowed");
                }
            }
            catch (Exception failure)
            {
                Complete(requestId, null, failure);
            }
        }

        private JObject CaptureState()
        {
            var actor = _actors.Current;
            var network = actor as MultiplayerActorBase;
            var nativeGame = Game.Instance;
            var realTime = network?.Game?.RealTimeCombat;
            var endpoint = actor?.GetGameConnectivity()?.Endpoint as IPEndPoint;
            var party = new JArray();
            var combatBuffs = new JArray();
            if (nativeGame?.Player?.PartyAndPets != null)
            {
                foreach (var unit in nativeGame.Player.PartyAndPets.Where(unit => unit != null))
                {
                    party.Add(new JObject
                    {
                        ["id"] = unit.UniqueId,
                        ["name"] = unit.CharacterName,
                        ["owner"] = actor?.GetCharacterOwnerName(unit.UniqueId),
                        ["controlledLocally"] = actor?.IsControlledByLocalPlayer(unit.UniqueId) == true,
                        ["isInCombat"] = unit.IsInCombat,
                        ["hp"] = unit.HPLeft,
                        ["maxHp"] = unit.MaxHP,
                        ["position"] = new JObject
                        {
                            ["x"] = unit.Position.x,
                            ["y"] = unit.Position.y,
                            ["z"] = unit.Position.z
                        }
                    });
                }
            }
            if (nativeGame?.State?.Units?.All != null)
            {
                foreach (var unit in nativeGame.State.Units.All.Where(unit => unit != null && unit.IsInCombat))
                {
                    combatBuffs.Add(new JObject
                    {
                        ["unitId"] = unit.UniqueId,
                        ["buffs"] = new JArray(unit.Buffs.Enumerable.Select(buff => new JObject
                        {
                            ["id"] = buff.UniqueId,
                            ["name"] = buff.NameForAcronym,
                            ["blueprintId"] = buff.Blueprint?.AssetGuid.ToString(),
                            ["casterId"] = buff.Context?.MaybeCaster?.UniqueId,
                            ["sourceAbilityBlueprintId"] = buff.Context?.SourceAbility?.AssetGuid.ToString(),
                            ["rank"] = buff.Rank,
                            ["hidden"] = buff.Hidden,
                            ["permanent"] = buff.IsPermanent
                        }))
                    });
                }
            }

            var players = new JArray((actor?.GetPlayers() ?? []).Select(player => new JObject
            {
                ["id"] = player.Id,
                ["name"] = player.Name,
                ["isHost"] = player.IsHost,
                ["isReady"] = player.IsReady,
                ["syncStatus"] = player.LobbySyncStatus.ToString()
            }));
            return new JObject
            {
                ["capturedUtc"] = DateTime.UtcNow.ToString("O"),
                ["processId"] = System.Diagnostics.Process.GetCurrentProcess().Id,
                ["profile"] = _profile,
                ["modVersion"] = Main.ModVersion?.ToString(),
                ["protocolVersion"] = NetworkRealTimeCombatState.ProtocolVersion,
                ["role"] = ReferenceEquals(actor, _actors.Host) ? "host"
                    : ReferenceEquals(actor, _actors.Client) ? "client" : "inactive",
                ["multiplayerActive"] = actor?.IsActive == true,
                ["inLobby"] = actor?.IsInLobby == true,
                ["stage"] = network?.Game?.Stage.ToString(),
                ["gameId"] = network?.Game?.Id,
                ["localPlayerId"] = network?.Game?.LocalPlayerId,
                ["endpointAddress"] = endpoint?.Address?.ToString(),
                ["endpointPort"] = endpoint?.Port,
                ["players"] = players,
                ["characters"] = new JArray((actor?.GetCharacters() ?? []).Select(character => new JObject
                {
                    ["index"] = character.Index,
                    ["unitId"] = character.UnitId,
                    ["name"] = character.Name,
                    ["ownerId"] = character.Owner?.Id
                })),
                ["isPaused"] = nativeGame != null && _gameInteraction.IsPaused,
                ["gameMode"] = nativeGame == null ? null : _gameInteraction.CurrentGameMode.ToString(),
                ["mainMenuReady"] = nativeGame?.RootUiContext?.MainMenuVM != null,
                ["areaId"] = actor?.CurrentArea?.Id,
                ["areaName"] = actor?.CurrentArea?.Name,
                ["isInCombat"] = actor?.IsInCombat == true,
                ["networkCombatStage"] = network?.Game?.Combat?.Stage.ToString(),
                ["combatMode"] = realTime?.Mode.ToString(),
                ["preferredCombatMode"] = realTime?.PreferredMode.ToString(),
                ["combatPhase"] = realTime?.Phase.ToString(),
                ["combatEpoch"] = realTime?.Epoch,
                ["combatSequence"] = realTime?.LastAuthoritativeSequence,
                ["combatHash"] = realTime?.CanonicalStateHash.ToString("X16"),
                ["pausePolicy"] = realTime?.PausePolicy.ToString(),
                ["pauseSequence"] = realTime?.PauseSequence,
                ["pausedPlayers"] = realTime == null ? new JArray() : new JArray(realTime.PausedPlayers.OrderBy(id => id)),
                ["resumeVotes"] = realTime == null ? new JArray() : new JArray(realTime.ResumeVotes.OrderBy(id => id)),
                ["combatBarrierActive"] = Main.Multiplayer?.IsRealTimeCombatBarrierActive == true,
                ["savingBlocked"] = Main.Multiplayer?.IsSavingBlockedByCombatSync == true,
                ["recoveryStatus"] = realTime?.RecoveryStatus,
                ["recoveryFailure"] = realTime?.RecoveryFailure,
                ["forcedPause"] = network?.Game?.ForcedPause?.Reason.ToString(),
                ["dialogActive"] = network?.Game?.DialogState != null,
                ["dialogCue"] = network?.Game?.DialogState?.CurrentCueName,
                ["dialogSelectingAnswer"] = network?.Game?.DialogState?.IsSelectingAnswer,
                ["dialogHasSystemAnswer"] = network?.Game?.DialogState?.HasSystemAnswer,
                ["dialogVoteRound"] = network?.Game?.DialogState?.VoteRoundId,
                ["dialogVoteCommitted"] = network?.Game?.DialogState?.VoteCommitted,
                ["dialogVoteParticipants"] = network?.Game?.DialogState == null
                    ? new JArray()
                    : new JArray(network.Game.DialogState.VoteParticipants.OrderBy(id => id)),
                ["levelingActive"] = network?.Game?.Leveling != null,
                ["levelingUnitId"] = network?.Game?.Leveling?.UnitId,
                ["levelingType"] = network?.Game?.Leveling?.Type.ToString(),
                ["levelingPhase"] = network?.Game?.Leveling?.PhaseIndex,
                ["levelingReadyPlayers"] = network?.Game?.Leveling == null
                    ? new JArray()
                    : new JArray(network.Game.Leveling.PlayerReadiness.OrderBy(id => id)),
                ["playersInRespec"] = network?.Game?.PlayersInRespecWindow?.Count ?? 0,
                ["playersInTransitionMap"] = network?.Game?.PlayersInTransitionMap == null
                    ? new JArray()
                    : new JArray(network.Game.PlayersInTransitionMap.OrderBy(id => id)),
                ["party"] = party,
                ["combatBuffs"] = combatBuffs
            };
        }

        private JObject AdvanceStartup()
        {
            if (_startupAdvanced)
            {
                return new JObject
                {
                    ["invoked"] = 0,
                    ["alreadyAdvanced"] = true,
                    ["mainMenuReady"] = Game.Instance?.RootUiContext?.MainMenuVM != null
                };
            }
            var type = typeof(Game).Assembly.GetType("Kingmaker.UI.MainMenuUI.SplashScreenController", false);
            if (type == null) throw new InvalidOperationException("splash-controller-type-not-found");
            var complete = type.GetMethod("Complete", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(type.FullName, "Complete");
            var invoked = 0;
            foreach (var candidate in Resources.FindObjectsOfTypeAll(type))
            {
                if (candidate is Component component && component.gameObject.activeInHierarchy)
                {
                    complete.Invoke(candidate, null);
                    invoked++;
                }
            }
            if (invoked > 0) _startupAdvanced = true;
            return new JObject
            {
                ["invoked"] = invoked,
                ["mainMenuReady"] = Game.Instance?.RootUiContext?.MainMenuVM != null
            };
        }

        private static NetworkGameStartUp ReadStartup(string path, out string gameId)
        {
            using var stream = File.OpenRead(path);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, false);
            var entry = archive.GetEntry("header.json")
                ?? throw new InvalidDataException("save-header-missing");
            JObject header;
            using (var reader = new StreamReader(entry.Open()))
            {
                header = JObject.Parse(reader.ReadToEnd());
            }

            gameId = header.Value<string>("GameId");
            if (string.IsNullOrWhiteSpace(gameId)) throw new InvalidDataException("save-game-id-missing");
            var playerName = header.Value<string>("PlayerCharacterName");
            var characters = new List<NetworkCharacter>();
            var portraits = header["PartyPortraits"] as JArray ?? new JArray();
            for (var index = 0; index < portraits.Count && index < Main.MaxCharactersInParty; index++)
            {
                var portrait = portraits[index] as JObject;
                characters.Add(new NetworkCharacter
                {
                    Index = index,
                    Name = index == 0 && !string.IsNullOrWhiteSpace(playerName)
                        ? playerName : "Party " + (index + 1),
                    Portrait = portrait?.Value<string>("m_Blueprint")
                });
            }
            if (characters.Count == 0) throw new InvalidDataException("save-party-missing");

            return new NetworkGameStartUp
            {
                SavePath = path,
                Title = header.Value<string>("Name") ?? Path.GetFileNameWithoutExtension(path),
                Characters = characters,
                IsNewGameSequence = false,
                AutoStart = false
            };
        }

        private IMultiplayerHost RequireHost()
        {
            if (_actors.Host?.IsActive != true) throw new InvalidOperationException("host-session-not-active");
            return _actors.Host;
        }

        private static bool RequireBoolean(JObject arguments, string name)
        {
            var value = arguments[name];
            if (value?.Type != JTokenType.Boolean) throw new InvalidDataException(name + "-must-be-boolean");
            return value.Value<bool>();
        }

        private void CompleteAsync<T>(string requestId, Task<T> task, Func<T, JObject> project)
        {
            task.ContinueWith(completed =>
            {
                if (completed.IsCanceled) Complete(requestId, null, new TaskCanceledException(completed));
                else if (completed.IsFaulted) Complete(requestId, null, completed.Exception?.GetBaseException());
                else
                {
                    try { Complete(requestId, project(completed.Result), null); }
                    catch (Exception failure) { Complete(requestId, null, failure); }
                }
            }, TaskScheduler.Default);
        }

        private void Complete(string requestId, JToken result, Exception failure)
        {
            try
            {
                var response = new JObject
                {
                    ["id"] = requestId,
                    ["sessionId"] = _sessionId,
                    ["ok"] = failure == null,
                    ["result"] = result,
                    ["error"] = failure?.GetBaseException().Message,
                    ["completedUtc"] = DateTime.UtcNow.ToString("O")
                };
                WriteAtomic(Path.Combine(_responses, requestId + ".json"), response);
            }
            catch (Exception writeFailure)
            {
                _logger.LogError(writeFailure, "[MCP] Unable to write test-control response. RequestId={RequestId}", requestId);
            }
            finally
            {
                lock (_pendingLock)
                {
                    _pending.Remove(requestId);
                    _completed.Add(requestId);
                }
            }
        }

        private static bool ConstantTimeEquals(string left, string right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            var difference = 0;
            for (var index = 0; index < left.Length; index++) difference |= left[index] ^ right[index];
            return difference == 0;
        }

        private static void WriteAtomic(string path, JToken value)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, value.ToString(Formatting.None));
            try
            {
                // UMM and the persistent startup pump can complete the same
                // short-lived request close to the MCP reader deleting its
                // response.  Delete+Move leaves a race where another writer
                // recreates the destination between both calls.  Replace is
                // atomic when the destination exists; retry the opposite case
                // when the reader changes it between the probe and operation.
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(temporary, path, null, true);
                        else File.Move(temporary, path);
                        return;
                    }
                    catch (IOException) when (attempt < 7)
                    {
                        Thread.Yield();
                    }
                }
            }
            finally
            {
                TryDelete(temporary);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
#endif
    }

#if RTWP_HOST_PRESENTATION_PROTOTYPE
    /// <summary>
    /// Keeps the isolated test bridge responsive during startup screens where
    /// Unity Mod Manager does not yet invoke the mod's OnUpdate callback.
    /// </summary>
    internal sealed class TestControlBridgePump : MonoBehaviour
    {
        private TestControlBridge _bridge;

        internal void Initialize(TestControlBridge bridge)
        {
            _bridge = bridge;
        }

        private void Update()
        {
            _bridge?.Tick();
        }
    }
#endif

    internal static class TestControlBridgePolicy
    {
        internal static bool TryValidateActivation(string requestedProfile, string token,
            out string profile, out string error)
        {
            profile = null;
            error = null;
            if (string.IsNullOrWhiteSpace(requestedProfile)) error = "isolated-profile-required";
            else if (string.IsNullOrWhiteSpace(token) || token.Length < 32) error = "session-token-too-short";
            else
            {
                profile = Path.GetFullPath(requestedProfile).TrimEnd(Path.DirectorySeparatorChar);
                if (!File.Exists(Path.Combine(profile, RealTimeTestProfileBootstrap.MarkerFile)))
                    error = "isolated-profile-marker-missing";
            }
            return error == null;
        }

        internal static string ResolveSavePath(string profile, string fileName)
        {
            if (string.IsNullOrWhiteSpace(profile)) throw new InvalidOperationException("isolated-profile-required");
            if (string.IsNullOrWhiteSpace(fileName)
                || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)
                || !string.Equals(Path.GetExtension(fileName), ".zks", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("save-file-name-invalid");
            var saves = Path.GetFullPath(Path.Combine(profile, "Saved Games"))
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(saves, fileName));
            if (!path.StartsWith(saves, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("save-path-outside-profile");
            return path;
        }
    }
}
