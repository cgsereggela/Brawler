using System.Collections.Generic;
using System.Linq;

namespace Brawler.Logic;

/// <summary>
/// The host's card pool: which cards may be offered in a match, kept as the set of disabled ids so cards added
/// later (new versions, custom cards) start enabled. Ids that aren't registered are kept and simply match nothing.
/// </summary>
public static class CardPool
{
    /// <summary>The most cards a pick can offer (the lobby's choice spin boxes stop here).</summary>
    public const int MaxChoices = 7;

    /// <summary>The pool must keep at least this many common cards, so any pick the lobby allows can be filled.</summary>
    public const int MinEnabled = MaxChoices;

    public static bool IsEnabled(int id, IReadOnlySet<int> disabled) => disabled == null || !disabled.Contains(id);

    /// <summary>Enabled common cards, in registry order: what an ordinary offer draws from.</summary>
    public static List<int> Allowed(IReadOnlySet<int> disabled)
        => CardRegistry.All.Where(c => !c.IsRare && !c.IsCurse && IsEnabled(c.Id, disabled)).Select(c => c.Id).ToList();

    /// <summary>Enabled rare cards, in registry order.</summary>
    public static List<int> AllowedRares(IReadOnlySet<int> disabled)
        => CardRegistry.All.Where(c => c.IsRare && !c.IsCurse && IsEnabled(c.Id, disabled)).Select(c => c.Id).ToList();

    /// <summary>Enabled curses, in registry order.</summary>
    public static List<int> AllowedCurses(IReadOnlySet<int> disabled)
        => CardRegistry.All.Where(c => c.IsCurse && IsEnabled(c.Id, disabled)).Select(c => c.Id).ToList();

    /// <summary>Enabled common cards: <see cref="MinEnabled"/> applies to these, since rares and curses are extras.</summary>
    public static int CommonCount(IReadOnlySet<int> disabled) => Allowed(disabled).Count;

    public static int EnabledCount(IReadOnlySet<int> disabled) => CardRegistry.All.Count(c => IsEnabled(c.Id, disabled));

    /// <summary>Wire form: each id as an unsigned 16-bit little-endian value, sorted. Ids outside 0..65535 are dropped.</summary>
    public static byte[] Encode(IEnumerable<int> disabled)
    {
        var ids = (disabled ?? Enumerable.Empty<int>()).Where(id => id is >= 0 and <= ushort.MaxValue).Distinct().OrderBy(id => id).ToList();
        var bytes = new byte[ids.Count * 2];
        for (int i = 0; i < ids.Count; i++)
        {
            bytes[i * 2] = (byte)ids[i];
            bytes[i * 2 + 1] = (byte)(ids[i] >> 8);
        }
        return bytes;
    }

    /// <summary>Reverse of <see cref="Encode"/>. A trailing odd byte is ignored; unknown ids are kept.</summary>
    public static HashSet<int> Decode(byte[] data)
    {
        var set = new HashSet<int>();
        if (data == null) return set;
        for (int i = 0; i + 1 < data.Length; i += 2) set.Add(data[i] | (data[i + 1] << 8));
        return set;
    }
}
