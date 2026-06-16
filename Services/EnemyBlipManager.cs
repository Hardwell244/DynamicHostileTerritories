using System.Collections.Generic;
using System.Drawing;
using LSPD_First_Response.Mod.API;
using Rage;
using Rage.Native;

namespace DynamicHostileTerritories.Services
{
    /// <summary>
    /// Owns the enemy map blips for a squad. Blips are created on demand only while the
    /// crew is hostile (red for grunts, a bigger gold marker for the lieutenant) and are
    /// removed the moment a ped dies/despawns or the crew stands down. Keyed by ped, so it
    /// never needs to know anything about the squad's internals. Never throws.
    /// </summary>
    public sealed class EnemyBlipManager
    {
        private readonly Dictionary<Ped, Blip> _blips = new Dictionary<Ped, Blip>();

        /// <summary>
        /// Shows red grunt blips and a gold boss blip for the living peds, or hides them
        /// all. Only adds blips that don't exist yet, so it's safe to call every tick.
        /// </summary>
        public void SetVisible(IReadOnlyList<Ped> peds, Ped boss, string gangName, bool show)
        {
            if (!show)
            {
                Clear();
                return;
            }

            if (peds == null)
                return;

            foreach (Ped ped in peds)
            {
                if (ped == null || !ped.Exists() || ped.IsDead)
                    continue;
                if (_blips.ContainsKey(ped))
                    continue;

                bool isBoss = boss != null && ped == boss;

                try
                {
                    Blip blip;
                    if (isBoss)
                    {
                        // The lieutenant gets a bigger gold marker, visible at any range.
                        blip = new Blip(ped)
                        {
                            Color = Color.Gold,
                            Scale = 1.0f,
                            Name = (gangName ?? "Gang") + " Lieutenant"
                        };
                    }
                    else
                    {
                        blip = new Blip(ped)
                        {
                            Color = Color.Red,
                            Scale = 0.7f,
                            Name = "Gang member"
                        };
                        NativeFunction.Natives.SET_BLIP_AS_SHORT_RANGE(blip, true);
                    }

                    _blips[ped] = blip;
                }
                catch { /* a blip failure must never affect gameplay */ }
            }
        }

        /// <summary>Removes blips for any ped that has died, despawned or been arrested.</summary>
        public void Prune()
        {
            List<Ped> gone = null;

            foreach (KeyValuePair<Ped, Blip> kv in _blips)
            {
                bool remove;
                try
                {
                    // Dead, despawned OR cuffed: an arrested ped (e.g. via StopThePed) is
                    // still alive and present, so without this its blip would linger.
                    remove = kv.Key == null || !kv.Key.Exists() || kv.Key.IsDead
                             || Functions.IsPedArrested(kv.Key);
                }
                catch
                {
                    remove = true; // anything odd about the handle: drop its blip
                }

                if (remove)
                {
                    try { if (kv.Value != null && kv.Value.Exists()) kv.Value.Delete(); }
                    catch { }
                    (gone ?? (gone = new List<Ped>())).Add(kv.Key);
                }
            }

            if (gone != null)
                foreach (Ped p in gone)
                    _blips.Remove(p);
        }

        /// <summary>Removes every blip this manager created.</summary>
        public void Clear()
        {
            foreach (KeyValuePair<Ped, Blip> kv in _blips)
            {
                try { if (kv.Value != null && kv.Value.Exists()) kv.Value.Delete(); }
                catch { }
            }
            _blips.Clear();
        }
    }
}