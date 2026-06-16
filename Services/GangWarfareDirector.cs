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
    /// weakest rival/abandoned turf - so the map keeps shifting on its own. Runs on its own
    /// slow fiber with no Abort, and never touches the territory the player is inside or a
    /// turf the player has just hit (a short post-police truce protects your work).
    ///
    /// Anti-snowball: income has diminishing returns per turf, an overextension upkeep, and a
    /// power ceiling (PowerSoftCap) so PWR plateaus instead of ballooning. A dominant "kingpin"
    /// gang pays a surcharge to expand while rivals get a discount to take its turf, and the
    /// player can directly bleed a gang's war chest by hitting its people (DrainWarChest) - so
    /// a runaway leader can be stopped, not just watched. The kingpin title is sticky and hard
    /// to reach. Each gang has a home turf / redoubt (IsStronghold) the sim never conquers, and
    /// raids wear turf down slowly so taking territory is a real campaign. Inter-gang diplomacy
    /// (GangDiplomacy) is respected: gangs never conquer an ally and prefer to strike an enemy.
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

        // The current most-powerful gang holding turf (the "kingpin"), recomputed each cycle.
        private Gang _kingpin;

        // Kingpin stability: a challenger must clearly out-power the current king AND sustain
        // that lead for a while before being crowned, so the title doesn't flip every cycle.
        private Gang _pendingLeader;
        private DateTime _pendingSinceUtc;
        private DateTime _lastKingpinNotifyUtc = DateTime.MinValue;

        // Inter-gang diplomacy (allies / enemies) and the rival each gang is currently
        // pushing on, surfaced to the intel board and respected by the warfare sim.
        private readonly GangDiplomacy _diplomacy;
        private readonly Dictionary<Gang, Gang> _warTarget = new Dictionary<Gang, Gang>();

        // Tunables (kept in code for now; can move to the .ini later).
        private const double CycleSeconds = 120.0;            // how often the meta-cycle runs (slow, organic)
        private const double ConquestSuppressionMinutes = 15.0; // post-police truce on a turf
        private const double ReclaimPowerCost = 18.0;         // power to push a held turf back up
        private const float ReclaimStrengthGain = 8f;
        private const double ConquerPowerCost = 120.0;        // power to flip a turf
        private const float ConquerSeedStrength = 45f;        // strength the new owner starts with
        private const float WeakTurfThreshold = 30f;          // a turf this weak is a conquest target
        private const double ConquerChance = 0.5;             // chance a capable gang expands per cycle

        // Anti-snowball tuning.
        private const double IncomeDiminish = 0.18;           // per extra turf, income per turf falls off
        private const int UpkeepFreeTurfs = 3;                // turfs held free; beyond this you pay upkeep
        private const double UpkeepInfluencePerTurf = 2.0;    // influence drained per over-cap turf / cycle
        private const double UpkeepWeaponsPerTurf = 1.0;      // weapons drained per over-cap turf / cycle
        private const double KingpinExpandSurcharge = 1.5;    // the leader pays more to grab more
        private const double KingpinTargetDiscount = 0.6;     // rivals pay less to take the leader's turf

        // Power ceiling: income tapers to zero as a gang approaches this, so PWR plateaus
        // instead of ballooning to 140+ over a session.
        private const double PowerSoftCap = 150.0;

        // Becoming the "Most Wanted" gang is hard and sticky: a challenger must out-power the
        // sitting kingpin by this margin AND hold that lead for KingpinHoldSeconds before the
        // crown changes hands. A gang also needs at least MinKingpinPower to be dominant at all.
        private const double MinKingpinPower = 40.0;
        private const double KingpinLeadMargin = 1.25;        // challenger needs 25% more power
        private const double KingpinHoldSeconds = 420.0;      // ...sustained for ~7 minutes
        private const double KingpinNotifyCooldownSeconds = 600.0; // at most one notice / 10 min

        // Gang-vs-gang warfare: strong gangs raid rival turf along their front line,
        // wearing it down over several cycles until it's weak enough to storm.
        private const double RaidPowerCost = 22.0;            // power a gang spends per raid
        private const float RaidDamage = 9f;                  // strength a raid knocks off (low = war takes time)
        private const int MaxRaidsPerCycle = 2;               // how many raids can happen citywide per cycle

        // Comeback for a wiped-out gang: it doesn't vanish - it earns a quiet underground
        // income and claws a foothold back at a cheaper cost than a normal conquest.
        private const double ResurgenceChipCost = 8.0;        // cheap push while landless
        private const double ResurgenceClaimCost = 45.0;      // cheaper than a normal flip

        public GangWarfareDirector(TerritoryRepository repository, TerritoryController controller)
        {
            _repository = repository;
            _controller = controller;
            _diplomacy = new GangDiplomacy(repository.Territories);
        }

        /// <summary>The current dominant gang (most power, holds turf), or null. For the UI.</summary>
        public Gang Kingpin => _kingpin;

        /// <summary>The inter-gang diplomacy table (allies / enemies). For the UI and the sim.</summary>
        public GangDiplomacy Diplomacy => _diplomacy;

        /// <summary>The gang a given gang is currently raiding, if any. For the intel board.</summary>
        public Gang CurrentWarTarget(Gang gang)
        {
            if (gang != null && _warTarget.TryGetValue(gang, out Gang t))
                return t;
            return null;
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

        /// <summary>
        /// Player pressure on a gang's people bleeds its war chest, so hitting a turf doesn't
        /// just drop the local grip - it cripples the gang's ability to conquer elsewhere. A
        /// lieutenant kill hits much harder than a grunt. This is how the player counters a
        /// dominant gang. Safe to call from the controller's tick.
        /// </summary>
        public void DrainWarChest(Gang gang, bool heavy)
        {
            if (gang == null)
                return;

            Resources r = ResourcesFor(gang);
            if (heavy)
            {
                r.Money = Math.Max(0.0, r.Money - 40.0);
                r.Weapons = Math.Max(0.0, r.Weapons - 25.0);
                r.Influence = Math.Max(0.0, r.Influence - 15.0);
            }
            else
            {
                r.Money = Math.Max(0.0, r.Money - 10.0);
                r.Weapons = Math.Max(0.0, r.Weapons - 6.0);
            }
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
            UpdateKingpin();
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

        /// <summary>
        /// Each gang earns from every turf it holds, but with DIMINISHING returns per turf,
        /// an overextension upkeep beyond a few holdings, and a power ceiling that fades income
        /// to zero near the cap - so holding the whole map is expensive, self-limiting, and
        /// PWR plateaus instead of running away.
        /// </summary>
        private void EarnIncome()
        {
            Dictionary<Gang, int> counts = CountHoldings();

            foreach (Territory t in _repository.Territories)
            {
                Resources r = ResourcesFor(t.ControllingGang);

                int n;
                if (!counts.TryGetValue(t.ControllingGang, out n) || n < 1)
                    n = 1;

                // The more turf a gang holds, the less each one pays. Main brake on a leader.
                double dim = 1.0 / (1.0 + IncomeDiminish * (n - 1));

                // Income fades to zero as the gang nears the power ceiling, so PWR plateaus
                // around PowerSoftCap instead of climbing without bound.
                double cap = Math.Max(0.0, 1.0 - r.Power / PowerSoftCap);
                double gain = dim * cap;

                r.Money += (1.0 + t.Strength * 0.05) * gain;
                r.Influence += 1.0 * gain;
                r.Weapons += (0.5 + t.Strength * 0.02) * gain;
            }

            // Overextension upkeep: every turf beyond UpkeepFreeTurfs bleeds influence and
            // weapons, so a sprawling empire is genuinely costly to hold.
            foreach (KeyValuePair<Gang, int> kv in counts)
            {
                int over = kv.Value - UpkeepFreeTurfs;
                if (over <= 0)
                    continue;

                Resources r = ResourcesFor(kv.Key);
                r.Influence = Math.Max(0.0, r.Influence - over * UpkeepInfluencePerTurf);
                r.Weapons = Math.Max(0.0, r.Weapons - over * UpkeepWeaponsPerTurf);
            }

            // Underground income: a wiped-out gang doesn't vanish - it quietly rebuilds a
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

        /// <summary>Turf count per gang that currently holds at least one.</summary>
        private Dictionary<Gang, int> CountHoldings()
        {
            Dictionary<Gang, int> counts = new Dictionary<Gang, int>();
            foreach (Territory t in _repository.Territories)
            {
                if (t.ControllingGang == null)
                    continue;
                int c;
                counts.TryGetValue(t.ControllingGang, out c);
                counts[t.ControllingGang] = c + 1;
            }
            return counts;
        }

        /// <summary>
        /// Recomputes the dominant gang ("Most Wanted"). Deliberately STICKY and hard to
        /// reach: a challenger must out-power the sitting kingpin by KingpinLeadMargin AND
        /// sustain that lead for KingpinHoldSeconds before the crown changes hands, so the
        /// title doesn't bounce between gangs every cycle. The notification is also rate
        /// limited, so it stops spamming the screen.
        /// </summary>
        private void UpdateKingpin()
        {
            HashSet<Gang> landed = ControllingGangs();
            DateTime now = DateTime.UtcNow;

            // Strongest gang that currently holds turf.
            Gang top = null;
            double topPower = -1.0;
            foreach (KeyValuePair<Gang, Resources> kv in _resources)
            {
                if (!landed.Contains(kv.Key))
                    continue;
                double p = kv.Value.Power;
                if (p > topPower)
                {
                    topPower = p;
                    top = kv.Key;
                }
            }

            // Nobody is strong enough to be "dominant" yet: no kingpin.
            if (top == null || topPower < MinKingpinPower)
            {
                _kingpin = null;
                _pendingLeader = null;
                return;
            }

            // The reigning king is still on top: keep the crown, drop any pending challenge.
            if (top == _kingpin)
            {
                _pendingLeader = null;
                return;
            }

            // There's a sitting king and a different gang leads: the king only loses the crown
            // if it has fallen behind by a clear margin (or lost all its turf). A small lead is
            // not enough - the king keeps it.
            if (_kingpin != null)
            {
                double kingPower = GetPower(_kingpin).Power;
                bool kingStillStrong = landed.Contains(_kingpin) && kingPower >= MinKingpinPower;
                if (kingStillStrong && topPower < kingPower * KingpinLeadMargin)
                {
                    _pendingLeader = null;
                    return;
                }
            }

            // The challenger must SUSTAIN the lead for a while before being crowned.
            if (_pendingLeader != top)
            {
                _pendingLeader = top;
                _pendingSinceUtc = now;
                return;
            }
            if ((now - _pendingSinceUtc).TotalSeconds < KingpinHoldSeconds)
                return; // not held long enough yet

            // Crown the new dominant gang.
            _kingpin = top;
            _pendingLeader = null;
            Logger.Info("Most-wanted gang is now " + top.Name + " (PWR " + (int)topPower + ").");

            // Rate-limited so it never spams the screen.
            if ((now - _lastKingpinNotifyUtc).TotalSeconds >= KingpinNotifyCooldownSeconds)
            {
                _lastKingpinNotifyUtc = now;
                Notifier.Show("Most Wanted", "~r~" + top.Name,
                    "are now the dominant gang in Los Santos - break their hold.");
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
        /// Adjusts a conquest's power cost for the kingpin dynamic: the dominant gang pays a
        /// surcharge to expand (anti-snowball), while everyone else gets a discount when the
        /// target belongs to the kingpin (rivals gang up on the leader).
        /// </summary>
        private double ConquerCost(Gang attacker, Territory target, double baseCost)
        {
            double cost = baseCost;
            if (_kingpin != null)
            {
                if (attacker == _kingpin)
                    cost *= KingpinExpandSurcharge;
                else if (target != null && target.ControllingGang == _kingpin)
                    cost *= KingpinTargetDiscount;
            }
            return cost;
        }

        /// <summary>
        /// A gang with no turf left claws its way back: it pushes on the weakest turf in the
        /// city (any owner) until a foothold opens, then re-establishes there at a cheaper
        /// cost. So losing every turf is a setback, not a death - the gang returns to the map.
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
                    continue; // still has turf - not landless

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

                if (target.Strength <= WeakTurfThreshold)
                {
                    double cost = ConquerCost(gang, target, ResurgenceClaimCost);
                    if (r.Power >= cost)
                        Conquer(gang, r, target, "re-established in", cost);
                }

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
                if (t.IsStronghold)
                    continue; // never claw a foothold from a gang's redoubt
                if (_diplomacy.IsAlly(gang, t.ControllingGang))
                    continue; // never claw a foothold from an ally
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
                // A gang's home turf / redoubt heals faster - it's their fortress.
                float gain = t.IsStronghold ? ReclaimStrengthGain * 2f : ReclaimStrengthGain;
                t.Strength = Math.Min(100f, t.Strength + gain);
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
                // Cheap pre-filter against the lowest possible adjusted cost.
                if (r.Power < ConquerPowerCost * KingpinTargetDiscount)
                    continue;
                if (_rng.NextDouble() > ConquerChance)
                    continue;

                Territory target = PickConquestTarget(attacker, active, now);
                if (target == null)
                    continue;

                double cost = ConquerCost(attacker, target, ConquerPowerCost);
                if (r.Power < cost)
                    continue;

                Conquer(attacker, r, target, "took", cost);
                return; // one opportunistic grab per cycle keeps the map from churning too hard
            }
        }

        /// <summary>
        /// Gang-vs-gang warfare. The strongest gangs push along their front line: each picks
        /// the rival turf closest to its own holdings and knocks its strength down. When that
        /// turf is worn weak enough and the attacker can afford it, the raider storms and takes
        /// it on the spot - so the map shifts on its own, independently of the player.
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

                _warTarget[attacker] = target.ControllingGang;

                Spend(r, RaidPowerCost);
                target.Strength = Math.Max(0f, target.Strength - RaidDamage);
                target.RecentHeat = Math.Min(100f, target.RecentHeat + 10f);
                raids++;

                Logger.Debug(attacker.Name + " is pushing into " + target.Name
                    + " (now " + (int)target.Strength + "%).");

                // Worn down enough and the raider can pay for it - storm it now.
                if (target.Strength <= WeakTurfThreshold)
                {
                    double cost = ConquerCost(attacker, target, ConquerPowerCost);
                    if (r.Power >= cost)
                        Conquer(attacker, r, target, "stormed", cost);
                }
            }
        }

        /// <summary>
        /// The rival turf closest to the attacker's own holdings (its front line), skipping
        /// the player's active turf, turfs under a post-police truce, turfs already weak
        /// enough for an opportunistic grab (those are left to Expand), strongholds, and any
        /// allied turf. A declared enemy's turf is preferred over a neutral one.
        /// </summary>
        private Territory PickRaidTarget(Gang attacker, Territory active, DateTime now)
        {
            Territory bestEnemy = null;
            float bestEnemyDist = float.MaxValue;
            Territory bestOther = null;
            float bestOtherDist = float.MaxValue;

            foreach (Territory t in _repository.Territories)
            {
                if (t == active || t.ControllingGang == attacker)
                    continue;
                if (IsSuppressed(t, now))
                    continue;
                if (t.Strength <= WeakTurfThreshold)
                    continue;
                if (t.IsStronghold)
                    continue; // a gang's home turf / redoubt is never raided by the sim
                if (_diplomacy.IsAlly(attacker, t.ControllingGang))
                    continue; // never raid an ally

                float d = NearestOwnedDistance(attacker, t);

                // Prefer a declared enemy's turf; otherwise fall back to the nearest neutral.
                if (_diplomacy.IsEnemy(attacker, t.ControllingGang))
                {
                    if (d < bestEnemyDist)
                    {
                        bestEnemyDist = d;
                        bestEnemy = t;
                    }
                }
                else if (d < bestOtherDist)
                {
                    bestOtherDist = d;
                    bestOther = t;
                }
            }

            return bestEnemy ?? bestOther;
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

            // Global on purpose: the player wants to see every gang-war conquest / turf
            // takeover across the whole city, not only the ones nearby.
            Notifier.Show("Turf War", "~r~" + attacker.Name,
                body + " ~o~" + target.Name + "~w~ from ~y~" + previous.Name + "~w~.");
        }

        /// <summary>The weakest unsuppressed turf the attacker doesn't already own (never a stronghold or ally's).</summary>
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
                if (t.IsStronghold)
                    continue; // never conquer a gang's home turf / redoubt
                if (_diplomacy.IsAlly(attacker, t.ControllingGang))
                    continue; // never conquer an ally
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