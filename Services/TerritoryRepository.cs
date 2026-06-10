using System.Collections.Generic;
using System.Drawing;
using DynamicHostileTerritories.Data;
using Rage;

namespace DynamicHostileTerritories.Services
{
    /// <summary>
    /// Builds and holds the catalog of hostile territories. This is the single
    /// source of truth for which areas exist, who controls them and where they are.
    /// It only assembles data; spawning and behaviour live in other services.
    /// </summary>
    public sealed class TerritoryRepository
    {
        private const float DefaultRadius = 150f;
        private const float DefaultStrength = 50f;

        private readonly List<Territory> _territories;

        public IReadOnlyList<Territory> Territories => _territories;

        public TerritoryRepository()
        {
            _territories = BuildTerritories();
        }

        private static List<Territory> BuildTerritories()
        {
            Gang families = new Gang(
                "Families",
                Color.Green,
                new[] { "g_m_y_famca_01", "g_m_y_famdnf_01", "g_m_y_famfor_01" },
                new[] { "voodoo2", "manana", "emperor" });

            Gang ballas = new Gang(
                "Ballas",
                Color.Purple,
                new[] { "g_m_y_ballaeast_01", "g_m_y_ballaorig_01", "g_m_y_ballasout_01" },
                new[] { "buccaneer2", "baller", "oracle" });

            Gang vagos = new Gang(
                "Vagos",
                Color.Yellow,
                new[] { "g_m_y_mexgoon_01", "g_m_y_mexgoon_02", "g_m_y_mexgoon_03" },
                new[] { "tornado", "chino2", "faction2" });

            return new List<Territory>
            {
                new Territory("Grove Street", families, new Vector3(96.0f, -1924.0f, 20.8f), DefaultRadius)
                {
                    Strength = DefaultStrength,
                    Hostility = HostilityLevel.Watchful
                },
                new Territory("Rancho", ballas, new Vector3(366.0f, -2036.0f, 21.0f), DefaultRadius)
                {
                    Strength = DefaultStrength,
                    Hostility = HostilityLevel.Watchful
                },
                new Territory("Cypress Flats", vagos, new Vector3(788.0f, -1290.0f, 26.0f), DefaultRadius)
                {
                    Strength = DefaultStrength,
                    Hostility = HostilityLevel.Watchful
                }
            };
        }
    }
}