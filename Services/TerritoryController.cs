using System;
using System.Collections.Generic;
using DynamicHostileTerritories.Configuration;
using DynamicHostileTerritories.Core;
using DynamicHostileTerritories.Data;
using LSPD_First_Response.Mod.API;
using Rage;

namespace DynamicHostileTerritories.Services
{
    /// <summary>
    /// The brain of the plugin. On its own fiber it activates the nearest territory
    /// only when the player is close (performance), ages every territory's strength/heat
    /// over real time, reacts to police actions, and delegates the live encounter to the
    /// EncounterDirector. Owns its loop fiber and tears everything down on Stop().
    /// </summary>
    public sealed class TerritoryController
    {
        private readonly PluginSettings _settings;
        private readonly TerritoryRepository _repository;
        private readonly EncounterDirector _director;
        private readonly GangSpawnManager _spawnManager;
        private readonly TerritoryStateStore _stateStore;

        private readonly HashSet<Ped> _countedNeutralised = new HashSet<Ped>();

        private GameFiber _loop;
        private bool _running;
        private Territory _activeTerritory;
        private DateTime _lastTickUtc;
        private DateTime _lastSaveUtc;

        public Territory ActiveTerritory => _activeTerritory;

        public TerritoryController(
            PluginSettings settings,
            TerritoryRepository repository,
            EncounterDirector director,
            GangSpawnManager spawnManager,
            TerritoryStateStore stateStore)
        {
            _settings = settings;
            _repository = repository;
            _director = director;
            _spawnManager = spawnManager;
            _stateStore = stateStore;
        }

        public void Start()
        {
            if (_running)
                return;

            _running = true;
            _lastTickUtc = DateTime.UtcNow;
            _lastSaveUtc = DateTime.UtcNow;
            _loop = GameFiber.StartNew(Loop);

            Logger.Info("Territory controller started. Watching " + _repository.Territories.Count + " territories.");
        }

        public void Stop()
        {
            if (!_running && _loop == null)
                return;

            _running = false;

            _director.End();
            _activeTerritory = null;
            _countedNeutralised.Clear();
            _stateStore.Save(_repository.Territories);

            if (_loop != null && _loop.IsAlive)
                _loop.Abort();
            _loop = null;

            Logger.Info("Territory controller stopped.");
        }

        private void Loop()
        {
            while (_running)
            {
                try
                {
                    Tick();
                }
                catch (Exception ex)
                {
                    Logger.Error("Controller tick error", ex);
                }

                GameFiber.Sleep(_settings.UpdateIntervalMs);
            }
        }

        private void Tick()
        {
            DateTime now = DateTime.UtcNow;
            float hoursElapsed = (float)(now - _lastTickUtc).TotalHours;
            _lastTickUtc = now;

            Ped player = Game.LocalPlayer.Character;
            if (!player.Exists())
                return;

            AgeTerritories(hoursElapsed, now);

            Territory nearest = FindActiveTerritory(player.Position);
            if (nearest != _activeTerritory)
                SwitchActiveTerritory(nearest);

            if (_activeTerritory != null)
            {
                DetectPoliceActions(_activeTerritory, now);
                _director.Update(_activeTerritory, player);
            }

            if ((now - _lastSaveUtc).TotalMinutes >= 1.0)
            {
                _stateStore.Save(_repository.Territories);
                _lastSaveUtc = now;
            }
        }

        private void AgeTerritories(float hoursElapsed, DateTime now)
        {
            const float heatDecayPerHour = 200f;

            foreach (Territory t in _repository.Territories)
            {
                if (t.RecentHeat > 0f)
                    t.RecentHeat = Math.Max(0f, t.RecentHeat - heatDecayPerHour * hoursElapsed);

                bool suppressed = (now - t.LastPoliceActionUtc).TotalHours < _settings.SuppressionHours;
                bool ignored = t != _activeTerritory;

                if (ignored && !suppressed && t.Strength < 100f)
                    t.Strength = Math.Min(100f, t.Strength + _settings.StrengthRegrowthPerHour * hoursElapsed);
            }
        }

        private Territory FindActiveTerritory(Vector3 playerPos)
        {
            Territory best = null;
            float bestDistance = _settings.ActivationDistance;

            foreach (Territory t in _repository.Territories)
            {
                float distance = playerPos.DistanceTo(t.Center);
                if (distance <= bestDistance)
                {
                    best = t;
                    bestDistance = distance;
                }
            }

            return best;
        }

        /// <summary>
        /// Switches the active territory. Hardened so a failure in Begin can never leave
        /// us stranded on a half-initialised territory (the cause of "warned once, then
        /// never again"): on failure we reset to null and retry on the next tick.
        /// </summary>
        private void SwitchActiveTerritory(Territory nearest)
        {
            if (_activeTerritory != null)
            {
                Logger.Info("Leaving territory: " + _activeTerritory.Name + ".");
                _director.End();
            }

            _countedNeutralised.Clear();

            if (nearest == null)
            {
                _activeTerritory = null;
                return;
            }

            try
            {
                _director.Begin(nearest);
                _activeTerritory = nearest;
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to begin encounter at " + nearest.Name + "; will retry", ex);
                _director.End();
                _activeTerritory = null;
            }
        }

        private void DetectPoliceActions(Territory territory, DateTime now)
        {
            foreach (Ped ped in _spawnManager.SpawnedPeds)
            {
                if (!ped.Exists() || _countedNeutralised.Contains(ped))
                    continue;

                bool neutralised = ped.IsDead || Functions.IsPedArrested(ped);
                if (!neutralised)
                    continue;

                _countedNeutralised.Add(ped);

                territory.Strength = Math.Max(0f, territory.Strength - _settings.PoliceActionStrengthDrop);
                territory.LastPoliceActionUtc = now;
                territory.RecentHeat = 100f;

                Game.DisplayNotification(
                    "~g~Police pressure~w~ in ~y~" + territory.Name
                    + "~w~. " + territory.ControllingGang.Name + " grip: " + (int)territory.Strength + "%.");

                Logger.Info("Police action in " + territory.Name + " -> strength " + (int)territory.Strength + "%.");
            }
        }

        public void ForcePacifyActive()
        {
            if (_activeTerritory == null)
            {
                Game.DisplayNotification("~y~No active territory to pacify.~w~");
                return;
            }

            _activeTerritory.Strength = 0f;
            _activeTerritory.Hostility = HostilityLevel.Pacified;
            _activeTerritory.LastPoliceActionUtc = DateTime.UtcNow;
            _activeTerritory.RecentHeat = 0f;
            _director.ForcePacify();

            Game.DisplayNotification("~g~" + _activeTerritory.Name + " pacified.~w~ The gang has scattered.");
            Logger.Info(_activeTerritory.Name + " force-pacified via menu.");
        }
    }
}