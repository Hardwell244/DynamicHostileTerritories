using System;
using System.Collections.Generic;
using System.Drawing;
using DynamicHostileTerritories.Core;
using DynamicHostileTerritories.Data;
using Rage;
using Rage.Native;

namespace DynamicHostileTerritories.Services
{
    /// <summary>
    /// Gang retaliation. When the player neutralises a gang's people in one of its turfs,
    /// that gang holds a grudge and - a good while later (10-15 min) - sends ONE carload of
    /// gunmen to hunt the player out on the streets. The crew has random weapons and poor,
    /// erratic aim so it's a threat without being cheap, and carries map blips. Only one
    /// squad is ever out at a time. Runs on its own fiber, owns everything it spawns, and
    /// cleans up when the crew is wiped, loses the player, or times out. No Abort.
    /// </summary>
    public sealed class RetaliationDirector
    {
        private sealed class Hunter
        {
            public Ped Ped;
            public Blip Blip;
        }

        private static readonly string[] HitWeapons =
        {
            "weapon_pistol", "weapon_combatpistol", "weapon_microsmg",
            "weapon_smg", "weapon_pumpshotgun", "weapon_assaultrifle"
        };

        // Tunables (tranquilo: one squad, a long fuse).
        private const double DelayMinMinutes = 10.0;
        private const double DelayMaxMinutes = 15.0;
        private const int SquadSize = 4;
        private const float SpawnDistance = 130f;       // spawn this far from the player, BEHIND them, on a street
        private const float DespawnDistance = 275f;     // if they lose the player by this much, give up
        private const double SquadTimeoutMinutes = 5.0; // a squad that never connects expires

        private readonly TerritoryRepository _repository;
        private readonly Random _rng = new Random();
        private readonly List<Hunter> _hunters = new List<Hunter>();

        private Vehicle _vehicle;
        private RelationshipGroup _group;
        private bool _hasGroup;

        private Gang _pendingGang;
        private DateTime _dispatchAtUtc;
        private bool _hasPending;

        private bool _squadActive;
        private DateTime _squadDispatchUtc;

        private GameFiber _loop;
        private bool _running;
        private int _generation;

        public RetaliationDirector(TerritoryRepository repository)
        {
            _repository = repository;
        }

        public void Start()
        {
            if (_running)
                return;

            _running = true;
            int generation = ++_generation;
            _loop = GameFiber.StartNew(() => Loop(generation));
            Logger.Info("Retaliation director started.");
        }

        public void Stop()
        {
            if (!_running && _loop == null)
                return;

            _running = false;
            _generation++; // invalidate the loop so it ends itself
            _loop = null;  // no Abort

            Cleanup();
            _hasPending = false;
            _pendingGang = null;

            Logger.Info("Retaliation director stopped.");
        }

        /// <summary>
        /// Called when the player has just neutralised someone in this gang's turf. Schedules
        /// a single retaliation a good while out. Ignored if a squad is already pending or out
        /// - only one at a time, so it never turns into a car every five seconds.
        /// </summary>
        public void RegisterAggravation(Gang gang)
        {
            if (gang == null || _hasPending || _squadActive)
                return;

            double delay = DelayMinMinutes + _rng.NextDouble() * (DelayMaxMinutes - DelayMinMinutes);
            _pendingGang = gang;
            _dispatchAtUtc = DateTime.UtcNow.AddMinutes(delay);
            _hasPending = true;

            Logger.Debug("Retaliation scheduled by " + gang.Name + " in ~" + (int)delay + " min.");
        }

        private void Loop(int generation)
        {
            while (_running && generation == _generation)
            {
                try
                {
                    if (_squadActive)
                    {
                        Maintain();
                    }
                    else if (_hasPending && DateTime.UtcNow >= _dispatchAtUtc)
                    {
                        Gang g = _pendingGang;
                        _hasPending = false;
                        _pendingGang = null;
                        Dispatch(g);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("Retaliation cycle error", ex);
                }

                int slept = 0;
                while (slept < 1500 && _running && generation == _generation)
                {
                    GameFiber.Sleep(250);
                    slept += 250;
                }
            }
        }

        private void Dispatch(Gang gang)
        {
            if (gang == null)
                return;

            Ped player = Game.LocalPlayer.Character;
            if (player == null || !player.Exists() || !player.IsAlive)
                return;

            EnsureGroup();

            // Spawn BEHIND the player and well back, on a street, so the crew approaches from
            // the rear and catches up by driving - instead of popping into existence right in
            // front of the player on the highway. The dispatch warning gives them a heads-up.
            Vector3 behind = player.Position - player.ForwardVector * SpawnDistance;
            Vector3 street = World.GetNextPositionOnStreet(behind);

            Model vModel = new Model(PickVehicle(gang));
            if (!vModel.IsValid)
                return;

            try
            {
                _vehicle = new Vehicle(vModel, street);
            }
            catch (Exception ex)
            {
                Logger.Warn("Retaliation vehicle failed: " + ex.Message);
                _vehicle = null;
                return;
            }

            if (_vehicle == null || !_vehicle.Exists())
                return;

            _vehicle.IsPersistent = true;

            for (int i = 0; i < SquadSize; i++)
            {
                Ped ped;
                try
                {
                    ped = new Ped(PickPed(gang), street, 0f);
                }
                catch (Exception ex)
                {
                    Logger.Warn("Retaliation ped failed: " + ex.Message);
                    continue;
                }

                if (!ped.Exists())
                    continue;

                ped.IsPersistent = true;
                ped.BlockPermanentEvents = true;
                ped.RelationshipGroup = _group;
                ped.Accuracy = _rng.Next(3, 13); // bad, erratic aim
                ped.Inventory.GiveNewWeapon(HitWeapons[_rng.Next(HitWeapons.Length)], -1, true);

                int seat = i == 0 ? -1 : i - 1; // -1 driver, then front passenger, then rear
                // Only warp into a seat that actually exists and is free. A 2-seater can't
                // take 4 gunmen, and warping into a missing seat dumps the ped on the roof.
                // Anyone without a seat simply starts on foot next to the car and still hunts.
                if (_vehicle.Exists() && _vehicle.IsSeatFree(seat))
                    ped.WarpIntoVehicle(_vehicle, seat);

                NativeFunction.Natives.TASK_COMBAT_PED(ped, player, 0, 16);

                Blip blip = null;
                try
                {
                    blip = new Blip(ped)
                    {
                        Color = Color.OrangeRed,
                        Scale = 0.8f,
                        Name = gang.Name + " Hit Squad"
                    };
                }
                catch { /* a blip failure must never affect gameplay */ }

                _hunters.Add(new Hunter { Ped = ped, Blip = blip });
            }

            if (_hunters.Count == 0)
            {
                Cleanup();
                return;
            }

            _squadActive = true;
            _squadDispatchUtc = DateTime.UtcNow;
            Notifier.Show("Retaliation", "~r~" + gang.Name,
                "They've sent a crew after you. Watch the streets.");
            Logger.Info("Retaliation squad dispatched by " + gang.Name + " (" + _hunters.Count + " gunmen).");
        }

        private void Maintain()
        {
            Ped player = Game.LocalPlayer.Character;
            bool playerOk = player != null && player.Exists();

            int alive = 0;
            Vector3 anchor = Vector3.Zero;
            bool haveAnchor = false;

            foreach (Hunter h in _hunters)
            {
                if (h.Ped != null && h.Ped.Exists() && !h.Ped.IsDead)
                {
                    alive++;
                    if (!haveAnchor)
                    {
                        anchor = h.Ped.Position;
                        haveAnchor = true;
                    }
                }
                else if (h.Blip != null && h.Blip.Exists())
                {
                    h.Blip.Delete(); // drop the blip the moment a gunman is down
                    h.Blip = null;
                }
            }

            bool tooFar = false;
            if (playerOk && haveAnchor)
                tooFar = player.Position.DistanceTo(anchor) > DespawnDistance;
            else if (playerOk && _vehicle != null && _vehicle.Exists())
                tooFar = player.Position.DistanceTo(_vehicle.Position) > DespawnDistance;

            bool timedOut = (DateTime.UtcNow - _squadDispatchUtc).TotalMinutes >= SquadTimeoutMinutes;

            if (alive == 0 || tooFar || timedOut)
            {
                Logger.Debug("Retaliation squad cleaned up (alive " + alive + ", tooFar " + tooFar + ", timedOut " + timedOut + ").");
                Cleanup();
            }
        }

        private void Cleanup()
        {
            foreach (Hunter h in _hunters)
            {
                if (h.Blip != null && h.Blip.Exists()) h.Blip.Delete();

                if (h.Ped != null && h.Ped.Exists())
                {
                    if (h.Ped.IsDead)
                        h.Ped.IsPersistent = false; // leave the body; let the engine clear it later
                    else
                        h.Ped.Delete();             // a live straggler (timed out / lost the player)
                }
            }
            _hunters.Clear();

            if (_vehicle != null && _vehicle.Exists())
                _vehicle.Delete();
            _vehicle = null;

            _squadActive = false;
        }

        private void EnsureGroup()
        {
            if (_hasGroup)
                return;

            _group = new RelationshipGroup("DHT_Retaliation");

            RelationshipGroup player = Game.LocalPlayer.Character.RelationshipGroup;
            RelationshipGroup cops = RelationshipGroup.Cop;
            _group.SetRelationshipWith(player, Relationship.Hate);
            _group.SetRelationshipWith(cops, Relationship.Hate);
            player.SetRelationshipWith(_group, Relationship.Hate);
            cops.SetRelationshipWith(_group, Relationship.Hate);

            _hasGroup = true;
        }

        private string PickVehicle(Gang gang)
        {
            var v = gang.VehicleModels;
            if (v == null || v.Count == 0)
                return "baller";
            return v[_rng.Next(v.Count)];
        }

        private string PickPed(Gang gang)
        {
            var p = gang.PedModels;
            if (p == null || p.Count == 0)
                return "a_m_y_genstreet_01";
            return p[_rng.Next(p.Count)];
        }
    }
}