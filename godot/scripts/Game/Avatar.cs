using System.Collections.Generic;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Logic;
using Godot;

namespace Brawler.Game;

/// <summary>Persistent per-match player record (survives across battles; the Avatar does not).</summary>
public sealed class MatchPlayer
{
    public PlayerInfo Info;
    public int Slot => Info.Slot;
    public Color Color => Palette.Player(Info.ColorIdx);
    public readonly List<int> Cards = new();
    public StatBlock Stats = StatBlock.Base();
    public readonly List<ICardEffect> Effects = new();
    public int RoundWins, BattleWins;
    public bool Left; // disconnected mid-match
    public Avatar Avatar;
    public IInputSource Input;
    /// <summary>How flashy this player's effects are, from their cards (cosmetic).</summary>
    public FxStyle Fx = FxStyle.None;

    public void RecomputeStats()
    {
        Stats = StatBlock.Compute(CardRegistry.Resolve(Cards));
        Effects.Clear();
        foreach (var c in CardRegistry.Resolve(Cards))
            if (CardEffects.Create(c.EffectId) is { } e) Effects.Add(e);
        Fx = FxStyle.From(Cards);
    }
}

/// <summary>
/// The in-world body of a player for one battle. The host runs everything (movement, weapon, block, damage);
/// a client runs only movement for its own avatar (prediction) and smooths everyone else toward snapshots.
/// </summary>
public partial class Avatar : CharacterBody2D
{
    public const float BaseW = Body.W, BaseH = Body.H;
    private const float CoyoteTime = 0.1f, JumpBuffer = 0.12f, WallSlideSpeed = 45f, MaxFall = 520f;

    public MatchPlayer Player;
    public GameManager Game;
    public StatBlock S => Player.Stats;

    // Authoritative state (host) / replicated state (clients).
    public float Hp;
    public bool Alive = true;
    public int Ammo;
    public bool Reloading;
    public float ReloadFrac;     // 0..1 progress while reloading
    public float BlockActive;    // seconds of block remaining
    public float BlockCd;        // seconds until block is ready
    public Vector2 AimDir = Vector2.Right;
    public bool Frozen = true;
    public int LastHitBy = -1;   // host: who hit us last, and when (credits knock-off kills)
    public float LastHitAt = -99;

    public Vector2 HalfSize => new Vector2(BaseW, BaseH) * S[Stat.Size] * 0.5f;
    public Rect2 Bounds => new(GlobalPosition - HalfSize, HalfSize * 2);
    public bool BlockReady => BlockCd <= 0;

    private float _coyote, _wallCoyote, _jumpBuffer, _wallJumpLock;
    private float _wallNormalX;
    private int _lastJump = -1, _lastBlock = -1;
    private float _fireCd, _reloadT, _idleT, _burstT;
    private int _burstLeft;
    private float _hurtFlash, _hpGhost = -1, _ghostHold;
    private Vector2 _netTarget;
    private bool _hasNetTarget;

    // Animation only (never affects the simulation).
    private int _facing = 1;
    private float _walkPhase, _animT, _blinkT, _nextBlink = 2f, _kick, _flash;
    public GunLook Gun = GunLook.Default;

    public void Setup(MatchPlayer owner, GameManager game, Vector2 pos)
    {
        Player = owner;
        Game = game;
        Name = $"P{owner.Slot}";
        Position = pos;
        Hp = S[Stat.MaxHp];
        Ammo = S.Int(Stat.Ammo);
        CollisionLayer = 2;
        CollisionMask = 1;
        FloorSnapLength = 4;
        var shape = new CollisionShape2D { Shape = new RectangleShape2D { Size = HalfSize * 2 } };
        AddChild(shape);
    }

    // ---------- simulation ----------

    /// <summary>One physics tick. <paramref name="authority"/> = host: also runs weapon and block.</summary>
    public void Step(InputFrame f, float dt, bool authority)
    {
        if (!Alive) return;
        bool jumpPressed = Edge(ref _lastJump, f.JumpPresses);
        bool blockPressed = Edge(ref _lastBlock, f.BlockPresses);
        if (Frozen) { f = default; jumpPressed = blockPressed = false; f.Aim = GlobalPosition + AimDir; }

        var toAim = f.Aim - GlobalPosition;
        if (toAim.LengthSquared() > 1) AimDir = toAim.Normalized();

        Move(f, jumpPressed, dt);
        if (authority)
        {
            Weapon(f, dt);
            Block(blockPressed, dt);
        }
        Animate(dt);
    }

    private static bool Edge(ref int last, byte value)
    {
        bool pressed = last >= 0 && last != value;
        last = value;
        return pressed;
    }

    private void Move(InputFrame f, bool jumpPressed, float dt)
    {
        var v = Velocity;
        float g = S[Stat.Gravity];
        float speed = S[Stat.MoveSpeed];
        bool onFloor = IsOnFloor();
        bool onWall = IsOnWallOnly();

        if (onFloor) _coyote = CoyoteTime; else _coyote -= dt;
        if (onWall)
        {
            _wallCoyote = CoyoteTime;
            _wallNormalX = Mathf.Sign(GetWallNormal().X);
        }
        else _wallCoyote -= dt;
        if (jumpPressed) _jumpBuffer = JumpBuffer; else _jumpBuffer -= dt;
        _wallJumpLock -= dt;

        float control = _wallJumpLock > 0 ? 0.25f : 1f;
        float accel = (onFloor ? 1500f : 950f) * control;
        v.X = Mathf.MoveToward(v.X, f.MoveX * speed, accel * dt);

        v.Y += g * dt;
        if (!f.JumpHeld && v.Y < 0) v.Y += g * 1.2f * dt; // short hop on release
        bool pushingWall = onWall && f.MoveX * _wallNormalX < -0.1f;
        if (pushingWall && !onFloor && v.Y > WallSlideSpeed) v.Y = WallSlideSpeed;
        v.Y = Mathf.Min(v.Y, MaxFall);

        if (_jumpBuffer > 0)
        {
            float jv = S[Stat.JumpVelocity];
            if (_coyote > 0)
            {
                v.Y = -jv;
                _coyote = 0;
                _jumpBuffer = 0;
                Game.OnJump(this);
            }
            else if (_wallCoyote > 0)
            {
                v.Y = -jv * 0.95f;
                v.X = _wallNormalX * speed * 1.5f;
                _wallCoyote = 0;
                _jumpBuffer = 0;
                _wallJumpLock = 0.14f;
                Game.OnJump(this);
            }
        }

        // Jump pads launch anyone standing on them (runs on the host and in client prediction alike).
        if (onFloor && v.Y >= 0 && Game.Map != null && MapGenerator.OnPad(Game.Map, GlobalPosition.X, GlobalPosition.Y + HalfSize.Y))
        {
            v.Y = -MapGenerator.PadVelocity;
            _coyote = 0;
        }

        Velocity = v;
        MoveAndSlide();
    }

    private void Weapon(InputFrame f, float dt)
    {
        int maxAmmo = S.Int(Stat.Ammo);
        _fireCd -= dt;
        _idleT += dt;

        if (Reloading)
        {
            _reloadT -= dt;
            ReloadFrac = 1f - Mathf.Clamp(_reloadT / S[Stat.ReloadTime], 0, 1);
            if (_reloadT <= 0)
            {
                Reloading = false;
                Ammo = maxAmmo;
            }
        }
        else if (Ammo < maxAmmo && _burstLeft == 0 && (Ammo == 0 || _idleT > 1.1f))
        {
            StartReload();
        }

        if (f.Shoot && _fireCd <= 0 && _burstLeft == 0 && Ammo > 0 && !Reloading)
        {
            Ammo--;
            _fireCd = S[Stat.FireCooldown];
            _burstLeft = S.Int(Stat.BurstCount);
            _burstT = 0;
            _idleT = 0;
        }

        if (_burstLeft > 0)
        {
            _burstT -= dt;
            if (_burstT <= 0)
            {
                FireVolley();
                _burstLeft--;
                _burstT = S[Stat.BurstInterval];
            }
        }
    }

    private void StartReload()
    {
        Reloading = true;
        _reloadT = S[Stat.ReloadTime];
        ReloadFrac = 0;
    }

    private void FireVolley()
    {
        int n = S.Int(Stat.Projectiles);
        float spread = S[Stat.Spread];
        if (n > 1 && spread <= 0) spread = 0.08f * (n - 1);
        float baseAngle = AimDir.Angle();
        var origin = MuzzlePoint();
        for (int i = 0; i < n; i++)
        {
            float t = n == 1 ? 0.5f : i / (float)(n - 1);
            float jitter = n > 1 ? Game.HostRng.Range(-0.03f, 0.03f) : 0;
            float a = baseAngle + (t - 0.5f) * spread + jitter;
            var dir = Vector2.FromAngle(a);
            Game.HostSpawnBullet(BulletSystem.FromStats(Player.Slot, origin, dir * S[Stat.BulletSpeed], S));
        }
    }

    private void Block(bool pressed, float dt)
    {
        BlockActive = Mathf.Max(0, BlockActive - dt);
        BlockCd = Mathf.Max(0, BlockCd - dt);
        if (!pressed || BlockCd > 0) return;
        BlockActive = S[Stat.BlockDuration];
        BlockCd = S[Stat.BlockCooldown];
        Game.HostOnBlock(this);
        foreach (var e in Player.Effects) e.OnBlock(this, Game);
    }

    /// <summary>Host: apply a hit. Returns false when the block absorbed it.</summary>
    public bool TakeHit(float damage, Vector2 knockback)
    {
        if (!Alive) return false;
        if (BlockActive > 0) return false;
        Hp -= damage;
        Velocity += knockback;
        return true;
    }

    public void Heal(float amount) => Hp = Mathf.Min(S[Stat.MaxHp], Hp + amount);

    public void OnHurtFx()
    {
        _hurtFlash = 0.12f;
        _ghostHold = 0;
    }

    // ---------- replication ----------

    public void SetNetTarget(Vector2 pos, Vector2 vel, bool predicted)
    {
        if (predicted)
        {
            // Own avatar on a client: trust local movement unless we've drifted (teleports, knockback misses).
            if (GlobalPosition.DistanceTo(pos) > 24) GlobalPosition = pos;
            return;
        }
        _netTarget = pos;
        Velocity = vel;
        if (!_hasNetTarget) GlobalPosition = pos;
        _hasNetTarget = true;
    }

    public void SmoothRemote(float dt)
    {
        if (!_hasNetTarget) return;
        _netTarget += Velocity * dt; // extrapolate between snapshots
        GlobalPosition = GlobalPosition.Lerp(_netTarget, 0.5f);
        Animate(dt);
    }

    // ---------- animation and drawing ----------

    /// <summary>Called on every peer when this avatar fires a bullet (recoil and muzzle flash).</summary>
    public void OnFired()
    {
        _kick = 1;
        _flash = 1;
    }

    /// <summary>Bullet spawn point: the gun's muzzle, pulled back if a wall is in between.</summary>
    private Vector2 MuzzlePoint()
    {
        var from = GlobalPosition;
        var delta = AvatarArt.Muzzle(AimDir, _facing, S[Stat.Size], Gun);
        var hit = Sweep.Cast(new System.Numerics.Vector2(from.X, from.Y), new System.Numerics.Vector2(delta.X, delta.Y),
            Mathf.Max(1, S[Stat.BulletRadius]), Game.Map.Solids);
        float t = hit is { } h ? Mathf.Max(0, h.T - 0.05f) : 1;
        return from + delta * t;
    }

    private void Animate(float dt)
    {
        _hurtFlash = Mathf.Max(0, _hurtFlash - dt);
        // The ghost bar holds briefly after a hit, then drains down to the real HP.
        if (_hpGhost < Hp) { _hpGhost = Hp; _ghostHold = 0; }
        else if (_hpGhost > Hp)
        {
            _ghostHold += dt;
            if (_ghostHold > 0.35f) _hpGhost = Mathf.MoveToward(_hpGhost, Hp, S[Stat.MaxHp] * 1.5f * dt);
        }
        _kick = Mathf.Max(0, _kick - dt * 9);
        _flash = Mathf.Max(0, _flash - dt * 14);
        _animT += dt;
        if (Mathf.Abs(AimDir.X) > 0.15f) _facing = AimDir.X < 0 ? -1 : 1;
        _walkPhase += Mathf.Abs(Velocity.X) * dt / 7f;
        _blinkT -= dt;
        if (_blinkT < -_nextBlink)
        {
            _blinkT = 0.12f;
            _nextBlink = 1.8f + (GetInstanceId() % 7) * 0.4f + (_animT % 1.3f);
        }
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (!Alive) return;
        float size = S[Stat.Size];
        var pose = new AvatarPose
        {
            Body = Player.Color,
            Look = Player.Info.Look,
            Facing = _facing,
            Aim = AimDir,
            Scale = size,
            Time = _animT,
            WalkPhase = _walkPhase,
            Grounded = Mathf.Abs(Velocity.Y) < 1f,
            Moving = Mathf.Abs(Velocity.X) > 12f,
            Blink = _blinkT > 0,
            Hurt = _hurtFlash > 0,
            Gun = true,
            GunLook = Gun,
            Kick = _kick,
            Flash = _flash,
        };
        // A soft aura in the player color keeps characters readable on any backdrop.
        GlowTex.Draw(this, Vector2.Zero, 22 * size, Player.Color with { A = 0.22f });
        AvatarArt.Draw(this, Vector2.Zero, pose);

        var hs = HalfSize;
        float top = AvatarArt.TopY(Player.Info.Look) * size;
        bool mine = Player == Game.LocalMatchPlayer;

        // HP bar above the head (and hat), with a white "ghost" that drains after a hit.
        float max = S[Stat.MaxHp];
        float frac = Mathf.Clamp(Hp / max, 0, 1), ghost = Mathf.Clamp(_hpGhost / max, 0, 1);
        const float barW = 24, barH = 3.4f;
        var barPos = new Vector2(-barW / 2, top - 9);
        DrawColoredPolygon(Shapes.RoundRect(new Rect2(barPos - new Vector2(1, 1), new Vector2(barW + 2, barH + 2)), 2.2f), new Color(0.05f, 0.04f, 0.1f, 0.75f));
        if (ghost > frac)
            DrawColoredPolygon(Shapes.RoundRect(new Rect2(barPos, new Vector2(barW * ghost, barH)), 1.6f), new Color(1, 1, 1, 0.85f));
        if (frac > 0)
        {
            var hpCol = frac > 0.35f ? new Color("#52f08a") : new Color("#ff4d6a");
            var bar = Shapes.RoundRect(new Rect2(barPos, new Vector2(Mathf.Max(barH, barW * frac), barH)), 1.6f);
            Shapes.Gradient(this, bar, hpCol.Lightened(0.3f), hpCol.Darkened(0.15f));
        }

        // Ammo dots or a reload sweep.
        var pipPos = barPos + new Vector2(0, barH + 2.6f);
        if (Reloading)
        {
            DrawColoredPolygon(Shapes.RoundRect(new Rect2(pipPos, new Vector2(barW, 1.4f)), 0.7f), new Color(1, 1, 1, 0.18f));
            DrawColoredPolygon(Shapes.RoundRect(new Rect2(pipPos, new Vector2(Mathf.Max(1.4f, barW * ReloadFrac), 1.4f)), 0.7f), Palette.Accent);
        }
        else
        {
            int ammo = S.Int(Stat.Ammo);
            float gap = Mathf.Min(3.2f, barW / Mathf.Max(1, ammo));
            float r = Mathf.Clamp(gap * 0.36f, 0.5f, 1.1f);
            for (int i = 0; i < ammo; i++)
                DrawCircle(pipPos + new Vector2(gap * (i + 0.5f), 0.7f), r, i < Ammo ? Palette.Accent : new Color(1, 1, 1, 0.2f), true, -1, true);
        }

        // "You" marker over the local player.
        if (mine)
        {
            float bobY = Mathf.Sin(_animT * 4) * 0.8f;
            var tip = new Vector2(0, barPos.Y - 3 + bobY);
            DrawColoredPolygon(new[] { tip + new Vector2(-2.6f, -3.4f), tip + new Vector2(2.6f, -3.4f), tip }, Player.Color.Lightened(0.25f));
        }

        if (BlockActive > 0)
        {
            float r = Mathf.Max(hs.X, hs.Y) + 6;
            float k = Mathf.Clamp(BlockActive / Mathf.Max(0.05f, S[Stat.BlockDuration]), 0, 1);
            GlowTex.Draw(this, Vector2.Zero, r * 1.6f, Player.Color with { A = 0.35f });
            DrawCircle(Vector2.Zero, r, Player.Color.Lightened(0.4f) with { A = 0.18f }, true, -1, true);
            DrawArc(Vector2.Zero, r, 0, Mathf.Tau, 40, Shapes.Hdr(Colors.White, 1.4f) with { A = 0.6f + 0.4f * k }, 1.4f, true);
            DrawArc(Vector2.Zero, r + 2.2f, _animT * 6, _animT * 6 + Mathf.Pi * 1.2f, 20, Shapes.Hdr(Player.Color, 1.5f), 1f, true);
        }
        else if (mine)
        {
            // Block cooldown ring segment under the local player.
            float ready = 1f - Mathf.Clamp(BlockCd / S[Stat.BlockCooldown], 0, 1);
            var p = new Vector2(-8, hs.Y + 3.5f);
            DrawColoredPolygon(Shapes.RoundRect(new Rect2(p, new Vector2(16, 1.4f)), 0.7f), new Color(1, 1, 1, 0.15f));
            if (ready > 0)
                DrawColoredPolygon(Shapes.RoundRect(new Rect2(p, new Vector2(Mathf.Max(1.4f, 16 * ready), 1.4f)), 0.7f),
                    ready >= 1 ? Shapes.Hdr(Colors.White, 1.2f) : new Color(1, 1, 1, 0.5f));
        }
    }
}
