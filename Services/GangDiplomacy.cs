using System;
using System.Collections.Generic;
using DynamicHostileTerritories.Data;

namespace DynamicHostileTerritories.Services
{
    /// <summary>
    /// The inter-gang diplomacy layer: who is allied with whom and who are sworn rivals.
    /// This is real gameplay data (not just an engine relationship-group detail): the
    /// warfare sim reads it so gangs never conquer an ally and prefer to strike a declared
    /// enemy, and the intel board surfaces it to the player. Relationships are seeded once
    /// from the gang roster and are symmetric (if A allies B, B allies A).
    /// </summary>
    public sealed class GangDiplomacy
    {
        private static readonly Gang[] None = new Gang[0];

        private readonly Dictionary<Gang, HashSet<Gang>> _allies = new Dictionary<Gang, HashSet<Gang>>();
        private readonly Dictionary<Gang, HashSet<Gang>> _enemies = new Dictionary<Gang, HashSet<Gang>>();

        public GangDiplomacy(IReadOnlyList<Territory> territories)
        {
            Dictionary<string, Gang> byName = new Dictionary<string, Gang>();
            if (territories != null)
            {
                foreach (Territory t in territories)
                {
                    Gang g = t.ControllingGang;
                    if (g != null && !byName.ContainsKey(g.Name))
                        byName[g.Name] = g;
                }
            }

            // Alliance blocs (mutual): a gang never attacks an ally in the warfare sim.
            Ally(byName, "Vagos", "Varrios Los Aztecas");
            Ally(byName, "Vagos", "Madrazo Cartel");
            Ally(byName, "Varrios Los Aztecas", "Madrazo Cartel");
            Ally(byName, "The Lost MC", "Rednecks");

            // Sworn rivalries (mutual): a gang prefers to strike a declared enemy first.
            Enemy(byName, "Families", "Ballas");
            Enemy(byName, "Families", "Vagos");
            Enemy(byName, "Ballas", "Vagos");
            Enemy(byName, "Marabunta Grande", "Kkangpae");
            Enemy(byName, "Armenian Mob", "Kkangpae");
        }

        /// <summary>True if the two gangs are allied (symmetric). A gang is never its own ally.</summary>
        public bool IsAlly(Gang a, Gang b)
        {
            if (a == null || b == null || a == b)
                return false;
            return _allies.TryGetValue(a, out HashSet<Gang> set) && set.Contains(b);
        }

        /// <summary>True if the two gangs are sworn enemies (symmetric). Alliance takes precedence.</summary>
        public bool IsEnemy(Gang a, Gang b)
        {
            if (a == null || b == null || a == b)
                return false;
            if (IsAlly(a, b))
                return false;
            return _enemies.TryGetValue(a, out HashSet<Gang> set) && set.Contains(b);
        }

        /// <summary>This gang's allies (possibly empty).</summary>
        public IEnumerable<Gang> Allies(Gang gang)
        {
            return (gang != null && _allies.TryGetValue(gang, out HashSet<Gang> set)) ? (IEnumerable<Gang>)set : None;
        }

        /// <summary>This gang's sworn enemies (possibly empty).</summary>
        public IEnumerable<Gang> Enemies(Gang gang)
        {
            return (gang != null && _enemies.TryGetValue(gang, out HashSet<Gang> set)) ? (IEnumerable<Gang>)set : None;
        }

        // --- Seeding ----------------------------------------------------------------------

        private void Ally(Dictionary<string, Gang> byName, string a, string b)
        {
            if (!byName.TryGetValue(a, out Gang ga) || !byName.TryGetValue(b, out Gang gb))
                return;
            Add(_allies, ga, gb);
            Add(_allies, gb, ga);
        }

        private void Enemy(Dictionary<string, Gang> byName, string a, string b)
        {
            if (!byName.TryGetValue(a, out Gang ga) || !byName.TryGetValue(b, out Gang gb))
                return;
            Add(_enemies, ga, gb);
            Add(_enemies, gb, ga);
        }

        private static void Add(Dictionary<Gang, HashSet<Gang>> map, Gang key, Gang value)
        {
            if (!map.TryGetValue(key, out HashSet<Gang> set))
            {
                set = new HashSet<Gang>();
                map[key] = set;
            }
            set.Add(value);
        }
    }
}