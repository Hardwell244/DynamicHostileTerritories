using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using DynamicHostileTerritories.Data;
using Newtonsoft.Json;
using Rage;

namespace DynamicHostileTerritories.Services
{
    /// <summary>
    /// Builds and holds the catalog of hostile territories. The catalog is loaded from an
    /// editable JSON file (territories_setup.json) so anyone can define their own gangs and
    /// areas without recompiling. On first run the built-in defaults are written out to that
    /// file. If the file is missing or invalid the built-in defaults are used, so the plugin
    /// never fails to start. Assembles data only; spawning/behaviour live elsewhere.
    /// </summary>
    public sealed class TerritoryRepository
    {
        private const string SetupFolder = @"Plugins\LSPDFR\DynamicHostileTerritories";
        private const string SetupFileName = "territories_setup.json";
        private const float DefaultRadius = 120f;
        private const float DefaultStrength = 50f;

        private readonly List<Territory> _territories;

        public IReadOnlyList<Territory> Territories => _territories;

        public TerritoryRepository()
        {
            _territories = Load();
        }

        private static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SetupFolder, SetupFileName); }
        }

        // --- Loading ----------------------------------------------------------------------

        private List<Territory> Load()
        {
            DefinitionFile def;

            try
            {
                string path = FilePath;

                if (!File.Exists(path))
                {
                    def = BuildDefaults();
                    WriteDefaults(path, def);
                    Game.LogTrivial("[DHT] No territories_setup.json found — wrote a default you can edit.");
                }
                else
                {
                    string json = File.ReadAllText(path);
                    def = JsonConvert.DeserializeObject<DefinitionFile>(json);

                    if (def == null || def.Gangs == null || def.Territories == null
                        || def.Gangs.Count == 0 || def.Territories.Count == 0)
                    {
                        Game.LogTrivial("[DHT] territories_setup.json empty/invalid — using built-in defaults.");
                        def = BuildDefaults();
                    }
                }
            }
            catch (Exception ex)
            {
                Game.LogTrivial("[DHT] Failed to read territories_setup.json, using built-in defaults: " + ex);
                def = BuildDefaults();
            }

            List<Territory> list = BuildFrom(def);

            if (list.Count == 0)
            {
                Game.LogTrivial("[DHT] No valid territories built — falling back to built-in defaults.");
                list = BuildFrom(BuildDefaults());
            }

            Game.LogTrivial("[DHT] Loaded " + list.Count + " territories from setup.");
            return list;
        }

        private static List<Territory> BuildFrom(DefinitionFile def)
        {
            Dictionary<string, Gang> gangs = new Dictionary<string, Gang>();

            foreach (GangDef g in def.Gangs)
            {
                if (g == null || string.IsNullOrEmpty(g.Name) || gangs.ContainsKey(g.Name))
                    continue;

                Gang gang = new Gang(
                    g.Name,
                    Color.FromArgb(g.ColorR, g.ColorG, g.ColorB),
                    g.PedModels ?? new List<string>(),
                    g.VehicleModels ?? new List<string>());

                gangs[g.Name] = gang;
            }

            List<Territory> list = new List<Territory>();

            foreach (TerritoryDef t in def.Territories)
            {
                if (t == null || string.IsNullOrEmpty(t.Name) || string.IsNullOrEmpty(t.Gang))
                    continue;

                if (!gangs.TryGetValue(t.Gang, out Gang gang))
                {
                    Game.LogTrivial("[DHT] Territory '" + t.Name + "' references unknown gang '" + t.Gang + "' — skipped.");
                    continue;
                }

                float radius = t.Radius > 0f ? t.Radius : DefaultRadius;
                float strength = t.Strength > 0f ? t.Strength : DefaultStrength;

                list.Add(new Territory(t.Name, gang, new Vector3(t.X, t.Y, t.Z), radius)
                {
                    Strength = strength,
                    Hostility = HostilityLevel.Watchful
                });
            }

            return list;
        }

        private static void WriteDefaults(string path, DefinitionFile def)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(path, JsonConvert.SerializeObject(def, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Game.LogTrivial("[DHT] Could not write default territories_setup.json: " + ex);
            }
        }

        // --- Built-in defaults (your original 22 territories / 10 gangs) -------------------

        private static GangDef G(string name, int r, int g, int b, string[] peds, string[] vehicles)
        {
            return new GangDef
            {
                Name = name,
                ColorR = r,
                ColorG = g,
                ColorB = b,
                PedModels = new List<string>(peds),
                VehicleModels = new List<string>(vehicles)
            };
        }

        private static TerritoryDef T(string name, string gang, float x, float y, float z)
        {
            return new TerritoryDef
            {
                Name = name,
                Gang = gang,
                X = x,
                Y = y,
                Z = z,
                Radius = DefaultRadius,
                Strength = DefaultStrength
            };
        }

        private static DefinitionFile BuildDefaults()
        {
            return new DefinitionFile
            {
                Gangs = new List<GangDef>
                {
                    G("Families", 0, 128, 0,
                        new[] { "g_m_y_famca_01", "g_m_y_famdnf_01", "g_m_y_famfor_01" },
                        new[] { "voodoo2", "manana", "emperor" }),

                    G("Ballas", 128, 0, 128,
                        new[] { "g_m_y_ballaeast_01", "g_m_y_ballaorig_01", "g_m_y_ballasout_01" },
                        new[] { "buccaneer2", "baller", "oracle" }),

                    G("Vagos", 255, 255, 0,
                        new[] { "g_m_y_mexgoon_01", "g_m_y_mexgoon_02", "g_m_y_mexgoon_03" },
                        new[] { "tornado", "chino2", "faction2" }),

                    G("Marabunta Grande", 0, 128, 128,
                        new[] { "g_m_y_salvaboss_01", "g_m_y_salvagoon_01", "g_m_y_salvagoon_02", "g_m_y_salvagoon_03" },
                        new[] { "manana", "tornado", "primo" }),

                    G("Kkangpae", 211, 211, 211,
                        new[] { "g_m_m_korboss_01", "g_m_y_korean_01", "g_m_y_korean_02", "g_m_y_korlieut_01" },
                        new[] { "kuruma", "sultan", "dilettante" }),

                    G("Armenian Mob", 139, 0, 0,
                        new[] { "g_m_m_armboss_01", "g_m_m_armlieut_01", "g_m_y_armgoon_01", "g_m_y_armgoon_02" },
                        new[] { "schafter2", "oracle", "cognoscenti" }),

                    G("Madrazo Cartel", 255, 165, 0,
                        new[] { "g_m_y_mexgoon_01", "g_m_y_mexgoon_02", "g_m_y_mexgoon_03" },
                        new[] { "cavalcade", "baller", "granger" }),

                    G("Varrios Los Aztecas", 0, 0, 255,
                        new[] { "g_m_y_azteca_01" },
                        new[] { "tornado", "chino", "voodoo" }),

                    G("The Lost MC", 105, 105, 105,
                        new[] { "g_m_y_lost_01", "g_m_y_lost_02", "g_m_y_lost_03" },
                        new[] { "daemon", "hexer", "sovereign" }),

                    G("Rednecks", 139, 69, 19,
                        new[] { "a_m_m_hillbilly_01", "a_m_m_hillbilly_02" },
                        new[] { "rebel2", "bodhi2", "sadler" })
                },

                Territories = new List<TerritoryDef>
                {
                    T("Ballas Turf 1", "Ballas", 101.0282f, -1938.165f, 20.23903f),
                    T("Ballas Turf 2", "Ballas", -23.34521f, -1826.804f, 25.11959f),

                    T("Vagos Turf 1", "Vagos", 292.8668f, -2000.102f, 19.80942f),
                    T("Vagos Turf 2", "Vagos", 490.5248f, -1779.046f, 27.85685f),
                    T("Vagos Turf 3", "Vagos", 944.0488f, -1854.581f, 30.53767f),

                    T("Marabunta Turf 1", "Marabunta Grande", 1283.708f, -1734.576f, 51.98082f),
                    T("Marabunta Turf 2", "Marabunta Grande", 1212.528f, -1631.301f, 46.61679f),
                    T("Marabunta Turf 3", "Marabunta Grande", -1121.539f, -1562.336f, 3.701513f),

                    T("Kkangpae Turf 1", "Kkangpae", -754.5678f, -920.0692f, 18.44455f),

                    T("Armenian Turf 1", "Armenian Mob", -605.262f, -1797.159f, 22.90413f),

                    T("Madrazo Turf 1", "Madrazo Cartel", 1370.042f, 1146.712f, 113.1948f),

                    T("Aztecas Turf 1", "Varrios Los Aztecas", 1889.336f, 3821.397f, 31.74322f),
                    T("Aztecas Turf 2", "Varrios Los Aztecas", -214.2826f, 6428.561f, 30.86472f),
                    T("Aztecas Turf 3", "Varrios Los Aztecas", -3228.169f, 1085.752f, 10.16032f),

                    T("Families Turf 1", "Families", -14.71192f, -1457.348f, 29.88582f),
                    T("Families Turf 2", "Families", -179.4559f, -1589.602f, 34.08165f),

                    T("Lost MC Turf 1", "The Lost MC", 971.0652f, -126.2237f, 73.77003f),
                    T("Lost MC Turf 2", "The Lost MC", 72.5417f, 3710.01f, 39.75491f),
                    T("Lost MC Turf 3", "The Lost MC", -2199.119f, 4295.706f, 47.94391f),

                    T("Rednecks Turf 1", "Rednecks", 952.0675f, 3618.137f, 32.55708f),
                    T("Rednecks Turf 2", "Rednecks", 591.8459f, 2739.823f, 42.07438f),
                    T("Rednecks Turf 3", "Rednecks", 1669.691f, 4769.295f, 41.84365f)
                }
            };
        }

        // --- JSON shapes ------------------------------------------------------------------

        public sealed class DefinitionFile
        {
            public List<GangDef> Gangs { get; set; }
            public List<TerritoryDef> Territories { get; set; }
        }

        public sealed class GangDef
        {
            public string Name { get; set; }
            public int ColorR { get; set; }
            public int ColorG { get; set; }
            public int ColorB { get; set; }
            public List<string> PedModels { get; set; }
            public List<string> VehicleModels { get; set; }
        }

        public sealed class TerritoryDef
        {
            public string Name { get; set; }
            public string Gang { get; set; }
            public float X { get; set; }
            public float Y { get; set; }
            public float Z { get; set; }
            public float Radius { get; set; }
            public float Strength { get; set; }
        }
    }
}