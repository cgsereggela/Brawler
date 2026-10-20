using System;
using System.Linq;

namespace Brawler.Logic;

/// <summary>Player body size in pixels at Size = 1 (shared by the avatar, the art and the rules hash).</summary>
public static class Body
{
    public const float W = 14, H = 18;
}

public enum LookPart { Eyes, Nose, Mouth, Hair, HairColor, Hat, HatColor }

/// <summary>
/// A player's cosmetic choices, one index per <see cref="LookPart"/>. Purely visual: it never affects rules.
/// Sent in the lobby as a length-prefixed byte list, so parts can be appended later without breaking old data.
/// The art for each option lives in Game/AvatarArt.cs and must follow the order of the name lists here.
/// </summary>
public sealed class Appearance
{
    public static readonly string[] EyeNames = { "Round", "Big", "Dots", "Sleepy", "Angry", "Visor", "Cyclops", "Shades", "Happy" };
    public static readonly string[] NoseNames = { "None", "Dot", "Button", "Long", "Pig", "Beak", "Clown" };
    public static readonly string[] MouthNames = { "Smile", "Flat", "Open", "Grin", "Frown", "Fangs", "Tongue", "Mustache", "Cat" };
    public static readonly string[] HairNames = { "None", "Twin", "Crown", "Spike", "Swept", "Nubs", "Antlers", "Ram" }; // drawn as horns
    public static readonly string[] HatNames = { "None", "Cap", "Top Hat", "Beanie", "Crown", "Party", "Headband", "Viking", "Halo", "Propeller", "Chef" };

    /// <summary>Hair and hat dyes. Game code turns the hex into colors.</summary>
    public static readonly (string Name, string Hex)[] Dyes =
    {
        ("Black", "#2a2430"), ("Brown", "#6b4a36"), ("Blonde", "#d9c48a"), ("Ginger", "#b8693a"),
        ("Grey", "#9a98a3"), ("White", "#ece6d8"), ("Blue", "#4f6fa8"), ("Pink", "#c27a98"),
        ("Green", "#5f9a68"), ("Purple", "#7a5fa8"), ("Red", "#a84444"),
    };

    private static readonly string[] DyeNames = Dyes.Select(d => d.Name).ToArray();
    private static readonly string[][] Options = { EyeNames, NoseNames, MouthNames, HairNames, DyeNames, HatNames, DyeNames };

    public static readonly int PartCount = Options.Length;
    public static int Count(LookPart p) => Options[(int)p].Length;
    public static string PartLabel(LookPart p) => p switch
    {
        LookPart.Hair => "Horns",
        LookPart.HairColor => "Horn tint",
        LookPart.HatColor => "Hat color",
        _ => p.ToString(),
    };

    private readonly byte[] _v = new byte[PartCount];

    public int this[LookPart p]
    {
        get => _v[(int)p];
        set => _v[(int)p] = (byte)Wrap(value, Count(p));
    }

    public int Eyes => this[LookPart.Eyes];
    public int Nose => this[LookPart.Nose];
    public int Mouth => this[LookPart.Mouth];
    public int Hair => this[LookPart.Hair];
    public int HairColor => this[LookPart.HairColor];
    public int Hat => this[LookPart.Hat];
    public int HatColor => this[LookPart.HatColor];

    public string OptionName(LookPart p) => Options[(int)p][this[p]];

    public static Appearance Default()
    {
        var a = new Appearance();
        a[LookPart.Hair] = 1;      // Short
        a[LookPart.HairColor] = 1; // Brown
        a[LookPart.HatColor] = 10; // Red
        return a;
    }

    public static Appearance Random(Rng rng)
    {
        var a = new Appearance();
        foreach (LookPart p in Enum.GetValues<LookPart>()) a[p] = rng.Range(0, Count(p) - 1);
        if (rng.Range(0, 2) == 0) a[LookPart.Hat] = 0; // a third of the crowd goes hatless
        return a;
    }

    public Appearance Clone()
    {
        var a = new Appearance();
        _v.CopyTo(a._v, 0);
        return a;
    }

    public byte[] ToBytes() => (byte[])_v.Clone();

    /// <summary>Missing parts stay 0, unknown extra parts are ignored, out-of-range values wrap.</summary>
    public static Appearance FromBytes(byte[] data)
    {
        var a = new Appearance();
        if (data == null) return a;
        for (int i = 0; i < Math.Min(data.Length, PartCount); i++) a[(LookPart)i] = data[i];
        return a;
    }

    /// <summary>Text form for the profile file, e.g. "0,0,0,1,1,0,10".</summary>
    public string Encode() => string.Join(",", _v);

    public static Appearance Decode(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Default();
        var parts = s.Split(',');
        var bytes = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            bytes[i] = byte.TryParse(parts[i].Trim(), out byte b) ? b : (byte)0;
        return FromBytes(bytes);
    }

    public override bool Equals(object obj) => obj is Appearance o && _v.AsSpan().SequenceEqual(o._v);
    public override int GetHashCode() => BitConverter.ToInt32(_v.Concat(new byte[4]).Take(4).ToArray());

    private static int Wrap(int v, int n) => ((v % n) + n) % n;
}
