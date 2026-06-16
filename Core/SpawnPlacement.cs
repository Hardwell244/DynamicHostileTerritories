using System;
using Rage;
using Rage.Native;

namespace DynamicHostileTerritories.Core
{
    /// <summary>
    /// Shared spawn-placement utilities used by EVERY system that spawns peds or vehicles
    /// (turf garrison, reinforcements, skirmishes, ambient events, retaliation). Centralised
    /// on purpose: a placement fix lands everywhere at once instead of being patched into one
    /// system at a time.
    /// </summary>
    public static class SpawnPlacement
    {
        private static readonly Random _rng = new Random();

        /// <summary>
        /// Resolves a safe, on-foot-reachable, grounded point near 'around', at least
        /// 'minFromPlayer' metres from the player. Returns false if nothing safe resolves -
        /// the caller should then SKIP that spawn rather than drop a ped into a wall.
        ///
        /// We trust GET_SAFE_COORD_FOR_PED's result and only correct its HEIGHT. We never
        /// shove the point sideways afterwards (doing that is what used to push peds inside
        /// buildings). If the navmesh around 'around' isn't streamed yet GET_SAFE_COORD
        /// fails and we return false - which is why callers force the navmesh in first
        /// (EnsureNavmesh) and skip the spawn when this still can't resolve.
        /// </summary>
        public static bool TryResolve(Vector3 around, float scatter, float minFromPlayer, out Vector3 result)
        {
            Ped player = Game.LocalPlayer.Character;
            bool havePlayer = player != null && player.Exists();
            Vector3 playerPos = havePlayer ? player.Position : Vector3.Zero;

            for (int attempt = 0; attempt < 16; attempt++)
            {
                Vector3 candidate = attempt == 0 ? around : ScatterAround(around, scatter);

                if (NativeFunction.Natives.GET_SAFE_COORD_FOR_PED<bool>(
                        candidate.X, candidate.Y, candidate.Z, true, out Vector3 safe, 0))
                {
                    safe = GroundSnap(safe);

                    // STRICT: only accept a point that is genuinely far enough from the
                    // player. We NEVER return a too-close "fallback" any more - that was
                    // exactly what made peds materialise in the player's face. If nothing
                    // qualifies, return false and the caller spawns one ped fewer instead.
                    if (!havePlayer || safe.DistanceTo(playerPos) >= minFromPlayer)
                    {
                        result = safe;
                        return true;
                    }
                }
            }

            result = around;
            return false;
        }

        /// <summary>
        /// Forces the navmesh + collision around a point to stream in - even while the player
        /// is still far away - and waits (bounded, ~2s) until it's actually loaded. This is
        /// what lets a turf place its crew correctly while you're still approaching, instead
        /// of GET_SAFE_COORD failing at range and the spawn being dumped onto a far street
        /// outside the turf. Never throws.
        /// </summary>
        public static void EnsureNavmesh(Vector3 center, float radius)
        {
            NativeFunction.Natives.REQUEST_COLLISION_AT_COORD(center.X, center.Y, center.Z);
            NativeFunction.Natives.ADD_NAVMESH_REQUIRED_REGION(center.X, center.Y, radius);

            for (int tries = 0; tries < 40; tries++)
            {
                bool loaded = NativeFunction.Natives.IS_NAVMESH_LOADED_IN_AREA<bool>(
                    center.X - radius, center.Y - radius, center.Z - 50f,
                    center.X + radius, center.Y + radius, center.Z + 50f);
                if (loaded)
                    return;

                NativeFunction.Natives.ADD_NAVMESH_REQUIRED_REGION(center.X, center.Y, radius);
                NativeFunction.Natives.REQUEST_COLLISION_AT_COORD(center.X, center.Y, center.Z);
                GameFiber.Sleep(50);
            }
        }

        /// <summary>
        /// Z-ONLY ground snap. Reads the real ground height and drops the point onto it.
        /// It never moves X/Y, so it can NEVER push a ped sideways into a wall. If the
        /// collision under the point isn't streamed yet it leaves the point unchanged.
        /// </summary>
        public static Vector3 GroundSnap(Vector3 p)
        {
            NativeFunction.Natives.REQUEST_COLLISION_AT_COORD(p.X, p.Y, p.Z);

            for (int i = 0; i < 6; i++)
            {
                float groundZ;
                if (NativeFunction.Natives.GET_GROUND_Z_FOR_3D_COORD<bool>(
                        p.X, p.Y, p.Z + 5f, out groundZ, false) && groundZ != 0f)
                {
                    return new Vector3(p.X, p.Y, groundZ);
                }

                GameFiber.Sleep(25);
                NativeFunction.Natives.REQUEST_COLLISION_AT_COORD(p.X, p.Y, p.Z);
            }

            return p;
        }

        /// <summary>
        /// Pushes a point at least 'minDist' metres from the player, radially outward, so a
        /// crew never materialises right next to / behind the player.
        /// </summary>
        public static Vector3 PushFromPlayer(Vector3 p, float minDist)
        {
            Ped player = Game.LocalPlayer.Character;
            if (player == null || !player.Exists())
                return p;

            Vector3 pp = player.Position;
            float dx = p.X - pp.X, dy = p.Y - pp.Y;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy);
            if (dist >= minDist)
                return p;
            if (dist < 0.01f) { dx = 1f; dy = 0f; dist = 1f; }

            float scale = minDist / dist;
            return new Vector3(pp.X + dx * scale, pp.Y + dy * scale, p.Z);
        }

        /// <summary>
        /// Total seats in a vehicle model (driver included). Lets a crew size itself to the
        /// vehicle: a 4-door fills 4, a 2-seater or bike fills 2 - so a gunman is never left
        /// standing on the roof.
        /// </summary>
        public static int SeatCount(Model vehicleModel)
        {
            int seats = NativeFunction.Natives.GET_VEHICLE_MODEL_NUMBER_OF_SEATS<int>(vehicleModel.Hash);
            return seats < 1 ? 1 : seats;
        }

        private static Vector3 ScatterAround(Vector3 center, float radius)
        {
            double angle = _rng.NextDouble() * Math.PI * 2.0;
            float distance = (float)(_rng.NextDouble() * radius);
            float x = center.X + (float)Math.Cos(angle) * distance;
            float y = center.Y + (float)Math.Sin(angle) * distance;
            return new Vector3(x, y, center.Z);
        }
    }
}