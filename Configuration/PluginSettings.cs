using System;
using System.IO;
using System.Windows.Forms;
using Rage;

namespace DynamicHostileTerritories.Configuration
{
    /// <summary>
    /// Reads and exposes the plugin's configuration from the .ini file.
    /// Fully decoupled from gameplay: it only turns the .ini into strongly-typed,
    /// read-only settings. Missing file or keys fall back to safe defaults, so the
    /// plugin never fails to start because of a bad config.
    /// </summary>
    public sealed class PluginSettings
    {
        private const string ConfigFolder = @"Plugins\LSPDFR\DynamicHostileTerritories";
        private const string ConfigFileName = "DynamicHostileTerritories.ini";

        // --- General ---
        public bool Enabled { get; private set; }
        public bool DebugLogging { get; private set; }

        // --- Performance ---
        public float ActivationDistance { get; private set; }
        public int MaxSpawnedPeds { get; private set; }
        public int UpdateIntervalMs { get; private set; }

        // --- Territory control ---
        public float PoliceActionStrengthDrop { get; private set; }
        public float StrengthRegrowthPerHour { get; private set; }
        public float SuppressionHours { get; private set; }

        // --- Encounter / escalation ---
        // Seconds the player can linger before the gang starts shadowing them.
        public float SuspicionDelaySeconds { get; private set; }
        // How often (seconds) the area tier is re-evaluated while the player stays inside.
        public float TierRecheckSeconds { get; private set; }

        // --- Interaction ---
        public Keys MenuKey { get; private set; }

        private PluginSettings() { }

        public static PluginSettings Load()
        {
            string fullPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                ConfigFolder,
                ConfigFileName);

            var s = new PluginSettings();

            try
            {
                InitializationFile ini = new InitializationFile(fullPath);
                ini.Create();

                s.Enabled = ini.ReadBoolean("General", "Enabled", true);
                s.DebugLogging = ini.ReadBoolean("General", "DebugLogging", false);

                s.ActivationDistance = ini.ReadSingle("Performance", "ActivationDistance", 250f);
                s.MaxSpawnedPeds = ini.ReadInt32("Performance", "MaxSpawnedPeds", 6);
                s.UpdateIntervalMs = ini.ReadInt32("Performance", "UpdateIntervalMs", 1500);

                s.PoliceActionStrengthDrop = ini.ReadSingle("Territory", "PoliceActionStrengthDrop", 15f);
                s.StrengthRegrowthPerHour = ini.ReadSingle("Territory", "StrengthRegrowthPerHour", 4f);
                s.SuppressionHours = ini.ReadSingle("Territory", "SuppressionHours", 6f);

                s.SuspicionDelaySeconds = ini.ReadSingle("Encounter", "SuspicionDelaySeconds", 25f);
                s.TierRecheckSeconds = ini.ReadSingle("Encounter", "TierRecheckSeconds", 30f);

                string menuKeyText = ini.ReadString("Interaction", "MenuKey", "F7");
                s.MenuKey = Enum.TryParse(menuKeyText, true, out Keys parsedKey) ? parsedKey : Keys.F7;

                Game.LogTrivial("[DHT] Configuration loaded from \"" + fullPath + "\".");
            }
            catch (Exception ex)
            {
                Game.LogTrivial("[DHT] Failed to read configuration, falling back to defaults: " + ex);
                s.Enabled = true;
                s.DebugLogging = false;
                s.ActivationDistance = 250f;
                s.MaxSpawnedPeds = 6;
                s.UpdateIntervalMs = 1500;
                s.PoliceActionStrengthDrop = 15f;
                s.StrengthRegrowthPerHour = 4f;
                s.SuppressionHours = 6f;
                s.SuspicionDelaySeconds = 25f;
                s.TierRecheckSeconds = 30f;
                s.MenuKey = Keys.F7;
            }

            // Guard against nonsensical values.
            if (s.ActivationDistance < 50f) s.ActivationDistance = 50f;
            if (s.MaxSpawnedPeds < 0) s.MaxSpawnedPeds = 0;
            if (s.UpdateIntervalMs < 250) s.UpdateIntervalMs = 250;
            if (s.SuspicionDelaySeconds < 0f) s.SuspicionDelaySeconds = 0f;
            if (s.TierRecheckSeconds < 5f) s.TierRecheckSeconds = 5f;

            return s;
        }
    }
}