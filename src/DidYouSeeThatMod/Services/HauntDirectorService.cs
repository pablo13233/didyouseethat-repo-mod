using System;
using System.Collections.Generic;
using BepInEx.Logging;
using DidYouSeeThatMod.Configuration;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace DidYouSeeThatMod.Services
{
    /// <summary>
    /// Coordinates timed and event-driven hallucinations (both personal and team-synchronized),
    /// dynamically scaling frequency based on distance from teammates.
    /// </summary>
    public sealed class HauntDirectorService : IDisposable
    {
        private const byte HauntPhotonEventCode = 177;
        private const byte SubEventWelcomeDecision = 1;
        private const byte SubEventGroupSound = 2;
        private const byte SubEventGroupShadow = 3;

        private readonly ModConfig _config;
        private readonly SoundLibraryService _soundLibrary;
        private readonly TeamProximityService _proximityService;
        private readonly ShadowHallucinationService _shadowService;
        private readonly ManualLogSource _logger;

        private float _audioNormalizedProgress;
        private float _shadowNormalizedProgress;
        private float _audioJitterMultiplier = 1.0f;
        private float _shadowJitterMultiplier = 1.0f;
        private float _lastWakeUpTime = -999.0f;

        private bool _levelWelcomeEvaluated;
        private bool _receivedHostWelcomeDecision;
        private float _levelSpawnTimestamp = -1.0f;

        /// <summary>
        /// Initializes a new instance of the <see cref="HauntDirectorService"/> class.
        /// </summary>
        /// <param name="config">Mod configuration settings.</param>
        /// <param name="soundLibrary">Audio clip library and spatial playback service.</param>
        /// <param name="proximityService">Team distance and isolation calculator.</param>
        /// <param name="shadowService">Shadow figure spawner service.</param>
        /// <param name="logger">BepInEx logger instance.</param>
        public HauntDirectorService(
            ModConfig config,
            SoundLibraryService soundLibrary,
            TeamProximityService proximityService,
            ShadowHallucinationService shadowService,
            ManualLogSource logger)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _soundLibrary = soundLibrary ?? throw new ArgumentNullException(nameof(soundLibrary));
            _proximityService = proximityService ?? throw new ArgumentNullException(nameof(proximityService));
            _shadowService = shadowService ?? throw new ArgumentNullException(nameof(shadowService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            ResetJitterMultipliers();

            if (PhotonNetwork.NetworkingClient != null)
            {
                PhotonNetwork.NetworkingClient.EventReceived += OnPhotonEventReceived;
            }
        }

        /// <summary>
        /// Unsubscribes from Photon network events.
        /// </summary>
        public void Dispose()
        {
            if (PhotonNetwork.NetworkingClient != null)
            {
                PhotonNetwork.NetworkingClient.EventReceived -= OnPhotonEventReceived;
            }
        }

        /// <summary>
        /// Resets per-level state when a new level begins generating or switching scenes.
        /// </summary>
        public void OnNewLevelStarted()
        {
            _levelWelcomeEvaluated = false;
            _receivedHostWelcomeDecision = false;
            _levelSpawnTimestamp = -1.0f;
            _audioNormalizedProgress = UnityEngine.Random.Range(0.05f, 0.25f);
            _shadowNormalizedProgress = UnityEngine.Random.Range(0.10f, 0.35f);
            ResetJitterMultipliers();
        }

        /// <summary>
        /// Notifies the director that the local player has finished spawning inside the vehicle at the start of a level.
        /// </summary>
        public void NotifyPlayerSpawnedInVehicle()
        {
            if (!_config.Enabled.Value || _levelWelcomeEvaluated)
            {
                return;
            }

            // Only trigger welcome.ogg in actual playable levels when starting inside the truck.
            if (!SemiFunc.RunIsLevel() || SemiFunc.RunIsLobbyMenu() || SemiFunc.IsMainMenu())
            {
                return;
            }

            if (_levelSpawnTimestamp < 0.0f)
            {
                _levelSpawnTimestamp = Time.time;
            }
        }

        /// <summary>
        /// Evaluates the 10% chance to play <c>wake-up.ogg</c> locally when the local player crouches.
        /// </summary>
        public void OnLocalPlayerCrouched()
        {
            if (!_config.Enabled.Value)
            {
                return;
            }

            if (SemiFunc.IsMainMenu() || SemiFunc.IsSplashScreen() || SemiFunc.RunIsLobbyMenu())
            {
                return;
            }

            var now = Time.time;
            if (now - _lastWakeUpTime < _config.WakeUpCooldownSeconds.Value)
            {
                return;
            }

            var chance = Mathf.Clamp01(_config.WakeUpCrouchChance.Value);
            if (UnityEngine.Random.value <= chance)
            {
                _lastWakeUpTime = now;
                _soundLibrary.PlayWakeUpSound();
                _logger.LogDebug("Activado sonido personal 'wake-up.ogg' al agacharse (10% chance).");
            }
        }

        /// <summary>
        /// Per-frame tick called by the plugin component to advance vehicle welcome checks, audio timers, and shadow timers.
        /// </summary>
        /// <param name="deltaTime">Frame delta time in seconds.</param>
        public void Tick(float deltaTime)
        {
            if (!_config.Enabled.Value)
            {
                return;
            }

            if (!TeamProximityService.TryGetActiveLocalPlayer(out var localPlayer))
            {
                return;
            }

            TickVehicleWelcomeCheck();

            var isolationFactor = _proximityService.GetIsolationFactor(localPlayer);

            // Advance general random audio timer (80s near team -> 40s far from team).
            var targetAudioInterval = Mathf.Max(
                8.0f,
                _proximityService.GetTargetAudioIntervalSeconds(isolationFactor) * _audioJitterMultiplier);
            _audioNormalizedProgress += deltaTime / targetAudioInterval;

            if (_audioNormalizedProgress >= 1.0f)
            {
                _audioNormalizedProgress = 0.0f;
                _audioJitterMultiplier = UnityEngine.Random.Range(0.85f, 1.15f);
                TriggerRandomGeneralAudio(localPlayer, isolationFactor);
            }

            // Advance shadow hallucination timer (faster the farther the player is from the group).
            var targetShadowInterval = Mathf.Max(
                8.0f,
                _proximityService.GetTargetShadowIntervalSeconds(isolationFactor) * _shadowJitterMultiplier);
            _shadowNormalizedProgress += deltaTime / targetShadowInterval;

            if (_shadowNormalizedProgress >= 1.0f)
            {
                if (TriggerShadowHallucination(localPlayer, isolationFactor))
                {
                    _shadowNormalizedProgress = 0.0f;
                    _shadowJitterMultiplier = UnityEngine.Random.Range(0.85f, 1.15f);
                }
                else
                {
                    // Player is facing a close wall; retry in a couple of seconds when looking down a longer hallway.
                    _shadowNormalizedProgress = 0.92f;
                }
            }
        }

        private void TickVehicleWelcomeCheck()
        {
            if (_levelWelcomeEvaluated || _levelSpawnTimestamp < 0.0f)
            {
                return;
            }

            var elapsedSinceSpawn = Time.time - _levelSpawnTimestamp;

            // MasterClient / Singleplayer evaluates after 1.4s inside the truck.
            if (SemiFunc.IsMasterClientOrSingleplayer() && elapsedSinceSpawn >= 1.4f)
            {
                _levelWelcomeEvaluated = true;
                EvaluateAndBroadcastWelcomeSound();
                return;
            }

            // Non-host client fallback: if the Host doesn't have the mod installed, evaluate locally after 2.6s.
            if (!SemiFunc.IsMasterClientOrSingleplayer() && elapsedSinceSpawn >= 2.6f)
            {
                _levelWelcomeEvaluated = true;
                if (!_receivedHostWelcomeDecision)
                {
                    var chance = Mathf.Clamp01(_config.WelcomeSoundChance.Value);
                    if (UnityEngine.Random.value <= chance)
                    {
                        _soundLibrary.PlayWelcomeSound();
                        _logger.LogDebug("Activado 'welcome.ogg' al iniciar partida en el vehículo (roll local 25%).");
                    }
                }
            }
        }

        private void EvaluateAndBroadcastWelcomeSound()
        {
            var chance = Mathf.Clamp01(_config.WelcomeSoundChance.Value);
            var triggered = UnityEngine.Random.value <= chance;

            if (!SemiFunc.IsMultiplayer() || PhotonNetwork.CurrentRoom == null)
            {
                if (triggered)
                {
                    _soundLibrary.PlayWelcomeSound();
                    _logger.LogDebug("Activado 'welcome.ogg' al iniciar partida en el vehículo (Singleplayer 25%).");
                }

                return;
            }

            var photonPlayers = PhotonNetwork.PlayerList;
            if (photonPlayers == null || photonPlayers.Length == 0)
            {
                return;
            }

            var selectedActors = new List<int>();
            if (triggered)
            {
                // Pick 1 or several members of the team randomly.
                var countToPick = UnityEngine.Random.Range(1, photonPlayers.Length + 1);
                var pool = new List<Player>(photonPlayers);
                for (var i = 0; i < countToPick && pool.Count > 0; i++)
                {
                    var idx = UnityEngine.Random.Range(0, pool.Count);
                    selectedActors.Add(pool[idx].ActorNumber);
                    pool.RemoveAt(idx);
                }
            }

            var payload = new object[]
            {
                SubEventWelcomeDecision,
                selectedActors.ToArray()
            };

            var raiseOptions = new RaiseEventOptions { Receivers = ReceiverGroup.Others };
            PhotonNetwork.RaiseEvent(HauntPhotonEventCode, payload, raiseOptions, SendOptions.SendReliable);

            var localActor = PhotonNetwork.LocalPlayer != null ? PhotonNetwork.LocalPlayer.ActorNumber : -1;
            if (selectedActors.Contains(localActor))
            {
                _soundLibrary.PlayWelcomeSound();
                _logger.LogDebug($"Activado 'welcome.ogg' en el vehículo para {selectedActors.Count} jugador(es) del equipo.");
            }
        }

        private void TriggerRandomGeneralAudio(PlayerAvatar localPlayer, float isolationFactor)
        {
            if (!_soundLibrary.HasGeneralSounds)
            {
                return;
            }

            var clipKey = _soundLibrary.PickRandomGeneralClipKey();
            if (string.IsNullOrEmpty(clipKey))
            {
                return;
            }

            var isGroupEvent = ShouldTriggerAsGroupEvent(isolationFactor);
            var isEnvironmental = _soundLibrary.IsEnvironmentalClip(clipKey!);

            if (isGroupEvent)
            {
                var soundPos = ComputeEnvironmentalAudioPosition(localPlayer, minDistance: 3.5f, maxDistance: 9.5f);
                BroadcastGroupSound(clipKey!, soundPos);
                _soundLibrary.PlayWorldSpatialSound(clipKey!, soundPos, 0.95f);
                _logger.LogDebug($"Sonido grupal '{clipKey}.ogg' reproducido para el equipo.");
                return;
            }

            if (isEnvironmental)
            {
                var soundPos = ComputeEnvironmentalAudioPosition(localPlayer, minDistance: 4.5f, maxDistance: 11.5f);
                _soundLibrary.PlayWorldSpatialSound(clipKey!, soundPos, 0.92f);
            }
            else
            {
                var sideOffset = UnityEngine.Random.Range(-0.55f, 0.55f);
                var backOffset = UnityEngine.Random.Range(-0.75f, -0.35f);
                var headOffset = new Vector3(sideOffset, UnityEngine.Random.Range(-0.1f, 0.15f), backOffset);
                _soundLibrary.PlayCloseHallucinationSound(clipKey!, headOffset, 0.90f);
            }

            _logger.LogDebug($"Sonido personal '{clipKey}.ogg' reproducido (aislamiento={isolationFactor:F2}).");
        }

        private bool TriggerShadowHallucination(PlayerAvatar localPlayer, float isolationFactor)
        {
            if (!_shadowService.TryFindShadowTrajectory(localPlayer, out var startPos, out var endPos, out var duration))
            {
                return false;
            }

            var modelKind = _shadowService.PickRandomModel(out var entityIndex);
            var isGroupEvent = ShouldTriggerAsGroupEvent(isolationFactor);

            if (isGroupEvent)
            {
                BroadcastGroupShadow(modelKind, entityIndex, startPos, endPos, duration);
            }

            _shadowService.SpawnShadow(modelKind, entityIndex, startPos, endPos, duration);
            _logger.LogDebug(
                $"Sombra ({modelKind}, grupal={isGroupEvent}) generada por {duration:F1}s (aislamiento={isolationFactor:F2}).");
            return true;
        }

        private bool ShouldTriggerAsGroupEvent(float isolationFactor)
        {
            if (!SemiFunc.IsMultiplayer() || PhotonNetwork.CurrentRoom == null || PhotonNetwork.CurrentRoom.PlayerCount <= 1)
            {
                return false;
            }

            // When close to teammates, group events happen more often; when isolated far away, hallucinations are mostly personal.
            var baseGroupChance = Mathf.Clamp01(_config.GroupEventChance.Value);
            var adjustedChance = isolationFactor > 0.75f ? baseGroupChance * 0.4f : baseGroupChance;
            return UnityEngine.Random.value <= adjustedChance;
        }

        private static Vector3 ComputeEnvironmentalAudioPosition(PlayerAvatar localPlayer, float minDistance, float maxDistance)
        {
            var camTransform = SoundLibraryService.ResolveLocalListenerTransform() ?? localPlayer.transform;
            var forwardFlat = Vector3.ProjectOnPlane(camTransform.forward, Vector3.up).normalized;
            if (forwardFlat.sqrMagnitude < 0.01f)
            {
                forwardFlat = Vector3.forward;
            }

            // Prefer behind or to the sides of the player so they turn around.
            var angle = UnityEngine.Random.Range(95.0f, 265.0f);
            var dir = Quaternion.Euler(0.0f, angle, 0.0f) * forwardFlat;
            var dist = UnityEngine.Random.Range(minDistance, maxDistance);
            return localPlayer.transform.position + Vector3.up * 1.2f + dir * dist;
        }

        private static void BroadcastGroupSound(string clipKey, Vector3 worldPos)
        {
            if (!SemiFunc.IsMultiplayer() || PhotonNetwork.CurrentRoom == null)
            {
                return;
            }

            var payload = new object[]
            {
                SubEventGroupSound,
                clipKey,
                worldPos
            };

            var raiseOptions = new RaiseEventOptions { Receivers = ReceiverGroup.Others };
            PhotonNetwork.RaiseEvent(HauntPhotonEventCode, payload, raiseOptions, SendOptions.SendUnreliable);
        }

        private static void BroadcastGroupShadow(
            ShadowModelKind modelKind,
            int entityIndex,
            Vector3 startPos,
            Vector3 endPos,
            float duration)
        {
            if (!SemiFunc.IsMultiplayer() || PhotonNetwork.CurrentRoom == null)
            {
                return;
            }

            var payload = new object[]
            {
                SubEventGroupShadow,
                (byte)modelKind,
                entityIndex,
                startPos,
                endPos,
                duration
            };

            var raiseOptions = new RaiseEventOptions { Receivers = ReceiverGroup.Others };
            PhotonNetwork.RaiseEvent(HauntPhotonEventCode, payload, raiseOptions, SendOptions.SendUnreliable);
        }

        private void OnPhotonEventReceived(EventData photonEvent)
        {
            if (photonEvent == null || photonEvent.Code != HauntPhotonEventCode || !_config.Enabled.Value)
            {
                return;
            }

            if (photonEvent.CustomData is not object[] data || data.Length == 0 || data[0] is not byte subEvent)
            {
                return;
            }

            switch (subEvent)
            {
                case SubEventWelcomeDecision:
                    if (data.Length >= 2 && data[1] is int[] targetActors)
                    {
                        _receivedHostWelcomeDecision = true;
                        _levelWelcomeEvaluated = true;
                        var localActor = PhotonNetwork.LocalPlayer != null ? PhotonNetwork.LocalPlayer.ActorNumber : -1;
                        for (var i = 0; i < targetActors.Length; i++)
                        {
                            if (targetActors[i] == localActor)
                            {
                                _soundLibrary.PlayWelcomeSound();
                                break;
                            }
                        }
                    }

                    break;

                case SubEventGroupSound:
                    if (data.Length >= 3 && data[1] is string clipKey && data[2] is Vector3 soundPos)
                    {
                        _soundLibrary.PlayWorldSpatialSound(clipKey, soundPos, 0.95f);
                        // Partial reset so teammates don't immediately stack another group sound on top.
                        _audioNormalizedProgress = Mathf.Min(_audioNormalizedProgress, 0.45f);
                    }

                    break;

                case SubEventGroupShadow:
                    if (data.Length >= 6 &&
                        data[1] is byte rawKind &&
                        data[2] is int entityIndex &&
                        data[3] is Vector3 startPos &&
                        data[4] is Vector3 endPos &&
                        data[5] is float duration)
                    {
                        _shadowService.SpawnShadow((ShadowModelKind)rawKind, entityIndex, startPos, endPos, duration);
                        _shadowNormalizedProgress = Mathf.Min(_shadowNormalizedProgress, 0.45f);
                    }

                    break;
            }
        }

        private void ResetJitterMultipliers()
        {
            _audioJitterMultiplier = UnityEngine.Random.Range(0.88f, 1.12f);
            _shadowJitterMultiplier = UnityEngine.Random.Range(0.88f, 1.12f);
        }
    }
}

