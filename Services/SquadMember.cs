using Rage;

namespace DynamicHostileTerritories.Services
{
    /// <summary>What a squad member is doing as part of the living ambient presence.</summary>
    public enum Role
    {
        Sentry,
        Loiter,
        Patrol,
        Guard
    }

    /// <summary>
    /// One spawned gang member. Pure data shared between the spawner (GangSpawnManager)
    /// and the AI commander (SquadCommander).
    /// </summary>
    public sealed class Member
    {
        public Ped Ped;
        public Vector3 Home;
        public Role Role;
        public string Weapon;
        public bool IsBoss; // area lieutenant — killing/arresting them breaks the grip
    }
}