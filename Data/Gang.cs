using System.Collections.Generic;
using System.Drawing;

namespace DynamicHostileTerritories.Data
{
    /// <summary>
    /// Static identity of a gang. Pure data — no behaviour. Services read this to
    /// know which peds, vehicles and colour to use when populating a territory.
    /// </summary>
    public sealed class Gang
    {
        public string Name { get; }
        public Color BlipColor { get; }
        public IReadOnlyList<string> PedModels { get; }
        public IReadOnlyList<string> VehicleModels { get; }

        public Gang(string name, Color blipColor, IReadOnlyList<string> pedModels, IReadOnlyList<string> vehicleModels)
        {
            Name = name;
            BlipColor = blipColor;
            PedModels = pedModels;
            VehicleModels = vehicleModels;
        }
    }
}