using System;
using BepInEx.Configuration;

namespace DidYouSeeThatMod.Configuration
{
    /// <summary>
    /// Strongly-typed configuration container for the Did You See That? mod.
    /// </summary>
    public sealed class ModConfig
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ModConfig"/> class and binds entries to the provided <see cref="ConfigFile"/>.
        /// </summary>
        /// <param name="config">The BepInEx configuration file associated with the plugin.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="config"/> is <see langword="null"/>.</exception>
        public ModConfig(ConfigFile config)
        {
            if (config is null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            Enabled = config.Bind(
                "General",
                "Enabled",
                true,
                "Activa o desactiva por completo los efectos de alucinación, susurros y sombras.");

            MasterVolume = config.Bind(
                "Audio",
                "MasterVolume",
                0.85f,
                new ConfigDescription(
                    "Volumen general de los sonidos de alucinación y susurros (0.0 a 1.0).",
                    new AcceptableValueRange<float>(0.05f, 1.0f)));

            WelcomeSoundChance = config.Bind(
                "SpecialSounds",
                "WelcomeSoundChance",
                0.25f,
                new ConfigDescription(
                    "Probabilidad (0.25 = 25%) de que suene 'welcome.ogg' a 1 o varios miembros del equipo únicamente al iniciar la partida en el vehículo.",
                    new AcceptableValueRange<float>(0.0f, 1.0f)));

            WakeUpCrouchChance = config.Bind(
                "SpecialSounds",
                "WakeUpCrouchChance",
                0.10f,
                new ConfigDescription(
                    "Probabilidad (0.10 = 10%) de que suene 'wake-up.ogg' exclusivamente para el jugador que se agache.",
                    new AcceptableValueRange<float>(0.0f, 1.0f)));

            WakeUpCooldownSeconds = config.Bind(
                "SpecialSounds",
                "WakeUpCooldownSeconds",
                8.0f,
                new ConfigDescription(
                    "Tiempo mínimo en segundos entre activaciones de 'wake-up.ogg' al agacharse repetidamente.",
                    new AcceptableValueRange<float>(1.0f, 120.0f)));

            AudioIntervalNearTeamSeconds = config.Bind(
                "AudioSchedule",
                "AudioIntervalNearTeamSeconds",
                80.0f,
                new ConfigDescription(
                    "Tiempo promedio (en segundos) entre sonidos aleatorios cuando el jugador está acompañado por su equipo.",
                    new AcceptableValueRange<float>(15.0f, 300.0f)));

            AudioIntervalFarFromTeamSeconds = config.Bind(
                "AudioSchedule",
                "AudioIntervalFarFromTeamSeconds",
                40.0f,
                new ConfigDescription(
                    "Tiempo promedio (en segundos) entre sonidos aleatorios cuando el jugador está lejos del equipo / aislado.",
                    new AcceptableValueRange<float>(10.0f, 240.0f)));

            ShadowIntervalNearTeamSeconds = config.Bind(
                "Shadows",
                "ShadowIntervalNearTeamSeconds",
                80.0f,
                new ConfigDescription(
                    "Tiempo promedio (en segundos) entre apariciones de sombras cuando el jugador está cerca del grupo.",
                    new AcceptableValueRange<float>(15.0f, 300.0f)));

            ShadowIntervalFarFromTeamSeconds = config.Bind(
                "Shadows",
                "ShadowIntervalFarFromTeamSeconds",
                38.0f,
                new ConfigDescription(
                    "Tiempo promedio (en segundos) entre apariciones de sombras cuando el jugador está alejado del grupo (mientras más lejos, más frecuente).",
                    new AcceptableValueRange<float>(10.0f, 240.0f)));

            ShadowDurationMinSeconds = config.Bind(
                "Shadows",
                "ShadowDurationMinSeconds",
                2.0f,
                new ConfigDescription(
                    "Duración mínima (en segundos) antes de que la sombra desaparezca (2.0s por defecto).",
                    new AcceptableValueRange<float>(1.0f, 10.0f)));

            ShadowDurationMaxSeconds = config.Bind(
                "Shadows",
                "ShadowDurationMaxSeconds",
                2.3f,
                new ConfigDescription(
                    "Duración máxima (en segundos) para sombras que cruzan un pasillo a la distancia.",
                    new AcceptableValueRange<float>(1.2f, 15.0f)));

            ShadowDirectGazeHoldSeconds = config.Bind(
                "Shadows",
                "ShadowDirectGazeHoldSeconds",
                2.0f,
                new ConfigDescription(
                    "Tiempo (en segundos) que la sombra permanece visible antes de desvanecerse.",
                    new AcceptableValueRange<float>(0.5f, 5.0f)));

            FogDistanceMultiplier = config.Bind(
                "Shadows",
                "FogDistanceMultiplier",
                1.35f,
                new ConfigDescription(
                    "Multiplicador de distancia del fog (niebla) y cámara del juego (1.35 = +35% de visión en pasillos para poder ver las sombras a lo lejos).",
                    new AcceptableValueRange<float>(1.0f, 2.5f)));

            IsolationDistanceThreshold = config.Bind(
                "Proximity",
                "IsolationDistanceThreshold",
                18.0f,
                new ConfigDescription(
                    "Distancia en metros al compañero vivo más cercano a partir de la cual se considera que el jugador está lejos del equipo.",
                    new AcceptableValueRange<float>(6.0f, 60.0f)));

            GroupEventChance = config.Bind(
                "Multiplayer",
                "GroupEventChance",
                0.35f,
                new ConfigDescription(
                    "Probabilidad base (0.35 = 35%) de que un evento de sonido o sombra sea grupal (para el equipo) en vez de personal.",
                    new AcceptableValueRange<float>(0.0f, 1.0f)));
        }

        /// <summary>
        /// Gets the configuration entry indicating whether the mod is enabled.
        /// </summary>
        public ConfigEntry<bool> Enabled { get; }

        /// <summary>
        /// Gets the master volume configuration for hallucination sounds.
        /// </summary>
        public ConfigEntry<float> MasterVolume { get; }

        /// <summary>
        /// Gets the probability of triggering <c>welcome.ogg</c> at match start inside the truck.
        /// </summary>
        public ConfigEntry<float> WelcomeSoundChance { get; }

        /// <summary>
        /// Gets the probability of triggering <c>wake-up.ogg</c> locally when crouching.
        /// </summary>
        public ConfigEntry<float> WakeUpCrouchChance { get; }

        /// <summary>
        /// Gets the cooldown in seconds between <c>wake-up.ogg</c> activations.
        /// </summary>
        public ConfigEntry<float> WakeUpCooldownSeconds { get; }

        /// <summary>
        /// Gets the base interval in seconds between random sounds when near the team.
        /// </summary>
        public ConfigEntry<float> AudioIntervalNearTeamSeconds { get; }

        /// <summary>
        /// Gets the faster interval in seconds between random sounds when isolated from the team.
        /// </summary>
        public ConfigEntry<float> AudioIntervalFarFromTeamSeconds { get; }

        /// <summary>
        /// Gets the base interval in seconds between shadow sightings when near the team.
        /// </summary>
        public ConfigEntry<float> ShadowIntervalNearTeamSeconds { get; }

        /// <summary>
        /// Gets the faster interval in seconds between shadow sightings when isolated from the team.
        /// </summary>
        public ConfigEntry<float> ShadowIntervalFarFromTeamSeconds { get; }

        /// <summary>
        /// Gets the minimum duration in seconds for a shadow sighting.
        /// </summary>
        public ConfigEntry<float> ShadowDurationMinSeconds { get; }

        /// <summary>
        /// Gets the maximum duration in seconds for a shadow sighting.
        /// </summary>
        public ConfigEntry<float> ShadowDurationMaxSeconds { get; }

        /// <summary>
        /// Gets the time in seconds a shadow remains visible after the player looks directly at it.
        /// </summary>
        public ConfigEntry<float> ShadowDirectGazeHoldSeconds { get; }

        /// <summary>
        /// Gets the multiplier applied to the game's fog end distance and camera far clip plane.
        /// </summary>
        public ConfigEntry<float> FogDistanceMultiplier { get; }

        /// <summary>
        /// Gets the distance in meters to the nearest living teammate at which isolation acceleration reaches its target.
        /// </summary>
        public ConfigEntry<float> IsolationDistanceThreshold { get; }

        /// <summary>
        /// Gets the probability that a random audio or shadow hallucination is shared with the team.
        /// </summary>
        public ConfigEntry<float> GroupEventChance { get; }
    }
}

