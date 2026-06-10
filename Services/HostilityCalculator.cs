using DynamicHostileTerritories.Data;
using Rage;

namespace DynamicHostileTerritories.Services
{
    /// <summary>
    /// Turns a territory's continuous state (strength, recent heat) plus the time of
    /// day into a discrete <see cref="HostilityLevel"/>. Pure decision logic: it reads
    /// the territory and the world clock, and returns a level. It never spawns anything.
    /// </summary>
    public sealed class HostilityCalculator
    {
        // Strength thresholds for the base tier.
        private const float PacifiedBelow = 20f;
        private const float WatchfulBelow = 50f;
        private const float AggressiveBelow = 80f;

        // Recent heat above this counts as "something just happened here".
        private const float HotHeatThreshold = 40f;

        public HostilityLevel Evaluate(Territory territory)
        {
            int level = (int)BaseLevelFromStrength(territory.Strength);

            // Night emboldens the gang: bump one tier between 20:00 and 06:00.
            if (IsNight())
                level++;

            // A recent shootout/arrest spikes aggression for a while.
            if (territory.RecentHeat >= HotHeatThreshold)
                level++;

            // A freshly pacified area never escalates, regardless of clock or heat.
            if (territory.Strength < PacifiedBelow)
                return HostilityLevel.Pacified;

            return Clamp(level);
        }

        private static HostilityLevel BaseLevelFromStrength(float strength)
        {
            if (strength < PacifiedBelow) return HostilityLevel.Pacified;
            if (strength < WatchfulBelow) return HostilityLevel.Watchful;
            if (strength < AggressiveBelow) return HostilityLevel.Aggressive;
            return HostilityLevel.Warzone;
        }

        private static bool IsNight()
        {
            int hour = World.TimeOfDay.Hours;
            return hour >= 20 || hour < 6;
        }

        private static HostilityLevel Clamp(int level)
        {
            if (level < (int)HostilityLevel.Pacified) return HostilityLevel.Pacified;
            if (level > (int)HostilityLevel.Warzone) return HostilityLevel.Warzone;
            return (HostilityLevel)level;
        }
    }
}