namespace Brawler.Logic;

/// <summary>Which jump a buffered jump press turns into (see <see cref="JumpCharges.Use"/>).</summary>
public enum JumpKind { None, Ground, Wall, Ledge, Air, Jet }

/// <summary>
/// The jumps a player has left until they land. Leaving the ground without jumping (walking or falling off a ledge,
/// being knocked off) keeps the ground jump, usable at any point in the air as a "ledge jump"; after it, the
/// AirJumps card jumps still apply. Engine-independent so the rules can be unit tested.
/// </summary>
public struct JumpCharges
{
    /// <summary>The ground jump: spent by jumping from the ground, a ledge jump, or a jump pad launch.</summary>
    public bool Ground;
    /// <summary>Extra mid-air jumps left (Stat.AirJumps).</summary>
    public int Air;

    /// <summary>Standing on the floor: everything comes back.</summary>
    public void Land(int airJumps)
    {
        Ground = true;
        Air = airJumps;
    }

    /// <summary>
    /// Spends the jump a press becomes, in priority order: a ground jump while on (or just off) the floor, a wall
    /// jump while on (or just off) a wall, the kept ledge jump, an air jump, then (on a fresh press only) the jetpack.
    /// Wall jumps cost nothing.
    /// </summary>
    public JumpKind Use(bool coyote, bool wallCoyote, bool hasFuel, bool freshPress)
    {
        if (coyote && Ground) { Ground = false; return JumpKind.Ground; }
        if (wallCoyote) return JumpKind.Wall;
        if (Ground) { Ground = false; return JumpKind.Ledge; }
        if (Air > 0) { Air--; return JumpKind.Air; }
        if (hasFuel && freshPress) return JumpKind.Jet;
        return JumpKind.None;
    }

    /// <summary>Jumps left before landing or touching a wall again (wall jumps not counted).</summary>
    public int Left => (Ground ? 1 : 0) + Air;
}
