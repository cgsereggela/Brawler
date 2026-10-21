using System;

namespace Brawler.Logic;

/// <summary>One quick chat line. Ghosts (dead players waiting for the next battle) say their own version of some.</summary>
public readonly record struct EmoteDef(string Text, string GhostText);

/// <summary>
/// The emotes on the number keys, and the ping. Ids are indices into <see cref="All"/> and go over the network, so
/// only ever add to the end.
/// </summary>
public static class Emotes
{
    public static readonly EmoteDef[] All =
    {
        new("GG", "GG"),
        new("HAHA!", "BOO!"),
        new("OOPS", "OOPS"),
        new("COME AT ME", "AVENGE ME!"),
    };

    /// <summary>The input action for emote <paramref name="id"/>: "emote_1" for id 0.</summary>
    public static string Action(int id) => $"emote_{id + 1}";

    public static bool Valid(int id) => id >= 0 && id < All.Length;

    public static string Text(int id, bool ghost) => Valid(id) ? ghost ? All[id].GhostText : All[id].Text : "";

    /// <summary>Seconds an emote bubble stays up, and a ping marker.</summary>
    public const float BubbleTime = 2.2f, PingTime = 2.5f;
}

/// <summary>
/// Spam guard for emotes and pings: a small burst is fine, then one every <see cref="Refill"/> seconds. The host runs
/// one per player (so a modified client can't flood everyone), and each player's own copy keeps them from pressing
/// into a wall of refusals.
/// </summary>
public sealed class EmoteLimiter
{
    public readonly int Burst;
    public readonly float Refill;
    private float _tokens;

    public EmoteLimiter(int burst = 3, float refill = 1.5f)
    {
        Burst = burst;
        Refill = refill;
        _tokens = burst;
    }

    public float Tokens => _tokens;

    public void Tick(float dt) => _tokens = Math.Min(Burst, _tokens + dt / Refill);

    /// <summary>Spends one use if there is one.</summary>
    public bool TryUse()
    {
        if (_tokens < 1) return false;
        _tokens -= 1;
        return true;
    }

    public void Reset() => _tokens = Burst;
}
