# Avatar art guide

How characters are drawn, and how to make the art better. Written for anyone (people or agents) improving the look
of eyes, mouths, hair, hats, the body or the gun. The steps for adding a new option are in the README
(*Adding a face part, hairstyle or hat*); this guide is about how they are drawn and how to draw them well.

## How rendering works

All character art is vector shapes built in code every frame, in `godot/scripts/Game/AvatarArt.cs`. There are no
image files.

- **Pose in, triangles out.** Callers fill an `AvatarPose` (player color, `Appearance`, facing, aim, time, walk phase,
  air/wall blends, hurt flash, recoil…) and call `AvatarArt.Draw(canvas, origin, pose)`. Callers: `Avatar._Draw`
  (in a match), `UI/AvatarPreview`, `UI/HudUI`, `Fx/IconMaker` and the screenshot modes in `Main.Shots.cs`.
- **Sprite space.** Art is drawn facing right with the hitbox centered on (0,0): x −7..7, y −9..9 (14×18 units).
  The bone mask spans y −9.5..1.35, the cloak hangs to about y 3.4 (`BodyBottom`), the soles sit on y = 9.
  1 unit = 1 world pixel at `Stat.Size = 1`. The draw transform is `(origin, scale = (facing·size, size))`, so facing
  left is a mirror and size cards scale everything.
- **Draw order** (`Draw`): legs → arm (gun only) → hair behind the head (ponytail) → body (cloak, collar, mask) →
  eyes → mouth → hair → hat (moved by `HatDrop` / `HatLift`) → gun. Later shapes cover earlier ones.
- **Rig.** `Solve` turns the pose into a body offset and foot placement (two-bone legs, stride, air and wall poses).
  Everything above the hips moves by `BodyOffset`, so face parts, hair and hats must stay above y ≈ 3.
- **Primitives** (the `Ctx` class, on top of `Fx/TriBatch`):
  - `Fill(pts, fill, outline, width)`: soft dark outline, then the fill. This gives the ink rim.
  - `Gradient(pts, top, bottom)`: vertical ramp laid over a shape (top to bottom only).
  - `Poly`, `Line`, `Arc`, `Circle`: plain shapes. Circles get a 0.5-unit feather, lines get soft edges.
  - `Glow`: a textured soft sprite. It flushes the batch (an extra draw call), so use it rarely.
  - Shape helpers: `Shapes.Ellipse / RoundRect / Capsule / Star / Offset` (`Fx/Visuals.cs`), plus `Dome`, `Cap`,
    `Crescent` in `AvatarArt`. Fixed outlines are `static readonly Vector2[]` built once (`MaskOutline`, `ShortHair`…).
- **Batching.** Every shape is triangulated into one triangle array per character (one draw call instead of ~150).
  2D MSAA smooths edges.
- **Color.** Palette constants at the top of `AvatarArt`. The player color drives the cloak (`Cloak()`), the collar
  and a 15% tint on the mask. Hair and hat colors come from `Appearance.Dyes`. **Every color must go through
  `c.H(...)`** so the hurt flash turns the whole character white.

### Data that must stay in sync

- Name lists in `godot/scripts/Logic/Appearance.cs` are **append-only** (profiles store indices). Each index needs a
  `case` in `DrawEyes` / `DrawMouth` / `DrawHair` / `DrawHat`. Never reorder or remove.
- Hats: `HatTop` (HP bar placement), `HatDrop` (how far it sits down over the mask), and where needed `Covers`,
  `HidesMohawk`, `HatLift` (sitting on tall hair).
- Hair: `HairTop`. Tucked variants (`SpikyTucked`, `AfroTucked`) are used under covering hats.
- Gun: geometry must match `Logic/GunRig.cs`, which places the muzzle (bullet origin) on the host.

### Hard limits

- `TriBatch` **skips any polygon with more than 256 points** without warning.
- There is no clipping or masking: a shadow or highlight must lie inside its base shape on its own.
- Polygons must be simple (no self-intersections), or triangulation fails and the shape vanishes.
- Only vertical gradients. For other directions use per-vertex colors (see *Shading by angle*).
- Art is cosmetic and drawn per peer. It may use `Time`, but must never feed back into rules.

## The process

### 1. Work from a grid, not from guesses

- Sketch at 10–20× on a 14×18 grid (origin in the middle, y down), or trace over a screenshot in a vector tool on an
  artboard of x −14..14, y −26..10 (room for tall hats). Read coordinates off the grid or the node inspector.
- Iterate: change a shape, build, take shots, compare with the previous folder.

```bash
dotnet build godot/Brawler.csproj
"$GODOT" --path godot -- --lookshots=C:/tmp/shots_after --lookscale=4.2              # design detail
"$GODOT" --path godot -- --lookshots=C:/tmp/shots_after --lookscale=1                # in-match size: the real test
"$GODOT" --path godot -- --lookshots=C:/tmp/shots_after --lookcombos --lookscale=4.2 # every hat on every hair, every mouth with every eyes
"$GODOT" --path godot -- --walkshots=C:/tmp/shots_after                              # leg poses (body, cloak, legs)
"$GODOT" --path godot -- --aimshots=C:/tmp/shots_after                               # gun at every angle (gun changes)
```

Take a "before" set first. **If a part doesn't read at `--lookscale=1`, it doesn't work.** Look at the PNGs; don't
judge from the code.

### 2. Silhouette first

- Draw the part in solid `Ink` with no inner detail. If you can't tell it from the other options, fix the outline
  before anything else.
- Exaggerate: bigger horns, wider brims, sharper crown points. At game size a hat is ~15×10 pixels.
- Avoid tangents: don't run an edge exactly along the mask edge or end a spike exactly on the outline. Overlap
  clearly or leave ≥ 0.8 units.

### 3. Build each part in five layers

The mask, cap and crown already follow this; apply it to every part:

```csharp
// 1. Base + outline: the silhouette, and the line weight
c.Fill(shape, dye, dyeLine, 0.7f);
// 2. Form gradient: light top, dark bottom
c.Gradient(shape, c.H(Lit(c.Hat)), c.H(Shade(c.Hat)));
// 3. Core shadow: a shape inside the base, on the side away from the light
c.Poly(shadowShape, c.H(Shade(c.Hat)) with { A = 0.5f });
// 4. Detail lines: seams, folds, bands (thin, semi-transparent)
c.Line(a, b, dyeLine with { A = 0.5f }, 0.4f);
// 5. Highlight: small, bright, top front
c.Poly(Shapes.Ellipse(spot, 1.6f, 0.6f, 12), new Color(1, 1, 1, 0.35f));
```

Since nothing is clipped, build shadows from the outline itself: draw the full shape in the shadow color, then the
outline pulled toward the light (each point lerped toward a point on the lit side by ~0.15) in the lit color on top.
What is left uncovered on the far side is the shadow.

### 4. One light

The light comes from the **upper front**: the mask's sheen is at (−1.2, −7.8), its shade at the back (x ≈ −5) and the
jaw. Highlights go up and toward +x, shadows down and toward −x. Facing mirrors the art, so this holds both ways.
Never light one part from a different side.

### 5. Shift hue, don't just darken

`Darkened()` / `Lightened()` mix toward black/white and look muddy or chalky. Shade toward cool violet, light toward
warm cream (matching `Ink` #17141f and the cloak's #4a4652):

```csharp
private static readonly Color ShadeTint = new("#2a2340"), LightTint = new("#fff1d0");
private static Color Shade(Color c, float k = 0.35f) => c.Lerp(ShadeTint, k);
private static Color Lit(Color c, float k = 0.25f) => c.Lerp(LightTint, k);
```

(Add these to `AvatarArt` if they aren't there yet.) Keep `Darkened(0.5f)` for outlines.

Values: about three steps per part (light, mid, dark). The mask is the brightest thing on the character and anchors
the style; a light hat next to it needs a darker outline or a shadow band so they don't merge.

### 6. Line weight order

| Line | Width | Alpha |
|---|---|---|
| Outer silhouette | 0.7–1.0 (mask 1.0, cloak 0.9, hair 0.8, hats 0.6–0.7) | 1 |
| Inner overlap edges (brim over dome) | 0.5–0.6 | 1 |
| Detail (seams, folds, bands) | 0.35–0.45 | 0.4–0.6 |

Under ~0.35 a line disappears at scale 1: make it thicker or delete it.

### 7. Smooth curves

Hand-typed outlines of 7–12 points look faceted when size cards scale characters up. Type rough control points and
smooth them once, into a `static readonly` array (never per frame):

```csharp
/// <summary>Chaikin corner cutting: each pass rounds every corner (point count doubles).</summary>
private static Vector2[] Smooth(Vector2[] pts, int passes = 2)
{
    for (int p = 0; p < passes; p++)
    {
        var r = new Vector2[pts.Length * 2];
        for (int i = 0; i < pts.Length; i++)
        {
            Vector2 a = pts[i], b = pts[(i + 1) % pts.Length];
            r[2 * i] = a.Lerp(b, 0.25f);
            r[2 * i + 1] = a.Lerp(b, 0.75f);
        }
        pts = r;
    }
    return pts;
}
```

It rounds every corner. Keep deliberate points (crown tips, spikes) as separate shapes. Stay under 256 points.

### 8. Shading by angle

`TriBatch.Polygon(pts, cols)` takes a color per point, so round convex parts (domes, puffs, pom-poms) can be shaded by
how much each edge faces the light:

```csharp
var light = new Vector2(0.4f, -1f).Normalized();
for (int i = 0; i < pts.Length; i++)
    cols[i] = Shade(dye).Lerp(Lit(dye), 0.5f + 0.5f * (pts[i] - center).Normalized().Dot(light));
```

On concave shapes the triangulation can streak; use the layers in step 3 there.

### 9. Small motion

`c.P.Time` drives idle motion (the ponytail swings by `Mathf.Sin(Time * 3f) * 0.6f`). Keep amplitudes at 0.3–0.8 units
and give each part its own frequency so nothing moves in sync.

## Checklist for each part

- [ ] Reads at `--lookscale=1` as what it is, and the silhouette is distinct from every other option
- [ ] Lit from the upper front; three value steps; hue-shifted shadows and lights
- [ ] Line weights follow the table; nothing under 0.35
- [ ] Every color goes through `c.H(...)` (hurt flash)
- [ ] Fits every hair style (`--lookcombos`); `HatTop` / `HatDrop` / `HatLift` / `Covers` / `HairTop` updated
- [ ] Reads with dark, light and saturated dyes (Black, White, Blonde, Red) and on several player colors
- [ ] No polygon over 256 points, none self-intersecting; fixed shapes are `static readonly`
- [ ] Name lists in `Appearance.cs` only appended to
- [ ] Before/after `--lookshots` compared by eye

## Working order for a larger pass

1. Take the "before" shot sets.
2. Add the shared helpers (`Shade`, `Lit`, `Smooth`) once.
3. Redo **one** part fully (a flat hat like the Beanie or Cap is a good start) and agree it is right. It becomes the
   reference for the rest.
4. Go part by part (hats, hair, mouths, eyes), one commit per part or small group, each with fresh shots.
5. Finish with `--lookcombos`, `--walkshots`, and `tools/check.sh` (or at least `--autotest`) so nothing else broke.
