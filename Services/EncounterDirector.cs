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
        // The per-visit "mood" of a turf, rolled on entry so encounters feel organic
        // instead of always escalating the same way.
        private enum EncounterProfile
        {
            Quiet,   // armed presence, but they won't open fire unless the player does
            Tense,   // they'll aim up / encircle if you push, but don't fire first
            Hostile  // ambush — they can open fire just for being here
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
            _nextEventCheckUtc = _enteredUtc.AddSeconds(20);

            _tier = _hostility.Evaluate(territory);
            territory.Hostility = _tier;
            _profile = RollProfile(_tier);

            // 1. MOVA A NOTIFICAÇÃO E O LOG PARA CÁ! (Avisa o jogador na hora)
            Logger.Info("Encounter begin at " + territory.Name + " — tier " + _tier
                + ", mood " + _profile + ", strength " + (int)territory.Strength + "%, ambient presence staged.");
            NotifyEntering(territory, _tier, _profile);

            // 2. Agora sim ele vai gerar os NPCs com calma
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

            // The crew breaks when only a fraction of the garrison is left standing — NOT
            // at a fixed strength. The survivors then each react on their own (some hands
            // up, some flee, some fight to the death) via BreakResolve.
            int garrison = Math.Max(1, _spawnManager.SpawnedPeds.Count);
            int breakAt = Math.Max(2, garrison / 4);
            int living = _spawnManager.LivingFighters;
            bool engaged = (int)_state >= (int)EncounterState.Provoked
                           || territory.Strength < _settings.PacifiedBelow;

            if (engaged && living > 0 && living <= breakAt)
            {
                if (!_gripBrokenAnnounced && _spawnManager.BreakResolve())
                {
                    _gripBrokenAnnounced = true;
                    Logger.Info("Grip broken at " + territory.Name + " — crew scattered.");
                    Notifier.Show("Grip Broken", "~b~" + territory.ControllingGang.Name,
                        "Their hold on ~y~" + territory.Name + "~w~ is breaking — some run, some fight, some give up.");
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

            // FIX: Se o jogador já chamou a atenção (Suspicious, Provoked ou War),
            // NÃO damos respawn na gangue para não fazer os inimigos sumirem no meio do tiro.
            if (_state != EncounterState.Observing)
            {
                // Apenas atualizamos as variáveis para o jogo registrar que enfraqueceu
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

            // The player drawing blood (or a downed member) tips ANY block into open war.
            if (player.IsShooting || territory.RecentHeat >= 60f)
                return EncounterState.War;

            float distance = player.Position.DistanceTo(territory.Center);
            float notice = NoticeRadius(_tier);
            double dwellSeconds = (DateTime.UtcNow - _enteredUtc).TotalSeconds;

            EncounterState desired = EncounterState.Observing;

            // Being noticed (lingering, or inside the notice radius) makes them watch you.
            if (dwellSeconds >= _settings.SuspicionDelaySeconds || distance <= notice)
                desired = EncounterState.Suspicious;

            // A tense/hostile block aims up or encircles when you get close or go armed.
            if (_profile != EncounterProfile.Quiet
                && (NativeFunction.Natives.IS_PED_ARMED<bool>(player, 7) || distance <= notice * 0.6f))
                desired = Max(desired, EncounterState.Provoked);

            // Only a hostile block opens fire just for you being here — the ambush/fatal entry.
            if (_profile == EncounterProfile.Hostile && distance <= notice * 0.5f)
                desired = Max(desired, EncounterState.War);

            return desired;
        }

        /// <summary>
        /// Rolls the per-visit mood. Weak turfs are usually quiet; strong turfs and the
        /// night lean hostile — but every tier keeps a real chance of "nothing happens"
        /// and of an ambush, so no two visits feel scripted.
        /// </summary>
        private EncounterProfile RollProfile(HostilityLevel tier)
        {
            double quiet, tense; // hostile = the remainder

            switch (tier)
            {
                case HostilityLevel.Warzone: quiet = 0.20; tense = 0.40; break;
                case HostilityLevel.Aggressive: quiet = 0.35; tense = 0.45; break;
                default: quiet = 0.55; tense = 0.40; break;
            }

            // Night makes the streets meaner: shift weight out of quiet into hostile.
            if (IsNight())
                quiet = Math.Max(0.05, quiet - 0.15);

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
                case HostilityLevel.Warzone: return 80f;
                case HostilityLevel.Aggressive: return 70f;
                default: return 35f;
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
                default: mood = "Quiet for now — stay sharp."; break;
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