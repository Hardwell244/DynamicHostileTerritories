using System;
using System.Collections.Generic;
using System.Linq;
using DynamicHostileTerritories.Core;
using DynamicHostileTerritories.Data;
using Rage;

namespace DynamicHostileTerritories.Services
{
    /// <summary>
    /// The city-wide meta-simulation. Independently of the player's local encounter, gangs
    /// accumulate resources (money, influence, weapons) from the turf they hold and spend
    /// that power to (a) reclaim their own weakened turf and (b) attack and conquer the
    /// weakest rival/abandoned turf — so the map keeps shifting on its own. Runs on its own
    /// slow fiber with no Abort, and never touches the territory the player is inside or a
    /// turf the player has just hit (a short post-police truce protects your work).
    /// </summary>
    public sealed class GangWarfareDirector
    {
        private sealed class Resources
        {
            public double Money;
            public double Influence;
            public double Weapons;
            public double Power => Money * 0.5 + Influence * 1.0 + Weapons * 1.5;
        }

        private readonly TerritoryRepository _repository;
        private readonly TerritoryController _controller;
        private readonly Dictionary<Gang, Resources> _resources = new Dictionary<Gang, Resources>();
        private readonly Random _rng = new Random();

        private GameFiber _loop;
        private bool _running;
        private int _generation;
        private DateTime _lastCycleUtc;

        // Tunables (kept in code for now; can move to the .ini later).
        private const double CycleSeconds = 35.0;             // how often the meta-cycle runs
        private const double ConquestSuppressionMinutes = 3.0; // post-police truce on a turf
        private const double ReclaimPowerCost = 18.0;         // power to push a held turf back up
        private const float ReclaimStrengthGain = 8f;
        private const double ConquerPowerCost = 120.0;        // power to flip a turf
        private const float ConquerSeedStrength = 45f;        // strength the new owner starts with
        private const float WeakTurfThreshold = 30f;          // a turf this weak is a conquest target
        private const double ConquerChance = 0.5;             // chance a capable gang expands per cycle

        public GangWarfareDirector(TerritoryRepository repository, TerritoryController controller)
        {
            _repository = repository;
            _controller = controller;
        }

        /// <summary>Read-only snapshot of a gang's resources for the UI.</summary>
        public sealed class PowerInfo
        {
            public double Money;
            public double Influence;
            public double Weapons;
            public double Power;
        }

        /// <summary>Current resources/power for a gang (zeroed if it has earned nothing yet).</summary>
        public PowerInfo GetPower(Gang gang)
        {
            if (gang != null && _resources.TryGetValue(gang, out Resources r))
                return new PowerInfo { Money = r.Money, Influence = r.Influence, Weapons = r.Weapons, Power = r.Power };
            return new PowerInfo();
        }

        public void Start()
        {
            if (_running)
                return;

            _running = true;
            _lastCycleUtc = DateTime.UtcNow;

            int generation = ++_generation;
            _loop = GameFiber.StartNew(() => Loop(generation));
            Logger.Info("Gang warfare director started.");
        }

        public void Stop()
        {
            if (!_running && _loop == null)
                return;

            _running = false;
            _generation++; // invalidate the loop so it ends itself
            _loop = null;  // no Abort
            Logger.Info("Gang warfare director stopped.");
        }

        private void Loop(int generation)
        {
            while (_running && generation == _generation)
            {
                try
                {
                    if ((DateTime.UtcNow - _lastCycleUtc).TotalSeconds >= CycleSeconds)
                    {
                        _lastCycleUtc = DateTime.UtcNow;
                        Cycle();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("Warfare cycle error", ex);
                }

                int slept = 0;
                while (slept < 2000 && _running && generation == _generation)
                {
                    GameFiber.Sleep(200);
                    slept += 200;
                }
            }
        }

        private void Cycle()
        {
            EarnIncome();
            Reclaim();
            Expand();
        }

        private Resources ResourcesFor(Gang gang)
        {
            if (!_resources.TryGetValue(gang, out Resources r))
            {
                r = new Resources();
                _resources[gang] = r;
            }
            return r;
        }

        private bool IsSuppressed(Territory t, DateTime now)
        {
            return (now - t.LastPoliceActionUtc).TotalMinutes < ConquestSuppressionMinutes;
        }

        /// <summary>Each gang earns from every turf it holds — stronger turf pays more.</summary>
        private void EarnIncome()
        {
            foreach (Territory t in _repository.Territories)
            {
                Resources r = ResourcesFor(t.ControllingGang);
                r.Money += 1.0 + t.Strength * 0.05;
                r.Influence += 1.0;
                r.Weapons += 0.5 + t.Strength * 0.02;
            }
        }

        /// <summary>Gangs pour power into their own weakened turf to bring it back up.</summary>
        private void Reclaim()
        {
            Territory active = _controller.ActiveTerritory;
            DateTime now = DateTime.UtcNow;

            foreach (Territory t in _repository.Territories)
            {
                if (t == active || t.Strength >= 100f)
                    continue;
                if (IsSuppressed(t, now))
                    continue; // your pacification holds during the truce

                Resources r = ResourcesFor(t.ControllingGang);
                if (r.Power < ReclaimPowerCost)
                    continue;

                Spend(r, ReclaimPowerCost);
                t.Strength = Math.Min(100f, t.Strength + ReclaimStrengthGain);
            }
        }

        /// <summary>
        /// A capable gang attacks the weakest turf it doesn't own (including the ones the
        /// player just pacified, once their truce expires) and, if strong enough, takes it.
        /// </summary>
        private void Expand()
        {
            Territory active = _controller.ActiveTerritory;
            DateTime now = DateTime.UtcNow;

            // Strongest gang gets first crack.
            List<Gang> gangs = _resources.Keys.ToList();
            gangs.Sort((a, b) => ResourcesFor(b).Power.CompareTo(ResourcesFor(a).Power));

            foreach (Gang attacker in gangs)
            {
                Resources r = ResourcesFor(attacker);
                if (r.Power < ConquerPowerCost)
                    continue;
                if (_rng.NextDouble() > ConquerChance)
                    continue;

                Territory target = PickConquestTarget(attacker, active, now);
                if (target == null)
                    continue;

                Spend(r, ConquerPowerCost);

                Gang previous = target.ControllingGang;
                target.ControllingGang = attacker;
                target.Strength = ConquerSeedStrength;
                target.RecentHeat = 0f;
                target.LastPoliceActionUtc = DateTime.MinValue;

                Logger.Info("CONQUEST: " + attacker.Name + " took " + target.Name + " from " + previous.Name + ".");
                Game.DisplayNotification(
                    "~r~Turf war~w~: ~y~" + attacker.Name + "~w~ have taken ~o~" + target.Name
                    + "~w~ from ~y~" + previous.Name + "~w~.");

                return; // one conquest per cycle keeps the map from churning too hard
            }
        }

        /// <summary>The weakest unsuppressed turf the attacker doesn't already own.</summary>
        private Territory PickConquestTarget(Gang attacker, Territory active, DateTime now)
        {
            Territory best = null;
            float bestStrength = WeakTurfThreshold;

            foreach (Territory t in _repository.Territories)
            {
                if (t == active || t.ControllingGang == attacker)
                    continue;
                if (IsSuppressed(t, now))
                    continue;
                if (t.Strength <= bestStrength)
                {
                    best = t;
                    bestStrength = t.Strength;
                }
            }

            return best;
        }

        private static void Spend(Resources r, double power)
        {
            // Drain the three pools proportionally so Power actually drops.
            double total = r.Money * 0.5 + r.Influence * 1.0 + r.Weapons * 1.5;
            if (total <= 0)
                return;

            double factor = Math.Max(0.0, 1.0 - power / total);
            r.Money *= factor;
            r.Influence *= factor;
            r.Weapons *= factor;
        }
    }
}