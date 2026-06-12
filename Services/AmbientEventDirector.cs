using System;
using System.Collections.Generic;
using DynamicHostileTerritories.Core;
using DynamicHostileTerritories.Data;
using Rage;
using Rage.Native;

namespace DynamicHostileTerritories.Services
{
    /// <summary>
    /// Stages self-contained ambient street events inside an active turf (a drug deal, a
    /// mugging, a back-alley execution) so the block feels alive even before the player is
    /// noticed. Event actors are their own peds, neutral to the player and cops, and never
    /// affect the gang's grip — the player can watch or interrupt. Everything is cleaned up
    /// on End(). One relationship group is created once and reused (no leak).
    /// </summary>
    public sealed class AmbientEventDirector
    {
        private enum EventKind { Deal, Mugging, Execution }

        private static readonly string[] CivilianModels =
        {
            "a_m_y_hipster_01", "a_m_m_business_01", "a_m_y_skater_01",
            "a_f_y_hipster_02", "a_m_y_genstreet_01", "a_m_m_tramp_01"
        };

        private readonly Random _rng;
        private readonly List<Ped> _peds = new List<Ped>();

        private RelationshipGroup _group;
        private bool _hasGroup;
        private bool _active;
        private DateTime _endsUtc;

        public bool IsActive => _active;

        public AmbientEventDirector(Random rng)
        {
            _rng = rng;
        }

        /// <summary>Rolls a street event and stages it. Returns true if one started.</summary>
        public bool TryStart(Territory territory)
        {
            if (_active)
                return false;

            EnsureGroup();

            EventKind kind = (EventKind)_rng.Next(0, 3);
            Vector3 spot = PickSpot(territory);

            bool ok;
            string label;
            switch (kind)
            {
                case EventKind.Deal:
                    ok = StartDeal(territory, spot);
                    label = "~g~Street deal~w~ going down in ~o~" + territory.Name + "~w~.";
                    break;
                case EventKind.Mugging:
                    ok = StartMugging(territory, spot);
                    label = "~o~A mugging~w~ in ~o~" + territory.Name + "~w~.";
                    break;
                default:
                    ok = StartExecution(territory, spot);
                    label = "~r~An execution~w~ is happening in ~o~" + territory.Name + "~w~.";
                    break;
            }

            if (!ok)
            {
                End();
                return false;
            }

            _active = true;
            _endsUtc = DateTime.UtcNow.AddSeconds(75);
            Game.DisplayNotification(label);
            Logger.Info("Ambient event (" + kind + ") at " + territory.Name + ".");
            return true;
        }

        /// <summary>Times the event out and cleans it up.</summary>
        public void Update()
        {
            if (!_active)
                return;

            if (DateTime.UtcNow >= _endsUtc)
                End();
        }

        public void End()
        {
            foreach (Ped p in _peds)
                if (p.Exists()) p.Delete();
            _peds.Clear();
            _active = false;
        }

        // --- Event builders ---------------------------------------------------------------

        private bool StartDeal(Territory territory, Vector3 spot)
        {
            Ped dealer = SpawnActor(GangModel(territory), spot);
            Ped buyer = SpawnActor(Civilian(), spot + new Vector3(1.2f, 0f, 0f));
            if (dealer == null || buyer == null)
                return false;

            NativeFunction.Natives.TASK_START_SCENARIO_IN_PLACE(dealer, "WORLD_HUMAN_DRUG_DEALER", 0, true);
            NativeFunction.Natives.TASK_TURN_PED_TO_FACE_ENTITY(buyer, dealer, -1);
            NativeFunction.Natives.TASK_START_SCENARIO_IN_PLACE(buyer, "WORLD_HUMAN_STAND_MOBILE", 0, true);
            return true;
        }

        private bool StartMugging(Territory territory, Vector3 spot)
        {
            Ped mugger = SpawnActor(GangModel(territory), spot);
            Ped victim = SpawnActor(Civilian(), spot + new Vector3(1.0f, 0f, 0f));
            if (mugger == null || victim == null)
                return false;

            mugger.Inventory.GiveNewWeapon("weapon_knife", -1, true);
            victim.BlockPermanentEvents = true;
            NativeFunction.Natives.TASK_TURN_PED_TO_FACE_ENTITY(mugger, victim, -1);
            NativeFunction.Natives.TASK_HANDS_UP(victim, 15000, 0, -1, 0);
            return true;
        }

        private bool StartExecution(Territory territory, Vector3 spot)
        {
            Ped executioner = SpawnActor(GangModel(territory), spot);
            Ped victim = SpawnActor(Civilian(), spot + new Vector3(1.5f, 0f, 0f));
            if (executioner == null || victim == null)
                return false;

            executioner.Inventory.GiveNewWeapon("weapon_pistol", -1, true);
            victim.BlockPermanentEvents = true;
            NativeFunction.Natives.TASK_HANDS_UP(victim, -1, 0, -1, 0);
            // TASK_COMBAT_PED forces the executioner to attack regardless of relationship.
            NativeFunction.Natives.TASK_COMBAT_PED(executioner, victim, 0, 16);
            return true;
        }

        // --- Helpers ----------------------------------------------------------------------

        private void EnsureGroup()
        {
            if (_hasGroup)
                return;

            _group = new RelationshipGroup("DHT_AmbientEvent");

            RelationshipGroup player = Game.LocalPlayer.Character.RelationshipGroup;
            RelationshipGroup cops = RelationshipGroup.Cop;
            _group.SetRelationshipWith(player, Relationship.Neutral);
            _group.SetRelationshipWith(cops, Relationship.Neutral);
            player.SetRelationshipWith(_group, Relationship.Neutral);
            cops.SetRelationshipWith(_group, Relationship.Neutral);

            _hasGroup = true;
        }

        private Ped SpawnActor(string model, Vector3 around)
        {
            Vector3 pos = around;
            if (NativeFunction.Natives.GET_SAFE_COORD_FOR_PED<bool>(around.X, around.Y, around.Z, true, out Vector3 safe, 0))
                pos = safe;

            Ped ped;
            try
            {
                ped = new Ped(model, pos, _rng.Next(0, 360));
            }
            catch (Exception ex)
            {
                Logger.Warn("Ambient event actor '" + model + "' failed: " + ex.Message);
                return null;
            }

            if (!ped.Exists())
                return null;

            ped.IsPersistent = true;
            ped.BlockPermanentEvents = false;
            ped.RelationshipGroup = _group;
            _peds.Add(ped);
            return ped;
        }

        private string GangModel(Territory territory)
        {
            var models = territory.ControllingGang.PedModels;
            if (models == null || models.Count == 0)
                return "a_m_y_genstreet_01";
            return models[_rng.Next(models.Count)];
        }

        private string Civilian()
        {
            return CivilianModels[_rng.Next(CivilianModels.Length)];
        }

        private Vector3 PickSpot(Territory territory)
        {
            double angle = _rng.NextDouble() * Math.PI * 2.0;
            float dist = territory.Radius * (0.3f + (float)_rng.NextDouble() * 0.4f);
            float x = territory.Center.X + (float)Math.Cos(angle) * dist;
            float y = territory.Center.Y + (float)Math.Sin(angle) * dist;

            Vector3 p = new Vector3(x, y, territory.Center.Z);
            if (NativeFunction.Natives.GET_SAFE_COORD_FOR_PED<bool>(p.X, p.Y, p.Z, true, out Vector3 safe, 0))
                p = safe;
            return p;
        }
    }
}