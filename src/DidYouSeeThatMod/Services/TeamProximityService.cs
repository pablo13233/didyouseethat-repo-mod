using System;
using DidYouSeeThatMod.Configuration;
using DidYouSeeThatMod.Internal;
using UnityEngine;

namespace DidYouSeeThatMod.Services
{
    /// <summary>
    /// Evaluates how isolated the local player is from living teammates to scale audio and shadow hallucination frequency.
    /// </summary>
    public sealed class TeamProximityService
    {
        private const float CloseTeammateDistanceMeters = 5.5f;
        private readonly ModConfig _config;

        /// <summary>
        /// Initializes a new instance of the <see cref="TeamProximityService"/> class.
        /// </summary>
        /// <param name="config">Mod configuration instance.</param>
        public TeamProximityService(ModConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        /// <summary>
        /// Determines whether the local player is alive and currently inside a playable level or shop.
        /// </summary>
        /// <param name="localPlayer">Outputs the local <see cref="PlayerAvatar"/> when valid.</param>
        /// <returns><see langword="true"/> if the local player is active and alive in gameplay; otherwise <see langword="false"/>.</returns>
        public static bool TryGetActiveLocalPlayer(out PlayerAvatar localPlayer)
        {
            localPlayer = SemiFunc.PlayerAvatarLocal();
            if (localPlayer == null || PlayerAvatarAccess.IsDead(localPlayer) || PlayerAvatarAccess.IsDisabled(localPlayer))
            {
                return false;
            }

            if (SemiFunc.IsMainMenu() || SemiFunc.IsSplashScreen() || SemiFunc.RunIsLobbyMenu())
            {
                return false;
            }

            return SemiFunc.LevelGenDone();
        }

        /// <summary>
        /// Calculates the distance in meters to the nearest living teammate, or <see cref="float.PositiveInfinity"/> if none exist.
        /// </summary>
        /// <param name="localPlayer">The local player avatar.</param>
        /// <param name="totalOtherPlayers">Outputs the count of other players in the session.</param>
        /// <param name="livingTeammates">Outputs the count of living teammates.</param>
        /// <returns>Distance in meters to the nearest living teammate.</returns>
        public float GetNearestLivingTeammateDistance(PlayerAvatar localPlayer, out int totalOtherPlayers, out int livingTeammates)
        {
            totalOtherPlayers = 0;
            livingTeammates = 0;

            if (localPlayer == null)
            {
                return float.PositiveInfinity;
            }

            var players = SemiFunc.PlayerGetList();
            if (players == null || players.Count <= 1)
            {
                return float.PositiveInfinity;
            }

            var localPos = localPlayer.transform.position;
            var minDistance = float.PositiveInfinity;

            for (var i = 0; i < players.Count; i++)
            {
                var other = players[i];
                if (other == null || other == localPlayer || PlayerAvatarAccess.IsLocal(other))
                {
                    continue;
                }

                totalOtherPlayers++;
                if (PlayerAvatarAccess.IsDead(other) || PlayerAvatarAccess.IsDisabled(other))
                {
                    continue;
                }

                livingTeammates++;
                var dist = Vector3.Distance(localPos, other.transform.position);
                if (dist < minDistance)
                {
                    minDistance = dist;
                }
            }

            return minDistance;
        }

        /// <summary>
        /// Computes an isolation factor where <c>0.0</c> means right next to teammates,
        /// <c>1.0</c> means isolated beyond <see cref="ModConfig.IsolationDistanceThreshold"/>,
        /// and up to <c>1.3</c> when extremely far from the group.
        /// </summary>
        /// <param name="localPlayer">The local player avatar.</param>
        /// <returns>Isolation factor in the range <c>[0.0, 1.3]</c>.</returns>
        public float GetIsolationFactor(PlayerAvatar localPlayer)
        {
            var nearestDist = GetNearestLivingTeammateDistance(localPlayer, out var totalOthers, out var livingTeammates);
            if (totalOthers == 0)
            {
                // Singleplayer session: moderate isolation so events occur at an engaging pace.
                return 0.45f;
            }

            if (livingTeammates == 0)
            {
                // Last survivor in multiplayer: high paranoia.
                return 1.15f;
            }

            var threshold = Mathf.Max(CloseTeammateDistanceMeters + 2.0f, _config.IsolationDistanceThreshold.Value);
            if (nearestDist <= CloseTeammateDistanceMeters)
            {
                return 0.0f;
            }

            var normalized = (nearestDist - CloseTeammateDistanceMeters) / (threshold - CloseTeammateDistanceMeters);
            return Mathf.Clamp(normalized, 0.0f, 1.3f);
        }

        /// <summary>
        /// Computes the target interval in seconds for the next random audio hallucination based on team distance.
        /// </summary>
        /// <param name="isolationFactor">Current isolation factor (<c>0.0</c> to <c>1.3</c>).</param>
        /// <returns>Target audio interval in seconds (e.g., ~80s near team, ~40s far from team).</returns>
        public float GetTargetAudioIntervalSeconds(float isolationFactor)
        {
            var near = _config.AudioIntervalNearTeamSeconds.Value;
            var far = _config.AudioIntervalFarFromTeamSeconds.Value;
            return Mathf.Lerp(near, far, Mathf.Clamp01(isolationFactor));
        }

        /// <summary>
        /// Computes the target interval in seconds for the next shadow sighting based on team distance.
        /// The farther the player is from the group, the shorter the interval between shadows.
        /// </summary>
        /// <param name="isolationFactor">Current isolation factor (<c>0.0</c> to <c>1.3</c>).</param>
        /// <returns>Target shadow interval in seconds.</returns>
        public float GetTargetShadowIntervalSeconds(float isolationFactor)
        {
            var near = _config.ShadowIntervalNearTeamSeconds.Value;
            var far = _config.ShadowIntervalFarFromTeamSeconds.Value;
            var baseInterval = Mathf.Lerp(near, far, Mathf.Clamp01(isolationFactor));

            if (isolationFactor > 1.0f)
            {
                // Extra acceleration when extremely far from the group.
                var extra = isolationFactor - 1.0f;
                baseInterval *= Mathf.Lerp(1.0f, 0.78f, Mathf.Clamp01(extra / 0.3f));
            }

            return Mathf.Max(12.0f, baseInterval);
        }
    }
}

