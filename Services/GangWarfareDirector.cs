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
        private const double ConquestSuppressionMinutes = 15.0; // post-police truce on a turf
        private const double ReclaimPowerCost = 18.0;         // power to push a held turf back up
        private const float ReclaimStrengthGain = 8f;
        private const double ConquerPowerCost = 120.0;        // power to flip a turf
        private const float ConquerSeedStrength = 45f;        // strength the new owner starts with
        private const float WeakTurfThreshold = 30f;          // a turf this weak is a conquest target
        private const double ConquerChance = 0.5;             // chance a capable gang expands per cycle

        // Gang-vs-gang warfare: strong gangs raid rival turf along their front line,
        // wearing it down over several cycles until it's weak enough to storm.
        private const double RaidPowerCost = 22.0;            // power a gang spends per raid
        private const float RaidDamage = 15f;                 // strength a raid knocks off a rival turf
        private const int MaxRaidsPerCycle = 2;               // how many raids can happen citywide per cycle

        // Comeback for a wiped-out gang: it doesn't vanish — it earns a quiet underground
        // income and claws a foothold back at a cheaper cost than a normal conquest.
        private const double ResurgenceChipCost = 8.0;        // cheap push while landless
        private const double ResurgenceClaimCost = 45.0;      // cheaper than a normal flip

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
            WageWar();
            Resurgence();
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

            // Underground income: a wiped-out gang doesn't vanish — it quietly rebuilds a
            // war chest so it can claw a foothold back (see Resurgence).
            HashSet<Gang> landed = ControllingGangs();
            foreach (KeyValuePair<Gang, Resources> kv in _resources)
            {
                if (landed.Contains(kv.Key))
                    continue;

                kv.Value.Money += 6.0;
                kv.Value.Influence += 4.0;
                kv.Value.Weapons += 4.0;
            }
        }

        /// <summary>Every gang that currently controls at least one turf.</summary>
        private HashSet<Gang> ControllingGangs()
        {
            HashSet<Gang> set = new HashSet<Gang>();
            foreach (Territory t in _repository.Territories)
                if (t.ControllingGang != null)
                    set.Add(t.ControllingGang);
            return set;
        }

        /// <summary>
        /// A gang with no turf left claws its way back: it pushes on the weakest turf in the
        /// city (any owner) until a foothold opens, then re-establishes there at a cheaper
        /// cost. So losing every turf is a setback, not a death — the gang returns to the map.
        /// </summary>
        private void Resurgence()
        {
            Territory active = _controller.ActiveTerritory;
            DateTime now = DateTime.UtcNow;
            HashSet<Gang> landed = ControllingGangs();

            foreach (KeyValuePair<Gang, Resources> kv in _resources)
            {
                Gang gang = kv.Key;
                if (landed.Contains(gang))
                    continue; // still has turf — not landless

                Resources r = kv.Value;
                if (r.Power < ResurgenceChipCost)
                    continue;

                Territory target = WeakestTakeableTurf(gang, active, now);
                if (target == null)
                    continue;

                Spend(r, ResurgenceChipCost);
                target.Strength = Math.Max(0f, target.Strength - RaidDamage);
                target.RecentHeat = Math.Min(100f, target.RecentHeat + 10f);

                Logger.Debug(gang.Name + " (landless) is clawing back at " + target.Name
                    + " (now " + (int)target.Strength + "%).");

                if (target.Strength <= WeakTurfThreshold && r.Power >= ResurgenceClaimCost)
                    Conquer(gang, r, target, "re-established in", ResurgenceClaimCost);

                return; // one comeback push per cycle citywide
            }
        }

        /// <summary>The weakest turf a gang can move on (any owner), skipping active/suppressed.</summary>
        private Territory WeakestTakeableTurf(Gang gang, Territory active, DateTime now)
        {
            Territory best = null;
            float bestStrength = float.MaxValue;

            foreach (Territory t in _repository.Territories)
            {
                if (t == active || t.ControllingGang == gang)
                    continue;
                if (IsSuppressed(t, now))
                    continue;
                if (t.Strength < bestStrength)
                {
                    bestStrength = t.Strength;
                    best = t;
                }
            }

            return best;
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

                Conquer(attacker, r, target, "took", ConquerPowerCost);
                return; // one opportunistic grab per cycle keeps the map from churning too hard
            }
        }

        /// <summary>
        /// Gang-vs-gang warfare. The strongest gangs push along their front line: each picks
        /// the rival turf closest to its own holdings and knocks its strength down. When that
        /// turf is worn weak enough and the attacker can afford it, the raider storms and takes
        /// it on the spot — so the map shifts on its own, independently of the player.
        /// </summary>
        private void WageWar()
        {
            Territory active = _controller.ActiveTerritory;
            DateTime now = DateTime.UtcNow;

            List<Gang> gangs = _resources.Keys.ToList();
            gangs.Sort((a, b) => ResourcesFor(b).Power.CompareTo(ResourcesFor(a).Power));

            int raids = 0;
            foreach (Gang attacker in gangs)
            {
                if (raids >= MaxRaidsPerCycle)
                    break;

                Resources r = ResourcesFor(attacker);
                if (r.Power < RaidPowerCost)
                    continue;

                Territory target = PickRaidTarget(attacker, active, now);
                if (target == null)
                    continue;

                Spend(r, RaidPowerCost);
                target.Strength = Math.Max(0f, target.Strength - RaidDamage);
                target.RecentHeat = Math.Min(100f, target.RecentHeat + 10f);
                raids++;

                Logger.Debug(attacker.Name + " is pushing into " + target.Name
                    + " (now " + (int)target.Strength + "%).");

                // Worn down enough and the raider can pay for it — storm it now.
                if (target.Strength <= WeakTurfThreshold && r.Power >= ConquerPowerCost)
                    Conquer(attacker, r, target, "stormed", ConquerPowerCost);
            }
        }

        /// <summary>
        /// The rival turf closest to the attacker's own holdings (its front line), skipping
        /// the player's active turf, turfs under a post-police truce, and turfs already weak
        /// enough for an opportunistic grab (those are left to Expand).
        /// </summary>
        private Territory PickRaidTarget(Gang attacker, Territory active, DateTime now)
        {
            Territory best = null;
            float bestDist = float.MaxValue;

            foreach (Territory t in _repository.Territories)
            {
                if (t == active || t.ControllingGang == attacker)
                    continue;
                if (IsSuppressed(t, now))
                    continue;
                if (t.Strength <= WeakTurfThreshold)
                    continue;

                float d = NearestOwnedDistance(attacker, t);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = t;
                }
            }

            return best;
        }

        /// <summary>Distance from a turf to the attacker's nearest own turf (its front line).</summary>
        private float NearestOwnedDistance(Gang gang, Territory target)
        {
            float best = float.MaxValue;
            foreach (Territory t in _repository.Territories)
            {
                if (t.ControllingGang != gang)
                    continue;
                float d = t.Center.DistanceTo(target.Center);
                if (d < best)
                    best = d;
            }
            return best; // float.MaxValue if the gang owns nothing
        }

        /// <summary>Flips a turf to the attacker and seeds it at a low grip. Shared by war + expand + resurgence.</summary>
        private void Conquer(Gang attacker, Resources r, Territory target, string verb, double cost)
        {
            Spend(r, cost);

            Gang previous = target.ControllingGang;
            target.ControllingGang = attacker;
            target.Strength = ConquerSeedStrength;
            target.RecentHeat = 0f;
            target.LastPoliceActionUtc = DateTime.MinValue;

            string body = char.ToUpper(verb[0]) + verb.Substring(1);
            Logger.Info("CONQUEST: " + attacker.Name + " " + verb + " " + target.Name + " from " + previous.Name + ".");
            Notifier.Show("Turf War", "~r~" + attacker.Name,
                body + " ~o~" + target.Name + "~w~ from ~y~" + previous.Name + "~w~.");
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