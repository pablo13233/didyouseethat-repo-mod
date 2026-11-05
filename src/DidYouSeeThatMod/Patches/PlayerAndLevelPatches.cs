using DidYouSeeThatMod.Internal;
using DidYouSeeThatMod.Services;
using HarmonyLib;

namespace DidYouSeeThatMod.Patches
{
    /// <summary>
    /// Harmony patches for <see cref="PlayerAvatar"/> and <see cref="LevelGenerator"/> to hook
    /// match-start vehicle spawns (<c>welcome.ogg</c>) and crouch actions (<c>wake-up.ogg</c>).
    /// </summary>
    [HarmonyPatch]
    internal static class PlayerAndLevelPatches
    {
        private static HauntDirectorService? _director;

        /// <summary>
        /// Initializes the Harmony patch callbacks with the active <see cref="HauntDirectorService"/>.
        /// </summary>
        /// <param name="director">The haunt director service instance.</param>
        public static void Initialize(HauntDirectorService director)
        {
            _director = director;
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

