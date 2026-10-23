using System;
using System.Numerics;

namespace Brawler.Logic;

/// <summary>
/// A hanging platform swinging on its last chain after the other was shot through. The platform swings down about the
/// chain's end while the chain swings about its bolt, so the platform's swing is passed into the chain and back: a
/// double pendulum with the chain as a weightless rod, losing a little speed to <see cref="Damping"/> until it hangs
/// still. Worked out once when the chain is cut, so every peer (and the kill cam) shows the same swing from the time
/// since.
/// </summary>
public sealed class HangerSwing
{
    public const float Gravity = Hanger.Gravity;
    /// <summary>How fast the swing dies down (1/s): it all but stops in about ten seconds.</summary>
    public const float Damping = 0.6f;
    /// <summary>Seconds worked out; after that it hangs where the last one left it.</summary>
    public const float Seconds = 12f;

    private const float Dt = 1f / 240;
    private const int StepsPerSample = 4;
    private const float SampleDt = Dt * StepsPerSample;

    /// <summary>Where the chain is bolted on, and its length.</summary>
    public readonly Vector2 Anchor;
    public readonly float ChainLength;
    // The platform's centre seen from the chain's end, in the platform's own frame: rho at angle alpha. k2 is its
    // moment of inertia about its centre over its mass (a solid bar).
    private readonly float _rho, _alpha, _k2;
    private readonly float[] _samples; // chain angle, platform angle and their rates, per sample

    /// <param name="h">The platform as it hung.</param>
    /// <param name="chainX">Where the remaining chain meets it.</param>
    /// <param name="anchorY">Where that chain is bolted on.</param>
    public HangerSwing(Hanger h, int chainX, int anchorY)
    {
        Anchor = new Vector2(chainX, anchorY);
        ChainLength = Math.Max(1, h.Y - anchorY);
        float sx = h.X + h.W * 0.5f - chainX, sy = h.H * 0.5f;
        _rho = MathF.Sqrt(sx * sx + sy * sy);
        _alpha = MathF.Atan2(sy, sx);
        _k2 = (h.W * h.W + h.H * h.H) / 12f;

        int n = (int)(Seconds / SampleDt) + 1;
        _samples = new float[n * 4];
        // phi: the chain's angle from straight down (positive swings its end toward +x). psi: the direction from the
        // chain's end to the platform's centre (y down), alpha while it hangs level.
        float phi = 0, psi = _alpha, dphi = 0, dpsi = 0;
        for (int i = 0; i < n; i++)
        {
            _samples[i * 4] = phi;
            _samples[i * 4 + 1] = psi;
            _samples[i * 4 + 2] = dphi;
            _samples[i * 4 + 3] = dpsi;
            for (int k = 0; k < StepsPerSample; k++) Step(ref phi, ref psi, ref dphi, ref dpsi);
        }
    }

    /// <summary>The swing <paramref name="t"/> seconds after the cut: the platform's centre, its turn (radians, 0 = level), and how fast both are changing.</summary>
    public void At(float t, out Vector2 center, out float rotation, out Vector2 velocity, out float spin)
    {
        Angles(t, out float phi, out float psi, out float dphi, out float dpsi);
        var end = ChainEnd(phi);
        center = end + _rho * new Vector2(MathF.Cos(psi), MathF.Sin(psi));
        rotation = psi - _alpha;
        velocity = ChainLength * dphi * new Vector2(MathF.Cos(phi), -MathF.Sin(phi)) + _rho * dpsi * new Vector2(-MathF.Sin(psi), MathF.Cos(psi));
        spin = dpsi;
    }

    /// <summary>Where the chain meets the platform <paramref name="t"/> seconds after the cut.</summary>
    public Vector2 ChainEndAt(float t)
    {
        Angles(t, out float phi, out _, out _, out _);
        return ChainEnd(phi);
    }

    private Vector2 ChainEnd(float phi) => Anchor + ChainLength * new Vector2(MathF.Sin(phi), MathF.Cos(phi));

    private void Angles(float t, out float phi, out float psi, out float dphi, out float dpsi)
    {
        int last = _samples.Length / 4 - 1;
        float f = Math.Clamp(t, 0, Seconds) / SampleDt;
        int i = Math.Min((int)f, last), j = Math.Min(i + 1, last);
        float u = f - i;
        float Lerp(int k) => _samples[i * 4 + k] + (_samples[j * 4 + k] - _samples[i * 4 + k]) * u;
        phi = Lerp(0);
        psi = Lerp(1);
        dphi = Lerp(2);
        dpsi = Lerp(3);
    }

    /// <summary>One RK4 step of the equations of motion (from the Lagrangian of a weightless rod holding a bar).</summary>
    private void Step(ref float phi, ref float psi, ref float dphi, ref float dpsi)
    {
        Accel(phi, psi, dphi, dpsi, out float a1, out float b1);
        Accel(phi + 0.5f * Dt * dphi, psi + 0.5f * Dt * dpsi, dphi + 0.5f * Dt * a1, dpsi + 0.5f * Dt * b1, out float a2, out float b2);
        Accel(phi + 0.5f * Dt * (dphi + 0.5f * Dt * a1), psi + 0.5f * Dt * (dpsi + 0.5f * Dt * b1), dphi + 0.5f * Dt * a2, dpsi + 0.5f * Dt * b2, out float a3, out float b3);
        Accel(phi + Dt * (dphi + 0.5f * Dt * a2), psi + Dt * (dpsi + 0.5f * Dt * b2), dphi + Dt * a3, dpsi + Dt * b3, out float a4, out float b4);
        phi += Dt * (dphi + Dt / 6 * (a1 + a2 + a3));
        psi += Dt * (dpsi + Dt / 6 * (b1 + b2 + b3));
        dphi += Dt / 6 * (a1 + 2 * a2 + 2 * a3 + a4);
        dpsi += Dt / 6 * (b1 + 2 * b2 + 2 * b3 + b4);
    }

    private void Accel(float phi, float psi, float dphi, float dpsi, out float ddphi, out float ddpsi)
    {
        float l = ChainLength, s = MathF.Sin(phi + psi), c = MathF.Cos(phi + psi);
        float a11 = l * l, a12 = -l * _rho * s, a22 = _rho * _rho + _k2;
        float r1 = l * _rho * c * dpsi * dpsi - Gravity * l * MathF.Sin(phi) - Damping * a11 * dphi;
        float r2 = l * _rho * c * dphi * dphi + Gravity * _rho * MathF.Cos(psi) - Damping * a22 * dpsi;
        float det = a11 * a22 - a12 * a12;
        ddphi = (r1 * a22 - a12 * r2) / det;
        ddpsi = (a11 * r2 - a12 * r1) / det;
    }
}

/// <summary>
/// A hanging platform's fall once its last chain goes: from wherever it was (hanging level, or mid-swing with the
/// swing's speed and spin), under <see cref="Hanger.Gravity"/>, turning back level as it comes down so it lands flat on
/// the first thing under it. Planned when the chain goes, so every peer shows the same fall.
/// </summary>
public sealed class HangerFall
{
    public readonly Vector2 From, Velocity;
    public readonly float Rotation, Spin;
    /// <summary>Seconds until it lands (∞ if it falls out of the arena), and where: its centre x and its top.</summary>
    public readonly float LandT = float.PositiveInfinity;
    public readonly float LandX;
    public readonly int? LandTop;
    private readonly int _w, _h;
    private readonly float _flat; // the level turn it settles to (a bar looks the same either way up)

    public HangerFall(MapLayout map, Hanger h, Vector2 from, Vector2 velocity, float rotation, float spin)
    {
        From = from;
        Velocity = velocity;
        Rotation = rotation;
        Spin = spin;
        _w = h.W;
        _h = h.H;
        const float step = 1f / 120;
        float prevTop = from.Y - h.H * 0.5f;
        for (float t = step; t < 4; t += step)
        {
            var c = CenterAt(t);
            float top = c.Y - h.H * 0.5f;
            // Ground under it, at or below where its top was a step ago: it comes down on the first one it passes.
            if (HangerMath.LandY(map, h with { X = (int)MathF.Round(c.X - h.W * 0.5f), Y = (int)MathF.Floor(prevTop) }) is { } ground && top >= ground)
            {
                LandT = t;
                LandX = c.X;
                LandTop = ground;
                break;
            }
            if (top > MapLayout.KillY) break;
            prevTop = top;
        }
        float end = rotation + spin * (float.IsInfinity(LandT) ? 0 : LandT);
        _flat = MathF.Round(end / MathF.PI) * MathF.PI;
    }

    /// <summary><paramref name="t"/> seconds into the fall: the centre and turn, and whether it has landed.</summary>
    public bool At(float t, out Vector2 center, out float rotation)
    {
        if (t >= LandT)
        {
            center = new Vector2(LandX, LandTop!.Value + _h * 0.5f);
            rotation = 0;
            return true;
        }
        center = CenterAt(t);
        rotation = Rotation + Spin * t;
        // Ease back level by the time it lands.
        if (!float.IsInfinity(LandT))
        {
            float u = Math.Clamp(t / LandT, 0, 1);
            rotation += (_flat - rotation) * u * u * (3 - 2 * u);
        }
        return false;
    }

    private Vector2 CenterAt(float t)
    {
        float x = Math.Clamp(From.X + Velocity.X * t, _w * 0.5f, MapLayout.Width - _w * 0.5f);
        return new Vector2(x, From.Y + Velocity.Y * t + 0.5f * Hanger.Gravity * t * t);
    }
}
