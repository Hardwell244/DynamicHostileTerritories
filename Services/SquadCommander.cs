using System;
using System.Collections.Generic;
using DynamicHostileTerritories.Core;
using DynamicHostileTerritories.Data;
using Rage;
using Rage.Native;

namespace DynamicHostileTerritories.Services
{
    /// <summary>
    /// Commands the squad's AI: turns the current encounter state into ped tasks. Observing
    /// is the living ambient presence (lookouts, loiterers, patrols); Suspicious posts up
    /// armed and watches; Provoked springs an encircling ambush; War is open combat. It only
    /// tasks peds — relationships and blips are handled by the squad before this runs.
    /// </summary>
    public sealed class SquadCommander
    {
        private static readonly string[] LoiterScenarios =
        {
            "WORLD_HUMAN_SMOKING",
            "WORLD_HUMAN_STAND_MOBILE",
            "WORLD_HUMAN_HANG_OUT_STREET",
            "WORLD_HUMAN_DRINKING",
            "WORLD_HUMAN_LEANING",
            "WORLD_HUMAN_AA_SMOKE"
        };

        private readonly Random _rng = new Random();

        /// <summary>Re-tasks every living member for the given encounter state.</summary>
        public void Command(IReadOnlyList<Member> members, Territory territory, EncounterState state, HostilityLevel tier)
        {
            Ped player = Game.LocalPlayer.Character;
            if (player == null || !player.Exists() || !player.IsAlive)
                return;

            // For the Provoked ambush: mobile members (patrol/loiter) peel off to encircle
            // the player from different bearings while the posted ones hold the core.
            int flankTotal = 0;
            if (state == EncounterState.Provoked)
                foreach (Member fm in members)
                    if (fm.Role == Role.Patrol || fm.Role == Role.Loiter) flankTotal++;
            int flankIndex = 0;

            foreach (Member m in members)
            {
                if (!m.Ped.Exists())
                    continue;

                switch (state)
                {
                    case EncounterState.Observing:
                        StagePresence(m, territory, tier);
                        break;

                    case EncounterState.Suspicious:
                        // Noticed you: stop, draw, face you and hold the spot. No chasing.
                        m.Ped.Tasks.Clear();
                        m.Ped.BlockPermanentEvents = false;
                        EquipWeapon(m);
                        NativeFunction.Natives.TASK_TURN_PED_TO_FACE_ENTITY(m.Ped, player, 4000);
                        NativeFunction.Natives.TASK_GUARD_CURRENT_POSITION(m.Ped, 8f, 8f, true);
                        break;

                    case EncounterState.Provoked:
                        // AMBUSH: mobile members move to surround the player from several
                        // bearings; posted members (sentry/roadblock) hold the core. When
                        // war breaks out everyone is already closing the pincer.
                        m.Ped.Tasks.Clear();
                        m.Ped.BlockPermanentEvents = false;
                        EquipWeapon(m);
                        if (m.Role == Role.Patrol || m.Role == Role.Loiter)
                        {
                            Vector3 flank = EncirclePoint(player.Position, 18f, flankIndex, flankTotal);
                            flankIndex++;
                            m.Ped.Tasks.FollowNavigationMeshToPosition(flank, 0f, 2.5f);
                        }
                        else
                        {
                            NativeFunction.Natives.TASK_GUARD_CURRENT_POSITION(m.Ped, 20f, 20f, true);
                        }
                        break;

                    case EncounterState.War:
                        // Open combat: the AI advances, flanks and uses cover on its own.
                        m.Ped.Tasks.Clear();
                        m.Ped.BlockPermanentEvents = false;
                        NativeFunction.Natives.TASK_COMBAT_PED(m.Ped, player, 0, 16);
                        break;
                }
            }

            Logger.Debug("Applied posture " + state + " to " + members.Count + " members.");
        }

        // --- Living ambient presence (Observing) ------------------------------------------

        private void StagePresence(Member m, Territory territory, HostilityLevel tier)
        {
            m.Ped.BlockPermanentEvents = true;
            bool armedPresence = tier >= HostilityLevel.Aggressive;

            switch (m.Role)
            {
                case Role.Sentry:
                case Role.Guard:
                    if (armedPresence)
                    {
                        // Armed lookout: stands holding the weapon, watching the block.
                        EquipWeapon(m);
                        NativeFunction.Natives.TASK_GUARD_CURRENT_POSITION(m.Ped, 5f, 5f, true);
                    }
                    else
                    {
                        NativeFunction.Natives.TASK_START_SCENARIO_IN_PLACE(m.Ped, "WORLD_HUMAN_GUARD_STAND", 0, true);
                    }
                    break;

                case Role.Loiter:
                    string scenario = LoiterScenarios[_rng.Next(LoiterScenarios.Length)];
                    NativeFunction.Natives.TASK_START_SCENARIO_IN_PLACE(m.Ped, scenario, 0, true);
                    break;

                case Role.Patrol:
                    // Walks the block. Weapon holstered while patrolling; drawn when alerted.
                    Vector3 c = territory.Center;
                    NativeFunction.Natives.TASK_WANDER_IN_AREA(m.Ped, c.X, c.Y, c.Z, territory.Radius * 0.7f, 4f, 7f);
                    break;
            }
        }

        private void EquipWeapon(Member m)
        {
            if (string.IsNullOrEmpty(m.Weapon) || m.Weapon == "weapon_unarmed")
                return;

            NativeFunction.Natives.SET_CURRENT_PED_WEAPON(m.Ped, Game.GetHashKey(m.Weapon), true);
        }

        /// <summary>
        /// A snapped point on a ring around the player, used to surround them during an
        /// ambush. Bearings are spread so closers come in from different sides. Resolved
        /// through the shared SpawnPlacement helper (grounded, navmesh-safe); if nothing
        /// resolves we fall back to the raw ring point.
        /// </summary>
        private Vector3 EncirclePoint(Vector3 playerPos, float radius, int index, int count)
        {
            double angle = (index / (double)Math.Max(1, count)) * Math.PI * 2.0;
            float x = playerPos.X + (float)Math.Cos(angle) * radius;
            float y = playerPos.Y + (float)Math.Sin(angle) * radius;

            Vector3 point = new Vector3(x, y, playerPos.Z);
            if (SpawnPlacement.TryResolve(point, 4f, 0f, out Vector3 safe))
                return safe;
            return point;
        }
    }
}