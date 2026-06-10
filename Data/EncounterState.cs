namespace DynamicHostileTerritories.Data
{
    /// <summary>
    /// The live posture of the gang during an encounter, escalating as the player
    /// behaves more threateningly. Separate from HostilityLevel (the area's tier):
    /// the tier sets how many gangsters and what weapons; this sets what they DO.
    /// </summary>
    public enum EncounterState
    {
        Observing = 0,  // they notice and watch, no aggression
        Suspicious = 1, // they shadow / follow the player
        Provoked = 2,   // weapons ready, holding defensive positions
        War = 3         // open fire from cover
    }
}