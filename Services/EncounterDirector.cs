using System;
using DynamicHostileTerritories.Configuration;
using DynamicHostileTerritories.Core;
using DynamicHostileTerritories.Data;
using Rage;
using Rage.Native;

namespace DynamicHostileTerritories.Services
{
    /// <summary>
    /// Runs the live escalation while the player is inside an active territory:
    /// Observing -> Suspicious -> Provoked -> War, based on how the player behaves.
    /// Also re-checks the area tier over time and respawns if it changes. It tells the
    /// GangSpawnManager what to do; it never spawns or cleans up entities itself.
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

            _tier = _hostility.Evaluate(territory);
            territory.Hostility = _tier;

            _spawnManager.Activate(territory, _tier);

            _state = _tier >= HostilityLevel.Warzone ? EncounterState.Suspicious : EncounterState.Observing;
            _spawnManager.ApplyPosture(_state, _tier);

            Logger.Info("Encounter begin at " + territory.Name + " — tier " + _tier
                + ", strength " + (int)territory.Strength + "%, initial state " + _state + ".");
            NotifyEntering(territory);
        }

        public void Update(Territory territory, Ped player)
        {
            if (_territory == null || _tier == HostilityLevel.Pacified)
                return;

            RecheckTierIfDue(territory);

            EncounterState desired = EvaluateState(territory, player);

            if ((int)desired > (int)_state)
            {
                _state = desired;
                _spawnManager.ApplyPosture(_state, _tier);
                Logger.Info("Escalation at " + territory.Name + " -> " + _state + ".");
                NotifyEscalation(territory, _state);
            }
        }

        public void End()
        {
            _spawnManager.Deactivate();
            _territory = null;
            _state = EncounterState.Observing;
        }

        public void ForcePacify()
        {
            _tier = HostilityLevel.Pacified;
            _state = EncounterState.Observing;
            _spawnManager.Deactivate();
            Logger.Info("Encounter force-pacified.");
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
            _spawnManager.ApplyPosture(_state, _tier);
        }

        private EncounterState EvaluateState(Territory territory, Ped player)
        {
            bool bloodDrawn = territory.RecentHeat >= 60f;
            bool shooting = player.IsShooting;
            bool armed = NativeFunction.Natives.IS_PED_ARMED<bool>(player, 7);
            double dwellSeconds = (DateTime.UtcNow - _enteredUtc).TotalSeconds;

            if (shooting || bloodDrawn)
                return EncounterState.War;
            if (armed)
                return EncounterState.Provoked;
            if (dwellSeconds >= _settings.SuspicionDelaySeconds)
                return EncounterState.Suspicious;

            return EncounterState.Observing;
        }

        private static void NotifyEntering(Territory territory)
        {
            Game.DisplayNotification(
                "~y~" + territory.ControllingGang.Name + "~w~ territory: ~o~" + territory.Name
                + "~w~.~n~They are watching you.");
        }

        private static void NotifyEscalation(Territory territory, EncounterState state)
        {
            string line;
            switch (state)
            {
                case EncounterState.Suspicious:
                    line = "~o~" + territory.ControllingGang.Name + "~w~ are getting suspicious.";
                    break;
                case EncounterState.Provoked:
                    line = "~o~" + territory.ControllingGang.Name + "~w~ are reaching for weapons.";
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