using System;
using DynamicHostileTerritories.Configuration;
using DynamicHostileTerritories.Core;
using DynamicHostileTerritories.Data;
using Rage;
using Rage.Native;

namespace DynamicHostileTerritories.Services
{
    /// <summary>
    /// Runs the live encounter inside an active territory. The gang starts as a LIVING
    /// ambient presence (Observing) for every tier — patrolling, posted, loitering — and
    /// only escalates as a REACTION when the player is noticed: the closer to the core,
    /// the longer they linger, drawing a weapon or firing pushes Observing -> Suspicious
    /// -> Provoked -> War. The tier controls how big/armed the presence is and how far
    /// out they notice you. It commands the spawn manager; it never spawns entities.
    /// </summary>
    public sealed class EncounterDirector
    {
        private readonly PluginSettings _settings;
        private readonly HostilityCalculator _hostility;
        private readonly GangSpawnManager _spawnManager;

        private Territory _territory;
        private EncounterState _state;
        private HostilityLevel _tier;
        private DateTime _enteredUtc;
        private DateTime _lastTierCheckUtc;
        private bool _reinforced;

        private readonly Random _rng = new Random();
        private bool _skirmishTriggered;
        private DateTime _nextSkirmishCheckUtc;

        public EncounterState State => _state;

        public EncounterDirector(PluginSettings settings, HostilityCalculator hostility, GangSpawnManager spawnManager)
        {
            _settings = settings;
            _hostility = hostility;
            _spawnManager = spawnManager;
        }

        public void Begin(Territory territory)
        {
            _territory = territory;
            _enteredUtc = DateTime.UtcNow;
            _lastTierCheckUtc = _enteredUtc;
            _reinforced = false;
            _skirmishTriggered = false;
            _nextSkirmishCheckUtc = _enteredUtc.AddSeconds(20);

            _tier = _hostility.Evaluate(territory);
            territory.Hostility = _tier;

            // 1. MOVA A NOTIFICAÇÃO E O LOG PARA CÁ! (Avisa o jogador na hora)
            Logger.Info("Encounter begin at " + territory.Name + " — tier " + _tier
                + ", strength " + (int)territory.Strength + "%, ambient presence staged.");
            NotifyEntering(territory, _tier);

            // 2. Agora sim ele vai gerar os NPCs com calma
            _spawnManager.Activate(territory, _tier);

            _state = EncounterState.Observing;
            _spawnManager.ApplyPosture(_state, _tier);
        }

        public void Update(Territory territory, Ped player)
        {
            if (_territory == null || _tier == HostilityLevel.Pacified)
                return;

            _spawnManager.UpdateSkirmish();
            MaybeTriggerSkirmish(territory);

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

            // When a turf's war is all but lost, the last men standing give up so the
            // player can cuff them instead of having to kill every last one.
            if (_state == EncounterState.War)
            {
                int left = _spawnManager.LivingFighters;
                if (left > 0 && left <= 2 && _spawnManager.Surrender())
                {
                    Logger.Info("Surrender at " + territory.Name + " (" + left + " left).");
                    Game.DisplayNotification("~o~" + territory.ControllingGang.Name
                        + "~w~ are giving up — cuff them.");
                }
            }

        }

        public void End()
        {
            _spawnManager.Deactivate();
            _territory = null;
            _state = EncounterState.Observing;
            _reinforced = false;
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
        /// Occasionally lets a rival crew invade a busy turf, once per visit, while the
        /// gang isn't already locked onto the player.
        /// </summary>
        private void MaybeTriggerSkirmish(Territory territory)
        {
            if (_skirmishTriggered || _tier < HostilityLevel.Aggressive)
                return;
            if ((int)_state > (int)EncounterState.Suspicious)
                return;
            if (DateTime.UtcNow < _nextSkirmishCheckUtc)
                return;

            if (_rng.NextDouble() < 0.35)
            {
                if (_spawnManager.TriggerSkirmish(territory))
                    _skirmishTriggered = true;
            }
            else
            {
                _nextSkirmishCheckUtc = DateTime.UtcNow.AddSeconds(20);
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

            Logger.Info("Tier change at " + territory.Name + ": " + _tier + " -> " + newTier + " (respawning).");

            _tier = newTier;
            territory.Hostility = newTier;

            _spawnManager.Activate(territory, newTier);
            _spawnManager.ApplyPosture(_state, _tier); // keep current alert level after respawn
        }

        private EncounterState EvaluateState(Territory territory, Ped player)
        {
            if (player == null || !player.Exists() || !player.IsAlive)
                return _state;

            float distance = player.Position.DistanceTo(territory.Center);
            float notice = NoticeRadius(_tier);

            EncounterState desired = EncounterState.Observing;

            double dwellSeconds = (DateTime.UtcNow - _enteredUtc).TotalSeconds;
            if (dwellSeconds >= _settings.SuspicionDelaySeconds || distance <= notice)
                desired = EncounterState.Suspicious;

            if (NativeFunction.Natives.IS_PED_ARMED<bool>(player, 7) || distance <= notice * 0.6f)
                desired = Max(desired, EncounterState.Provoked);

            // A high-tier turf doesn't stay merely "suspicious" once it has clocked you.
            if (_tier >= HostilityLevel.Warzone && (int)desired >= (int)EncounterState.Suspicious)
                desired = Max(desired, EncounterState.Provoked);

            // A drawn shot or a downed member tips the whole block into open war.
            if (player.IsShooting || territory.RecentHeat >= 60f)
                desired = EncounterState.War;

            return desired;
        }

        /// <summary>
        /// How far from the core the gang clocks the player. A Warzone notices you almost
        /// the moment you enter; a Watchful block lets you get close before reacting.
        /// </summary>
        private static float NoticeRadius(HostilityLevel tier)
        {
            switch (tier)
            {
                case HostilityLevel.Warzone: return 110f;
                case HostilityLevel.Aggressive: return 70f;
                default: return 35f;
            }
        }

        private static EncounterState Max(EncounterState a, EncounterState b)
        {
            return (int)a >= (int)b ? a : b;
        }

        private static void NotifyEntering(Territory territory, HostilityLevel tier)
        {
            string mood;
            switch (tier)
            {
                case HostilityLevel.Warzone: mood = "~r~Armed and they want you gone."; break;
                case HostilityLevel.Aggressive: mood = "~o~Armed crew holding the block."; break;
                default: mood = "~y~Locals are watching you."; break;
            }

            Game.DisplayNotification(
                "~y~" + territory.ControllingGang.Name + "~w~ territory: ~o~" + territory.Name
                + "~w~ [" + tier + "].~n~" + mood);
        }

        private static void NotifyEscalation(Territory territory, EncounterState state)
        {
            string line;
            switch (state)
            {
                case EncounterState.Suspicious:
                    line = "~o~" + territory.ControllingGang.Name + "~w~ are on alert.";
                    break;
                case EncounterState.Provoked:
                    line = "~o~" + territory.ControllingGang.Name + "~w~ have you in their sights.";
                    break;
                case EncounterState.War:
                    line = "~r~" + territory.ControllingGang.Name + " opened fire!";
                    break;
                default:
                    return;
            }

            Game.DisplayNotification(line);
        }
    }
}