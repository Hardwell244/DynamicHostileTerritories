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
    /// noticed. Unlike the old static tableaux, every event now has a LIFECYCLE and a
    /// resolution: a deal can go bad into a shooting, a mugging plays out then both bolt,
    /// an execution actually kills (the executioner and victim sit in opposing hate groups
    /// so TASK_COMBAT_PED reliably connects). Actors are neutral to the player and cops, so
    /// the player can watch or interrupt, and never affect the gang's grip. Placement goes
    /// through the shared SpawnPlacement helper. Everything is cleaned up on End().
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

        // Three cached groups: bystanders (neutral), and an aggressor/victim pair that hate
        // each other so a scripted attack actually happens. All neutral to player and cops.
        private RelationshipGroup _neutralGroup;
        private RelationshipGroup _aggressorGroup;
        private RelationshipGroup _victimGroup;
        private bool _hasGroups;

        private bool _active;
        private DateTime _endsUtc;

        private EventKind _kind;
        private Ped _actor;   // dealer / mugger / executioner
        private Ped _target;  // buyer / victim
        private DateTime _phaseUtc;
        private bool _phaseDone;

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

            EnsureGroups();

            // Resolve ONE grounded, navmesh-safe base spot for the whole scene, kept 25m off
            // the player. If nothing safe resolves (area not streamed), skip and retry later.
            Vector3 raw = PickSpot(territory);
            if (!SpawnPlacement.TryResolve(raw, territory.Radius * 0.4f, 25f, out Vector3 spot))
                return false;

            _kind = (EventKind)_rng.Next(0, 3);
            _phaseDone = false;
            _actor = null;
            _target = null;

            bool ok;
            string label;
            switch (_kind)
            {
                case EventKind.Deal:
                    ok = StartDeal(territory, spot);
                    label = "~g~A street deal is going down.";
                    _phaseUtc = DateTime.UtcNow.AddSeconds(12);
                    break;
                case EventKind.Mugging:
                    ok = StartMugging(territory, spot);
                    label = "~o~A mugging in progress.";
                    _phaseUtc = DateTime.UtcNow.AddSeconds(8);
                    break;
                default:
                    ok = StartExecution(territory, spot);
                    label = "~r~An execution is happening.";
                    _phaseUtc = DateTime.UtcNow.AddSeconds(30);
                    break;
            }

            if (!ok)
            {
                End();
                return false;
            }

            _active = true;
            _endsUtc = DateTime.UtcNow.AddSeconds(75);
            Notifier.Show("Street Activity", "~o~" + territory.Name, label);
            Logger.Info("Ambient event (" + _kind + ") at " + territory.Name + ".");
            return true;
        }

        /// <summary>Advances the event's lifecycle, then times it out and cleans it up.</summary>
        public void Update()
        {
            if (!_active)
                return;

            switch (_kind)
            {
                case EventKind.Deal: UpdateDeal(); break;
                case EventKind.Mugging: UpdateMugging(); break;
                case EventKind.Execution: UpdateExecution(); break;
            }

            if (DateTime.UtcNow >= _endsUtc)
                End();
        }

        public void End()
        {
            foreach (Ped p in _peds)
                if (p.Exists()) p.Delete();
            _peds.Clear();
            _actor = null;
            _target = null;
            _active = false;
        }

        // --- Event builders ---------------------------------------------------------------

        private bool StartDeal(Territory territory, Vector3 spot)
        {
            Ped dealer = SpawnActor(GangModel(territory), spot, _neutralGroup);
            Ped buyer = SpawnActor(Civilian(), GroundOffset(spot, 1.2f, 0f), _neutralGroup);
            if (dealer == null || buyer == null)
                return false;

            NativeFunction.Natives.TASK_START_SCENARIO_IN_PLACE(dealer, "WORLD_HUMAN_DRUG_DEALER", 0, true);
            NativeFunction.Natives.TASK_TURN_PED_TO_FACE_ENTITY(buyer, dealer, -1);
            NativeFunction.Natives.TASK_START_SCENARIO_IN_PLACE(buyer, "WORLD_HUMAN_STAND_MOBILE", 0, true);

            _actor = dealer;
            _target = buyer;
            return true;
        }

        private bool StartMugging(Territory territory, Vector3 spot)
        {
            Ped mugger = SpawnActor(GangModel(territory), spot, _neutralGroup);
            Ped victim = SpawnActor(Civilian(), GroundOffset(spot, 1.0f, 0f), _neutralGroup);
            if (mugger == null || victim == null)
                return false;

            mugger.Inventory.GiveNewWeapon("weapon_knife", -1, true);
            victim.BlockPermanentEvents = true;
            NativeFunction.Natives.TASK_TURN_PED_TO_FACE_ENTITY(mugger, victim, -1);
            NativeFunction.Natives.TASK_HANDS_UP(victim, -1, 0, -1, 0);

            _actor = mugger;
            _target = victim;
            return true;
        }

        private bool StartExecution(Territory territory, Vector3 spot)
        {
            // Executioner and victim go into opposing HATE groups so the attack is reliable.
            Ped executioner = SpawnActor(GangModel(territory), spot, _aggressorGroup);
            Ped victim = SpawnActor(Civilian(), GroundOffset(spot, 1.5f, 0f), _victimGroup);
            if (executioner == null || victim == null)
                return false;

            executioner.Inventory.GiveNewWeapon("weapon_pistol", -1, true);
            victim.BlockPermanentEvents = true;
            NativeFunction.Natives.TASK_HANDS_UP(victim, -1, 0, -1, 0);
            // Forced combat on top of the hate relationship: the victim is killed where it kneels.
            NativeFunction.Natives.TASK_COMBAT_PED(executioner, victim, 0, 16);

            _actor = executioner;
            _target = victim;
            return true;
        }

        // --- Event lifecycles -------------------------------------------------------------

        private void UpdateDeal()
        {
            if (_phaseDone || DateTime.UtcNow < _phaseUtc)
                return;

            _phaseDone = true;

            bool actorOk = _actor != null && _actor.Exists() && !_actor.IsDead;
            bool targetOk = _target != null && _target.Exists() && !_target.IsDead;
            if (!actorOk || !targetOk)
                return;

            // ~35% of deals go bad: the dealer double-crosses the buyer and opens fire.
            if (_rng.NextDouble() < 0.35)
            {
                _actor.RelationshipGroup = _aggressorGroup;
                _target.RelationshipGroup = _victimGroup;
                _actor.Tasks.Clear();
                _actor.Inventory.GiveNewWeapon("weapon_pistol", -1, true);
                NativeFunction.Natives.TASK_COMBAT_PED(_actor, _target, 0, 16);
                Notifier.Show("Street Activity", "~r~Deal Gone Bad", "Shots fired - the deal turned ugly.");
            }
            else
            {
                // Clean deal: the buyer pockets it and walks off, dealer drifts away.
                WalkAwayFrom(_target, _actor, 45f);
            }
        }

        private void UpdateMugging()
        {
            if (_phaseDone || DateTime.UtcNow < _phaseUtc)
                return;

            _phaseDone = true;

            // The mugger grabs the loot and bolts; the victim recovers and flees the other way.
            if (_actor != null && _actor.Exists() && !_actor.IsDead)
                WalkAwayFrom(_actor, _target, 60f);

            if (_target != null && _target.Exists() && !_target.IsDead)
            {
                _target.Tasks.Clear();
                _target.BlockPermanentEvents = false;
                WalkAwayFrom(_target, _actor, 50f);
            }
        }

        private void UpdateExecution()
        {
            if (_phaseDone)
                return;

            // Once the victim is down, the executioner stops loitering and slips away.
            bool victimDown = _target == null || !_target.Exists() || _target.IsDead;
            if (!victimDown)
                return;

            _phaseDone = true;
            if (_actor != null && _actor.Exists() && !_actor.IsDead)
            {
                _actor.Tasks.Clear();
                _actor.RelationshipGroup = _neutralGroup;
                NativeFunction.Natives.TASK_SMART_FLEE_PED(_actor, Game.LocalPlayer.Character, 80f, -1, false, false);
            }
        }

        // --- Helpers ----------------------------------------------------------------------

        private void EnsureGroups()
        {
            if (_hasGroups)
                return;

            _neutralGroup = new RelationshipGroup("DHT_AmbientEvent");
            _aggressorGroup = new RelationshipGroup("DHT_AmbientAggr");
            _victimGroup = new RelationshipGroup("DHT_AmbientVictim");

            NeutralToWorld(_neutralGroup);
            NeutralToWorld(_aggressorGroup);
            NeutralToWorld(_victimGroup);

            _neutralGroup.SetRelationshipWith(_neutralGroup, Relationship.Companion);
            _aggressorGroup.SetRelationshipWith(_aggressorGroup, Relationship.Companion);
            _victimGroup.SetRelationshipWith(_victimGroup, Relationship.Companion);

            // The only hostile relationship in the whole system: aggressor vs victim.
            _aggressorGroup.SetRelationshipWith(_victimGroup, Relationship.Hate);
            _victimGroup.SetRelationshipWith(_aggressorGroup, Relationship.Hate);

            _hasGroups = true;
        }

        private void NeutralToWorld(RelationshipGroup g)
        {
            RelationshipGroup player = Game.LocalPlayer.Character.RelationshipGroup;
            RelationshipGroup cops = RelationshipGroup.Cop;
            g.SetRelationshipWith(player, Relationship.Neutral);
            g.SetRelationshipWith(cops, Relationship.Neutral);
            player.SetRelationshipWith(g, Relationship.Neutral);
            cops.SetRelationshipWith(g, Relationship.Neutral);
        }

        private Ped SpawnActor(string model, Vector3 pos, RelationshipGroup group)
        {
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
            ped.RelationshipGroup = group;
            _peds.Add(ped);
            return ped;
        }

        private static void WalkAwayFrom(Ped who, Ped from, float distance)
        {
            if (who == null || !who.Exists())
                return;

            who.Tasks.Clear();
            who.BlockPermanentEvents = false;

            Vector3 away = who.Position;
            if (from != null && from.Exists())
            {
                Vector3 dir = who.Position - from.Position;
                float len = dir.Length();
                if (len > 0.1f)
                    away = who.Position + (dir / len) * distance;
            }
            who.Tasks.FollowNavigationMeshToPosition(away, 0f, 2.5f);
        }

        private static Vector3 GroundOffset(Vector3 baseSpot, float dx, float dy)
        {
            return SpawnPlacement.GroundSnap(new Vector3(baseSpot.X + dx, baseSpot.Y + dy, baseSpot.Z));
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
            return new Vector3(x, y, territory.Center.Z);
        }
    }
}