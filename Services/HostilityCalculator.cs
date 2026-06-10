using DynamicHostileTerritories.Configuration;
using DynamicHostileTerritories.Data;
using Rage;

namespace DynamicHostileTerritories.Services
{
    /// <summary>
    /// Turns a territory's continuous state (strength, recent heat) plus the time of
    /// day into a discrete <see cref="HostilityLevel"/>. All thresholds, the night
    /// bump and the maximum tier come from the .ini, so hostility is fully tunable.
    /// Pure decision logic: it never spawns anything.
    /// </summary>
    public sealed class HostilityCalculator
    {
        private readonly PluginSettings _settings;

        public HostilityCalculator(PluginSettings settings)
        {
            _settings = settings;
        }

        public HostilityLevel Evaluate(Territory territory)
        {
            if (territory.Strength < _settings.PacifiedBelow)
                return HostilityLevel.Pacified;

            int level = (int)BaseLevelFromStrength(territory.Strength);

            if (_settings.NightEscalation && IsNight())
                level++;

            if (territory.RecentHeat >= _settings.HeatThreshold)
                level++;

            return Clamp(level);
        }

        private HostilityLevel BaseLevelFromStrength(float strength)
        {
            if (strength < _settings.PacifiedBelow) return HostilityLevel.Pacified;
            if (strength < _settings.WatchfulBelow) return HostilityLevel.Watchful;
            if (strength < _settings.AggressiveBelow) return HostilityLevel.Aggressive;
            return HostilityLevel.Warzone;
        }

        private static bool IsNight()
        {
            int hour = World.TimeOfDay.Hours;
            return hour >= 20 || hour < 6;
        }

        private HostilityLevel Clamp(int level)
        {
            int max = _settings.MaxHostility;
            if (max > (int)HostilityLevel.Warzone) max = (int)HostilityLevel.Warzone;

            if (level < (int)HostilityLevel.Pacified) level = (int)HostilityLevel.Pacified;
            if (level > max) level = max;

            return (HostilityLevel)level;
        }
    }
}