using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using DidYouSeeThatMod.Configuration;
using UnityEngine;
using UnityEngine.Networking;

namespace DidYouSeeThatMod.Services
{
    /// <summary>
    /// Loads and plays <c>.ogg</c> audio clips from the plugin's <c>sounds</c> folder,
    /// keeping <c>welcome.ogg</c> and <c>wake-up.ogg</c> strictly reserved for their special triggers.
    /// </summary>
    public sealed class SoundLibraryService
    {
        /// <summary>
        /// Clip key for the match start vehicle sound (<c>welcome.ogg</c>).
        /// </summary>
        public const string WelcomeClipKey = "welcome";

        /// <summary>
        /// Clip key for the crouch trigger sound (<c>wake-up.ogg</c>).
        /// </summary>
        public const string WakeUpClipKey = "wake-up";

        private static readonly HashSet<string> EnvironmentalClipKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chasquido-de-dedos",
            "door",
            "efecto-eco-dj-somar-2",
            "golpe-mesa",
            "haunting-ambience",
            "quot-sound-effect",
            "another-nightmare_trimmed"
        };

        private readonly ModConfig _config;
        private readonly ManualLogSource _logger;
        private readonly Dictionary<string, AudioClip> _clipsByName = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _generalPoolKeys = new List<string>();
        private string? _lastRandomClipKey;

        /// <summary>
        /// Initializes a new instance of the <see cref="SoundLibraryService"/> class.
        /// </summary>
        /// <param name="config">Mod configuration settings.</param>
        /// <param name="logger">BepInEx logger instance.</param>
        public SoundLibraryService(ModConfig config, ManualLogSource logger)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Gets a value indicating whether at least one general sound clip has finished loading.
        /// </summary>
        public bool HasGeneralSounds => _generalPoolKeys.Count > 0;

        /// <summary>
        /// Asynchronously loads all <c>.ogg</c> files located in the plugin's <c>sounds</c> directory.
        /// </summary>
        /// <param name="pluginAssemblyLocation">Full file path of the plugin DLL.</param>
        /// <returns>Enumerator for Unity coroutine execution.</returns>
        public IEnumerator LoadAllSoundsCoroutine(string pluginAssemblyLocation)
        {
            var pluginDir = Path.GetDirectoryName(pluginAssemblyLocation);
            if (string.IsNullOrEmpty(pluginDir))
            {
                _logger.LogWarning("No se pudo determinar el directorio del plugin para cargar sonidos.");
                yield break;
            }

            var candidateDirs = new List<string>
            {
                Path.Combine(pluginDir, "sounds"),
                pluginDir
            };

            var discoveredFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in candidateDirs)
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                var oggFiles = Directory.GetFiles(dir, "*.ogg", SearchOption.TopDirectoryOnly);
                foreach (var filePath in oggFiles)
                {
                    var key = Path.GetFileNameWithoutExtension(filePath);
                    if (!string.IsNullOrWhiteSpace(key) && !discoveredFiles.ContainsKey(key))
                    {
                        discoveredFiles[key] = filePath;
                    }
                }
            }

            if (discoveredFiles.Count == 0)
            {
                _logger.LogWarning($"No se encontraron archivos .ogg en '{Path.Combine(pluginDir, "sounds")}'.");
                yield break;
            }

            foreach (var pair in discoveredFiles)
            {
                var key = pair.Key;
                var filePath = pair.Value;
                var fileUri = new Uri(filePath).AbsoluteUri;

                using var request = UnityWebRequestMultimedia.GetAudioClip(fileUri, AudioType.OGGVORBIS);
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    _logger.LogWarning($"Error al cargar sonido '{filePath}': {request.error}");
                    continue;
                }

                var clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null)
                {
                    continue;
                }

                clip.name = key;
                _clipsByName[key] = clip;

                if (!string.Equals(key, WelcomeClipKey, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(key, WakeUpClipKey, StringComparison.OrdinalIgnoreCase))
                {
                    _generalPoolKeys.Add(key);
                }
            }

            _logger.LogInfo(
                $"Sonidos cargados: {_clipsByName.Count} total ({_generalPoolKeys.Count} en pool aleatorio general, " +
                $"welcome={_clipsByName.ContainsKey(WelcomeClipKey)}, wake-up={_clipsByName.ContainsKey(WakeUpClipKey)}).");
        }

        /// <summary>
        /// Selects a random sound key from the general pool (excluding <c>welcome</c> and <c>wake-up</c>) with equal probability.
        /// </summary>
        /// <returns>The selected clip key, or <see langword="null"/> if no general clips are loaded.</returns>
        public string? PickRandomGeneralClipKey()
        {
            if (_generalPoolKeys.Count == 0)
            {
                return null;
            }

            if (_generalPoolKeys.Count == 1)
            {
                return _generalPoolKeys[0];
            }

            string candidate;
            var attempts = 0;
            do
            {
                var index = UnityEngine.Random.Range(0, _generalPoolKeys.Count);
                candidate = _generalPoolKeys[index];
                attempts++;
            }
            while (attempts < 4 && string.Equals(candidate, _lastRandomClipKey, StringComparison.OrdinalIgnoreCase));

            _lastRandomClipKey = candidate;
            return candidate;
        }

        /// <summary>
        /// Determines whether a clip should be positioned in the surrounding environment rather than close to the player's ears/neck.
        /// </summary>
        /// <param name="clipKey">The sound key without extension.</param>
        /// <returns><see langword="true"/> if the sound is an environmental knock/door/snap; otherwise <see langword="false"/>.</returns>
        public bool IsEnvironmentalClip(string clipKey)
        {
            return EnvironmentalClipKeys.Contains(clipKey);
        }

        /// <summary>
        /// Plays the reserved <c>welcome.ogg</c> sound for the local player inside the truck at match start.
        /// </summary>
        public void PlayWelcomeSound()
        {
            PlayCloseHallucinationSound(WelcomeClipKey, new Vector3(0.0f, 0.05f, -0.55f), 0.95f);
        }

        /// <summary>
        /// Plays the reserved <c>wake-up.ogg</c> sound exclusively for the local player when crouching.
        /// </summary>
        public void PlayWakeUpSound()
        {
            var side = UnityEngine.Random.value < 0.5f ? -0.45f : 0.45f;
            PlayCloseHallucinationSound(WakeUpClipKey, new Vector3(side, 0.0f, -0.45f), 0.95f);
        }

        /// <summary>
        /// Plays a close whisper/hallucination sound attached to an offset relative to the local player's head/camera.
        /// </summary>
        /// <param name="clipKey">The clip key to play.</param>
        /// <param name="localHeadOffset">Offset in local space of the camera/player head.</param>
        /// <param name="volumeMultiplier">Relative volume multiplier.</param>
        public void PlayCloseHallucinationSound(string clipKey, Vector3 localHeadOffset, float volumeMultiplier = 0.9f)
        {
            if (!_config.Enabled.Value || !_clipsByName.TryGetValue(clipKey, out var clip) || clip == null)
            {
                return;
            }

            var anchorTransform = ResolveLocalListenerTransform();
            if (anchorTransform == null)
            {
                return;
            }

            var emitterObj = new GameObject($"DYST_Whisper_{clipKey}");
            var follower = emitterObj.AddComponent<LocalHeadAudioFollower>();
            follower.Initialize(anchorTransform, localHeadOffset);

            var source = emitterObj.AddComponent<AudioSource>();
            source.clip = clip;
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = string.Equals(clipKey, "8d", StringComparison.OrdinalIgnoreCase) ? 0.35f : 0.85f;
            source.minDistance = 0.25f;
            source.maxDistance = 6.0f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.dopplerLevel = 0.0f;
            source.pitch = UnityEngine.Random.Range(0.96f, 1.03f);
            source.volume = Mathf.Clamp01(_config.MasterVolume.Value * volumeMultiplier);
            source.Play();

            UnityEngine.Object.Destroy(emitterObj, clip.length + 0.5f);
        }

        /// <summary>
        /// Plays a 3D spatial sound at a fixed world position (e.g., footsteps, door creaks, knocks, or shared group whispers).
        /// </summary>
        /// <param name="clipKey">The clip key to play.</param>
        /// <param name="worldPosition">World coordinates where the sound originates.</param>
        /// <param name="volumeMultiplier">Relative volume multiplier.</param>
        public void PlayWorldSpatialSound(string clipKey, Vector3 worldPosition, float volumeMultiplier = 0.95f)
        {
            if (!_config.Enabled.Value || !_clipsByName.TryGetValue(clipKey, out var clip) || clip == null)
            {
                return;
            }

            var emitterObj = new GameObject($"DYST_WorldAudio_{clipKey}");
            emitterObj.transform.position = worldPosition;

            var source = emitterObj.AddComponent<AudioSource>();
            source.clip = clip;
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 1.0f;
            source.minDistance = 1.8f;
            source.maxDistance = 22.0f;
            source.rolloffMode = AudioRolloffMode.Custom;
            source.dopplerLevel = 0.0f;
            source.pitch = UnityEngine.Random.Range(0.95f, 1.04f);
            source.volume = Mathf.Clamp01(_config.MasterVolume.Value * volumeMultiplier);
            source.Play();

            UnityEngine.Object.Destroy(emitterObj, clip.length + 0.5f);
        }

        /// <summary>
        /// Resolves the best transform representing the local player's ears/camera.
        /// </summary>
        /// <returns>The camera or local player transform, or <see langword="null"/> if unavailable.</returns>
        public static Transform? ResolveLocalListenerTransform()
        {
            if (SemiFunc.MainCamera() != null)
            {
                return SemiFunc.MainCamera().transform;
            }

            if (Camera.main != null)
            {
                return Camera.main.transform;
            }

            var localPlayer = SemiFunc.PlayerAvatarLocal();
            if (localPlayer != null)
            {
                return localPlayer.transform;
            }

            return null;
        }

        /// <summary>
        /// Helper component that keeps a close whisper emitter positioned behind/around the local player's head.
        /// </summary>
        private sealed class LocalHeadAudioFollower : MonoBehaviour
        {
            private Transform? _target;
            private Vector3 _localOffset;

            public void Initialize(Transform target, Vector3 localOffset)
            {
                _target = target;
                _localOffset = localOffset;
                UpdatePosition();
            }

            private void LateUpdate()
            {
                UpdatePosition();
            }

            private void UpdatePosition()
            {
                if (_target == null)
                {
                    return;
                }

                transform.position = _target.TransformPoint(_localOffset);
            }
        }
    }
}

