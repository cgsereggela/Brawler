using Brawler.Core;
using Brawler.Logic;
using Godot;

namespace Brawler.Game;

/// <summary>Builds collision bodies for a generated layout and draws it as chunky pixel tiles.</summary>
public partial class MapNode : Node2D
{
    public MapLayout Layout { get; private set; }

    public void Build(MapLayout layout)
    {
        foreach (var c in GetChildren()) c.QueueFree();
        Layout = layout;
        QueueRedraw();
        if (layout == null) return;
        foreach (var r in layout.Solids)
        {
            var body = new StaticBody2D { CollisionLayer = 1, CollisionMask = 0, Position = new Vector2(r.X + r.W / 2f, r.Y + r.H / 2f) };
            body.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = new Vector2(r.W, r.H) } });
            AddChild(body);
        }
        QueueRedraw();
    }

    public override void _Draw()
    {
        // Background dot grid.
        var dot = new Color(1, 1, 1, 0.04f);
        for (int x = 16; x < MapLayout.Width; x += 32)
            for (int y = 16; y < MapLayout.Height; y += 32)
                DrawRect(new Rect2(x, y, 1, 1), dot);

        if (Layout == null) return;
        var rng = new Rng((ulong)(uint)Layout.Seed ^ 0x9E3779B9UL);
        foreach (var r in Layout.Solids)
        {
            DrawRect(new Rect2(r.X, r.Y, r.W, r.H), Palette.Solid);
            // Per-tile speckle for a pixel texture.
            for (int tx = r.X; tx < r.Right; tx += MapGenerator.Tile)
                for (int ty = r.Y; ty < r.Bottom; ty += MapGenerator.Tile)
                {
                    int px = tx + rng.Range(1, 6), py = ty + rng.Range(1, 6);
                    DrawRect(new Rect2(px, py, 1, 1), Palette.SolidDark);
                    if (rng.Chance(0.4f)) DrawRect(new Rect2(tx + rng.Range(1, 6), ty + rng.Range(1, 6), 1, 1), Palette.SolidLight with { A = 0.5f });
                }
            DrawRect(new Rect2(r.X, r.Y, r.W, 1), Palette.SolidLight);
            DrawRect(new Rect2(r.X, r.Bottom - 1, r.W, 1), Palette.SolidDark);
        }
    }
}
