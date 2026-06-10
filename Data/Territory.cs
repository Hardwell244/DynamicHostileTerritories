using System;
using Rage;

namespace DynamicHostileTerritories.Data
{
    /// <summary>
    /// A hostile territory: a circular area controlled by a gang. Holds both its
    /// static definition (set once) and its runtime state (mutated by services).
    /// Pure data — no behaviour lives here.
    /// </summary>
    public sealed class Territory
    {
        // --- Static definition: set on creation, never changes at runtime. ---
        public string Name { get; }
        public Gang ControllingGang { get; }
        public Vector3 Center { get; }
        public float Radius { get; }

        // --- Persisted runtime state (saved to / loaded from JSON). ---

        // 0..100. The gang's grip on the area. Decays when ignored, grows over time,
        // drops after successful police operations.
        public float Strength { get; set; }

        // When the last successful police action happened here, for temporary weakening.
        public DateTime LastPoliceActionUtc { get; set; }

        // --- Transient runtime state (recomputed, not persisted). ---

        // The active behaviour tier, recalculated each update by the hostility calculator.
        public HostilityLevel Hostility { get; set; }

        // 0..100. Short-lived "heat" raised by recent shootouts/arrests in the area,
        // which temporarily pushes hostility up. Decays back to zero over time.
        public float RecentHeat { get; set; }

        public Territory(string name, Gang controllingGang, Vector3 center, float radius)
        {
            Name = name;
            ControllingGang = controllingGang;
            Center = center;
            Radius = radius;
        }
    }
}