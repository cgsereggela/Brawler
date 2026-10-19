using Brawler.Logic;
using Godot;
using NVec = System.Numerics.Vector2;

namespace Brawler.Game;

/// <summary>
/// Behavior hooks for cards that need more than stat changes. Runs on the host only.
/// Add a hook method here, call it from the matching place in Avatar/GameManager, and register the effect id in Create().
/// </summary>
public interface ICardEffect
{
    void OnBlock(Avatar self, GameManager game) { }
    void OnDealDamage(Avatar self, Avatar victim, float damage, GameManager game) { }
    void OnBattleStart(Avatar self, GameManager game) { }
}

public static class CardEffects
{
    public static ICardEffect Create(string id) => id switch
    {
        "blink" => new BlinkEffect(),
        _ => null,
    };
}

/// <summary>Blocking teleports you toward your cursor, stopping short of walls.</summary>
public sealed class BlinkEffect : ICardEffect
{
    private const float DistancePerStack = 90f;

    public void OnBlock(Avatar self, GameManager game)
    {
        // Several Blink cards stack distance (via the stat) but only the first one teleports.
        if (self.Player.Effects.Find(e => e is BlinkEffect) != this) return;

        float dist = DistancePerStack * self.S[Stat.TeleportOnBlock];
        var from = self.GlobalPosition;
        var delta = self.AimDir * dist;
        float r = Mathf.Max(self.HalfSize.X, self.HalfSize.Y);
        var hit = Sweep.Cast(new NVec(from.X, from.Y), new NVec(delta.X, delta.Y), r, game.Map.Solids);
        float t = hit?.T ?? 1f;
        var to = from + delta * Mathf.Max(0, t - 0.02f);
        self.GlobalPosition = to;
        game.HostFx(FxKind.Blink, from, 0, self.Player.Slot, to);
    }
}
