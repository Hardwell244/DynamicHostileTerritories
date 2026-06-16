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
        public float StrengthRegrowthPerHour { get; private set; }
        public float SuppressionHours { get; private set; }

        // --- Encounter / escalation ---
        public float SuspicionDelaySeconds { get; private set; }
        public float TierRecheckSeconds { get; private set; }

        // --- Hostility tuning (drives HostilityCalculator) ---
        public float PacifiedBelow { get; private set; }
        public float WatchfulBelow { get; private set; }
        public float AggressiveBelow { get; private set; }
        public float HeatThreshold { get; private set; }
        public bool NightEscalation { get; private set; }
        public int MaxHostility { get; private set; } // 0=Pacified .. 3=Warzone

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

                s.ActivationDistance = ini.ReadSingle("Performance", "ActivationDistance", 150f);
                s.MaxSpawnedPeds = ini.ReadInt32("Performance", "MaxSpawnedPeds", 10);
                s.UpdateIntervalMs = ini.ReadInt32("Performance", "UpdateIntervalMs", 750);

                s.StrengthRegrowthPerHour = ini.ReadSingle("Territory", "StrengthRegrowthPerHour", 4f);
                s.SuppressionHours = ini.ReadSingle("Territory", "SuppressionHours", 6f);

                s.SuspicionDelaySeconds = ini.ReadSingle("Encounter", "SuspicionDelaySeconds", 12f);
                s.TierRecheckSeconds = ini.ReadSingle("Encounter", "TierRecheckSeconds", 30f);

                s.PacifiedBelow = ini.ReadSingle("Hostility", "PacifiedBelow", 20f);
                s.WatchfulBelow = ini.ReadSingle("Hostility", "WatchfulBelow", 50f);
                s.AggressiveBelow = ini.ReadSingle("Hostility", "AggressiveBelow", 80f);
                s.HeatThreshold = ini.ReadSingle("Hostility", "HeatThreshold", 40f);
                s.NightEscalation = ini.ReadBoolean("Hostility", "NightEscalation", true);
                s.MaxHostility = ini.ReadInt32("Hostility", "MaxHostility", 3);

                string menuKeyText = ini.ReadString("Interaction", "MenuKey", "F7");
                s.MenuKey = Enum.TryParse(menuKeyText, true, out Keys parsedKey) ? parsedKey : Keys.F7;

                Game.LogTrivial("[DHT] Configuration loaded from \"" + fullPath + "\".");
            }
            catch (Exception ex)
            {
                Game.LogTrivial("[DHT] Failed to read configuration, falling back to defaults: " + ex);
                s.Enabled = true;
                s.DebugLogging = false;
                s.ActivationDistance = 150f;
                s.MaxSpawnedPeds = 10;
                s.UpdateIntervalMs = 1500;
                s.StrengthRegrowthPerHour = 4f;
                s.SuppressionHours = 6f;
                s.SuspicionDelaySeconds = 12f;
                s.TierRecheckSeconds = 30f;
                s.PacifiedBelow = 20f;
                s.WatchfulBelow = 50f;
                s.AggressiveBelow = 80f;
                s.HeatThreshold = 40f;
                s.NightEscalation = true;
                s.MaxHostility = 3;
                s.MenuKey = Keys.F7;
            }

            if (s.ActivationDistance < 50f) s.ActivationDistance = 50f;
            if (s.MaxSpawnedPeds < 0) s.MaxSpawnedPeds = 0;
            if (s.UpdateIntervalMs < 250) s.UpdateIntervalMs = 250;
            if (s.SuspicionDelaySeconds < 0f) s.SuspicionDelaySeconds = 0f;
            if (s.TierRecheckSeconds < 5f) s.TierRecheckSeconds = 5f;
            if (s.MaxHostility < 0) s.MaxHostility = 0;
            if (s.MaxHostility > 3) s.MaxHostility = 3;

            return s;
        }
    }
}