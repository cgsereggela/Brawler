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
    public bool Benched; // Survival: a bot sitting out this wave (no avatar)
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
    // Gravity is gentle (see StatBlock.Base) and leaving the ground without jumping keeps the ground jump for the
    // whole time in the air (JumpCharges), so a missed step can be saved mid-air. Coyote time now only decides that
    // a press just after leaving the floor is a ground jump rather than a wall jump. Falls are capped low enough to
    // read and react to.
    private const float CoyoteTime = 0.2f, WallCoyoteTime = 0.1f, JumpBuffer = 0.12f, WallSlideSpeed = 40f, MaxFall = 430f;
    private const float PushSpeed = 0.55f; // crates move at this fraction of your run speed
    // A wall jump kicks you off at this multiple of run speed, then steering is weak for WallJumpLock seconds. Kept small
    // so you can steer straight back and climb a single wall by jumping off it while holding toward it.
    private const float WallJumpKick = 1.15f, WallJumpLock = 0.05f;
    // Ground/air acceleration and the jetpack's climb speed, scaled with the 25% faster run and higher jump so speeding
    // up takes as long as before and a tank of fuel lifts you 25% further.
    private const float GroundAccel = 1875f, AirAccel = 1190f, JetClimb = 262f;

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
    public float CounterT;       // host: seconds of Riposte bonus damage left
    public Vector2 AimDir = Vector2.Right;
    public bool Frozen = true;
    public int LastHitBy = -1;   // host: who hit us last, and when (credits knock-off kills)
    public float LastHitAt = -99;
    /// <summary>When this body came in (<see cref="GameManager.Clock"/>), and whether it was a respawn, which guards it for a moment.</summary>
    public float BornAt;
    public bool Respawned;
    public bool Guarded => Respawned && Alive && Game.Clock - BornAt < GameModes.SpawnGuard;

    public Vector2 HalfSize => new Vector2(BaseW, BaseH) * S[Stat.Size] * 0.5f;
    public Rect2 Bounds => new(GlobalPosition - HalfSize, HalfSize * 2);
    public bool BlockReady => BlockCd <= 0;
    /// <summary>Jumps usable in the air right now: the kept ground (ledge) jump plus card air jumps. For bots.</summary>
    public int MidAirJumpsLeft => _jumps.Left;

    private float _coyote, _wallCoyote, _jumpBuffer, _wallJumpLock;
    private JumpCharges _jumps;
    private int _livesUsed;
    private float _fuel, _leechT;
    private bool _jetting;
    private float _wallNormalX;
    private int _lastJump = -1, _lastBlock = -1;
    private float _fireCd, _reloadT, _burstT;
    private int _burstLeft;
    private float _hurtFlash, _hpGhost = -1, _ghostHold;
    private Vector2 _netTarget;
    private bool _hasNetTarget;

    // Animation only (never affects the simulation).
    private int _facing = 1;
    private float _walkPhase, _animT, _blinkT, _nextBlink = 2f, _kick, _flash;
    // Legs: blends eased toward what the geometry and velocity say (works the same for remote players).
    private float _walk, _air, _wall, _land, _airVy, _groundDrop, _groundSlope;
    private Vector2 _elbow, _elbowVel, _armSway, _armLastPos, _armBodyVel; // arm follow-through (sprite space), see AnimateArm
    private int _armFacing;
    private int _moveDir = 1, _wallSide = 1;
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
        // Slopes are at most 40 degrees (TerrainGen.MaxAngle), under the 45 degree floor limit. Constant speed
        // keeps running uphill as quick as on the flat, and the longer snap holds you to a ramp running down it at
        // full speed (the 400 px/s cap drops ~5.6 px a tick on the steepest ramp) instead of hopping off it.
        FloorMaxAngle = Mathf.DegToRad(45);
        FloorConstantSpeed = true;
        FloorSnapLength = 8;
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

        _authority = authority;
        Move(f, jumpPressed, dt);
        if (authority)
        {
            Weapon(f, dt);
            Block(blockPressed, dt);
            Unwedge(dt);
            if (!Frozen) Passives(dt);
        }
        Animate(dt);
    }

    private bool _authority;
    private float _wedgedT;
    private Vector2 _wedgedAt;

    /// <summary>
    /// Host: a body can end up a fraction of a pixel inside a gap shorter than itself (a Giant is 32.4 px tall, many
    /// shelves leave 32), where the physics can push it out neither up nor down and it never moves again. If we've
    /// overlapped the map without moving for a moment, pop out to the nearest free spot.
    /// </summary>
    private void Unwedge(float dt)
    {
        var map = Game.Map;
        if (map == null) return;
        var hs = HalfSize - new Vector2(0.01f, 0.01f);
        if (GlobalPosition.DistanceTo(_wedgedAt) > 0.5f || !Squeezed(map, GlobalPosition, hs))
        {
            _wedgedAt = GlobalPosition;
            _wedgedT = 0;
            return;
        }
        _wedgedT += dt;
        if (_wedgedT < 0.3f) return;
        _wedgedT = 0;
        for (int d = 1; d <= 64; d++)
            foreach (var off in new[] { new Vector2(d, 0), new Vector2(-d, 0), new Vector2(0, -d), new Vector2(0, d) })
                if (!OverlapsMap(map, GlobalPosition + off, hs + Vector2.One))
                {
                    GlobalPosition += off;
                    return;
                }
    }

    /// <summary>Overlapping solids on opposite sides (above and below, or left and right): pinched, not just resting.</summary>
    private static bool Squeezed(MapLayout map, Vector2 c, Vector2 hs)
    {
        bool up = false, down = false, left = false, right = false;
        foreach (var r in map.Solids)
        {
            if (!(c.X + hs.X > r.X && c.X - hs.X < r.Right && c.Y + hs.Y > r.Y && c.Y - hs.Y < r.Bottom)) continue;
            if (r.Bottom < c.Y) up = true;
            if (r.Y > c.Y) down = true;
            if (r.Right < c.X) left = true;
            if (r.X > c.X) right = true;
        }
        // Sloped ground only ever pinches from below or the side it rises on.
        foreach (var s in map.Slopes)
        {
            if (!s.Overlaps(c.X - hs.X, c.Y - hs.Y, c.X + hs.X, c.Y + hs.Y)) continue;
            down = true;
            if (s.X2 < c.X) left = true;
            if (s.X1 > c.X) right = true;
        }
        return (up && down) || (left && right);
    }

    private static bool OverlapsMap(MapLayout map, Vector2 c, Vector2 hs)
    {
        foreach (var r in map.Solids)
            if (c.X + hs.X > r.X && c.X - hs.X < r.Right && c.Y + hs.Y > r.Y && c.Y - hs.Y < r.Bottom) return true;
        foreach (var s in map.Slopes)
            if (s.Overlaps(c.X - hs.X, c.Y - hs.Y, c.X + hs.X, c.Y + hs.Y)) return true;
        return false;
    }

    /// <summary>Host: per-tick card effects that aren't tied to an input (regeneration, Leech Field).</summary>
    private void Passives(float dt)
    {
        float regen = S[Stat.Regen];
        if (regen > 0 && Hp < S[Stat.MaxHp]) Heal(regen * dt);
        float leech = S[Stat.Leech];
        if (leech > 0)
        {
            _leechT -= dt;
            if (_leechT <= 0)
            {
                const float Pulse = 0.33f;
                _leechT = Pulse;
                Game.HostLeech(this, leech * Pulse);
            }
        }
    }

    /// <summary>Under 40% HP with Adrenaline: the bonus (e.g. 0.5 = +50%), else 0. Hp is replicated, so clients agree.</summary>
    public float Rage => S[Stat.Adrenaline] > 0 && Hp < S[Stat.MaxHp] * 0.4f ? S[Stat.Adrenaline] : 0;

    /// <summary>Host: Second Wind. Brings a lethal hit back to half HP if a revive is left.</summary>
    public bool TryRevive()
    {
        if (_livesUsed >= S.Int(Stat.ExtraLives)) return false;
        _livesUsed++;
        Hp = S[Stat.MaxHp] * 0.5f;
        return true;
    }

    /// <summary>Recoil Jet: each bullet pushes the shooter back (split across a multi-bullet volley).</summary>
    public void ApplyRecoil(Vector2 shotDir)
    {
        float recoil = S[Stat.Recoil];
        if (recoil <= 0 || Frozen) return;
        var v = Velocity - shotDir * recoil / Mathf.Max(1, S[Stat.Projectiles]);
        Velocity = v.LimitLength(620);
    }

    /// <summary>Sets velocity and briefly reduces steering, so a dash or launch carries.</summary>
    public void Launch(Vector2 velocity, float lockTime)
    {
        Velocity = velocity;
        _wallJumpLock = lockTime;
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
        float speed = S[Stat.MoveSpeed] * (1 + Rage);
        bool onFloor = IsOnFloor();
        bool onWall = IsOnWallOnly();

        // Landing restores every jump; touching a wall restores the air jumps and fuel but not the ground jump.
        if (onFloor) _jumps.Land(S.Int(Stat.AirJumps));
        else if (onWall) _jumps.Air = S.Int(Stat.AirJumps);
        if (onFloor || onWall) _fuel = S[Stat.Jetpack];
        if (onFloor) _coyote = CoyoteTime; else _coyote -= dt;
        if (onWall)
        {
            _wallCoyote = WallCoyoteTime;
            _wallNormalX = Mathf.Sign(GetWallNormal().X);
        }
        else _wallCoyote -= dt;
        if (jumpPressed) _jumpBuffer = JumpBuffer; else _jumpBuffer -= dt;
        _wallJumpLock -= dt;

        float control = _wallJumpLock > 0 ? 0.25f : 1f;
        float accel = (onFloor ? GroundAccel : AirAccel) * control * S[Stat.Accel];
        v.X = Mathf.MoveToward(v.X, f.MoveX * speed, accel * dt);

        v.Y += g * dt;
        if (!f.JumpHeld && v.Y < 0 && !_jetting) v.Y += g * 1.2f * dt; // short hop on release
        bool pushingWall = onWall && f.MoveX * _wallNormalX < -0.1f;
        float slide = WallSlideSpeed / S[Stat.WallJump];
        if (pushingWall && !onFloor && v.Y > slide) v.Y = slide;
        if (S[Stat.Glide] > 0 && !onFloor && f.JumpHeld && v.Y > 50) v.Y = 50; // Glider
        v.Y = Mathf.Min(v.Y, MaxFall);

        // Jetpack: once armed by a mid-air jump press, holding jump thrusts upward until the fuel runs out.
        if (!f.JumpHeld || _fuel <= 0 || onFloor) _jetting = false;
        if (_jetting)
        {
            _fuel -= dt;
            v.Y = Mathf.MoveToward(v.Y, -JetClimb, 2200 * dt);
        }

        // Jump pads launch anyone standing on them (runs on the host and in client prediction alike). The launch keeps
        // the ground jump, so it can still be used any time before landing.
        if (onFloor && v.Y >= 0 && Game.Map != null && MapGenerator.OnPad(Game.Map, GlobalPosition.X, GlobalPosition.Y + HalfSize.Y))
        {
            v.Y = -MapGenerator.PadVelocity;
            _coyote = 0;
        }

        if (_jumpBuffer > 0)
        {
            float jv = S[Stat.JumpVelocity];
            var kind = _jumps.Use(_coyote > 0, _wallCoyote > 0, _fuel > 0, jumpPressed, risingFaster: v.Y < -jv);
            if (kind == JumpKind.Ground)
            {
                v.Y = -jv;
                _coyote = 0;
                _jumpBuffer = 0;
                Game.OnJump(this);
            }
            else if (_wallCoyote > 0)
            {
                v.Y = -jv * 0.95f * S[Stat.WallJump];
                v.X = _wallNormalX * speed * WallJumpKick;
                _wallCoyote = 0;
                _jumpBuffer = 0;
                _wallJumpLock = WallJumpLock;
                Game.OnJump(this);
            }
            else if (kind is JumpKind.Ledge or JumpKind.Air)
            {
                // The kept ground jump (walked or knocked off a ledge) is a full jump; card air jumps are a bit weaker.
                v.Y = kind == JumpKind.Ledge ? -jv : -jv * 0.9f;
                _jumpBuffer = 0;
                Game.OnJump(this, air: true);
            }
            else if (kind == JumpKind.Jet)
            {
                _jetting = true;
                _jumpBuffer = 0;
            }
        }

        Velocity = v;
        MoveAndSlide();

        // Walking into a crate or barrel shoves it along, and so does walking into a drifting platform, which also
        // sinks under whoever lands or stands on it (the host moves them; clients see it in snapshots).
        if (_authority)
            for (int i = 0; i < GetSlideCollisionCount(); i++)
            {
                var c = GetSlideCollision(i);
                var n = c.GetNormal();
                if (c.GetCollider() is DriftBody drift)
                {
                    if (n.Y < -0.7f) Game.Arena.DriftLoad(drift, v.Y);
                    else if (Mathf.Abs(n.X) > 0.7f && f.MoveX * n.X < -0.1f) Game.Arena.DriftPush(drift, -n.X);
                    continue;
                }
                if (c.GetCollider() is not Prop { Pushable: true, Broken: false } prop) continue;
                if (Mathf.Abs(n.X) > 0.7f && f.MoveX * n.X < -0.1f) prop.Push(-n.X * speed * PushSpeed);
            }

        // Pinball: bounce off whatever we hit hard enough, keeping most of the speed.
        if (S[Stat.Pinball] > 0)
        {
            for (int i = 0; i < GetSlideCollisionCount(); i++)
            {
                var n = GetSlideCollision(i).GetNormal();
                float into = v.Dot(n);
                if (into > -130) continue;
                v -= n * into * 1.85f; // reflect with 85% restitution on the normal component
                Velocity = v;
                _wallJumpLock = 0.12f;
                break;
            }
        }
    }

    private void Weapon(InputFrame f, float dt)
    {
        int maxAmmo = S.Int(Stat.Ammo);
        _fireCd -= dt;

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
        // By itself only once the magazine is empty; earlier when the player asks (the reload key).
        else if (Ammo < maxAmmo && _burstLeft == 0 && (Ammo == 0 || f.Reload))
        {
            StartReload();
        }

        if (f.Shoot && _fireCd <= 0 && _burstLeft == 0 && Ammo > 0 && !Reloading)
        {
            Ammo--;
            _fireCd = S[Stat.FireCooldown] / (1 + Rage);
            _burstLeft = S.Int(Stat.BurstCount);
            _burstT = 0;
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
        // Berserk: more damage the lower your HP.
        float rage = 1 + S[Stat.Berserk] * (1 - Mathf.Clamp(Hp / S[Stat.MaxHp], 0, 1));
        bool wild = S[Stat.Wild] > 0;
        for (int i = 0; i < n; i++)
        {
            float t = n == 1 ? 0.5f : i / (float)(n - 1);
            float jitter = n > 1 ? Game.HostRng.Range(-0.03f, 0.03f) : 0;
            float a = baseAngle + (t - 0.5f) * spread + jitter;
            var dir = Vector2.FromAngle(a);
            var b = BulletSystem.FromStats(Player.Slot, origin, dir * S[Stat.BulletSpeed], S);
            b.Damage *= rage;
            if (CounterT > 0) b.Damage *= 1 + S[Stat.Counter];
            if (wild)
            {
                // Wild Rounds: big ones are slow and hit hard, small ones are quick.
                float size = Game.HostRng.Range(0.4f, 2.8f);
                b.Radius = Mathf.Clamp(b.Radius * size, 1, 14);
                b.Vel *= Game.HostRng.Range(0.55f, 1.6f) / Mathf.Sqrt(size);
                b.Damage *= Game.HostRng.Range(0.6f, 1.3f) * Mathf.Sqrt(size);
                b.Knockback *= size;
            }
            Game.HostSpawnBullet(b);
        }
    }

    private void Block(bool pressed, float dt)
    {
        BlockActive = Mathf.Max(0, BlockActive - dt);
        BlockCd = Mathf.Max(0, BlockCd - dt);
        CounterT = Mathf.Max(0, CounterT - dt);
        if (!pressed || BlockCd > 0) return;
        BlockActive = S[Stat.BlockDuration];
        BlockCd = S[Stat.BlockCooldown];
        Game.HostOnBlock(this);
        foreach (var e in Player.Effects) e.OnBlock(this, Game);
    }

    /// <summary>Host: Reactive Reload. Ends any reload and fills the magazine.</summary>
    public void RefillAmmo()
    {
        Reloading = false;
        ReloadFrac = 0;
        Ammo = S.Int(Stat.Ammo);
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
            if (GlobalPosition.DistanceTo(pos) > 24 && Alive) GlobalPosition = pos;
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
        var from = GlobalPosition;
        GlobalPosition = GlobalPosition.Lerp(_netTarget, 0.5f);
        if (!Alive) GhostTravel += from.DistanceTo(GlobalPosition);
        if (Alive) Animate(dt);
        else GhostTick(dt);
    }

    // ---------- ghost and emotes ----------

    /// <summary>Seconds after dying before the ghost rises, so the death burst plays out first.</summary>
    public const float GhostDelay = 0.7f;
    private const float GhostSpeed = 260f, GhostMargin = 12f;
    private float _deadT;
    private Node2D _ghostArt;
    private int _emote = -1;
    private float _emoteT;
    private bool _emoteAsGhost;
    /// <summary>How far the ghost has floated (a test hook: --chatcheck checks ghosts really move, on every peer).</summary>
    public float GhostTravel;

    /// <summary>True once this dead player's ghost has risen. Players who left the game don't haunt it.</summary>
    public bool GhostUp => !Alive && !Player.Left && _deadT >= GhostDelay;
    public float GhostAge => GhostUp ? _deadT - GhostDelay : 0;

    /// <summary>Hides the ghost straight away when its player leaves (left players get no more ghost ticks).</summary>
    public void SyncGhost()
    {
        if (_ghostArt != null) _ghostArt.Visible = GhostUp;
        QueueRedraw();
    }

    /// <summary>
    /// Dead: float toward <paramref name="target"/> (the player's cursor), through walls, anywhere in the arena. Run by
    /// the host for everyone and by a client for its own ghost; ghosts touch nothing, so this never affects the fight.
    /// </summary>
    public void GhostStep(Vector2 target, float dt)
    {
        GhostTick(dt);
        if (!GhostUp)
        {
            Velocity = Vector2.Zero;
            return;
        }
        var want = ((target - GlobalPosition) * 5f).LimitLength(GhostSpeed);
        Velocity = Velocity.Lerp(want, 1f - Mathf.Exp(-8f * dt));
        var from = GlobalPosition;
        var p = GlobalPosition + Velocity * dt;
        GlobalPosition = new Vector2(Mathf.Clamp(p.X, GhostMargin, MapLayout.Width - GhostMargin), Mathf.Clamp(p.Y, GhostMargin, MapLayout.Height - GhostMargin));
        GhostTravel += from.DistanceTo(GlobalPosition);
    }

    /// <summary>Ghost animation, on every peer: fade in, bob, face the way it drifts.</summary>
    private void GhostTick(float dt)
    {
        _deadT += dt;
        _animT += dt;
        _emoteT = Mathf.Max(0, _emoteT - dt);
        if (Mathf.Abs(Velocity.X) > 8) _facing = Velocity.X < 0 ? -1 : 1;
        UpdateGhostArt();
    }

    private void UpdateGhostArt()
    {
        if (_ghostArt == null)
        {
            // Its own node so the whole figure fades as one, drawn under this node's emote bubble.
            _ghostArt = new Node2D { ShowBehindParent = true };
            _ghostArt.Draw += DrawGhostArt;
            AddChild(_ghostArt);
        }
        _ghostArt.Visible = GhostUp;
        float fade = Mathf.Clamp((_deadT - GhostDelay) / 0.5f, 0, 1);
        _ghostArt.Modulate = new Color(0.85f, 0.95f, 1.15f, 0.5f * fade);
        _ghostArt.Position = new Vector2(0, Mathf.Sin(_animT * 2.6f) * 2.5f);
        _ghostArt.QueueRedraw();
        QueueRedraw();
    }

    private void DrawGhostArt()
    {
        float size = S[Stat.Size];
        GlowTex.Draw(_ghostArt, Vector2.Zero, 26 * size, Player.Color with { A = 0.4f });
        AvatarArt.Draw(_ghostArt, Vector2.Zero, new AvatarPose
        {
            Body = Player.Color.Lightened(0.35f),
            Look = Player.Info.Look,
            Facing = _facing,
            Aim = new Vector2(_facing, 0.4f).Normalized(),
            Scale = size,
            Time = _animT,
            Air = 1, // legs dangle
            Blink = true, // eyes shut
            Gun = false,
            GunLook = Gun,
        });
    }

    /// <summary>Shows a speech bubble over this player (or their ghost) for a couple of seconds.</summary>
    public void ShowEmote(int id)
    {
        _emote = id;
        _emoteT = Emotes.BubbleTime;
        _emoteAsGhost = !Alive;
        QueueRedraw();
    }

    /// <summary>The emote bubble, its tail touching (0, <paramref name="bottom"/>).</summary>
    private void DrawEmote(float bottom)
    {
        if (_emoteT <= 0 || !Emotes.Valid(_emote) || Player.Left) return;
        string text = Emotes.Text(_emote, _emoteAsGhost);
        float age = Emotes.BubbleTime - _emoteT;
        float alpha = Mathf.Min(1, _emoteT * 4) * (_emoteAsGhost ? 0.85f : 1f);
        float pop = Mathf.Min(1, age * 10) * (1f + 0.3f * Mathf.Exp(-age * 12f));
        if (pop <= 0) return;
        var font = Fonts.Heavy;
        const int size = 9;
        float w = font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X + 9, h = 13;
        DrawSetTransform(new Vector2(0, bottom), 0, Vector2.One * pop);
        var box = new Rect2(-w / 2, -h - 3, w, h);
        var paper = new Color("#f4efe4") with { A = alpha };
        var edge = Player.Color.Darkened(0.2f) with { A = alpha };
        DrawColoredPolygon(Shapes.RoundRect(box.Grow(1.2f), 5), edge);
        DrawColoredPolygon(new[] { new Vector2(-3.5f, -3.6f), new Vector2(3.5f, -3.6f), new Vector2(0, 0.8f) }, edge);
        DrawColoredPolygon(Shapes.RoundRect(box, 4), paper);
        DrawColoredPolygon(new[] { new Vector2(-2.2f, -3.2f), new Vector2(2.2f, -3.2f), new Vector2(0, -0.6f) }, paper);
        DrawString(font, new Vector2(-w / 2, -6.5f), text, HorizontalAlignment.Center, w, size, new Color("#17141f") with { A = alpha });
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }

    // ---------- kill cam ----------

    /// <summary>Everything this avatar draws from, at one tick (see <see cref="KillCam"/>).</summary>
    public struct Frame
    {
        public Vector2 Pos, Vel, Aim, ArmSway;
        public float Hp, HpGhost, BlockActive, BlockCd, ReloadFrac, AnimT, WalkPhase, BlinkT, Kick, Flash, HurtFlash;
        public float Walk, Air, Wall, Land, GroundDrop, GroundSlope, DeadT, EmoteT;
        public int Facing, MoveDir, WallSide, Emote;
        public bool Alive, Reloading, Jetting, EmoteAsGhost;
    }

    public Frame Capture() => new()
    {
        Pos = GlobalPosition, Vel = Velocity, Aim = AimDir, ArmSway = _armSway,
        Hp = Hp, HpGhost = _hpGhost, BlockActive = BlockActive, BlockCd = BlockCd, ReloadFrac = ReloadFrac,
        AnimT = _animT, WalkPhase = _walkPhase, BlinkT = _blinkT, Kick = _kick, Flash = _flash, HurtFlash = _hurtFlash,
        Walk = _walk, Air = _air, Wall = _wall, Land = _land, GroundDrop = _groundDrop, GroundSlope = _groundSlope,
        DeadT = _deadT, EmoteT = _emoteT, Facing = _facing, MoveDir = _moveDir, WallSide = _wallSide, Emote = _emote,
        Alive = Alive, Reloading = Reloading, Jetting = _jetting, EmoteAsGhost = _emoteAsGhost,
    };

    /// <summary>Kill cam: shows the avatar as it was, <paramref name="k"/> of the way from tick <paramref name="a"/> to the next.</summary>
    public void Show(in Frame a, in Frame b, float k)
    {
        GlobalPosition = a.Pos.DistanceSquaredTo(b.Pos) < 60 * 60 ? a.Pos.Lerp(b.Pos, k) : a.Pos; // no sliding across teleports
        Velocity = a.Vel.Lerp(b.Vel, k);
        AimDir = a.Aim.Slerp(b.Aim, k);
        _armSway = a.ArmSway.Lerp(b.ArmSway, k);
        _animT = Mathf.Lerp(a.AnimT, b.AnimT, k);
        _walkPhase = Mathf.Abs(b.WalkPhase - a.WalkPhase) < 3 ? Mathf.Lerp(a.WalkPhase, b.WalkPhase, k) : a.WalkPhase;
        _kick = Mathf.Lerp(a.Kick, b.Kick, k);
        _flash = Mathf.Lerp(a.Flash, b.Flash, k);
        _deadT = Mathf.Lerp(a.DeadT, b.DeadT, k);
        Hp = a.Hp;
        _hpGhost = a.HpGhost;
        BlockActive = a.BlockActive;
        BlockCd = a.BlockCd;
        ReloadFrac = a.ReloadFrac;
        _blinkT = a.BlinkT;
        _hurtFlash = a.HurtFlash;
        _walk = a.Walk;
        _air = a.Air;
        _wall = a.Wall;
        _land = a.Land;
        _groundDrop = a.GroundDrop;
        _groundSlope = a.GroundSlope;
        _emoteT = a.EmoteT;
        _facing = a.Facing;
        _moveDir = a.MoveDir;
        _wallSide = a.WallSide;
        _emote = a.Emote;
        Alive = a.Alive;
        Reloading = a.Reloading;
        _jetting = a.Jetting;
        _emoteAsGhost = a.EmoteAsGhost;
        if (!Alive || _ghostArt != null) UpdateGhostArt();
        QueueRedraw();
    }

    // ---------- animation and drawing ----------

    /// <summary>Called on every peer when this avatar fires a bullet (recoil and muzzle flash).</summary>
    public void OnFired()
    {
        _kick = 1;
        _flash = 1;
    }

    /// <summary>Bullet spawn point: the gun's muzzle, pulled back if a wall is in between. If an enemy stands within
    /// arm's reach (between body and muzzle), the bullet starts from the body so point-blank shots still connect.</summary>
    private Vector2 MuzzlePoint()
    {
        var from = GlobalPosition;
        var delta = AvatarArt.Muzzle(AimDir, _facing, S[Stat.Size], Gun);
        float radius = Mathf.Max(1, S[Stat.BulletRadius]);
        var nFrom = new System.Numerics.Vector2(from.X, from.Y);
        var nTo = new System.Numerics.Vector2(from.X + delta.X, from.Y + delta.Y);
        foreach (var a in Game.LiveAvatars())
        {
            if (a == this || Game.Spared(Player.Slot, a.Player.Slot)) continue;
            var r = a.Bounds;
            if (Sweep.SegmentTouchesRect(nFrom, nTo, radius, r.Position.X, r.Position.Y, r.Size.X, r.Size.Y)) return from;
        }
        var hit = Sweep.Cast(nFrom, new System.Numerics.Vector2(delta.X, delta.Y), radius, Game.Arena.Solids, Game.Map?.Slopes);
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
        _emoteT = Mathf.Max(0, _emoteT - dt);
        _animT += dt;
        TrailTick(dt);
        if (Mathf.Abs(AimDir.X) > 0.15f) _facing = AimDir.X < 0 ? -1 : 1;
        AnimateLegs(dt);
        AnimateArm(dt);
        _blinkT -= dt;
        if (_blinkT < -_nextBlink)
        {
            _blinkT = 0.12f;
            _nextBlink = 1.8f + (GetInstanceId() % 7) * 0.4f + (_animT % 1.3f);
        }
        QueueRedraw();
    }

    /// <summary>
    /// Leg animation state, from position, velocity and the level geometry only: the host, a predicting client and an
    /// interpolated remote player all have those, so everyone sees the same steps. Cosmetic; never touches the sim.
    /// </summary>
    private void AnimateLegs(float dt)
    {
        float size = S[Stat.Size];
        var hs = HalfSize;
        var pos = GlobalPosition;
        var solids = Game?.Arena?.Solids;
        var slopes = Game?.Map?.Slopes;
        var foot = FootingProbe.Probe(pos.X, pos.Y + hs.Y, hs.X, 4f * size, 12f * size, solids, slopes);
        float vx = Velocity.X;

        // One stride cycle (two steps) per ~30 px walked, capped so very fast builds don't blur their feet.
        _walkPhase = (_walkPhase + Mathf.Min(Mathf.Abs(vx), 240f) * dt * Mathf.Tau / (30f * size)) % (Mathf.Tau * 64);
        if (Mathf.Abs(vx) > 6) _moveDir = vx < 0 ? -1 : 1;
        _walk = Mathf.MoveToward(_walk, foot.Grounded ? Mathf.Clamp(Mathf.Abs(vx) / 45f, 0, 1) : 0, dt * 7);

        bool wasGrounded = _air < 0.5f;
        _air = Mathf.MoveToward(_air, foot.Grounded ? 0 : 1, dt * 12);
        if (!foot.Grounded) _airVy = Velocity.Y;
        else if (!wasGrounded || _airVy > 0)
        {
            // Touchdown: squash in proportion to how hard we came down.
            if (_airVy > 150) _land = Mathf.Max(_land, Mathf.Clamp(_airVy / 420f, 0.35f, 1));
            _airVy = 0;
        }
        _land = Mathf.MoveToward(_land, 0, dt * 6);

        _groundDrop = Mathf.Lerp(_groundDrop, foot.Grounded ? foot.Drop / size : 0, Mathf.Min(1, dt * 20));
        _groundSlope = Mathf.Lerp(_groundSlope, foot.Grounded ? foot.Slope : 0, Mathf.Min(1, dt * 20));

        // Wall slide: airborne, sliding down slowly, with a wall right beside us.
        int side = foot.Grounded ? 0 : FootingProbe.WallSide(pos.X, pos.Y, hs.X, hs.Y, solids);
        float slide = WallSlideSpeed / Mathf.Max(0.1f, S[Stat.WallJump]);
        bool sliding = side != 0 && Velocity.Y > 0 && Velocity.Y < slide + 30;
        if (side != 0) _wallSide = side;
        _wall = Mathf.MoveToward(_wall, sliding ? 1 : 0, dt * 10);
    }

    /// <summary>
    /// Arm follow-through (cosmetic): the elbow is a damped spring chasing its rest spot (GunRig.Arm), so it trails
    /// behind quick aim swings and recoil and settles with a small bounce. The body's own speed-ups and stops (starting
    /// a run, jumping, landing) shove it the other way, like the weight of a real arm; steady motion leaves it at rest.
    /// Facing turns and teleports snap it back.
    /// </summary>
    private void AnimateArm(float dt)
    {
        const float stiffness = 420f, damping = 18f, inertia = 0.35f;
        if (dt <= 0) return;
        float size = S[Stat.Size];
        var aim = GunRig.SpriteAim(new System.Numerics.Vector2(AimDir.X, AimDir.Y), _facing);
        var rest = GunRig.Arm(aim, _kick).Elbow;
        var target = new Vector2(rest.X, rest.Y);
        var moved = (GlobalPosition - _armLastPos) / size;
        _armLastPos = GlobalPosition;
        var bodyVel = new Vector2(moved.X * _facing, moved.Y) / dt; // sprite space
        if (_facing != _armFacing || moved.LengthSquared() > 400)
        {
            _armFacing = _facing;
            _elbow = target;
            _elbowVel = Vector2.Zero;
            _armBodyVel = bodyVel;
            return;
        }
        _elbowVel -= (bodyVel - _armBodyVel) * inertia;
        _armBodyVel = bodyVel;
        // Substeps keep the spring stable through frame hitches.
        int steps = Mathf.Clamp(Mathf.CeilToInt(dt / (1 / 120f)), 1, 8);
        float h = Mathf.Min(dt, 0.1f) / steps;
        for (int i = 0; i < steps; i++)
        {
            _elbowVel += (stiffness * (target - _elbow) - damping * _elbowVel) * h;
            _elbow += _elbowVel * h;
        }
        _armSway = _elbow - target;
    }

    /// <summary>Visible tells for cards that change how a player works (everyone can read them at a glance).</summary>
    private void DrawCardAuras(float size)
    {
        float leech = S[Stat.Leech];
        if (leech > 0)
        {
            float r = 44 * size + 10;
            var red = new Color("#ff2a4a");
            GlowTex.Draw(this, Vector2.Zero, r * 1.1f, red with { A = 0.12f });
            float spin = _animT * 1.5f;
            for (int i = 0; i < 12; i++)
            {
                float a0 = spin + i * Mathf.Tau / 12;
                DrawArc(Vector2.Zero, r, a0, a0 + 0.3f, 4, Shapes.Hdr(red, 1.3f) with { A = 0.55f }, 1.2f, true);
            }
        }
        if (Rage > 0)
        {
            float pulse = 0.5f + 0.5f * Mathf.Sin(_animT * 12);
            GlowTex.Draw(this, Vector2.Zero, 30 * size, new Color("#ff3d3d") with { A = 0.25f + 0.25f * pulse });
        }
        // Jetpack flame: from the local sim, or (for remote players) the steady climb the thrust settles at.
        bool thrust = _jetting || (S[Stat.Jetpack] > 0 && Mathf.Abs(Velocity.Y + JetClimb) < 8);
        if (thrust)
        {
            var nozzle = new Vector2(-_facing * HalfSize.X * 0.6f, HalfSize.Y * 0.4f);
            float flick = 0.8f + 0.2f * Mathf.Sin(_animT * 40);
            var flame = new[] { nozzle + new Vector2(-2.5f, 0), nozzle + new Vector2(2.5f, 0), nozzle + new Vector2(0, 11 * flick * size) };
            GlowTex.Draw(this, nozzle + new Vector2(0, 5), 10 * size, new Color("#ffa33b") with { A = 0.6f });
            DrawColoredPolygon(flame, Shapes.Hdr(new Color("#ffb347"), 1.6f));
        }
    }

    public override void _Draw()
    {
        if (!Alive)
        {
            // The ghost itself is _ghostArt; this node only adds its bubble.
            if (GhostUp) DrawEmote(_ghostArt.Position.Y + AvatarArt.TopY(Player.Info.Look) * S[Stat.Size] - 3);
            return;
        }
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
            Grounded = _air < 0.5f,
            Moving = _walk > 0,
            Walk = _walk,
            MoveDir = _moveDir,
            Air = _air,
            VelY = Velocity.Y,
            Wall = _wall,
            WallSide = _wallSide,
            GroundDrop = _groundDrop,
            GroundSlope = _groundSlope,
            Land = _land,
            Blink = _blinkT > 0,
            Hurt = _hurtFlash > 0 && !Accessibility.ReduceFlashes,
            Gun = true,
            GunLook = Gun,
            Kick = _kick,
            Flash = _flash,
            ArmSway = _armSway,
        };
        DrawTrail(size);
        // A soft aura in the player color keeps characters readable on any backdrop.
        GlowTex.Draw(this, Vector2.Zero, 22 * size, Player.Color with { A = 0.22f });
        DrawCardAuras(size);
        AvatarArt.Draw(this, Vector2.Zero, pose);

        var hs = HalfSize;
        float top = AvatarArt.TopY(Player.Info.Look) * size;
        bool mine = Game.IsLocalHuman(Player);

        // HP bar above the head (and hat), with a white "ghost" that drains after a hit.
        float max = S[Stat.MaxHp];
        float frac = Mathf.Clamp(Hp / max, 0, 1), ghost = Mathf.Clamp(_hpGhost / max, 0, 1);
        const float barW = 24, barH = 3.4f;
        var barPos = new Vector2(-barW / 2, top - 9);
        // Team modes: the bar sits in a frame of the team color, so sides read at a glance.
        if (Game.TeamOf(Player.Slot) is >= 0 and var team)
            DrawColoredPolygon(Shapes.RoundRect(new Rect2(barPos - new Vector2(2, 2), new Vector2(barW + 4, barH + 4)), 2.8f), Palette.Team(team));
        DrawColoredPolygon(Shapes.RoundRect(new Rect2(barPos - new Vector2(1, 1), new Vector2(barW + 2, barH + 2)), 2.2f), new Color(0.05f, 0.04f, 0.1f, 0.75f));
        if (ghost > frac)
            DrawColoredPolygon(Shapes.RoundRect(new Rect2(barPos, new Vector2(barW * ghost, barH)), 1.6f), new Color(1, 1, 1, 0.85f));
        if (frac > 0)
        {
            var hpCol = frac > 0.35f ? new Color("#52f08a") : new Color("#ff4d6a");
            var bar = Shapes.RoundRect(new Rect2(barPos, new Vector2(Mathf.Max(barH, barW * frac), barH)), 1.6f);
            Shapes.Gradient(this, bar, hpCol.Lightened(0.3f), hpCol.Darkened(0.15f));
        }

        // Your own ammo and reload are on the crosshair (UI/Crosshair.cs). Other players keep a thin reload sweep
        // under their HP bar, so you can see when they can't shoot back.
        if (Reloading && !mine)
        {
            var pipPos = barPos + new Vector2(0, barH + 2.2f);
            DrawColoredPolygon(Shapes.RoundRect(new Rect2(pipPos, new Vector2(barW, 1.2f)), 0.6f), new Color(1, 1, 1, 0.12f));
            DrawColoredPolygon(Shapes.RoundRect(new Rect2(pipPos, new Vector2(Mathf.Max(1.2f, barW * ReloadFrac), 1.2f)), 0.6f), Palette.Accent with { A = 0.7f });
        }

        // "You" marker over the local player.
        if (mine)
        {
            float bobY = Mathf.Sin(_animT * 4) * 0.8f;
            var tip = new Vector2(0, barPos.Y - 3 + bobY);
            DrawColoredPolygon(new[] { tip + new Vector2(-2.6f, -3.4f), tip + new Vector2(2.6f, -3.4f), tip }, Player.Color.Lightened(0.25f));
        }
        DrawEmote(barPos.Y - (mine ? 8 : 3));

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
