using System.Collections.Generic;

namespace Brawler.Logic;

/// <summary>
/// Draft mode (a lobby setting): before the first battle the host rolls one shared pool of cards, and the players take
/// turns taking a card from it, in snake order (1, 2, 3, then 3, 2, 1) so going first isn't an edge. Each player drafts
/// <see cref="PicksEach"/> cards; the pool has a couple spare so the last pick is still a choice.
/// </summary>
public static class Draft
{
    /// <summary>Cards each player drafts: two in small games, one with five or more (so the pool fits on screen).</summary>
    public static int PicksEach(int players) => players <= 4 ? 2 : 1;

    /// <summary>Cards in the shared pool: everyone's picks plus two spare. At most 10.</summary>
    public static int PoolSize(int players) => players * PicksEach(players) + 2;

    /// <summary>Seconds a drafter has before a card is taken for them.</summary>
    public const float TurnTime = 15f;

    /// <summary>The turn order: <paramref name="slots"/> forwards, then backwards, and so on for each pick.</summary>
    public static List<int> Order(IReadOnlyList<int> slots, int picksEach)
    {
        var order = new List<int>();
        for (int round = 0; round < picksEach; round++)
            for (int i = 0; i < slots.Count; i++)
                order.Add(slots[round % 2 == 0 ? i : slots.Count - 1 - i]);
        return order;
    }
}
