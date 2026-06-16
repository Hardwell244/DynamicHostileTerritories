using System;
using System.Collections.Generic;
using DynamicHostileTerritories.Configuration;
using DynamicHostileTerritories.Core;
using DynamicHostileTerritories.Data;
using LSPD_First_Response.Mod.API;
using Rage;
using Rage.Native;

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

        // Taking out the area lieutenant breaks the gang's hold far harder than a grunt.
        private const float BossNeutralizedStrengthDrop = 35f;

        private GameFiber _loop;
        private bool _running;
        private int _generation;
        private Territory _activeTerritory;
        private DateTime _lastTickUtc;
        private DateTime _lastSaveUtc;

        public Territory ActiveTerritory => _activeTerritory;

        /// <summary>Optional gang-retaliation director, notified when the player hits a turf.</summary>
        public RetaliationDirector Retaliation { get; set; }

        /// <summary>
        /// Optional city meta-sim. When set, the player's police actions bleed the hit gang's
        /// war chest, so hitting a turf cripples the gang's ability to conquer elsewhere.
        /// </summary>
        public GangWarfareDirector Warfare { get; set; }

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

            int generation = ++_generation;
            _loop = GameFiber.StartNew(() => Loop(generation));

            Logger.Info("Territory controller started. Watching " + _repository.Territories.Count + " territories.");
        }

        public void Stop()
        {
            if (!_running && _loop == null)
                return;

            _running = false;
            _generation++; // invalidate the running loop so it ends itself at the next slice

            _director.End();
            _activeTerritory = null;
            _countedNeutralised.Clear();
            _stateStore.Save(_repository.Territories);

            _loop = null; // no Abort — never interrupt a tick mid-spawn/save

            Logger.Info("Territory controller stopped.");
        }

        private void Loop(int generation)
        {
            while (_running && generation == _generation)
            {
                try
                {
                    Tick();
                }
                catch (Exception ex)
                {
                    Logger.Error("Controller tick error", ex);
                }

                // Sleep in small slices so Stop() is honoured promptly and we never Abort.
                int slept = 0;
                while (slept < _settings.UpdateIntervalMs && _running && generation == _generation)
                {
                    GameFiber.Sleep(100);
                    slept += 100;
                }
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

            Territory nearest = ResolveActiveTerritory(player.Position);
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
            const float heatDecayPerHour = 200f; // recent heat fades within ~30 minutes

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

        /// <summary>
        /// Picks the active territory with hysteresis: once inside one, we keep it until
        /// the player is clearly outside its activation range. This stops the encounter from
        /// flip-flopping (and constantly respawning) between two nearby turfs.
        /// </summary>
        private Territory ResolveActiveTerritory(Vector3 playerPos)
        {
            if (_activeTerritory != null)
            {
                // Leave a bit further out than we entered (hysteresis), based on the
                // ActivationDistance preload radius — NOT the small turf radius.
                float leaveDistance = _settings.ActivationDistance * 1.2f;
                if (playerPos.DistanceTo(_activeTerritory.Center) <= leaveDistance)
                    return _activeTerritory;
            }

            return FindActiveTerritory(playerPos);
        }

        private Territory FindActiveTerritory(Vector3 playerPos)
        {
            Territory best = null;
            float bestDistance = float.MaxValue;

            foreach (Territory t in _repository.Territories)
            {
                float distance = playerPos.DistanceTo(t.Center);

                // Activation uses ActivationDistance from the .ini (not the small turf
                // radius), so a turf activates and PRE-LOADS its peds while the player is
                // still far away. By the time the player reaches the area the crew is
                // already in place instead of popping in right on top of them.
                if (distance <= _settings.ActivationDistance && distance < bestDistance)
                {
                    best = t;
                    bestDistance = distance;
                }
            }

            return best;
        }

        /// <summary>
        /// Switches the active territory. Hardened so a failure in Begin can never leave
        /// us stranded on a half-initialised territory: on failure we reset to null and
        /// retry on the next tick.
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
            Ped player = Game.LocalPlayer.Character;
            bool playerOk = player.Exists();
            Ped bossPed = _spawnManager.BossPed;

            foreach (Ped ped in _spawnManager.SpawnedPeds)
            {
                // Safety guard: skip dead/despawned handles.
                if (ped == null || !ped.Exists())
                    continue;

                if (_countedNeutralised.Contains(ped))
                    continue;

                // Only count it if YOU neutralised them: arrested, OR killed by your damage.
                // That way friendly fire / skirmish / falls do NOT drop the grip on their own.
                bool arrested = Functions.IsPedArrested(ped);
                bool killedByPlayer = false;
                if (ped.IsDead && playerOk)
                {
                    // GET_PED_SOURCE_OF_DEATH returns the entity that killed the ped; compare
                    // its handle to the player's. (RPH Ped has no .Killer, and
                    // HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY proved flaky — kept only as a backup.)
                    int killerHandle = NativeFunction.Natives.GET_PED_SOURCE_OF_DEATH<int>(ped);
                    int playerHandle = NativeFunction.Natives.PLAYER_PED_ID<int>();
                    killedByPlayer = (killerHandle != 0 && killerHandle == playerHandle)
                        || NativeFunction.Natives.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY<bool>(ped, player, true);
                }

                if (arrested || killedByPlayer)
                {
                    _countedNeutralised.Add(ped);

                    bool isBoss = bossPed != null && ped == bossPed;

                    // Grip drop is proportional to the garrison size, so it's the NUMBER of
                    // fighters you clear that breaks a turf — not a fixed amount. A bigger crew
                    // (higher MaxSpawnedPeds) means each one matters less and the fight lasts
                    // longer. SpawnedPeds keeps the dead in the list, so its count is the
                    // garrison's peak size for this activation.
                    int garrison = Math.Max(1, _spawnManager.SpawnedPeds.Count);
                    int garrisonGrunts = Math.Max(1, garrison - (bossPed != null ? 1 : 0));
                    float gruntPool = bossPed != null ? (100f - BossNeutralizedStrengthDrop) : 100f;
                    float drop = isBoss ? BossNeutralizedStrengthDrop : (gruntPool / garrisonGrunts);

                    territory.Strength = Math.Max(0f, territory.Strength - drop);
                    territory.LastPoliceActionUtc = now;
                    territory.RecentHeat = 100f;

                    // The hit gang holds a grudge — a hit squad may come for the player later.
                    Retaliation?.RegisterAggravation(territory.ControllingGang);

                    // Player pressure also bleeds the gang's city-wide war chest, so hitting
                    // a turf cripples its ability to conquer elsewhere (a lieutenant hits hard).
                    Warfare?.DrainWarChest(territory.ControllingGang, isBoss);

                    // Grudge: hit one of a gang's turfs and the whole gang gets angrier.
                    foreach (Territory other in _repository.Territories)
                    {
                        if (other == territory)
                            continue;
                        if (other.ControllingGang == territory.ControllingGang)
                            other.RecentHeat = Math.Min(100f, other.RecentHeat + (isBoss ? 20f : 15f));
                    }

                    if (isBoss)
                    {
                        Notifier.Show("Lieutenant Down", "~b~" + territory.ControllingGang.Name,
                            "Their hold on ~y~" + territory.Name + "~w~ is breaking. Grip: " + (int)territory.Strength + "%.");
                        Logger.Info("BOSS neutralised in " + territory.Name + " -> strength " + (int)territory.Strength + "%.");
                    }
                    else
                    {
                        Notifier.Show("Police Pressure", "~o~" + territory.Name,
                            territory.ControllingGang.Name + " grip down to " + (int)territory.Strength + "%.");
                        Logger.Info("Police action in " + territory.Name + " -> strength " + (int)territory.Strength + "%.");
                    }
                }
            }
        }

        public void ForcePacifyActive()
        {
            if (_activeTerritory == null)
            {
                Notifier.Show("Field Control", "~o~No active territory", "Step inside a gang turf first.");
                return;
            }

            _activeTerritory.Strength = 0f;
            _activeTerritory.Hostility = HostilityLevel.Pacified;
            _activeTerritory.LastPoliceActionUtc = DateTime.UtcNow;
            _activeTerritory.RecentHeat = 0f;
            _director.ForcePacify();

            Notifier.Show("Area Pacified", "~b~" + _activeTerritory.Name, "The gang has scattered.");
            Logger.Info(_activeTerritory.Name + " force-pacified via menu.");
        }
    }
}