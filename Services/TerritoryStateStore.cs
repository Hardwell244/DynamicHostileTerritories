using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DynamicHostileTerritories.Core;
using DynamicHostileTerritories.Data;
using Newtonsoft.Json;

namespace DynamicHostileTerritories.Services
{
    /// <summary>
    /// Persists the dynamic part of each territory (strength and last police action)
    /// to a JSON file and restores it on load. We serialise a small DTO instead of the
    /// live Territory object, so the save file stays clean and is not coupled to game
    /// types like Vector3 or Gang.
    /// </summary>
    public sealed class TerritoryStateStore
    {
        private const string SaveFolder = @"Plugins\LSPDFR\DynamicHostileTerritories";
        private const string SaveFileName = "territories.json";

        private readonly string _fullPath;

        public TerritoryStateStore()
        {
            _fullPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                SaveFolder,
                SaveFileName);
        }

        public void Apply(IReadOnlyList<Territory> territories)
        {
            try
            {
                if (!File.Exists(_fullPath))
                {
                    Logger.Info("No save file found; using seeded defaults.");
                    return;
                }

                string json = File.ReadAllText(_fullPath);
                List<TerritorySaveData> saved = JsonConvert.DeserializeObject<List<TerritorySaveData>>(json)
                                                ?? new List<TerritorySaveData>();

                foreach (Territory territory in territories)
                {
                    TerritorySaveData data = saved.FirstOrDefault(d => d.Name == territory.Name);
                    if (data == null)
                        continue;

                    territory.Strength = data.Strength;
                    territory.LastPoliceActionUtc = data.LastPoliceActionUtc;
                }

                Logger.Info("Territory state restored from save (" + saved.Count + " entries).");
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to load save, keeping defaults", ex);
            }
        }

        public void Save(IReadOnlyList<Territory> territories)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_fullPath));

                List<TerritorySaveData> data = territories
                    .Select(t => new TerritorySaveData
                    {
                        Name = t.Name,
                        Strength = t.Strength,
                        LastPoliceActionUtc = t.LastPoliceActionUtc
                    })
                    .ToList();

                string json = JsonConvert.SerializeObject(data, Formatting.Indented);
                File.WriteAllText(_fullPath, json);

                Logger.Debug("Territory state saved (" + data.Count + " entries).");
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to save territory state", ex);
            }
        }

        private sealed class TerritorySaveData
        {
            public string Name { get; set; }
            public float Strength { get; set; }
            public DateTime LastPoliceActionUtc { get; set; }
        }
    }
}