using DidYouSeeThatMod.Configuration;
using DidYouSeeThatMod.Internal;
using DidYouSeeThatMod.Services;
using HarmonyLib;
using UnityEngine;

namespace DidYouSeeThatMod.Patches
{
    /// <summary>
    /// Harmony patches for <see cref="PlayerAvatar"/>, <see cref="LevelGenerator"/>, and <see cref="EnvironmentDirector"/>
    /// to hook match-start vehicle spawns (<c>welcome.ogg</c>), crouch actions (<c>wake-up.ogg</c>), and fog visibility distance.
    /// </summary>
    [HarmonyPatch]
    internal static class PlayerAndLevelPatches
    {
        private static HauntDirectorService? _director;
        private static ModConfig? _config;

        /// <summary>
        /// Initializes the Harmony patch callbacks with the active <see cref="HauntDirectorService"/> and <see cref="ModConfig"/>.
        /// </summary>
        /// <param name="director">The haunt director service instance.</param>
        /// <param name="config">The mod configuration instance.</param>
        public static void Initialize(HauntDirectorService director, ModConfig config)
        {
            _director = director;
            _config = config;
        }

        [HarmonyPatch(typeof(EnvironmentDirector), "FogLogic")]
        [HarmonyPostfix]
        private static void EnvironmentDirector_FogLogic_Postfix()
        {
            if (_config == null || !_config.Enabled.Value)
            {
                return;
            }

            var mult = _config.FogDistanceMultiplier.Value;
            if (mult <= 1.01f)
            {
                return;
            }

            RenderSettings.fogStartDistance *= mult;
            RenderSettings.fogEndDistance *= mult;

            var cam = SemiFunc.MainCamera();
            if (cam != null && cam.farClipPlane < RenderSettings.fogEndDistance + 2.0f)
            {
                cam.farClipPlane = RenderSettings.fogEndDistance + 2.0f;
            }
        }

        [HarmonyPatch(typeof(LevelGenerator), "Start")]
        [HarmonyPostfix]
        private static void LevelGenerator_Start_Postfix()
        {
            _director?.OnNewLevelStarted();
        }

        [HarmonyPatch(typeof(LevelGenerator), "GenerateDoneApply")]
        [HarmonyPostfix]
        private static void LevelGenerator_GenerateDoneApply_Postfix()
        {
            _director?.NotifyPlayerSpawnedInVehicle();
        }

        [HarmonyPatch(typeof(PlayerAvatar), nameof(PlayerAvatar.LoadingLevelAnimationCompleted))]
        [HarmonyPostfix]
        private static void PlayerAvatar_LoadingLevelAnimationCompleted_Postfix(PlayerAvatar __instance)
        {
            if (__instance != null && PlayerAvatarAccess.IsLocal(__instance))
            {
                _director?.NotifyPlayerSpawnedInVehicle();
            }
        }

        [HarmonyPatch(typeof(PlayerAvatar), nameof(PlayerAvatar.StandToCrouch))]
        [HarmonyPostfix]
        private static void PlayerAvatar_StandToCrouch_Postfix(PlayerAvatar __instance)
        {
            if (__instance != null && PlayerAvatarAccess.IsLocal(__instance) && !PlayerAvatarAccess.IsDead(__instance))
            {
                _director?.OnLocalPlayerCrouched();
            }
        }
    }
}

