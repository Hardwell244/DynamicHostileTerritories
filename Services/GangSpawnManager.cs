using System;
using System.Collections.Generic;
using DynamicHostileTerritories.Core;
using DynamicHostileTerritories.Data;
using Rage;
using Rage.Native;

namespace DynamicHostileTerritories.Services
{
    /// <summary>
    /// Owns every entity spawned for the active territory and keeps it feeling like a
    /// LIVING, gang-controlled block: posted lookouts (armed at higher tiers), members
    /// patrolling/circulating the turf, others loitering, plus a manned roadblock. When
    /// provoked, mobile members peel off to ambush/encircle the player. The ambient
    /// presence runs continuously; the EncounterDirector layers alert/combat on top by
    /// re-tasking on command. Nothing it spawns outlives Deactivate().
    /// </summary>
    public sealed class GangSpawnManager
    {
        private enum Role { Sentry, Loiter, Patrol, Guard }

        private sealed class Member
        {
            public Ped Ped;
            public Vector3 Home;
            public Role Role;
            public string Weapon;
        }

        private static readonly string[] LoiterScenarios =
        {
            "WORLD_HUMAN_SMOKING",
            "WORLD_HUMAN_STAND_MOBILE",
            "WORLD_HUMAN_HANG_OUT_STREET",
            "WORLD_HUMAN_DRINKING",
            "WORLD_HUMAN_LEANING",
            "WORLD_HUMAN_AA_SMOKE"
        };

        private static readonly string[] WatchfulWeapons =
        {
            "weapon_pistol", "weapon_pistol", "weapon_combatpistol", "weapon_microsmg"
        };

        private static readonly string[] AggressiveWeapons =
        {
            "weapon_microsmg", "weapon_smg", "weapon_assaultrifle", "weapon_pumpshotgun"
        };

        private static readonly string[] WarzoneWeapons =
        {
            "weapon_carbinerifle", "weapon_assaultrifle", "weapon_specialcarbine", "weapon_smg", "weapon_pumpshotgun"
        };

        private readonly int _maxPeds;
        private readonly Random _rng = new Random();

        private readonly List<Member> _members = new List<Member>();
        private readonly List<Ped> _peds = new List<Ped>(); // mirror, kept in sync for cleanup + police detection
        private readonly List<Vehicle> _vehicles = new List<Vehicle>();

        private RelationshipGroup _gangGroup;
        private bool _hasGangGroup;
        private bool _isActive;

        private Territory _territory;
        private HostilityLevel _tier;
        private Vector3 _roadblockPos;
        private bool _hasRoadblock;

        public IReadOnlyList<Ped> SpawnedPeds => _peds;
        public bool IsActive => _isActive;

        public GangSpawnManager(int maxPeds)
        {
            _maxPeds = maxPeds;
        }

        /// <summary>
        /// Spawns and stages the gang for the given tier. Spawns are staggered across
        /// frames. Behaviour is NOT set here — call ApplyPosture next.
        /// </summary>
        public void Activate(Territory territory, HostilityLevel tier)
        {
            if (_isActive)
                Deactivate();

            _territory = territory;
            _tier = tier;
            _hasRoadblock = false;

            if (tier == HostilityLevel.Pacified || _maxPeds <= 0)
            {
                _hasGangGroup = false;
                _isActive = true; // active but intentionally empty
                Logger.Debug("Activated " + territory.Name + " with no peds (tier " + tier + ").");
                return;
            }

            _gangGroup = new RelationshipGroup("DHT_" + territory.ControllingGang.Name);
            _gangGroup.SetRelationshipWith(_gangGroup, Relationship.Companion); // same crew — never shoot each other
            _hasGangGroup = true;

            int desired = Math.Min(PedCountFor(tier), _maxPeds);
            int guards = tier >= HostilityLevel.Warzone ? 3 : (tier >= HostilityLevel.Aggressive ? 2 : 0);
            guards = Math.Min(guards, desired);

            if (guards > 0 && !SpawnRoadblockVehicle(territory))
                guards = 0;
            else if (guards > 0)
                _hasRoadblock = true;

            int remaining = desired - guards;
            int patrols = Math.Max(1, remaining / 3);
            int sentries = remaining / 3;
            // whatever is left becomes loiterers

            int spawned = 0;
            for (int i = 0; i < desired; i++)
            {
                Role role;
                if (i < guards) role = Role.Guard;
                else if (i < guards + patrols) role = Role.Patrol;
                else if (i < guards + patrols + sentries) role = Role.Sentry;
                else role = Role.Loiter;

                Vector3 pos = (role == Role.Guard && _hasRoadblock)
                    ? RandomPointAround(_roadblockPos, 4f)
                    : RingPoint(territory.Center, territory.Radius, i, desired);

                if (SpawnMember(pos, role, tier))
                    spawned++;

                GameFiber.Sleep(100);
            }

            _isActive = true;
            Logger.Info("Activated " + territory.Name + " (" + territory.ControllingGang.Name
                + ", tier " + tier + "): " + spawned + "/" + desired + " peds (" + patrols + " patrol, "
                + sentries + " sentry, " + guards + " roadblock), roadblock " + _hasRoadblock + ".");
        }

        /// <summary>
        /// One extra wave of fighters from the edges of the turf when war erupts.
        /// Tasked by the next ApplyPosture(War) call.
        /// </summary>
        public void Reinforce(Territory territory, HostilityLevel tier)
        {
            if (!_hasGangGroup)
                return;

            int wave = tier >= HostilityLevel.Warzone ? 6 : 4;
            int spawned = 0;

            for (int i = 0; i < wave; i++)
            {
                Vector3 pos = RingPoint(territory.Center, territory.Radius, i, wave, 0.9f);
                if (SpawnMember(pos, Role.Guard, tier))
                    spawned++;
                GameFiber.Sleep(80);
            }

            Logger.Info("Reinforcements arrived at " + territory.Name + ": " + spawned + " extra fighters.");
        }

        public void Deactivate()
        {
            int peds = _peds.Count;

            foreach (Member m in _members)
                if (m.Ped.Exists()) m.Ped.Delete();
            _members.Clear();
            _peds.Clear();

            foreach (Vehicle v in _vehicles)
                if (v.Exists()) v.Delete();
            _vehicles.Clear();

            _hasGangGroup = false;
            _hasRoadblock = false;
            _isActive = false;

            if (peds > 0)
                Logger.Debug("Deactivated and cleaned up " + peds + " peds.");
        }

        /// <summary>
        /// Re-tasks every living member for the current posture, and sets how the gang
        /// feels about the player and the cops. Observing = the living ambient presence;
        /// Suspicious = noticed you, posting up armed; Provoked = ambush/encircle; War =
        /// open combat.
        /// </summary>
        public void ApplyPosture(EncounterState state, HostilityLevel tier)
        {
            if (!_hasGangGroup)
                return;

            ApplyRelationship(state);

            Ped player = Game.LocalPlayer.Character;

            // For the Provoked ambush: mobile members (patrol/loiter) peel off to encircle
            // the player from different bearings while the posted ones hold the core.
            int flankTotal = 0;
            if (state == EncounterState.Provoked)
                foreach (Member fm in _members)
                    if (fm.Role == Role.Patrol || fm.Role == Role.Loiter) flankTotal++;
            int flankIndex = 0;

            foreach (Member m in _members)
            {
                if (!m.Ped.Exists())
                    continue;

                switch (state)
                {
                    case EncounterState.Observing:
                        StagePresence(m);
                        break;

                    case EncounterState.Suspicious:
                        // Noticed you: stop, draw, face you and hold the spot. No chasing.
                        m.Ped.Tasks.Clear();
                        m.Ped.BlockPermanentEvents = false;
                        EquipWeapon(m);
                        NativeFunction.Natives.TASK_TURN_PED_TO_FACE_ENTITY(m.Ped, player, 4000);
                        NativeFunction.Natives.TASK_GUARD_CURRENT_POSITION(m.Ped, 8f, 8f, true);
                        break;

                    case EncounterState.Provoked:
                        // AMBUSH: mobile members move to surround the player from several
                        // bearings; posted members (sentry/roadblock) hold the core. When
                        // war breaks out everyone is already closing the pincer.
                        m.Ped.Tasks.Clear();
                        m.Ped.BlockPermanentEvents = false;
                        EquipWeapon(m);
                        if (m.Role == Role.Patrol || m.Role == Role.Loiter)
                        {
                            Vector3 flank = EncirclePoint(player.Position, 18f, flankIndex, flankTotal);
                            flankIndex++;
                            m.Ped.Tasks.FollowNavigationMeshToPosition(flank, 0f, 2.5f);
                        }
                        else
                        {
                            NativeFunction.Natives.TASK_GUARD_CURRENT_POSITION(m.Ped, 20f, 20f, true);
                        }
                        break;

                    case EncounterState.War:
                        // Open combat: the AI advances, flanks and uses cover on its own.
                        m.Ped.Tasks.Clear();
                        m.Ped.BlockPermanentEvents = false;
                        NativeFunction.Natives.TASK_COMBAT_PED(m.Ped, player, 0, 16);
                        break;
                }
            }

            Logger.Debug("Applied posture " + state + " to " + _members.Count + " members.");
        }

        // --- Living ambient presence (Observing) ------------------------------------------

        private void StagePresence(Member m)
        {
            m.Ped.BlockPermanentEvents = true;
            bool armedPresence = _tier >= HostilityLevel.Aggressive;

            switch (m.Role)
            {
                case Role.Sentry:
                case Role.Guard:
                    if (armedPresence)
                    {
                        // Armed lookout: stands holding the weapon, watching the block.
                        EquipWeapon(m);
                        NativeFunction.Natives.TASK_GUARD_CURRENT_POSITION(m.Ped, 5f, 5f, true);
                    }
                    else
                    {
                        NativeFunction.Natives.TASK_START_SCENARIO_IN_PLACE(m.Ped, "WORLD_HUMAN_GUARD_STAND", 0, true);
                    }
                    break;

                case Role.Loiter:
                    string scenario = LoiterScenarios[_rng.Next(LoiterScenarios.Length)];
                    NativeFunction.Natives.TASK_START_SCENARIO_IN_PLACE(m.Ped, scenario, 0, true);
                    break;

                case Role.Patrol:
                    // Walks the block. Weapon holstered while patrolling; drawn when alerted.
                    Vector3 c = _territory.Center;
                    NativeFunction.Natives.TASK_WANDER_IN_AREA(m.Ped, c.X, c.Y, c.Z, _territory.Radius * 0.7f, 4f, 7f);
                    break;
            }
        }

        // --- Spawning ---------------------------------------------------------------------

        private bool SpawnMember(Vector3 around, Role role, HostilityLevel tier)
        {
            string model = Pick(_territory.ControllingGang.PedModels);

            Vector3 spawnPos = around;
            if (NativeFunction.Natives.GET_SAFE_COORD_FOR_PED<bool>(around.X, around.Y, around.Z, true, out Vector3 safe, 0))
                spawnPos = safe;

            float heading = _rng.Next(0, 360);

            Ped ped;
            try
            {
                ped = new Ped(model, spawnPos, heading);
            }
            catch (Exception ex)
            {
                Logger.Warn("Failed to spawn ped model '" + model + "': " + ex.Message);
                return false;
            }

            if (!ped.Exists())
            {
                Logger.Warn("Ped model '" + model + "' did not materialise (invalid model?).");
                return false;
            }

            string weapon = PickWeapon(tier);

            ped.IsPersistent = true;
            ped.BlockPermanentEvents = true;
            ped.RelationshipGroup = _gangGroup;
            ApplyStats(ped, tier);
            ped.Inventory.GiveNewWeapon(weapon, -1, false); // holstered until staged/alerted

            _members.Add(new Member { Ped = ped, Home = spawnPos, Role = role, Weapon = weapon });
            _peds.Add(ped);
            return true;
        }

        private bool SpawnRoadblockVehicle(Territory territory)
        {
            string model = Pick(territory.ControllingGang.VehicleModels);
            Vector3 pos = World.GetNextPositionOnStreet(RandomPointAround(territory.Center, territory.Radius * 0.4f));

            try
            {
                Vehicle vehicle = new Vehicle(model, pos);
                if (!vehicle.Exists())
                {
                    Logger.Warn("Roadblock vehicle model '" + model + "' did not materialise.");
                    return false;
                }

                vehicle.IsPersistent = true;
                _vehicles.Add(vehicle);
                _roadblockPos = vehicle.Position;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn("Failed to spawn roadblock vehicle '" + model + "': " + ex.Message);
                return false;
            }
        }

        private void ApplyStats(Ped ped, HostilityLevel tier)
        {
            switch (tier)
            {
                case HostilityLevel.Watchful:
                    ped.Accuracy = 20;
                    ped.Armor = 0;
                    break;
                case HostilityLevel.Aggressive:
                    ped.Accuracy = 40;
                    ped.Armor = 25;
                    break;
                case HostilityLevel.Warzone:
                    ped.Accuracy = 60;
                    ped.Armor = 75;
                    break;
            }
        }

        private void EquipWeapon(Member m)
        {
            if (string.IsNullOrEmpty(m.Weapon) || m.Weapon == "weapon_unarmed")
                return;

            NativeFunction.Natives.SET_CURRENT_PED_WEAPON(m.Ped, Game.GetHashKey(m.Weapon), true);
        }

        private void ApplyRelationship(EncounterState state)
        {
            RelationshipGroup player = Game.LocalPlayer.Character.RelationshipGroup;
            RelationshipGroup cops = RelationshipGroup.Cop;

            Relationship rel;
            switch (state)
            {
                case EncounterState.War: rel = Relationship.Hate; break;
                case EncounterState.Provoked: rel = Relationship.Dislike; break;
                default: rel = Relationship.Neutral; break;
            }

            _gangGroup.SetRelationshipWith(player, rel);
            _gangGroup.SetRelationshipWith(cops, rel);

            if (state == EncounterState.War)
            {
                player.SetRelationshipWith(_gangGroup, Relationship.Hate);
                cops.SetRelationshipWith(_gangGroup, Relationship.Hate);
            }
        }

        // --- Helpers ----------------------------------------------------------------------

        private static int PedCountFor(HostilityLevel tier)
        {
            switch (tier)
            {
                case HostilityLevel.Watchful: return 6;
                case HostilityLevel.Aggressive: return 12;
                case HostilityLevel.Warzone: return 18;
                default: return 0;
            }
        }

        private string PickWeapon(HostilityLevel tier)
        {
            switch (tier)
            {
                case HostilityLevel.Watchful: return WatchfulWeapons[_rng.Next(WatchfulWeapons.Length)];
                case HostilityLevel.Aggressive: return AggressiveWeapons[_rng.Next(AggressiveWeapons.Length)];
                case HostilityLevel.Warzone: return WarzoneWeapons[_rng.Next(WarzoneWeapons.Length)];
                default: return "weapon_unarmed";
            }
        }

        private string Pick(IReadOnlyList<string> options)
        {
            return options[_rng.Next(options.Count)];
        }

        /// <summary>
        /// A snapped point on a ring around the player, used to surround them during an
        /// ambush. Bearings are spread so closers come in from different sides.
        /// </summary>
        private Vector3 EncirclePoint(Vector3 playerPos, float radius, int index, int count)
        {
            double angle = (index / (double)Math.Max(1, count)) * Math.PI * 2.0;
            float x = playerPos.X + (float)Math.Cos(angle) * radius;
            float y = playerPos.Y + (float)Math.Sin(angle) * radius;

            Vector3 point = new Vector3(x, y, playerPos.Z);
            if (NativeFunction.Natives.GET_SAFE_COORD_FOR_PED<bool>(point.X, point.Y, point.Z, true, out Vector3 safe, 0))
                point = safe;
            return point;
        }

        private Vector3 RingPoint(Vector3 center, float radius, int index, int count, float spreadFactor = 0.6f)
        {
            // Spread members around the core so the turf feels occupied as you push in.
            double baseAngle = (index / (double)Math.Max(1, count)) * Math.PI * 2.0;
            double jitter = (_rng.NextDouble() - 0.5) * 0.7;
            double angle = baseAngle + jitter;
            float distance = radius * spreadFactor * (0.5f + (float)_rng.NextDouble() * 0.8f);

            float x = center.X + (float)Math.Cos(angle) * distance;
            float y = center.Y + (float)Math.Sin(angle) * distance;
            return new Vector3(x, y, center.Z);
        }

        private Vector3 RandomPointAround(Vector3 center, float radius)
        {
            double angle = _rng.NextDouble() * Math.PI * 2.0;
            float distance = (float)(_rng.NextDouble() * radius);
            float x = center.X + (float)Math.Cos(angle) * distance;
            float y = center.Y + (float)Math.Sin(angle) * distance;
            return new Vector3(x, y, center.Z);
        }
    }
}