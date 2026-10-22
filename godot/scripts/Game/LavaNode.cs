using Brawler.Core;
using Brawler.Fx;
using Brawler.Logic;
using Godot;

namespace Brawler.Game;

/// <summary>
/// Draws the sudden-death lava (see <see cref="RisingLava"/>) over the players, so they sink into it, plus its glow,
/// embers, and the warnings before it starts. The damage is the host's job in <see cref="Arena.HostStep"/>.
/// </summary>
public partial class LavaNode : Node2D
{
    public GameManager Game;

    private static readonly Color Top = new("#ffd34a"), Mid = new("#ff6a1f"), Deep = new("#8a1410");
    private DrawLayer _glow;
    private float _t;
    private int _lastWarn = int.MaxValue;
    private Vector2[] _surface = System.Array.Empty<Vector2>();

    public override void _Ready()
    {
        _glow = new DrawLayer(DrawGlow, additive: true);
        AddChild(_glow);
    }

    private bool Showing => Game.Map != null && Game.Phase is Phase.Countdown or Phase.Fighting or Phase.Intermission;

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _t += dt;
        if (!Showing)
        {
            _lastWarn = int.MaxValue;
            if (_surface.Length > 0) { _surface = System.Array.Empty<Vector2>(); QueueRedraw(); _glow.QueueRedraw(); }
            return;
        }
        Warn();
        float y = Game.Arena.LavaY;
        if (y > MapLayout.Height + 40)
        {
            if (_surface.Length > 0) { _surface = System.Array.Empty<Vector2>(); QueueRedraw(); _glow.QueueRedraw(); }
            return;
        }
        BuildSurface(y);
        // Embers and the odd burst bubble off the top.
        if (y < MapLayout.Height && GD.Randf() < dt * 14)
        {
            var at = new Vector2((float)GD.RandRange(MapGenerator.Wall, MapLayout.Width - MapGenerator.Wall), y);
            Game.Fx.Emit(at, PKind.Ember, new Color("#ffb13b"), 1, 40, 1.1f, 1.2f, -60, Vector2.Up, 0.9f);
        }
        QueueRedraw();
        _glow.QueueRedraw();
    }

    /// <summary>Banners counting down to the lava, then one when it starts, on every peer from the shared clock.</summary>
    private void Warn()
    {
        int after = Game.LavaAfter;
        if (after <= 0 || Game.Phase != Phase.Fighting) return;
        float left = after - Game.Arena.FightTime;
        int sec = Mathf.CeilToInt(left);
        if (sec == _lastWarn) return;
        _lastWarn = sec;
        if (sec == (int)RisingLava.Warning) Game.ShowBanner($"LAVA IN {sec}", Mid, 0.9f);
        else if (sec == 0) { Game.ShowBanner(GameModes.Respawns(Game.Mode) ? "SUDDEN DEATH: NO MORE RESPAWNS!" : "THE FLOOR IS LAVA!", Top, 1.2f); Game.Sfx?.Play("go"); Game.Shake(0.25f); }
    }

    private void BuildSurface(float y)
    {
        // A rolling top: two waves at different speeds, a bit taller once it's climbing fast.
        int n = 72;
        if (_surface.Length != n + 1) _surface = new Vector2[n + 1];
        float x0 = -10, x1 = MapLayout.Width + 10;
        for (int i = 0; i <= n; i++)
        {
            float x = Mathf.Lerp(x0, x1, i / (float)n);
            float wave = Mathf.Sin(_t * 1.7f + x * 0.045f) * 2.2f + Mathf.Sin(_t * 3.1f - x * 0.11f) * 1.1f;
            _surface[i] = new Vector2(x, y + wave);
        }
    }

    public override void _Draw()
    {
        if (_surface.Length == 0) return;
        float y = Game.Arena.LavaY, bottom = MapLayout.Height + 40;
        var body = new Vector2[_surface.Length + 2];
        _surface.CopyTo(body, 0);
        body[^2] = new Vector2(_surface[^1].X, bottom);
        body[^1] = new Vector2(_surface[0].X, bottom);
        float depth = bottom - y;
        // Hot near the top, darkening with depth (the gradient spans the whole body, so keep the top band bright).
        Shapes.Gradient(this, body, Mid, depth > 120 ? Deep : Mid.Lerp(Deep, depth / 120f));
        // A bright crust band just under the surface.
        var band = new Vector2[_surface.Length * 2];
        for (int i = 0; i < _surface.Length; i++)
        {
            band[i] = _surface[i];
            band[^(i + 1)] = _surface[i] + new Vector2(0, 5);
        }
        DrawColoredPolygon(band, Top with { A = 0.55f });
        DrawPolyline(_surface, Shapes.Hdr(Top, 1.8f), 1.6f, true);
        // Slow blobs drifting in the body.
        for (int i = 0; i < 14; i++)
        {
            float bx = Mathf.PosMod(i * 83.7f + _t * (6 + i % 4 * 3), MapLayout.Width);
            float by = y + 12 + Mathf.PosMod(i * 37.1f, Mathf.Max(8, depth - 20));
            if (by > MapLayout.Height) continue;
            DrawCircle(new Vector2(bx, by), 3 + i % 3 * 2, Top with { A = 0.16f }, true, -1, true);
        }
    }

    private void DrawGlow(DrawLayer l)
    {
        if (_surface.Length == 0) return;
        float y = Game.Arena.LavaY;
        float pulse = 0.85f + 0.15f * Mathf.Sin(_t * 2.3f);
        // Heat haze above the surface, which also warns that it's coming while it's still under the bottom edge.
        l.DrawTextureRect(GlowTex.Tex, new Rect2(-60, y - 46, MapLayout.Width + 120, 92), false, Mid with { A = 0.5f * pulse });
        l.DrawTextureRect(GlowTex.Tex, new Rect2(-20, y - 14, MapLayout.Width + 40, 28), false, Top with { A = 0.35f * pulse });
    }
}
