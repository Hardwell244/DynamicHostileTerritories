using DynamicHostileTerritories.Data;
using Rage;
using Rage.Native;

namespace DynamicHostileTerritories.Core
{
    /// <summary>
    /// The single source of truth for how EVERY hostile ped the mod spawns fights: turf
    /// garrison, area lieutenant, reinforcements, rival skirmishers and retaliation hit
    /// squads all run through here. Centralised on purpose so the "feel" of a firefight -
    /// deliberately LOW aim, a competent tactical brain (cover/advance/flank), and a
    /// throttled volume of fire - is identical everywhere instead of each spawner rolling
    /// its own accuracy and combat attributes. The danger is meant to come from numbers and
    /// positioning, never from laser accuracy. Never throws.
    /// </summary>
    public static class CombatProfile
    {
        /// <summary>How tough a fighter is. Maps to a fixed accuracy/armour/brain bundle.</summary>
        public enum Tier
        {
            Light,    // pistols, poor aim, holds ground (Watchful grunt, hit-squad gunman)
            Standard, // SMGs, slightly better, will advance (Aggressive grunt, rival crew)
            Heavy,    // rifles, still beatable, advances and flanks (Warzone grunt)
            Boss      // the area lieutenant: best brain, better-than-grunt aim, still killable
        }

        /// <summary>
        /// Applies accuracy, armour, the combat brain (cover/advance/flank/vehicles),
        /// target-loss behaviour and a throttled shoot rate for the given tier. Health is
        /// left untouched (the boss sets its own bigger health pool where it spawns).
        /// </summary>
        public static void Apply(Ped ped, Tier tier)
        {
            if (ped == null || !ped.Exists())
                return;

            int accuracy, armor, ability, movement, shootRate;
            switch (tier)
            {
                case Tier.Light:
                    accuracy = 5; armor = 0; ability = 0; movement = 1; shootRate = 30;
                    break;
                case Tier.Standard:
                    accuracy = 8; armor = 25; ability = 1; movement = 2; shootRate = 35;
                    break;
                case Tier.Heavy:
                    accuracy = 12; armor = 50; ability = 1; movement = 2; shootRate = 35;
                    break;
                default: // Boss
                    accuracy = 20; armor = 100; ability = 2; movement = 2; shootRate = 55;
                    break;
            }

            ped.Accuracy = accuracy;
            ped.Armor = armor;

            NativeFunction.Natives.SET_PED_COMBAT_ATTRIBUTES(ped, 0, true);   // can use cover
            NativeFunction.Natives.SET_PED_COMBAT_ATTRIBUTES(ped, 1, true);   // can use vehicles
            NativeFunction.Natives.SET_PED_COMBAT_ATTRIBUTES(ped, 2, true);   // can do drive-bys
            NativeFunction.Natives.SET_PED_COMBAT_ATTRIBUTES(ped, 3, true);   // can leave vehicle
            NativeFunction.Natives.SET_PED_COMBAT_ATTRIBUTES(ped, 5, true);   // fight even while unarmed
            NativeFunction.Natives.SET_PED_COMBAT_ATTRIBUTES(ped, 46, true);  // stand and fight, don't flee

            NativeFunction.Natives.SET_PED_COMBAT_ABILITY(ped, ability);
            NativeFunction.Natives.SET_PED_COMBAT_MOVEMENT(ped, movement);
            NativeFunction.Natives.SET_PED_COMBAT_RANGE(ped, 2);             // engage from medium/far
            NativeFunction.Natives.SET_PED_TARGET_LOSS_RESPONSE(ped, 2);    // search, not aimbot-track
            NativeFunction.Natives.SET_PED_FLEE_ATTRIBUTES(ped, 0, false);
            NativeFunction.Natives.SET_PED_SHOOT_RATE(ped, shootRate);      // throttle the volume of fire
            NativeFunction.Natives.SET_PED_KEEP_TASK(ped, true);
        }

        /// <summary>Maps an area's hostility tier to the matching grunt combat tier.</summary>
        public static Tier FromHostility(HostilityLevel tier)
        {
            switch (tier)
            {
                case HostilityLevel.Warzone: return Tier.Heavy;
                case HostilityLevel.Aggressive: return Tier.Standard;
                default: return Tier.Light; // Watchful / Pacified
            }
        }
    }
}