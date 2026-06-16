using System;
using System.Collections.Generic;
using DynamicHostileTerritories.Core;
using DynamicHostileTerritories.Data;
using Rage;
using Rage.Native;

namespace DynamicHostileTerritories.Services
{
    /// <summary>
    /// Owns every entity spawned for the active territory and keeps it feeling like a
    /// LIVING, gang-controlled block: posted lookouts (armed at higher tiers), members
    /// patrolling/circulating the turf, others loitering, plus a manned roadblock. When
    /// provoked, mobile members peel off to ambush/encircle the player; when a war is
    /// nearly lost the survivors can surrender. The ambient presence runs continuously;
    /// the EncounterDirector layers alert/combat on top. Nothing it spawns outlives
    /// Deactivate(). All combat stats/brain come from the shared CombatProfile.
    /// </summary>
    public sealed class GangSpawnManager
    {
        private static readonly string[] WatchfulWeapons =
        {
            "weapon_pistol", "weapon_pistol", "weapon_combatpistol", "weapon_microsmg"
        };

        private static readonly string[] AggressiveWeapons =
        {
            "weapon_microsmg", "weapon_smg", "weapon_assaultrifle", "weapon_pumpshotgun"
        };

        private static readonly string[] WarzoneWeapons =
        {
            "weapon_carbinerifle", "weapon_assaultrifle", "weapon_specialcarbine", "weapon_smg", "weapon_pumpshotgun"
        };

        // Minimum distance any spawned ped must be from the player. Combined with the strict
        // SpawnPlacement.TryResolve, this is what stops peds materialising in the player's face.
        private const float MinSpawnDistanceFromPlayer = 40f;

        // Gang-vs-gang skirmish, fully owned by its own director (separate peds/groups).
        private readonly SkirmishDirector _skirmish = new SkirmishDirector();

        // Enemy map blips, fully owned by their own manager (created only when hostile).
        private readonly EnemyBlipManager _blipManager = new EnemyBlipManager();

        // Relationship-group plumbing (cache, gang setup, hostility), owned externally.
        private readonly GangRelationships _relationships = new GangRelationships();

        // AI / posture tasking, owned externally.
        private readonly SquadCommander _commander = new SquadCommander();

        private readonly int _maxPeds;
        private readonly Random _rng = new Random();

        private readonly List<Member> _members = new List<Member>();
        private readonly List<Ped> _peds = new List<Ped>(); // mirror, kept in sync for cleanup + police detection
        private readonly List<Vehicle> _vehicles = new List<Vehicle>();

        private RelationshipGroup _gangGroup;
        private bool _hasGangGroup;
        private bool _isActive;

        private Territory _territory;
        private HostilityLevel _tier;
        private Vector3 _roadblockPos;
        private bool _hasRoadblock;
        private bool _surrendered;

        private Member _boss; // the area lieutenant, if one is present this activation

        public IReadOnlyList<Ped> SpawnedPeds => _peds;
        public bool IsActive => _isActive;

        /// <summary>The living area lieutenant ped, or null if there isn't one.</summary>
        public Ped BossPed
        {
            get { return (_boss != null && _boss.Ped != null && _boss.Ped.Exists()) ? _boss.Ped : null; }
        }

        public GangSpawnManager(int maxPeds)
        {
            _maxPeds = maxPeds;
        }

        /// <summary>
        /// Spawns and stages the gang for the given tier. Spawns are staggered across
        /// frames. Behaviour is NOT set here - call ApplyPosture next.
        /// </summary>
        public void Activate(Territory territory, HostilityLevel tier)
        {
            if (_isActive)
                Deactivate();

            _territory = territory;
            _tier = tier;
            _hasRoadblock = false;
            _surrendered = false;

            if (tier == HostilityLevel.Pacified || _maxPeds <= 0)
            {
                _hasGangGroup = false;
                _isActive = true; // active but intentionally empty
                Logger.Debug("Activated " + territory.Name + " with no peds (tier " + tier + ").");
                return;
            }

            _gangGroup = _relationships.SetupGang(territory.ControllingGang.Name);
            _hasGangGroup = true;

            // Force the turf's navmesh/collision to stream in NOW (the player may still be far
            // away), so every ped resolves a real point INSIDE the turf instead of failing at
            // range and being dumped onto a far street outside the blip.
            SpawnPlacement.EnsureNavmesh(territory.Center, territory.Radius);

            int desired = Math.Min(PedCountFor(tier), _maxPeds);

            // An area lieutenant takes one slot at Aggressive+ so we never blow the cap.
            bool wantBoss = tier >= HostilityLevel.Aggressive && desired > 0;
            int crew = Math.Max(0, desired - (wantBoss ? 1 : 0));

            int guards = tier >= HostilityLevel.Warzone ? 3 : (tier >= HostilityLevel.Aggressive ? 2 : 0);
            guards = Math.Min(guards, crew);

            if (guards > 0 && !SpawnRoadblockVehicle(territory))
                guards = 0;
            else if (guards > 0)
                _hasRoadblock = true;

            int remaining = crew - guards;
            int patrols = Math.Max(1, remaining / 3);
            int sentries = remaining / 3;
            // whatever is left becomes loiterers

            int spawned = 0;
            for (int i = 0; i < crew; i++)
            {
                Role role;
                if (i < guards) role = Role.Guard;
                else if (i < guards + patrols) role = Role.Patrol;
                else if (i < guards + patrols + sentries) role = Role.Sentry;
                else role = Role.Loiter;

                // Roadblock guards cluster (loosely) around the car; everyone else is spread
                // evenly around the ring so the turf doesn't spawn three peds on one spot.
                Vector3 pos = (role == Role.Guard && _hasRoadblock)
                    ? RandomPointAround(_roadblockPos, 7f)
                    : RingPoint(territory.Center, territory.Radius, i, crew);

                if (SpawnMember(pos, role, tier))
                    spawned++;

                GameFiber.Sleep(100);
            }

            bool bossSpawned = false;
            if (wantBoss)
            {
                Vector3 bossPos = RingPoint(territory.Center, territory.Radius, 0, 1, 0.35f);
                if (SpawnBoss(bossPos, tier))
                {
                    spawned++;
                    bossSpawned = true;
                }
            }

            _isActive = true;
            Logger.Info("Activated " + territory.Name + " (" + territory.ControllingGang.Name
                + ", tier " + tier + "): " + spawned + "/" + desired + " peds (" + patrols + " patrol, "
                + sentries + " sentry, " + guards + " roadblock, boss " + bossSpawned + "), roadblock " + _hasRoadblock + ".");
        }

        /// <summary>
        /// Reinforcements are now part of the INITIAL garrison: the full crew is staged when
        /// the turf activates (while the player is still approaching, with the navmesh forced
        /// to load), so nothing "drives in" and materialises behind the player mid-fight.
        /// Kept as a no-op so the EncounterDirector's call site does not need to change.
        /// </summary>
        public void Reinforce(Territory territory, HostilityLevel tier)
        {
            // Intentionally empty - see summary. Pre-staging the whole garrison up front is
            // what removes the "ped spawning on my back out of nowhere" problem.
        }

        /// <summary>Number of members still alive and able to fight.</summary>
        public int LivingFighters
        {
            get
            {
                int n = 0;
                foreach (Member m in _members)
                    if (m.Ped.Exists() && !m.Ped.IsDead) n++;
                return n;
            }
        }

        /// <summary>
        /// The crew throws in the towel: stands down, hands up, no longer hostile to the
        /// player or cops, so the survivors can be cuffed (arrests still drop the grip).
        /// Idempotent - returns true only on the transition into surrender.
        /// </summary>
        public bool Surrender()
        {
            if (!_hasGangGroup || _surrendered)
                return false;

            _surrendered = true;

            _relationships.SetNeutral(_gangGroup);

            int count = 0;
            foreach (Member m in _members)
            {
                if (!m.Ped.Exists() || m.Ped.IsDead)
                    continue;

                m.Ped.Tasks.Clear();
                m.Ped.BlockPermanentEvents = true;
                NativeFunction.Natives.TASK_HANDS_UP(m.Ped, -1, 0, -1, 0);
                count++;
            }

            Logger.Info("Gang surrendered (" + count + " hands up).");
            return true;
        }

        /// <summary>
        /// The crew's hold is broken with fighters still standing. Instead of everyone
        /// throwing their hands up, each survivor reacts on its own: ~40% surrender (hands
        /// up), ~30% flee, ~30% fight to the death. Surrendering/fleeing peds are moved to
        /// a neutral group so they stop being hostile, while the diehards stay in the gang
        /// group and keep fighting. Idempotent - returns true only on the first call.
        /// </summary>
        public bool BreakResolve()
        {
            if (!_hasGangGroup || _surrendered)
                return false;

            _surrendered = true; // stop ApplyPosture from re-tasking the whole squad

            Ped player = Game.LocalPlayer.Character;
            bool playerOk = player != null && player.Exists();
            RelationshipGroup neutral = _relationships.StoodDownGroup();

            int gaveUp = 0, fled = 0, fighting = 0;
            foreach (Member m in _members)
            {
                if (!m.Ped.Exists() || m.Ped.IsDead)
                    continue;

                double roll = _rng.NextDouble();

                if (roll < 0.40)
                {
                    // Surrender: hands up, no longer hostile, ready to be cuffed.
                    m.Ped.Tasks.Clear();
                    m.Ped.BlockPermanentEvents = true;
                    m.Ped.RelationshipGroup = neutral;
                    NativeFunction.Natives.TASK_HANDS_UP(m.Ped, -1, 0, -1, 0);
                    gaveUp++;
                }
                else if (roll < 0.70)
                {
                    // Flee: bolt away from the player, no longer hostile.
                    m.Ped.Tasks.Clear();
                    m.Ped.BlockPermanentEvents = false;
                    m.Ped.RelationshipGroup = neutral;

                    Vector3 away = m.Ped.Position;
                    if (playerOk)
                    {
                        Vector3 dir = m.Ped.Position - player.Position;
                        float len = dir.Length();
                        if (len > 0.1f)
                            away = m.Ped.Position + (dir / len) * 60f;
                    }
                    m.Ped.Tasks.FollowNavigationMeshToPosition(away, 0f, 3.0f);
                    fled++;
                }
                else
                {
                    // Fight to the death: stays in the gang (hostile) group.
                    m.Ped.Tasks.Clear();
                    m.Ped.BlockPermanentEvents = false;
                    if (playerOk)
                        NativeFunction.Natives.TASK_COMBAT_PED(m.Ped, player, 0, 16);
                    fighting++;
                }
            }

            Logger.Info("Crew broke: " + gaveUp + " surrendered, " + fled + " fled, " + fighting + " fighting on.");
            return true;
        }

        // --- Gang-vs-gang skirmish (self-contained ambient event) -------------------------

        /// <summary>
        /// A rival crew rolls into the turf and fights the controlling gang. These are
        /// their own peds in their own groups (neutral to the player and cops) and are
        /// NOT part of the encounter - killing them does not move the grip. The player can
        /// just watch the two sides go at it, or wade in. Returns true if it started.
        /// </summary>
        public bool TriggerSkirmish(Territory territory)
        {
            // Guarded by the squad's own state; the skirmish itself is self-contained.
            if (!_isActive || _surrendered)
                return false;

            return _skirmish.TryStart(territory);
        }

        /// <summary>Cleans the skirmish up once it times out or one side is wiped.</summary>
        public void UpdateSkirmish()
        {
            _skirmish.Update();
        }

        /// <summary>
        /// Shows or hides the red grunt blips. They only appear once the crew is hostile
        /// (Provoked/War) so a quiet or watchful turf stays clean on the map. The boss
        /// blip is created on demand the same way (gold) by the blip manager.
        /// </summary>
        private void SetGruntBlipsVisible(bool show)
        {
            string gangName = _territory != null ? _territory.ControllingGang.Name : null;
            _blipManager.SetVisible(_peds, BossPed, gangName, show);
        }

        /// <summary>
        /// Removes the map blip from any member that has died or despawned, so a dead
        /// enemy's blip disappears immediately instead of lingering until Deactivate.
        /// The dead ped itself stays in the list so the controller can still credit the kill.
        /// </summary>
        public void PruneDeadBlips()
        {
            _blipManager.Prune();
        }

        public void Deactivate()
        {
            int peds = _peds.Count;
            _skirmish.End();

            _blipManager.Clear();

            foreach (Member m in _members)
            {
                if (m.Ped.Exists()) m.Ped.Delete();
            }
            _members.Clear();
            _peds.Clear();

            foreach (Vehicle v in _vehicles)
                if (v.Exists()) v.Delete();
            _vehicles.Clear();

            _boss = null;
            _hasGangGroup = false;
            _hasRoadblock = false;
            _surrendered = false;
            _isActive = false;

            if (peds > 0)
                Logger.Debug("Deactivated and cleaned up " + peds + " peds.");
        }

        /// <summary>
        /// Re-tasks every living member for the current posture, and sets how the gang
        /// feels about the player and the cops. Observing = the living ambient presence;
        /// Suspicious = noticed you, posting up armed; Provoked = ambush/encircle; War =
        /// open combat.
        /// </summary>
        public void ApplyPosture(EncounterState state, HostilityLevel tier)
        {
            if (!_hasGangGroup)
                return;

            if (_surrendered)
                return; // hands up - never re-task them back into the fight

            ApplyRelationship(state);

            // Red grunt blips only while the crew is actually hostile.
            SetGruntBlipsVisible((int)state >= (int)EncounterState.Provoked);

            // AI tasking is delegated to the commander.
            _commander.Command(_members, _territory, state, tier);
        }

        // --- Spawning ---------------------------------------------------------------------

        private bool SpawnMember(Vector3 around, Role role, HostilityLevel tier)
        {
            string model = Pick(_territory.ControllingGang.PedModels);

            if (!TryResolveSpawnPoint(around, out Vector3 spawnPos))
                return false; // no safe point inside the turf away from the player - spawn one fewer

            float heading = _rng.Next(0, 360);

            Ped ped;
            try
            {
                ped = new Ped(model, spawnPos, heading);
            }
            catch (Exception ex)
            {
                Logger.Warn("Failed to spawn ped model '" + model + "': " + ex.Message);
                return false;
            }

            if (!ped.Exists())
            {
                Logger.Warn("Ped model '" + model + "' did not materialise (invalid model?).");
                return false;
            }

            string weapon = PickWeapon(tier);

            ped.IsPersistent = true;
            ped.BlockPermanentEvents = true;
            ped.RelationshipGroup = _gangGroup;
            CombatProfile.Apply(ped, CombatProfile.FromHostility(tier));
            ped.Inventory.GiveNewWeapon(weapon, -1, false); // holstered until staged/alerted

            // Grunt blips are created on demand only when the crew turns hostile
            // (see SetGruntBlipsVisible), so a quiet/watchful turf shows no red dots.
            _members.Add(new Member { Ped = ped, Home = spawnPos, Role = role, Weapon = weapon });
            _peds.Add(ped); // MUST be tracked here, or police actions on grunts never count
            return true;
        }

        /// <summary>
        /// Spawns the area lieutenant: a tougher boss using the gang's boss/lieutenant model
        /// when it has one. Its gold blip is created on demand by the blip manager. The
        /// controller treats this ped specially - neutralising them craters the grip.
        /// </summary>
        private bool SpawnBoss(Vector3 around, HostilityLevel tier)
        {
            string model = PickBossModel(_territory.ControllingGang.PedModels);

            if (!TryResolveSpawnPoint(around, out Vector3 spawnPos))
                return false; // no safe point - skip the boss rather than drop it badly

            Ped ped;
            try
            {
                ped = new Ped(model, spawnPos, _rng.Next(0, 360));
            }
            catch (Exception ex)
            {
                Logger.Warn("Failed to spawn boss model '" + model + "': " + ex.Message);
                return false;
            }

            if (!ped.Exists())
            {
                Logger.Warn("Boss model '" + model + "' did not materialise (invalid model?).");
                return false;
            }

            const string bossWeapon = "weapon_carbinerifle";

            ped.IsPersistent = true;
            ped.BlockPermanentEvents = true;
            ped.RelationshipGroup = _gangGroup;
            ped.MaxHealth = 400;
            ped.Health = 400;
            CombatProfile.Apply(ped, CombatProfile.Tier.Boss); // tougher-but-not-aimbot brain + accuracy + armour
            ped.Inventory.GiveNewWeapon(bossWeapon, -1, false);

            Member boss = new Member
            {
                Ped = ped,
                Home = spawnPos,
                Role = Role.Sentry,
                Weapon = bossWeapon,
                IsBoss = true
            };

            _members.Add(boss);
            _peds.Add(ped);
            _boss = boss;

            Logger.Info("Area lieutenant spawned at " + _territory.Name + " (" + _territory.ControllingGang.Name + ").");
            return true;
        }

        /// <summary>Prefers a boss/lieutenant model if the gang defines one, else any model.</summary>
        private string PickBossModel(IReadOnlyList<string> models)
        {
            if (models == null || models.Count == 0)
                return "g_m_m_armboss_01"; // safe generic fallback

            foreach (string m in models)
            {
                if (m.IndexOf("boss", StringComparison.OrdinalIgnoreCase) >= 0
                    || m.IndexOf("lieut", StringComparison.OrdinalIgnoreCase) >= 0)
                    return m;
            }

            return models[_rng.Next(models.Count)];
        }

        private bool SpawnRoadblockVehicle(Territory territory)
        {
            string model = Pick(territory.ControllingGang.VehicleModels);
            Vector3 pos = World.GetNextPositionOnStreet(RandomPointAround(territory.Center, territory.Radius * 0.4f));

            try
            {
                Model vModel = new Model(model);
                if (!vModel.IsValid)
                {
                    Logger.Warn("Roadblock vehicle model '" + model + "' is invalid.");
                    return false;
                }

                Vehicle vehicle = new Vehicle(vModel, pos);
                if (!vehicle.Exists())
                {
                    Logger.Warn("Roadblock vehicle model '" + model + "' did not materialise.");
                    return false;
                }

                vehicle.IsPersistent = true;
                _vehicles.Add(vehicle);
                _roadblockPos = vehicle.Position;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn("Failed to spawn roadblock vehicle '" + model + "': " + ex.Message);
                return false;
            }
        }

        private void ApplyRelationship(EncounterState state)
        {
            _relationships.SetHostility(_gangGroup, state);
        }

        // --- Helpers ----------------------------------------------------------------------

        private int PedCountFor(HostilityLevel tier)
        {
            // Scales with MaxSpawnedPeds from the .ini, so raising the cap raises the
            // whole presence. Warzone fills the cap; weaker tiers are intentionally lighter.
            switch (tier)
            {
                case HostilityLevel.Watchful: return Math.Max(3, _maxPeds / 3);        // ~1/3 of the cap
                case HostilityLevel.Aggressive: return Math.Max(6, (_maxPeds * 2) / 3); // ~2/3 of the cap
                case HostilityLevel.Warzone: return _maxPeds;                          // full cap
                default: return 0;
            }
        }

        private string PickWeapon(HostilityLevel tier)
        {
            switch (tier)
            {
                case HostilityLevel.Watchful: return WatchfulWeapons[_rng.Next(WatchfulWeapons.Length)];
                case HostilityLevel.Aggressive: return AggressiveWeapons[_rng.Next(AggressiveWeapons.Length)];
                case HostilityLevel.Warzone: return WarzoneWeapons[_rng.Next(WarzoneWeapons.Length)];
                default: return "weapon_unarmed";
            }
        }

        private string Pick(IReadOnlyList<string> options)
        {
            return options[_rng.Next(options.Count)];
        }

        private Vector3 RingPoint(Vector3 center, float radius, int index, int count, float spreadFactor = 0.9f)
        {
            // Spread members around the core so the turf feels occupied as you push in.
            double baseAngle = (index / (double)Math.Max(1, count)) * Math.PI * 2.0;
            double jitter = (_rng.NextDouble() - 0.5) * 0.7;
            double angle = baseAngle + jitter;
            float distance = radius * spreadFactor * (0.55f + (float)_rng.NextDouble() * 0.5f);

            float x = center.X + (float)Math.Cos(angle) * distance;
            float y = center.Y + (float)Math.Sin(angle) * distance;
            return new Vector3(x, y, center.Z);
        }

        private Vector3 RandomPointAround(Vector3 center, float radius)
        {
            double angle = _rng.NextDouble() * Math.PI * 2.0;
            float distance = (float)(_rng.NextDouble() * radius);
            float x = center.X + (float)Math.Cos(angle) * distance;
            float y = center.Y + (float)Math.Sin(angle) * distance;
            return new Vector3(x, y, center.Z);
        }

        /// <summary>
        /// Resolves a usable spawn point near 'around' via the shared SpawnPlacement helper.
        /// We bias the INPUT inside the turf, then accept ONLY a navmesh-safe point that is
        /// also a safe distance from the player. There is NO blind street fallback any more
        /// (that was what scattered peds outside the blip): if nothing qualifies we return
        /// false and the caller simply spawns one ped fewer.
        /// </summary>
        private bool TryResolveSpawnPoint(Vector3 around, out Vector3 pos)
        {
            around = ClampToTurf(around);
            return SpawnPlacement.TryResolve(around, _territory.Radius * 0.45f, MinSpawnDistanceFromPlayer, out pos);
        }

        /// <summary>
        /// Pulls a point back inside the turf radius (95% of it). Applied to the spawn INPUT
        /// only, never to a resolved safe coord - clamping a safe coord is what used to push
        /// peds into a neighbouring building.
        /// </summary>
        private Vector3 ClampToTurf(Vector3 p)
        {
            if (_territory == null)
                return p;

            Vector3 c = _territory.Center;
            float dx = p.X - c.X;
            float dy = p.Y - c.Y;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy);
            float max = _territory.Radius * 0.95f;

            if (dist > max && dist > 0.01f)
            {
                float scale = max / dist;
                return new Vector3(c.X + dx * scale, c.Y + dy * scale, p.Z);
            }
            return p;
        }
    }
}