using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Brawler.Core;
using Brawler.Game;
using Godot;

namespace Brawler.Fx;

/// <summary>
/// --makeicon: builds the app icon, a bare player head (the bone mask and a cyan collar, no hat or hair), as an SVG
/// from the avatar art's own shapes and colors, then writes icon.svg (the window icon) and icon.ico (the exe's icon,
/// 16 to 256 px) to the project folder. Vector all the way down, so even the 16 px size is sharp.
/// </summary>
public static class IconMaker
{
    private static readonly int[] IcoSizes = { 16, 24, 32, 48, 64, 128, 256 };

    public static void Make(Node host)
    {
        string dir = ProjectSettings.GlobalizePath("res://");
        string svg = Svg();
        File.WriteAllText(Path.Combine(dir, "icon.svg"), svg);
        WriteIco(svg, Path.Combine(dir, "icon.ico"));
        Log.Info($"MAKEICON wrote icon.svg and icon.ico ({string.Join(", ", IcoSizes)} px) to {dir}");
        host.GetTree().Quit(0);
    }

    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Hex(Color c) => "#" + c.ToHtml(false);

    /// <summary>The head in sprite units, 16 units square, drawn as 16 px (scale it when rasterizing).</summary>
    public static string Svg()
    {
        var body = Palette.Player(6);
        var mask = AvatarArt.MaskBoneColor.Lerp(body, 0.15f);
        var shade = AvatarArt.MaskShadeColor.Lerp(body, 0.1f);
        string ink = Hex(AvatarArt.InkColor);
        string pts = string.Join(" ", AvatarArt.MaskShape().Select(p => $"{F(p.X)},{F(p.Y)}"));

        // Mouth: the "Smile" arc, as AvatarArt.DrawMouth draws it (centre 1.4,-2.0, radius 2.2).
        var c = new Vector2(1.4f, -2.0f);
        var m0 = c + Vector2.FromAngle(0.55f) * 2.2f;
        var m1 = c + Vector2.FromAngle(Mathf.Pi - 0.55f) * 2.2f;

        var s = new StringBuilder();
        s.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"16\" height=\"16\" viewBox=\"-8 -12.6 16 16\">");
        s.Append($"<defs><linearGradient id=\"g\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\"><stop offset=\"0\" stop-color=\"{Hex(mask.Lightened(0.06f))}\"/>");
        s.Append($"<stop offset=\"1\" stop-color=\"{Hex(shade)}\"/></linearGradient></defs>");
        // Collar under the jaw, in the player's color.
        s.Append($"<rect x=\"-6.2\" y=\"-1.2\" width=\"12.6\" height=\"2.4\" rx=\"1.2\" fill=\"{Hex(body.Lerp(shade, 0.12f))}\" stroke=\"{ink}\" stroke-width=\"1.1\"/>");
        s.Append($"<line x1=\"-5\" y1=\"-0.6\" x2=\"5.2\" y2=\"-0.6\" stroke=\"{Hex(body.Lightened(0.35f))}\" stroke-opacity=\"0.55\" stroke-width=\"0.35\"/>");
        // The mask: an ink rim, then the bone with its shading and brow sheen.
        s.Append($"<polygon points=\"{pts}\" fill=\"{ink}\" stroke=\"{ink}\" stroke-width=\"2\" stroke-linejoin=\"round\"/>");
        s.Append($"<polygon points=\"{pts}\" fill=\"url(#g)\"/>");
        s.Append($"<ellipse cx=\"-5.1\" cy=\"-3.2\" rx=\"1.2\" ry=\"4\" fill=\"{Hex(shade)}\" fill-opacity=\"0.3\"/>");
        s.Append("<ellipse cx=\"-1.2\" cy=\"-7.8\" rx=\"3.4\" ry=\"1\" fill=\"#fff\" fill-opacity=\"0.3\"/>");
        // Round eye holes and a small smile.
        string hollow = Hex(AvatarArt.HollowColor);
        s.Append($"<ellipse cx=\"-2.1\" cy=\"-4.3\" rx=\"1.5\" ry=\"2.3\" fill=\"{hollow}\"/>");
        s.Append($"<ellipse cx=\"2.8\" cy=\"-4.3\" rx=\"1.5\" ry=\"2.3\" fill=\"{hollow}\"/>");
        s.Append($"<path d=\"M{F(m0.X)},{F(m0.Y)} A2.2,2.2 0 0 1 {F(m1.X)},{F(m1.Y)}\" fill=\"none\" stroke=\"{ink}\" stroke-opacity=\"0.85\" stroke-width=\"0.6\" stroke-linecap=\"round\"/>");
        s.Append("</svg>");
        return s.ToString();
    }

    /// <summary>An .ico holding one PNG per size (Windows Vista and later read PNG entries at any size).</summary>
    private static void WriteIco(string svg, string path)
    {
        var pngs = IcoSizes.Select(size =>
        {
            var img = new Image();
            img.LoadSvgFromString(svg, size / 16f);
            return img.SavePngToBuffer();
        }).ToArray();
        using var w = new BinaryWriter(File.Create(path));
        w.Write((ushort)0);
        w.Write((ushort)1); // icon
        w.Write((ushort)IcoSizes.Length);
        int offset = 6 + 16 * IcoSizes.Length;
        for (int i = 0; i < IcoSizes.Length; i++)
        {
            int s = IcoSizes[i];
            w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)0); // no palette
            w.Write((byte)0);
            w.Write((ushort)1); // planes
            w.Write((ushort)32); // bits per pixel
            w.Write(pngs[i].Length);
            w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
    }
}
