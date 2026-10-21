using Godot;

namespace Brawler.Fx;

/// <summary>
/// The shaders that give the vector-drawn parts of the game a painted, raster finish: a brush grain on characters and
/// props, and a full-screen pass that lays the whole scene on canvas (paper texture, a touch of softness, slightly
/// muted color and a vignette).
/// </summary>
public static class PaintShaders
{
    private static ShaderMaterial _grain;

    /// <summary>
    /// Mottles a canvas item's colors with two scales of noise fixed to the item, so flat fills read as paint that
    /// moves with the character instead of a pattern sliding over it.
    /// </summary>
    public static ShaderMaterial Grain => _grain ??= Make(GrainCode);

    private const string GrainCode = @"
shader_type canvas_item;
uniform sampler2D grain : repeat_enable, filter_linear;
uniform float strength = 0.22;
varying vec2 local;
void vertex() { local = VERTEX; }
void fragment() {
    float fine = texture(grain, local * 0.0065).r;
    float broad = texture(grain, local * 0.0021 + vec2(0.37, 0.61)).r;
    COLOR.rgb *= 1.0 + (fine * 0.55 + broad * 0.45 - 0.5) * strength;
}";

    /// <summary>A full-rect node that repaints everything drawn below its canvas layer.</summary>
    public static ColorRect ScreenPass()
    {
        var rect = new ColorRect { Name = "PaintPass", MouseFilter = Control.MouseFilterEnum.Ignore, Material = Make(ScreenCode) };
        rect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        return rect;
    }

    private const string ScreenCode = @"
shader_type canvas_item;
uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;
uniform sampler2D grain : repeat_enable, filter_linear;
uniform float softness = 0.12;
uniform float saturation = 0.9;
uniform float vignette = 0.42;
void fragment() {
    vec3 c = textureLod(screen_tex, SCREEN_UV, 0.0).rgb;
    // A little of a blurrier copy, like paint that has settled into the canvas.
    vec3 soft = textureLod(screen_tex, SCREEN_UV, 1.3).rgb;
    c = mix(c, soft, softness);
    float l = dot(c, vec3(0.299, 0.587, 0.114));
    c = mix(vec3(l), c, saturation);
    // Canvas: a fine weave and broad blotches of paper tone.
    float weave = texture(grain, SCREEN_UV * vec2(3.2, 1.8)).r;
    float blot = texture(grain, SCREEN_UV * vec2(0.45, 0.25) + vec2(0.5)).r;
    c *= 1.0 + (weave - 0.5) * 0.07 + (blot - 0.5) * 0.1;
    vec2 d = (SCREEN_UV - 0.5) * vec2(1.0, 0.8);
    float v = 1.0 - smoothstep(0.3, 0.8, length(d) * 1.3);
    c *= mix(1.0 - vignette, 1.0, v);
    COLOR = vec4(c, 1.0);
}";

    private static ShaderMaterial Make(string code)
    {
        var m = new ShaderMaterial { Shader = new Shader { Code = code } };
        m.SetShaderParameter("grain", PaintNoise.GrainTexture);
        return m;
    }
}
