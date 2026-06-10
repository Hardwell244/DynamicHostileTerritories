using System;
using System.Collections.Generic;
using DynamicHostileTerritories.Core;
using DynamicHostileTerritories.Data;
using Rage;
using Rage.Native;

namespace DynamicHostileTerritories.Services
{
    /// <summary>
    /// Owns every entity spawned for the active territory and applies behaviour
    /// "postures" to them. It does not decide WHEN to escalate (that is the
    /// EncounterDirector); it only spawns/cleans up and re-tasks peds on command.
    /// Nothing it spawns may outlive a call to Deactivate().
    /// </summary>
    public sealed class GangSpawnManager
    {
        private readonly int _maxPeds;
        private readonly Random _rng = new Random();

        private readonly List<Ped> _peds = new List<Ped>();
        private readonly List<Vehicle> _vehicles = new List<Vehicle>();

        private RelationshipGroup _gangGroup;
        private bool _hasGangGroup; // RelationshipGroup is a struct (can't be null), so track this explicitly
        private bool _isActive;

        public IReadOnlyList<Ped> SpawnedPeds => _peds;
        public bool IsActive => _isActive;

        public GangSpawnManager(int maxPeds)
        {
            _maxPeds = maxPeds;
        }

        /// <summary>
        /// Spawns the gang for the given tier. Spawns are staggered across frames so
        /// we never mass-spawn. Behaviour is NOT set here — call ApplyPosture next.
        /// </summary>
        public void Activate(Territory territory, HostilityLevel tier)
        {
            if (_isActive)
                Deactivate();

            if (tier == HostilityLevel.Pacified || _maxPeds <= 0)
            {
                _hasGangGroup = false;
                _isActive = true; // active but intentionally empty
                Logger.Debug("Activated " + territory.Name + " with no peds (tier " + tier + ").");
                return;
            }

            _gangGroup = new RelationshipGroup("DHT_" + territory.ControllingGang.Name);
            _hasGangGroup = true;

            int desired = Math.Min(PedCountFor(tier), _maxPeds);
            string weapon = WeaponFor(tier);

            int spawned = 0;
            for (int i = 0; i < desired; i++)
            {
                if (SpawnGangMember(territory, weapon))
                    spawned++;
                GameFiber.Sleep(150); // stagger to spread the load
            }

            if (tier >= HostilityLevel.Aggressive)
                SpawnGangVehicle(territory);

            _isActive = true;
            Logger.Info("Activated " + territory.Name + " (" + territory.ControllingGang.Name
                + ", tier " + tier + "): " + spawned + "/" + desired + " peds, weapon " + weapon + ".");
        }

        public void Deactivate()
        {
            int peds = _peds.Count;
            int vehicles = _vehicles.Count;

            foreach (Ped ped in _peds)
                if (ped.Exists()) ped.Delete();
            _peds.Clear();

            foreach (Vehicle vehicle in _vehicles)
                if (vehicle.Exists()) vehicle.Delete();
            _vehicles.Clear();

            _hasGangGroup = false;
            _isActive = false;

            if (peds > 0 || vehicles > 0)
                Logger.Debug("Deactivated and cleaned up " + peds + " peds, " + vehicles + " vehicles.");
        }

        /// <summary>
        /// Re-tasks every living gang member to match the current encounter posture,
        /// and sets how the gang feels about the player AND the cops.
        /// </summary>
        public void ApplyPosture(EncounterState state, HostilityLevel tier)
        {
            if (!_hasGangGroup)
                return;

            ApplyRelationship(state);

            Ped player = Game.LocalPlayer.Character;

            foreach (Ped ped in _peds)
            {
                if (!ped.Exists())
                    continue;

                switch (state)
                {
                    case EncounterState.Observing:
                        ped.BlockPermanentEvents = true;
                        NativeFunction.Natives.TASK_TURN_PED_TO_FACE_ENTITY(ped, player, 3000);
                        break;

                    case EncounterState.Suspicious:
                        ped.BlockPermanentEvents = true;
                        NativeFunction.Natives.TASK_FOLLOW_TO_OFFSET_OF_ENTITY(
                            ped, player, 0f, 0f, 0f, 1.5f, -1, 8f, true);
                        break;

                    case EncounterState.Provoked:
                        ped.BlockPermanentEvents = false;
                        NativeFunction.Natives.TASK_GUARD_CURRENT_POSITION(ped, 15f, 15f, true);
                        break;

                    case EncounterState.War:
                        ped.BlockPermanentEvents = false;
                        ped.Tasks.TakeCoverAt(ped.Position, player.Position, -1, true);
                        break;
                }
            }

            Logger.Debug("Applied posture " + state + " to " + _peds.Count + " peds.");
        }

        private bool SpawnGangMember(Territory territory, string weapon)
        {
            string model = Pick(territory.ControllingGang.PedModels);
            Vector3 around = RandomPointAround(territory.Center, territory.Radius * 0.6f);

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

            ped.IsPersistent = true;
            ped.RelationshipGroup = _gangGroup;
            ped.Inventory.GiveNewWeapon(weapon, -1, false); // holstered until a hostile posture
            _peds.Add(ped);
            return true;
        }

        private void SpawnGangVehicle(Territory territory)
        {
            string model = Pick(territory.ControllingGang.VehicleModels);
            Vector3 pos = World.GetNextPositionOnStreet(RandomPointAround(territory.Center, territory.Radius * 0.5f));

            try
            {
                Vehicle vehicle = new Vehicle(model, pos);
                if (!vehicle.Exists())
                {
                    Logger.Warn("Vehicle model '" + model + "' did not materialise.");
                    return;
                }

                vehicle.IsPersistent = true;
                _vehicles.Add(vehicle);
            }
            catch (Exception ex)
            {
                Logger.Warn("Failed to spawn vehicle model '" + model + "': " + ex.Message);
            }
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

            // Gang's stance toward player AND police (so they engage your partner/backup too).
            _gangGroup.SetRelationshipWith(player, rel);
            _gangGroup.SetRelationshipWith(cops, rel);

            // At war, make it mutual so officers fight back.
            if (state == EncounterState.War)
            {
                player.SetRelationshipWith(_gangGroup, Relationship.Hate);
                cops.SetRelationshipWith(_gangGroup, Relationship.Hate);
            }
        }

        private static int PedCountFor(HostilityLevel tier)
        {
            switch (tier)
            {
                case HostilityLevel.Watchful: return 2;
                case HostilityLevel.Aggressive: return 3;
                case HostilityLevel.Warzone: return 4;
                default: return 0;
            }
        }

        private static string WeaponFor(HostilityLevel tier)
        {
            switch (tier)
            {
                case HostilityLevel.Watchful: return "weapon_pistol";
                case HostilityLevel.Aggressive: return "weapon_microsmg";
                case HostilityLevel.Warzone: return "weapon_carbinerifle";
                default: return "weapon_unarmed";
            }
        }

        private string Pick(IReadOnlyList<string> options)
        {
            return options[_rng.Next(options.Count)];
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