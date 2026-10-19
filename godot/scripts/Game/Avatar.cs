using System.Collections.Generic;
using Brawler.Core;
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

    public void RecomputeStats()
    {
        Stats = StatBlock.Compute(CardRegistry.Resolve(Cards));
        Effects.Clear();
        foreach (var c in CardRegistry.Resolve(Cards))
            if (CardEffects.Create(c.EffectId) is { } e) Effects.Add(e);
    }
}

/// <summary>
/// The in-world body of a player for one battle. The host runs everything (movement, weapon, block, damage);
/// a client runs only movement for its own avatar (prediction) and smooths everyone else toward snapshots.
/// </summary>
public partial class Avatar : CharacterBody2D
{
    public const float BaseW = 10, BaseH = 14;
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

    public Vector2 HalfSize => new Vector2(BaseW, BaseH) * S[Stat.Size] * 0.5f;
    public Rect2 Bounds => new(GlobalPosition - HalfSize, HalfSize * 2);
    public bool BlockReady => BlockCd <= 0;

    private float _coyote, _wallCoyote, _jumpBuffer, _wallJumpLock;
    private float _wallNormalX;
    private int _lastJump = -1, _lastBlock = -1;
    private float _fireCd, _reloadT, _idleT, _burstT;
    private int _burstLeft;
    private float _hurtFlash;
    private Vector2 _netTarget;
    private bool _hasNetTarget;

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
        _hurtFlash = Mathf.Max(0, _hurtFlash - dt);
        QueueRedraw();
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
        var origin = GlobalPosition + AimDir * (HalfSize.X + 3);
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

    public void OnHurtFx() => _hurtFlash = 0.12f;

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
        _hurtFlash = Mathf.Max(0, _hurtFlash - dt);
        QueueRedraw();
    }

    // ---------- drawing ----------

    public override void _Draw()
    {
        if (!Alive) return;
        var hs = HalfSize.Round();
        var body = new Rect2(-hs, hs * 2);
        var col = _hurtFlash > 0 ? Colors.White : Player.Color;

        DrawRect(new Rect2(body.Position + new Vector2(0, 1), body.Size), new Color(0, 0, 0, 0.35f));
        DrawRect(body, col);
        DrawRect(new Rect2(body.Position, new Vector2(body.Size.X, 2)), col.Lightened(0.35f));
        DrawRect(new Rect2(body.Position + new Vector2(0, body.Size.Y - 2), new Vector2(body.Size.X, 2)), col.Darkened(0.3f));

        // Eye looks toward the aim direction.
        var eye = (AimDir * new Vector2(hs.X * 0.45f, hs.Y * 0.35f) + new Vector2(0, -hs.Y * 0.25f)).Round();
        DrawRect(new Rect2(eye - new Vector2(2, 2), new Vector2(4, 4)), Colors.White);
        DrawRect(new Rect2(eye + (AimDir * 1.2f).Round() - new Vector2(1, 1), new Vector2(2, 2)), Palette.Bg);

        // Gun nub.
        var gun = (AimDir * (hs.X + 2)).Round();
        DrawRect(new Rect2(gun - new Vector2(2, 1), new Vector2(4, 3)), col.Darkened(0.45f));

        // HP bar.
        float frac = Mathf.Clamp(Hp / S[Stat.MaxHp], 0, 1);
        var barPos = new Vector2(-10, -hs.Y - 7);
        DrawRect(new Rect2(barPos, new Vector2(20, 3)), new Color(0, 0, 0, 0.6f));
        DrawRect(new Rect2(barPos, new Vector2(Mathf.Round(20 * frac), 3)), frac > 0.35f ? new Color("#5be37d") : new Color("#ff5050"));

        // Ammo pips or reload bar.
        var pipPos = barPos + new Vector2(0, 4);
        if (Reloading)
        {
            DrawRect(new Rect2(pipPos, new Vector2(20, 1)), new Color(1, 1, 1, 0.2f));
            DrawRect(new Rect2(pipPos, new Vector2(Mathf.Round(20 * ReloadFrac), 1)), Palette.Accent);
        }
        else
        {
            int max = S.Int(Stat.Ammo);
            int pip = max > 10 ? 1 : 2;
            for (int i = 0; i < max; i++)
            {
                var c = i < Ammo ? Palette.Accent : new Color(1, 1, 1, 0.2f);
                DrawRect(new Rect2(pipPos + new Vector2(i * (pip + 1), 0), new Vector2(pip, 1)), c);
            }
        }

        if (BlockActive > 0)
        {
            float r = Mathf.Max(hs.X, hs.Y) + 5;
            DrawArc(Vector2.Zero, r, 0, Mathf.Tau, 24, new Color(1, 1, 1, 0.9f), 1.5f);
            DrawArc(Vector2.Zero, r + 2, 0, Mathf.Tau, 24, Player.Color with { A = 0.5f }, 1f);
        }
        else if (Player == Game.LocalMatchPlayer)
        {
            // Block cooldown indicator under the local player.
            float ready = 1f - Mathf.Clamp(BlockCd / S[Stat.BlockCooldown], 0, 1);
            var p = new Vector2(-6, hs.Y + 3);
            DrawRect(new Rect2(p, new Vector2(12, 1)), new Color(1, 1, 1, 0.15f));
            DrawRect(new Rect2(p, new Vector2(Mathf.Round(12 * ready), 1)), ready >= 1 ? Colors.White : new Color(1, 1, 1, 0.5f));
        }
    }
}
