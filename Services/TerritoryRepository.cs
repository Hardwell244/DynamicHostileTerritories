using System.Collections.Generic;
using System.Drawing;
using DynamicHostileTerritories.Data;
using Rage;

namespace DynamicHostileTerritories.Services
{
    /// <summary>
    /// Builds and holds the catalog of hostile territories. Single source of truth for
    /// which areas exist, who controls them and where they are. Coordinates come from
    /// in-game readings; rename the territories or tweak coordinates freely here.
    /// It only assembles data; spawning and behaviour live in other services.
    /// </summary>
    public sealed class TerritoryRepository
    {
        private const float DefaultRadius = 120f;
        private const float DefaultStrength = 50f;

        private readonly List<Territory> _territories;

        public IReadOnlyList<Territory> Territories => _territories;

        public TerritoryRepository()
        {
            _territories = BuildTerritories();
        }

        private static Territory Make(string name, Gang gang, float x, float y, float z)
        {
            return new Territory(name, gang, new Vector3(x, y, z), DefaultRadius)
            {
                Strength = DefaultStrength,
                Hostility = HostilityLevel.Watchful
            };
        }

        private static List<Territory> BuildTerritories()
        {
            Gang families = new Gang("Families", Color.Green,
                new[] { "g_m_y_famca_01", "g_m_y_famdnf_01", "g_m_y_famfor_01" },
                new[] { "voodoo2", "manana", "emperor" });

            Gang ballas = new Gang("Ballas", Color.Purple,
                new[] { "g_m_y_ballaeast_01", "g_m_y_ballaorig_01", "g_m_y_ballasout_01" },
                new[] { "buccaneer2", "baller", "oracle" });

            Gang vagos = new Gang("Vagos", Color.Yellow,
                new[] { "g_m_y_mexgoon_01", "g_m_y_mexgoon_02", "g_m_y_mexgoon_03" },
                new[] { "tornado", "chino2", "faction2" });

            Gang marabunta = new Gang("Marabunta Grande", Color.Teal,
                new[] { "g_m_y_salvaboss_01", "g_m_y_salvagoon_01", "g_m_y_salvagoon_02", "g_m_y_salvagoon_03" },
                new[] { "manana", "tornado", "primo" });

            Gang kkangpae = new Gang("Kkangpae", Color.LightGray,
                new[] { "g_m_m_korboss_01", "g_m_y_korean_01", "g_m_y_korean_02", "g_m_y_korlieut_01" },
                new[] { "kuruma", "sultan", "dilettante" });

            Gang armenian = new Gang("Armenian Mob", Color.DarkRed,
                new[] { "g_m_m_armboss_01", "g_m_m_armlieut_01", "g_m_y_armgoon_01", "g_m_y_armgoon_02" },
                new[] { "schafter2", "oracle", "cognoscenti" });

            // Base GTA V has no dedicated cartel ped set; Mexican goon models stand in.
            Gang madrazo = new Gang("Madrazo Cartel", Color.Orange,
                new[] { "g_m_y_mexgoon_01", "g_m_y_mexgoon_02", "g_m_y_mexgoon_03" },
                new[] { "cavalcade", "baller", "granger" });

            Gang aztecas = new Gang("Varrios Los Aztecas", Color.Blue,
                new[] { "g_m_y_azteca_01" },
                new[] { "tornado", "chino", "voodoo" });

            Gang lost = new Gang("The Lost MC", Color.DimGray,
                new[] { "g_m_y_lost_01", "g_m_y_lost_02", "g_m_y_lost_03" },
                new[] { "daemon", "hexer", "sovereign" });

            Gang rednecks = new Gang("Rednecks", Color.SaddleBrown,
                new[] { "a_m_m_hillbilly_01", "a_m_m_hillbilly_02" },
                new[] { "rebel2", "bodhi2", "sadler" });

            return new List<Territory>
            {
                Make("Ballas Turf 1", ballas, 101.0282f, -1938.165f, 20.23903f),
                Make("Ballas Turf 2", ballas, -23.34521f, -1826.804f, 25.11959f),

                Make("Vagos Turf 1", vagos, 292.8668f, -2000.102f, 19.80942f),
                Make("Vagos Turf 2", vagos, 490.5248f, -1779.046f, 27.85685f),
                Make("Vagos Turf 3", vagos, 944.0488f, -1854.581f, 30.53767f),

                Make("Marabunta Turf 1", marabunta, 1283.708f, -1734.576f, 51.98082f),
                Make("Marabunta Turf 2", marabunta, 1212.528f, -1631.301f, 46.61679f),
                Make("Marabunta Turf 3", marabunta, -1121.539f, -1562.336f, 3.701513f),

                Make("Kkangpae Turf 1", kkangpae, -754.5678f, -920.0692f, 18.44455f),

                Make("Armenian Turf 1", armenian, -605.262f, -1797.159f, 22.90413f),

                Make("Madrazo Turf 1", madrazo, 1370.042f, 1146.712f, 113.1948f),

                Make("Aztecas Turf 1", aztecas, 1889.336f, 3821.397f, 31.74322f),
                Make("Aztecas Turf 2", aztecas, -214.2826f, 6428.561f, 30.86472f),
                Make("Aztecas Turf 3", aztecas, -3228.169f, 1085.752f, 10.16032f),

                Make("Families Turf 1", families, -14.71192f, -1457.348f, 29.88582f),
                Make("Families Turf 2", families, -179.4559f, -1589.602f, 34.08165f),

                Make("Lost MC Turf 1", lost, 971.0652f, -126.2237f, 73.77003f),
                Make("Lost MC Turf 2", lost, 72.5417f, 3710.01f, 39.75491f),
                Make("Lost MC Turf 3", lost, -2199.119f, 4295.706f, 47.94391f),

                Make("Rednecks Turf 1", rednecks, 952.0675f, 3618.137f, 32.55708f),
                Make("Rednecks Turf 2", rednecks, 591.8459f, 2739.823f, 42.07438f),
                Make("Rednecks Turf 3", rednecks, 1669.691f, 4769.295f, 41.84365f)
            };
        }
    }
}