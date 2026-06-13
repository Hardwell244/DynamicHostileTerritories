using System.Collections.Generic;
using DynamicHostileTerritories.Data;
using Rage;

namespace DynamicHostileTerritories.Services
{
    /// <summary>
    /// Owns relationship-group plumbing for the gangs: a name-keyed cache of relationship
    /// groups (reused across activations so they don't leak over a session), the setup of a
    /// gang's own group (companion with itself, allied with the matching vanilla ambient
    /// gang so they don't shoot each other), and switching a gang's stance toward the
    /// player and cops as an encounter escalates or the crew stands down.
    /// </summary>
    public sealed class GangRelationships
    {
        // Reuse relationship groups by name instead of creating a new one on every
        // activation — otherwise they pile up over a long session (leak).
        private static readonly Dictionary<string, RelationshipGroup> _cache =
            new Dictionary<string, RelationshipGroup>();

        /// <summary>Returns a relationship group by name, creating it once and reusing it.</summary>
        public RelationshipGroup GetGroup(string name)
        {
            if (!_cache.TryGetValue(name, out RelationshipGroup group))
            {
                group = new RelationshipGroup(name);
                _cache[name] = group;
            }
            return group;
        }

        /// <summary>
        /// Builds (or fetches) a gang's own group: same crew never shoots each other, and
        /// it's allied with the game's matching ambient gang so vanilla peds of the same
        /// kind don't fight ours. Returns the group to hold onto.
        /// </summary>
        public RelationshipGroup SetupGang(string gangName)
        {
            RelationshipGroup group = GetGroup("DHT_" + gangName);
            group.SetRelationshipWith(group, Relationship.Companion);
            AllyWithAmbientGang(group, gangName);
            return group;
        }

        /// <summary>
        /// Sets how the gang feels about the player and cops for the given encounter state,
        /// in BOTH directions so de-escalation actually clears the hostility on the cached
        /// group instead of leaving cops/player permanently hating this gang.
        /// </summary>
        public void SetHostility(RelationshipGroup gangGroup, EncounterState state)
        {
            Relationship rel;
            switch (state)
            {
                case EncounterState.War: rel = Relationship.Hate; break;
                case EncounterState.Provoked: rel = Relationship.Dislike; break;
                default: rel = Relationship.Neutral; break;
            }

            SetBoth(gangGroup, rel);
        }

        /// <summary>The crew stands down: neutral to the player and cops, both directions.</summary>
        public void SetNeutral(RelationshipGroup gangGroup)
        {
            SetBoth(gangGroup, Relationship.Neutral);
        }

        /// <summary>
        /// A shared, cached group that is neutral to the player and cops. Individual peds
        /// that surrender or flee are moved into it so they stop being hostile WITHOUT
        /// affecting the squadmates who choose to keep fighting (those stay in the gang group).
        /// </summary>
        public RelationshipGroup StoodDownGroup()
        {
            RelationshipGroup g = GetGroup("DHT_StoodDown");
            g.SetRelationshipWith(g, Relationship.Companion);
            SetNeutral(g);
            return g;
        }

        // --- Internals --------------------------------------------------------------------

        private void SetBoth(RelationshipGroup gangGroup, Relationship rel)
        {
            RelationshipGroup player = Game.LocalPlayer.Character.RelationshipGroup;
            RelationshipGroup cops = RelationshipGroup.Cop;

            gangGroup.SetRelationshipWith(player, rel);
            gangGroup.SetRelationshipWith(cops, rel);
            player.SetRelationshipWith(gangGroup, rel);
            cops.SetRelationshipWith(gangGroup, rel);
        }

        private void AllyWithAmbientGang(RelationshipGroup gangGroup, string gangName)
        {
            string ambient;
            switch (gangName)
            {
                case "Families": ambient = "AMBIENT_GANG_FAMILY"; break;
                case "Ballas": ambient = "AMBIENT_GANG_BALLAS"; break;
                case "Vagos": ambient = "AMBIENT_GANG_MEXICAN"; break;
                case "Varrios Los Aztecas": ambient = "AMBIENT_GANG_MEXICAN"; break;
                case "Madrazo Cartel": ambient = "AMBIENT_GANG_MEXICAN"; break;
                case "Marabunta Grande": ambient = "AMBIENT_GANG_MARABUNTE"; break;
                case "Kkangpae": ambient = "AMBIENT_GANG_KOREAN"; break;
                case "The Lost MC": ambient = "AMBIENT_GANG_LOST"; break;
                case "Rednecks": ambient = "AMBIENT_GANG_HILLBILLY"; break;
                default: return; // Armenian Mob etc. have no clean vanilla group — skip.
            }

            RelationshipGroup ambientGroup = GetGroup(ambient);
            gangGroup.SetRelationshipWith(ambientGroup, Relationship.Companion);
            ambientGroup.SetRelationshipWith(gangGroup, Relationship.Companion);
        }
    }
}