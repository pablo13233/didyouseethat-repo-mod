using BepInEx;
using DidYouSeeThatMod.Configuration;
using DidYouSeeThatMod.Patches;
using DidYouSeeThatMod.Services;
using HarmonyLib;
using UnityEngine;

namespace DidYouSeeThatMod
{
    /// <summary>
    /// BepInEx plugin entry point for the "Did You See That?" psychological horror mod.
    /// </summary>
    [BepInPlugin(PluginInfo.PluginGuid, PluginInfo.PluginName, PluginInfo.PluginVersion)]
    public sealed class DidYouSeeThatPlugin : BaseUnityPlugin
    {
        private Harmony? _harmony;
        private HauntDirectorService? _director;

        /// <summary>
        /// Gets the active singleton instance of the plugin.
        /// </summary>
        public static DidYouSeeThatPlugin? Instance { get; private set; }

        /// <summary>
        /// Gets the strongly-typed mod configuration.
        /// </summary>
        public ModConfig? ModConfig { get; private set; }

        private void Awake()
        {
            Instance = this;

            ModConfig = new ModConfig(Config);

            var soundLibrary = new SoundLibraryService(ModConfig, Logger);
            var proximityService = new TeamProximityService(ModConfig);
            var shadowService = new ShadowHallucinationService(ModConfig, Logger);
            _director = new HauntDirectorService(ModConfig, soundLibrary, proximityService, shadowService, Logger);

            PlayerAndLevelPatches.Initialize(_director, ModConfig);

            _harmony = new Harmony(PluginInfo.PluginGuid);
            _harmony.PatchAll(typeof(PlayerAndLevelPatches));

            StartCoroutine(soundLibrary.LoadAllSoundsCoroutine(Info.Location));

            Logger.LogInfo($"{PluginInfo.PluginName} v{PluginInfo.PluginVersion} cargado correctamente.");
        }

        private void Update()
        {
            _director?.Tick(Time.deltaTime);
        }

        private void OnDestroy()
        {
            _director?.Dispose();
            _harmony?.UnpatchSelf();
        }
    }
}

