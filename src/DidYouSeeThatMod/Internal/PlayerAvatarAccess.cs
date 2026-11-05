using HarmonyLib;

namespace DidYouSeeThatMod.Internal
{
    /// <summary>
    /// Provides fast, cached access to non-public fields on <see cref="PlayerAvatar"/>.
    /// </summary>
    internal static class PlayerAvatarAccess
    {
        private static readonly AccessTools.FieldRef<PlayerAvatar, bool> IsLocalRef =
            AccessTools.FieldRefAccess<PlayerAvatar, bool>("isLocal");

        private static readonly AccessTools.FieldRef<PlayerAvatar, bool> DeadSetRef =
            AccessTools.FieldRefAccess<PlayerAvatar, bool>("deadSet");

        private static readonly AccessTools.FieldRef<PlayerAvatar, bool> IsDisabledRef =
            AccessTools.FieldRefAccess<PlayerAvatar, bool>("isDisabled");

        public static bool IsLocal(PlayerAvatar avatar)
        {
            return avatar != null && (IsLocalRef(avatar) || avatar == SemiFunc.PlayerAvatarLocal());
        }

        public static bool IsDead(PlayerAvatar avatar)
        {
            return avatar == null || DeadSetRef(avatar);
        }

        public static bool IsDisabled(PlayerAvatar avatar)
        {
            return avatar == null || IsDisabledRef(avatar);
        }
    }
}

