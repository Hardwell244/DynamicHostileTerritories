using System;
using System.Collections.Generic;
using DynamicHostileTerritories.Core;
using DynamicHostileTerritories.Data;
using Rage;
using Rage.Native;

namespace DynamicHostileTerritories.Services
{
    /// <summary>
    /// A self-contained gang-vs-gang skirmish: a rival crew rolls into an active turf and
    /// fights the controlling gang. Both sides are their own peds in their own relationship
    /// groups, neutral to the player and cops, so killing them does NOT move the grip — the
    /// player can watch or wade in. Owns and cleans up everything it spawns. Placement and
    /// combat go through the shared SpawnPlacement / CombatProfile helpers, so the rival
    /// crew is no longer a wall of laser-accurate gunmen out of step with the turf garrison.
    /// </summary>
    public sealed class SkirmishDirector
    {
        // Generic rival crew that invades a turf during a gang-vs-gang skirmish.
        private static readonly string[] RivalModels =
        {
            "g_m_y_strpunk_01", "g_m_y_strpunk_02", "g_m_y_armgoon_02", "g_m_y_mexgoon_02"
        };

        private readonly Random _rng = new Random();
        private readonly List<Ped> _peds = new List<Ped>();

        private RelationshipGroup _defGroup;
        private RelationshipGroup _atkGroup;
        private bool _hasGroups;
        private bool _active;
        private DateTime _endsUtc;

        public bool IsActive => _active;

        /// <summary>
        /// Rolls a rival crew into the turf to fight the controlling gang. Returns true if
        /// one actually started.
        /// </summary>
        public bool TryStart(Territory territory)
        {
            if (_active)
                return false;

            EnsureGroups();

            Vector3 defPos = RingPoint(territory.Center, territory.Radius, 0, 1, 0.55f);
            Vector3 atkPos = RingPoint(territory.Center, territory.Radius, 0, 1, 0.85f);

            List<Ped> defenders = SpawnSide(territory.ControllingGang.PedModels, _defGroup, defPos, 3);
            List<Ped> attackers = SpawnSide(RivalModels, _atkGroup, atkPos, 3);

            if (defenders.Count == 0 || attackers.Count == 0)
            {
                End();
                return false;
            }

            foreach (Ped d in defenders)
            {
                Ped t = attackers[_rng.Next(attackers.Count)];
                if (d.Exists() && t.Exists())
                    NativeFunction.Natives.TASK_COMBAT_PED(d, t, 0, 16);
            }
            foreach (Ped a in attackers)
            {
                Ped t = defenders[_rng.Next(defenders.Count)];
                if (a.Exists() && t.Exists())
                    NativeFunction.Natives.TASK_COMBAT_PED(a, t, 0, 16);
            }

            _active = true;
            _endsUtc = DateTime.UtcNow.AddSeconds(90);
            Logger.Info("Turf skirmish at " + territory.Name + " (" + defenders.Count + " vs " + attackers.Count + ").");
            Notifier.Show("Gang Clash", "~o~" + territory.Name, "A rival crew is moving in.");
            return true;
        }

        /// <summary>Cleans the skirmish up once it times out or one side is wiped.</summary>
        public void Update()
        {
            if (!_active)
                return;

            int alive = 0;
            foreach (Ped p in _peds)
                if (p.Exists() && !p.IsDead) alive++;

            if (DateTime.UtcNow >= _endsUtc || alive <= 1)
                End();
        }

        public void End()
        {
            foreach (Ped p in _peds)
                if (p.Exists()) p.Delete();
            _peds.Clear();
            _active = false;
        }

        // --- Internals --------------------------------------------------------------------

        private void EnsureGroups()
        {
            if (_hasGroups)
                return;

            _defGroup = new RelationshipGroup("DHT_Skirmish_Def");
            _atkGroup = new RelationshipGroup("DHT_Skirmish_Atk");
            _defGroup.SetRelationshipWith(_defGroup, Relationship.Companion);
            _atkGroup.SetRelationshipWith(_atkGroup, Relationship.Companion);
            _defGroup.SetRelationshipWith(_atkGroup, Relationship.Hate);
            _atkGroup.SetRelationshipWith(_defGroup, Relationship.Hate);

            _hasGroups = true;
        }

        private List<Ped> SpawnSide(IReadOnlyList<string> models, RelationshipGroup group, Vector3 around, int count)
        {
            List<Ped> list = new List<Ped>();

            for (int i = 0; i < count; i++)
            {
                // Shared placement: grounded, navmesh-safe, kept 28m off the player. If
                // nothing safe resolves we SKIP this one rather than drop it into a wall.
                if (!SpawnPlacement.TryResolve(around, 6f, 28f, out Vector3 p))
                    continue;

                string model = models[_rng.Next(models.Count)];

                Ped ped;
                try
                {
                    ped = new Ped(model, p, _rng.Next(0, 360));
                }
                catch (Exception ex)
                {
                    Logger.Warn("Skirmish ped '" + model + "' failed: " + ex.Message);
                    continue;
                }

                if (!ped.Exists())
                    continue;

                ped.IsPersistent = true;
                ped.BlockPermanentEvents = false;
                ped.RelationshipGroup = group;
                CombatProfile.Apply(ped, CombatProfile.Tier.Standard); // same feel as an Aggressive grunt
                ped.Inventory.GiveNewWeapon("weapon_microsmg", -1, true);

                _peds.Add(ped);
                list.Add(ped);
                GameFiber.Sleep(60);
            }

            return list;
        }

        // --- Geometry helpers (kept local so this director is fully self-contained) -------

        private Vector3 RingPoint(Vector3 center, float radius, int index, int count, float spreadFactor)
        {
            double baseAngle = (index / (double)Math.Max(1, count)) * Math.PI * 2.0;
            double jitter = (_rng.NextDouble() - 0.5) * 0.7;
            double angle = baseAngle + jitter;
            float distance = radius * spreadFactor * (0.5f + (float)_rng.NextDouble() * 0.8f);

            float x = center.X + (float)Math.Cos(angle) * distance;
            float y = center.Y + (float)Math.Sin(angle) * distance;
            return new Vector3(x, y, center.Z);
        }
    }
}