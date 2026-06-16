using System;
using DynamicHostileTerritories.Configuration;
using DynamicHostileTerritories.Core;
using DynamicHostileTerritories.Data;
using Rage;

namespace DynamicHostileTerritories.Services
{
    /// <summary>
    /// Runs the live encounter inside an active territory. The gang starts as a LIVING
    /// ambient presence (Observing) for every tier - patrolling, posted, loitering - and
    /// only escalates as a REACTION when the player is noticed: the closer to the core,
    /// the longer they linger, drawing a weapon or firing pushes Observing -> Suspicious
    /// -> Provoked -> War. The tier controls how big/armed the presence is and how far
    /// out they notice you. It commands the spawn manager; it never spawns entities.
    /// </summary>
    public sealed class EncounterDirector
    {
        // The per-visit "mood" of a turf, rolled on entry so encounters feel organic
        // instead of always escalating the same way.
        private enum EncounterProfile
        {
            Quiet,   // armed presence, but they won't open fire unless the player does
            Tense,   // they'll aim up / encircle if you push, but don't fire first
            Hostile  // ambush - they can open fire just for being here
        }

        private readonly PluginSettings _settings;
        private readonly HostilityCalculator _hostility;
        private readonly GangSpawnManager _spawnManager;

        private Territory _territory;
        private EncounterState _state;
        private HostilityLevel _tier;
        private DateTime _enteredUtc;
        private DateTime _lastTierCheckUtc;
        private bool _reinforced;
        private bool _gripBrokenAnnounced;
        private EncounterProfile _profile;

        // The turf is pre-loaded from far away (ActivationDistance) so the crew is staged
        // before the player arrives. The "lingering" suspicion timer must only count time
        // spent ACTUALLY inside the turf footprint - these track that.
        private bool _playerInsideTurf;
        private DateTime _playerInsideSinceUtc;
        private bool _hasNotifiedEntry;

        private readonly Random _rng = new Random();
        private readonly AmbientEventDirector _events;
        private bool _eventTriggered;
        private DateTime _nextEventCheckUtc;

        public EncounterState State => _state;

        public EncounterDirector(PluginSettings settings, HostilityCalculator hostility, GangSpawnManager spawnManager)
        {
            _settings = settings;
            _hostility = hostility;
            _spawnManager = spawnManager;
            _events = new AmbientEventDirector(_rng);
        }

        public void Begin(Territory territory)
        {
            _territory = territory;
            _enteredUtc = DateTime.UtcNow;
            _lastTierCheckUtc = _enteredUtc;
            _reinforced = false;
            _gripBrokenAnnounced = false;
            _eventTriggered = false;
            _playerInsideTurf = false;
            _hasNotifiedEntry = false;
            _nextEventCheckUtc = _enteredUtc.AddSeconds(20);

            _tier = _hostility.Evaluate(territory);
            territory.Hostility = _tier;
            _profile = RollProfile(_tier);

            // Pre-stage the gang while the player is still far away. NO entry notification
            // here - that fires when the player actually crosses into the turf (EvaluateState).
            _spawnManager.Activate(territory, _tier);

            _state = EncounterState.Observing;
            _spawnManager.ApplyPosture(_state, _tier);
        }

        public void Update(Territory territory, Ped player)
        {
            if (_territory == null)
                return;

            // Maintenance ALWAYS runs while a turf is active. This is what stops dead
            // enemies keeping their blips and the director freezing when the grip breaks
            // mid-fight (tier hitting Pacified used to early-return and stall everything).
            _spawnManager.UpdateSkirmish();
            _spawnManager.PruneDeadBlips();
            _events.Update();

            int garrison = Math.Max(1, _spawnManager.SpawnedPeds.Count);
            int breakAt = Math.Max(2, garrison / 4);
            int living = _spawnManager.LivingFighters;
            bool engaged = (int)_state >= (int)EncounterState.Provoked
                           || territory.Strength < _settings.PacifiedBelow
                           || living < garrison; // any member down means the fight has started

            // The crew is finished when only a fraction is left standing OR everyone has been
            // neutralised. Either way the turf is taken - so we PACIFY it here (strength to 0)
            // instead of trusting the per-kill arithmetic to land exactly on zero. This is the
            // fix for "killed everyone but it stayed at 9%" and "grip hit 0 but nobody broke".
            if (engaged && living <= breakAt)
            {
                if (!_gripBrokenAnnounced)
                {
                    _gripBrokenAnnounced = true;

                    // If a few are still standing, run the scatter/surrender/flee resolve.
                    if (living > 0)
                        _spawnManager.BreakResolve();

                    territory.Strength = 0f;
                    territory.Hostility = HostilityLevel.Pacified;

                    Logger.Info("Grip broken at " + territory.Name + " - turf pacified.");
                    Notifier.Show("Grip Broken", "~b~" + territory.ControllingGang.Name,
                        "Their hold on " + territory.Name + " is broken.");
                }
                return;
            }

            // Entered an already-pacified, empty turf: nothing to run.
            if (_tier == HostilityLevel.Pacified)
                return;

            MaybeTriggerAmbientEvent(territory);

            RecheckTierIfDue(territory);

            EncounterState desired = EvaluateState(territory, player);

            // Encounters only escalate; they reset when the player leaves the area.
            if ((int)desired > (int)_state)
            {
                _state = desired;

                // First time it turns into open war, an armed turf calls in backup.
                if (_state == EncounterState.War && !_reinforced && _tier >= HostilityLevel.Aggressive)
                {
                    _spawnManager.Reinforce(territory, _tier);
                    _reinforced = true;
                }

                _spawnManager.ApplyPosture(_state, _tier);
                Logger.Info("Escalation at " + territory.Name + " -> " + _state + ".");
                NotifyEscalation(territory, _state);
            }

        }

        public void End()
        {
            _events.End();
            _spawnManager.Deactivate();
            _territory = null;
            _state = EncounterState.Observing;
            _reinforced = false;
            _playerInsideTurf = false;
            _hasNotifiedEntry = false;
        }

        public void ForcePacify()
        {
            _tier = HostilityLevel.Pacified;
            _state = EncounterState.Observing;
            _reinforced = false;
            _spawnManager.Deactivate();
            Logger.Info("Encounter force-pacified.");
        }

        /// <summary>
        /// Once per visit, while the gang isn't yet locked onto the player, there's a
        /// chance something kicks off in the turf: either a rival crew rolls in for a
        /// skirmish, or a street event (deal / mugging / execution) plays out.
        /// </summary>
        private void MaybeTriggerAmbientEvent(Territory territory)
        {
            if (_eventTriggered || _tier < HostilityLevel.Aggressive)
                return;
            if ((int)_state > (int)EncounterState.Suspicious)
                return;
            if (DateTime.UtcNow < _nextEventCheckUtc)
                return;

            if (_rng.NextDouble() < 0.4)
            {
                // Coin-flip between a turf war and a street event.
                bool started = (_rng.Next(0, 2) == 0)
                    ? _spawnManager.TriggerSkirmish(territory)
                    : _events.TryStart(territory);

                if (started)
                    _eventTriggered = true;
                else
                    _nextEventCheckUtc = DateTime.UtcNow.AddSeconds(20);
            }
            else
            {
                _nextEventCheckUtc = DateTime.UtcNow.AddSeconds(20);
            }
        }

        private void RecheckTierIfDue(Territory territory)
        {
            if ((DateTime.UtcNow - _lastTierCheckUtc).TotalSeconds < _settings.TierRecheckSeconds)
                return;

            _lastTierCheckUtc = DateTime.UtcNow;

            HostilityLevel newTier = _hostility.Evaluate(territory);
            if (newTier == _tier)
                return;

            // If the player has already drawn attention (Suspicious, Provoked or War), do
            // NOT respawn the gang - that would make enemies vanish in the middle of a fight.
            if (_state != EncounterState.Observing)
            {
                // Just update the values so the world registers the turf has weakened.
                _tier = newTier;
                territory.Hostility = newTier;
                return;
            }

            Logger.Info("Tier change at " + territory.Name + ": " + _tier + " -> " + newTier + " (respawning).");

            _tier = newTier;
            territory.Hostility = newTier;

            _spawnManager.Activate(territory, newTier);
            _spawnManager.ApplyPosture(_state, _tier);
        }

        private EncounterState EvaluateState(Territory territory, Ped player)
        {
            if (player == null || !player.Exists() || !player.IsAlive)
                return _state;

            float distance = player.Position.DistanceTo(territory.Center);
            float notice = NoticeRadius(_tier);

            // Track whether the player is inside the turf footprint, and fire the one-shot
            // "Entering <gang> Turf" notification on the way in (decoupled from the spawn,
            // which happened far away during pre-load).
            bool insideTurf = distance <= territory.Radius;
            if (insideTurf)
            {
                if (!_playerInsideTurf)
                {
                    _playerInsideTurf = true;
                    _playerInsideSinceUtc = DateTime.UtcNow;

                    if (!_hasNotifiedEntry)
                    {
                        Logger.Info("Player entered turf at " + territory.Name + " - tier " + _tier
                            + ", mood " + _profile + ", strength " + (int)territory.Strength + "%.");
                        NotifyEntering(territory, _tier, _profile);
                        _hasNotifiedEntry = true;
                    }
                }
            }
            else
            {
                _playerInsideTurf = false;
                _hasNotifiedEntry = false; // re-arm so a later re-entry notifies again
            }

            // "Just driving through": if the player is in a vehicle moving at speed, the crew
            // only watches - they do NOT open fire on someone passing on the street. Stopping,
            // getting out, or going in on foot is what lets things escalate. This is the fix
            // for being gunned down merely for driving past a turf.
            bool passingThrough = false;
            if (player.IsInAnyVehicle(false))
            {
                Vehicle veh = player.CurrentVehicle;
                if (veh != null && veh.Exists() && veh.Speed > 9f) // ~32 km/h and up
                    passingThrough = true;
            }

            // Confirmed action already happened in this turf (a shootout/arrest registered
            // here) -> straight to war. A shot fired while inside is also a clear provocation,
            // but only when you're actually IN the turf and not just speeding past.
            if (territory.RecentHeat >= 75f)
                return EncounterState.War;
            if (player.IsShooting && _playerInsideTurf && !passingThrough)
                return EncounterState.War;

            double dwellSeconds = _playerInsideTurf
                ? (DateTime.UtcNow - _playerInsideSinceUtc).TotalSeconds
                : 0.0;

            EncounterState desired = EncounterState.Observing;

            // Noticed (lingering inside the turf, or coming near the core): they watch you.
            // No aggression - you can patrol straight through.
            if (dwellSeconds >= _settings.SuspicionDelaySeconds || distance <= notice)
                desired = EncounterState.Suspicious;

            // Past this point the gang can go weapons-ready / open fire. None of it happens
            // while the player is just driving past at speed.
            if (passingThrough)
                return desired;

            // A tense/hostile block goes weapons-ready only if you push deep into the core
            // AND have actually entered the turf on foot - not for being out on the street.
            if (_profile != EncounterProfile.Quiet && _playerInsideTurf && distance <= notice * 0.45f)
                desired = Max(desired, EncounterState.Provoked);

            // A hostile (ambush) block opens fire, but only deep in the core, inside the turf,
            // and only after you've lingered a few seconds - no more instant fusillade for
            // merely clipping the edge of the area.
            if (_profile == EncounterProfile.Hostile && _playerInsideTurf
                && distance <= notice * 0.30f && dwellSeconds >= 4.0)
                desired = Max(desired, EncounterState.War);

            return desired;
        }

        /// <summary>
        /// Rolls the per-visit mood. Most visits are Quiet (you can patrol through), Tense
        /// only reacts if you push into the core, and Hostile (ambush on sight) is rare -
        /// it climbs at night and when the block has been stirred up recently.
        /// </summary>
        private EncounterProfile RollProfile(HostilityLevel tier)
        {
            double quiet, tense; // hostile = the remainder

            switch (tier)
            {
                case HostilityLevel.Warzone: quiet = 0.55; tense = 0.35; break;     // hostile ~0.10
                case HostilityLevel.Aggressive: quiet = 0.65; tense = 0.30; break;  // hostile ~0.05
                default: quiet = 0.80; tense = 0.18; break;                         // Watchful hostile ~0.02
            }

            // Night and a recently stirred-up block lean meaner: shift out of quiet.
            double meaner = 0.0;
            if (IsNight())
                meaner += 0.12;
            if (_territory != null && _territory.RecentHeat >= _settings.HeatThreshold)
                meaner += 0.12;
            quiet = Math.Max(0.20, quiet - meaner);

            double roll = _rng.NextDouble();
            if (roll < quiet) return EncounterProfile.Quiet;
            if (roll < quiet + tense) return EncounterProfile.Tense;
            return EncounterProfile.Hostile;
        }

        private static bool IsNight()
        {
            int hour = World.TimeOfDay.Hours;
            return hour >= 20 || hour < 6;
        }

        /// <summary>
        /// How far from the core the gang clocks the player. A Warzone notices you almost
        /// the moment you enter; a Watchful block lets you get close before reacting.
        /// </summary>
        private static float NoticeRadius(HostilityLevel tier)
        {
            switch (tier)
            {
                case HostilityLevel.Warzone: return 70f;
                case HostilityLevel.Aggressive: return 55f;
                default: return 30f;
            }
        }

        private static EncounterState Max(EncounterState a, EncounterState b)
        {
            return (int)a >= (int)b ? a : b;
        }

        private static void NotifyEntering(Territory territory, HostilityLevel tier, EncounterProfile profile)
        {
            string mood;
            switch (profile)
            {
                case EncounterProfile.Hostile: mood = "Something feels wrong here."; break;
                case EncounterProfile.Tense: mood = "Armed crew, eyes on you."; break;
                default: mood = "Quiet for now - stay sharp."; break;
            }

            Notifier.Show("Entering " + territory.ControllingGang.Name + " Turf",
                "~o~" + territory.Name + " ~w~[" + tier + "]", mood);
        }

        private static void NotifyEscalation(Territory territory, EncounterState state)
        {
            string line;
            switch (state)
            {
                case EncounterState.Suspicious:
                    line = territory.ControllingGang.Name + " are on alert.";
                    break;
                case EncounterState.Provoked:
                    line = territory.ControllingGang.Name + " have you in their sights.";
                    break;
                case EncounterState.War:
                    line = "~r~" + territory.ControllingGang.Name + " opened fire!";
                    break;
                default:
                    return;
            }

            Notifier.Show("Territory Alert", "~o~" + territory.Name, line);
        }
    }
}