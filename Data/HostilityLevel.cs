namespace DynamicHostileTerritories.Data
{
    /// <summary>
    /// The active behaviour tier of a territory. Higher means more aggressive.
    /// A service decides the current level from strength, time of day and recent events.
    /// </summary>
    public enum HostilityLevel
    {
        // Police operations have broken the gang's grip — calm, no aggression.
        Pacified = 0,

        // Level 1: hostile stares, pistols.
        Watchful = 1,

        // Level 2: roadblocks, SMGs.
        Aggressive = 2,

        // Level 3: rifles, ambushes.
        Warzone = 3
    }
}