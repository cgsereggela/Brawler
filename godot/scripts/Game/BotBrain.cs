using System.Collections.Generic;
using Brawler.Core;
using Brawler.Logic;
using Godot;
using NVec = System.Numerics.Vector2;

namespace Brawler.Game;

/// <summary>
/// Simple but scrappy AI: keeps a fighting distance from the nearest enemy, leads its shots
/// (compensating for bullet gravity), jumps over gaps and walls, keeps out of pools and away from sawblades, rides
/// moving platforms, and blocks incoming bullets.
/// It only reads world state, so it works the same on the host or driving a client.
/// </summary>
public sealed class BotBrain : IInputSource
{
    private readonly GameManager _game;
    private readonly RandomNumberGenerator _rng = new();
    private byte _jumps, _blocks;
    private float _jumpHold, _decisionT, _strafeDir = 1, _aimWobbleT;
    private Vector2 _aimWobble;
    private float _preferredDist;
    private readonly HashSet<int> _consideredBullets = new();
    private readonly BotTuning _tune;
    private Avatar _seen;
    private float _seenT;

    public BotBrain(GameManager game, BotSkill skill = BotSkill.Normal)
    {
        _game = game;
        _tune = BotTuning.Of(skill);
        _preferredDist = _rng.RandfRange(_tune.PrefMin, _tune.PrefMax);
    }

    public InputFrame Next(Avatar self, float dt)
    {
        var f = new InputFrame { JumpPresses = _jumps, BlockPresses = _blocks, Aim = self.GlobalPosition + Vector2.Right };
        var target = NearestEnemy(self);
        // Game modes: somewhere to be (the hill, a flag). We still shoot whoever is nearest on the way.
        var goal = _game.BotGoal(self);
        _jumpHold -= dt;
        _decisionT -= dt;
        _aimWobbleT -= dt;
        // Top up between fights: with nobody in sight and the magazine under half, reload early.
        bool lowAmmo = self.Ammo * 2 < self.S.Int(Stat.Ammo);
        f.Reload = target == null && lowAmmo;

        if (target == null && goal == null)
        {
            f.JumpHeld = _jumpHold > 0;
            return f;
        }

        var me = self.GlobalPosition;
        bool canShoot = false, chainJump = false, chainHold = false;
        float chainX = float.NaN;
        if (target != null)
        {
            // --- aim with lead and drop compensation ---
            var tTo = target.GlobalPosition - me;
            float tDist = tTo.Length();
            float speed = self.S[Stat.BulletSpeed];
            float t = tDist / speed;
            var predicted = target.GlobalPosition + target.Velocity * t * _tune.Lead;
            predicted.Y -= 0.5f * self.S[Stat.BulletGravity] * t * t;
            if (_aimWobbleT <= 0)
            {
                _aimWobbleT = 0.3f;
                _aimWobble = new Vector2(_rng.RandfRange(-1, 1), _rng.RandfRange(-1, 1)) * (6 + tDist * 0.06f) * _tune.AimError;
            }
            f.Aim = predicted + _aimWobble;
            bool sight = HasLineOfSight(me, target.GlobalPosition);
            // No straight line (someone up on a ledge, say), but an arc over the edge may get there: lob it.
            if (!sight && tDist < _tune.Range && LobAim(me, target.GlobalPosition, speed, self.S[Stat.BulletGravity], out var lob))
            {
                f.Aim = me + lob * tDist + _aimWobble;
                sight = true;
            }
            canShoot = sight;
            // Reaction time: a target has to stay in sight a moment before the first shot.
            if (!sight || target != _seen) _seenT = 0;
            else _seenT += dt;
            _seen = sight ? target : null;
            f.Shoot = sight && _seenT >= _tune.Reaction && tDist < _tune.Range && (self.Ammo > 1 || tDist < 180 || _rng.Randf() < 0.05f);
            f.Reload = !sight && lowAmmo;
            // A hanging platform over the target: shoot its chains and drop it on them.
            if (ChainShot(self, target, out var chain, out chainJump))
            {
                // A chain is a few px wide: aim much tighter than at a body. Out of reach until we jump: just jump.
                f.Aim = chain + _aimWobble * 0.15f;
                f.Shoot = self.Ammo > 0 && !chainJump;
                chainHold = true;
                f.Reload = f.Reload && self.Ammo == 0;
            }
            else chainX = ChainSpot(self, target);
        }

        // Movement heads for the goal if there is one, else the target.
        var focus = goal ?? target.GlobalPosition;
        float focusHalfY = goal != null ? self.HalfSize.Y : target.HalfSize.Y;
        var to = focus - me;
        float dist = to.Length();
        bool los = HasLineOfSight(me, focus);

        // --- movement ---
        if (_decisionT <= 0)
        {
            _decisionT = _rng.RandfRange(0.4f, 1.1f);
            _strafeDir = _rng.Randf() < 0.5f ? -1 : 1;
        }
        float move;
        // Leech Field only works up close, so its holder hugs the target instead of keeping range.
        bool hug = self.S[Stat.Leech] > 0;
        float pref = hug ? 20 : _preferredDist, band = hug ? 15 : 50;
        if (goal != null) move = Mathf.Abs(to.X) > 8 ? Mathf.Sign(to.X) : _strafeDir * 0.3f; // there, jostle about a little
        else if (dist > pref + band) move = Mathf.Sign(to.X);
        else if (dist < pref - band) move = -Mathf.Sign(to.X);
        else move = _strafeDir * 0.6f;
        if (!los) move = Mathf.Sign(to.X == 0 ? 1 : to.X);

        bool wantJump = false, fullHop = false;
        bool onFloor = self.IsOnFloor();

        // --- navigation: out of sight, or far above/below → follow the jump graph one surface at a time ---
        var map = NavMap(self);
        var feet = me + new Vector2(0, self.HalfSize.Y);
        int here = onFloor ? SurfaceAt(map, feet, 4) : -1;
        // On a ramp: plan from the surface at its nearer end.
        if (onFloor && here < 0) here = MapGenerator.SlopeSurface(map, feet.X, feet.Y);
        // On a crate, barrel or mover: plan from the surface it stands on. Otherwise a bot on a crate stack kept the
        // surface it jumped from and hopped on the stack forever, "climbing" to a ledge it was already above.
        if (onFloor && here < 0) here = SurfaceUnder(map, feet);
        if (here >= 0 && here != _surface) { _surface = here; _navT = 0; }
        // Long in the air (wall-jumping, gliding, jetting) means the surface we last stood on says little about
        // where we are; plan from where we'd land instead, or stale plans can loop forever without touching down.
        _airT = onFloor ? 0 : _airT + dt;
        if (_airT > 1f && SurfaceUnder(map, feet) is int below and >= 0 && below != _surface) { _surface = below; _navT = 0; }
        _navT -= dt;
        // Lava coming up under our surface: climb to one well above it, whatever the target is doing.
        float lava = _game.Arena.LavaY;
        bool flee = _surface >= 0 && map.Surfaces[_surface].Y > lava - LavaMargin;
        bool navigating = false, dropping = false, riding = false;
        int goalSurface = SurfaceUnder(map, focus + new Vector2(0, focusHalfY));
        bool far = goal != null ? goalSurface >= 0 && goalSurface != _surface : !los || Mathf.Abs(to.Y) > 75;
        // On a moving platform, or one is the way to the target (across a pit, say): follow the jump graph even with
        // the target in plain sight, or we'd walk straight at it, off the edge.
        if (!far && !flee && _surface >= 0 && goalSurface >= 0 && goalSurface != _surface)
        {
            if (_navT <= 0)
            {
                _navT = 0.3f;
                _hop = MapGenerator.NextHop(map, _surface, goalSurface);
            }
            far = _hop >= 0 && map.MoverOf.ContainsKey(_hop);
        }
        if (_surface >= 0 && map.MoverOf.ContainsKey(_surface)) far = true;
        if ((flee || far) && _surface >= 0)
        {
            if (flee && _navT <= 0)
            {
                _navT = 0.3f;
                _hop = MapGenerator.NextHop(map, _surface, EscapeSurface(map, focus, lava));
            }
            else if (_navT <= 0)
            {
                _navT = 0.3f;
                _hop = goalSurface >= 0 ? MapGenerator.NextHop(map, _surface, goalSurface) : -1;
                // Can't get to the target's surface (it's on a ledge with no way up from here): head for the
                // reachable surface with the best shot at it instead of milling about underneath.
                _atVantage = false;
                if (_hop < 0 && goal == null)
                {
                    int vantage = VantageSurface(map, self, target);
                    _hop = MapGenerator.NextHop(map, _surface, vantage);
                    _atVantage = vantage == _surface;
                }
            }
            navigating = _hop >= 0 && _hop != _surface;
        }
        // Already on the best surface for a shot at a target we can't reach: walk to the spot on it with a clear line,
        // rather than straight at the target (two bots stood under a ledge out of each other's sight until the lava).
        // With a shot (straight or lobbed), only strafe about near it: closing in would walk us back under the ledge.
        if (!navigating && far && !flee && goal == null && _atVantage && !float.IsNaN(_vantageX))
        {
            float off = _vantageX - me.X;
            if (!canShoot || Mathf.Abs(off) > 30) move = Mathf.Abs(off) < 4 ? 0 : Mathf.Sign(off);
            else move = _strafeDir * 0.6f;
            // The shot only opens up from the top of a jump: jump, and hold it all the way up.
            if (_vantageJump && onFloor && Mathf.Abs(off) <= 30) { wantJump = true; fullHop = true; }
        }
        // A platform hangs over the target, but no chain is in reach from here (they're steep shots up past its
        // edge): walk round to the side of the nearest one.
        if (!navigating && !flee && goal == null && !float.IsNaN(chainX) && onFloor)
            move = Mathf.Abs(chainX - me.X) < 6 ? 0 : Mathf.Sign(chainX - me.X);
        else if (chainHold && !navigating && !flee && goal == null) move = 0; // a chain in reach: stand and cut it
        if (navigating)
        {
            var hop = Live(map, _hop);
            var cur = Live(map, _surface);
            float x = me.X, aimX;
            if (MapGenerator.SlopeLinks(map, cur, hop)) aimX = Inside(x, hop, 10); // a ramp leads there: just run
            else if (hop.Y < cur.Y - 4)
            {
                // Climbing: get clear of the platform's underside, then jump toward it and steer onto it.
                bool overlap = cur.X2 > hop.X1 && cur.X1 < hop.X2;
                float edge = x < hop.X1 ? hop.X1 - x : x > hop.X2 ? x - hop.X2 : 0;
                if (!onFloor)
                {
                    // Until our feet are above its top, stay out from under it: rising into the underside
                    // kills the jump, and bots used to bonk their heads on the same ledge forever.
                    aimX = Inside(x, hop, 12);
                    float hw = self.HalfSize.X + 2;
                    if (feet.Y > hop.Y - 1 && UnderPlatform(map, hop, me, self.HalfSize))
                    {
                        bool leftFree = SideClear(map, hop, false, self.HalfSize), rightFree = SideClear(map, hop, true, self.HalfSize);
                        bool left = leftFree && rightFree ? x - hop.X1 < hop.X2 - x : leftFree;
                        aimX = left ? hop.X1 - hw : hop.X2 + hw;
                    }
                    // Beside it but not yet up to its top: wait just off its end. Steering into the side made a bot
                    // that launched next to a ledge touch its end, wall-jump away from it and try again forever.
                    else if (feet.Y > hop.Y - 1 && (x < hop.X1 || x > hop.X2))
                        aimX = x < hop.X1 ? hop.X1 - hw : hop.X2 + hw;
                }
                else if (!MapGenerator.CanTraverse(cur, hop) && MapGenerator.PadTo(map, cur, hop) is JumpPad pad)
                    aimX = (pad.X1 + pad.X2) * 0.5f; // too high to jump: ride the pad (it launches us on contact)
                else if (overlap)
                {
                    // Walk to a spot on this surface that's clear of the platform above, then jump.
                    float launch = MapGenerator.LaunchX(map, cur, hop, x);
                    aimX = float.IsNaN(launch) ? x : launch;
                    if (Mathf.Abs(aimX - x) < 5) wantJump = true;
                }
                else
                {
                    aimX = Inside(x, hop, 10);
                    if (edge < 44) wantJump = true;
                }
            }
            else if (hop.Y > cur.Y + 4 && cur.X2 > hop.X1 && cur.X1 < hop.X2 && feet.Y <= cur.Y + 2 && (onFloor || (x >= cur.X1 && x <= cur.X2)))
            {
                // Dropping onto something under this surface: walk off an edge it sticks out past (or the nearest),
                // but never one with no room to fall past, like a ledge ending a few px short of the border wall.
                // Also while airborne above this surface: steering straight down would just land back on it.
                dropping = true;
                bool leftOpen = SideClear(map, cur, false, self.HalfSize), rightOpen = SideClear(map, cur, true, self.HalfSize);
                bool leftOk = hop.X1 < cur.X1 - 4 && leftOpen, rightOk = hop.X2 > cur.X2 + 4 && rightOpen;
                if (!leftOk && !rightOk) { leftOk = leftOpen; rightOk = rightOpen; }
                bool goLeft = leftOk && rightOk ? x - cur.X1 < cur.X2 - x : leftOk || (!rightOk && x - cur.X1 < cur.X2 - x);
                aimX = goLeft ? cur.X1 - 10 : cur.X2 + 10;
            }
            else aimX = Inside(x, hop, 10); // same level, or already falling: steer onto it
            // Riding a moving platform, or waiting for one: stay put at the end facing the other, until they're close.
            if (onFloor && !InReach(map, cur, hop))
            {
                aimX = Inside(hop.CenterX, cur, self.HalfSize.X + 3);
                wantJump = false;
                riding = true;
            }
            move = Mathf.Abs(aimX - x) < 3 ? 0 : Mathf.Sign(aimX - x);
        }

        // Unstick: if we haven't really moved for a while and can't see anyone, wander and hop for a bit.
        _stuckT += dt;
        if (me.DistanceTo(_stuckFrom) > 24) { _stuckFrom = me; _stuckT = 0; }
        if (riding) _stuckT = 0; // waiting for a moving platform is not being stuck
        if (_stuckT > 2.5f && !los && _wanderT <= 0)
        {
            _wanderT = _rng.RandfRange(1f, 2f);
            _wanderDir = _rng.Randf() < 0.5f ? -1 : 1;
            _stuckT = 0;
        }
        if (_wanderT > 0)
        {
            _wanderT -= dt;
            move = _wanderDir;
            if (onFloor && _rng.Randf() < 0.05f) wantJump = true;
        }

        bool climbing = navigating && map.Surfaces[_hop].Y < feet.Y - 4, descending = navigating && map.Surfaces[_hop].Y > feet.Y + 4;
        if (onFloor && !navigating && to.Y < -38 && Mathf.Abs(to.X) < 175) wantJump = true; // target above
        // Wall ahead: jump it / wall-jump up it, but not on the way down, where kicking off a ledge's side just
        // bounces us back up next to it (bots wall-jumped against the same 8 px ledge end for minutes).
        if (self.IsOnWall() && move * self.GetWallNormal().X < 0 && !descending) wantJump = true;
        // Edge or pit ahead: jump it if a full jump comes down on something (the far side, or ground below). With
        // nothing to land on, like the bottomless gaps between a Drift arena's platforms, don't go: turn round if just
        // milling about, else wait at the edge. Bots used to hop blindly into those gaps over and over.
        if (onFloor && !dropping && !riding && !GroundAhead(self, move))
        {
            if (navigating || JumpLands(self, Mathf.Sign(move))) { wantJump = true; fullHop = true; move = Mathf.Sign(move); }
            else if (goal == null && los && !(move * to.X > 0 && dist > pref + band)) { move = -move; _strafeDir = Mathf.Sign(move); }
            else move = 0;
        }
        // Standing in a pool: get out by the nearer side, unless we're crossing it on the way somewhere.
        if (onFloor && !navigating && PoolUnder(feet, self.HalfSize.X) is { } pool)
            move = feet.X - pool.X1 < pool.X2 - feet.X ? -1 : 1;
        if (onFloor && _game.Arena.DangerAhead(self, move))
        {
            // A pool or sawblade in the way (or underfoot). Only milling about (strafing, backing off): turn round if
            // that way is clear. Going somewhere: hop it, and keep going rather than stopping in it.
            bool heading = navigating || goal != null || !los || (move * to.X > 0 && dist > pref + band);
            if (!heading && Mathf.Abs(move) >= 0.1f && !_game.Arena.DangerAhead(self, -move))
            {
                move = -move;
                _strafeDir = Mathf.Sign(move);
            }
            else
            {
                wantJump = true;
                if (Mathf.Abs(move) < 0.1f) move = _strafeDir;
            }
        }
        // A sawblade about to run into us: jump one coming along the ground, and steer clear of one higher up or one
        // we are coming down onto.
        if (SawComing(self, out var saw))
        {
            if (onFloor && saw.Y > me.Y - 2) wantJump = true;
            else move = Mathf.Sign(me.X - saw.X);
        }
        if (chainJump && onFloor) { wantJump = true; fullHop = true; } // the chain is in reach from the top of a jump
        // Committed to a jump: keep steering for where we jumped to until we land. Mid-air, fighting moves (backing off,
        // strafing) used to turn bots round over the gaps between drifting platforms, into the drop.
        _commitT -= dt;
        if (onFloor && _commitT <= 0) { _airHop = -1; _airMove = 0; }
        bool committed = !onFloor && !navigating && (_airHop >= 0 || _airMove != 0);
        if (committed && _airHop >= 0)
        {
            float landX = Inside(me.X, Live(map, _airHop), 10);
            move = Mathf.Abs(landX - me.X) < 3 ? 0 : Mathf.Sign(landX - me.X);
        }
        else if (committed) move = _airMove;
        if (!committed && !navigating && !onFloor && self.Velocity.Y > 60 && !GroundBelow(self, 175)) move = -move; // falling into a pit: steer back
        if (!onFloor && PoolLanding(self) is float side and not 0) move = side; // coming down in a pool: steer for its nearer side
        if (feet.Y > lava - 2) wantJump = true; // in the lava: jump out of it (it pops us up too)
        if (onFloor && !riding && _rng.Randf() < 0.008f) wantJump = true;

        // Card abilities: spend air jumps or jetpack fuel to climb toward the target or out of a fall,
        // and glide over pits. Without this, movement cards would only measure how well bots ignore them.
        bool jet = self.S[Stat.Jetpack] > 0;
        // Only a real pit (nothing below at all) counts as a fall to save: a jetpack bot that "saved" itself from
        // every long drop could never get down off a high ledge, and hovered over it all battle.
        bool pit = !onFloor && !GroundBelow(self, MapLayout.Height);
        // A bot that walked or was knocked off a ledge still has its ground jump (Avatar.MidAirJumpsLeft counts it).
        if (!onFloor && (self.MidAirJumpsLeft > 0 || jet) && self.Velocity.Y > 20 && ((!descending && (to.Y < -50 || climbing)) || pit))
            wantJump = true;
        bool glide = self.S[Stat.Glide] > 0 && !onFloor && self.Velocity.Y > 0 && ((!descending && to.Y < -25) || pit);

        if (wantJump && _jumpHold <= -0.15f)
        {
            _jumps++;
            _jumpHold = jet && !onFloor ? _rng.RandfRange(0.5f, 0.8f) : fullHop ? 0.6f : _rng.RandfRange(0.15f, 0.35f);
            // Jumping off for somewhere: commit to it until we land (see above). Only to or from a drifting platform:
            // elsewhere the fighting moves mid-air steer us clear of pools, and committed bots came down in them.
            bool drift = map.DriftOf.ContainsKey(_surface) || navigating && map.DriftOf.ContainsKey(_hop);
            if (onFloor && drift && (navigating || fullHop))
            {
                _airHop = navigating ? _hop : -1;
                _airMove = navigating ? 0 : Mathf.Sign(move);
                _commitT = 0.2f;
            }
            // Any other hop (at a target overhead, dodging, on a whim): come down on the surface we jumped from, or
            // strafing mid-air carries us off a drifting platform's end into the drop.
            else if (onFloor && drift)
            {
                _airHop = _surface;
                _airMove = 0;
                _commitT = 0.2f;
            }
        }
        f.MoveX = Mathf.Clamp(move, -1, 1);
        // Climbing: hold jump all the way up. Letting go early cuts the rise (the short-hop rule), which made bots
        // miss ledges and turned a jump pad's ~215 px launch into ~100, so pocketed bots never got out.
        // Same for a jump across a gap to a surface at our level: a short hop falls short of the far side.
        bool rising = navigating && !descending && !onFloor && self.Velocity.Y < 0;
        f.JumpHeld = _jumpHold > 0 || glide || rising;
        f.JumpPresses = _jumps;

        // --- block incoming bullets ---
        if (self.BlockReady && ThreatIncoming(self, out int bulletId) && _consideredBullets.Add(bulletId))
        {
            if (_rng.Randf() < _tune.BlockChance) _blocks++;
        }
        // Shield Bash: block into an enemy that's close enough to slam.
        else if (self.BlockReady && self.S[Stat.ShieldBash] > 0 && NearestEnemy(self) is { } close
                 && close.GlobalPosition.DistanceTo(self.GlobalPosition) < Mathf.Max(self.HalfSize.X, self.HalfSize.Y) + GameManager.BashReach + 4
                 && _rng.Randf() < 0.15f)
            _blocks++;
        _trailT -= dt;
        if (_trailT <= 0)
        {
            _trailT = 0.25f;
            _trail.Enqueue($"({me.X:0},{me.Y:0}){(onFloor ? "F" : "")}{(navigating ? "N" + _hop : "")}{(los ? "L" : "")}m{f.MoveX:0.#}{(wantJump ? "J" : "")}");
            if (_trail.Count > 16) _trail.Dequeue();
        }
        f.BlockPresses = _blocks;
        return f;
    }

    private Avatar NearestEnemy(Avatar self)
    {
        Avatar best = null;
        float bestD = float.MaxValue;
        foreach (var a in _game.LiveAvatars())
        {
            if (a == self || _game.Allies(a.Player.Slot, self.Player.Slot)) continue;
            float d = a.GlobalPosition.DistanceSquaredTo(self.GlobalPosition);
            if (a.Player.Slot == _game.HeadSlot) d *= 0.25f; // Headhunter: everyone wants the head
            if (d < bestD) { bestD = d; best = a; }
        }
        return best;
    }

    private MapLayout _navSource, _navMap;
    private Vector2 _navSize;

    /// <summary>The jump graph for our current body size (rebuilt when the arena or our size changes).</summary>
    private MapLayout NavMap(Avatar self)
    {
        var size = self.HalfSize * 2;
        if (_game.Map != _navSource || size != _navSize)
        {
            _navSource = _game.Map;
            _navSize = size;
            _navMap = MapGenerator.NavLayout(_game.Map, size.X, size.Y);
            _surface = -1;
            _hop = -1;
            _vantage = -1;
        }
        return _navMap;
    }

    private int _surface = -1, _hop = -1, _vantage = -1;
    /// <summary>Where on <see cref="_vantage"/> to stand; <see cref="_atVantage"/> while that's our own surface.</summary>
    private float _vantageX = float.NaN;
    private bool _atVantage, _vantageJump;
    /// <summary>The jump we're committed to until we land: the surface we jumped for, or a way to keep going.</summary>
    private int _airHop = -1;
    private float _airMove, _commitT;
    private float _navT, _stuckT, _wanderT, _wanderDir = 1, _airT;
    private Vector2 _stuckFrom;

    public string DebugState => $"surface {_surface} hop {_hop} vantage {_vantage}{(_atVantage ? "*" : "")} x {_vantageX:0} stuck {_stuckT:0.0}s wander {_wanderT:0.0}s trail {string.Join(" ", _trail)}";
    private readonly Queue<string> _trail = new();
    private float _trailT;

    /// <summary><paramref name="x"/> clamped into a surface, at most <paramref name="inset"/> px in from its ends.</summary>
    private static float Inside(float x, Surface s, float inset)
    {
        inset = Mathf.Min(inset, s.Width * 0.5f);
        return Mathf.Clamp(x, s.X1 + inset, s.X2 - inset);
    }

    /// <summary>
    /// True if a body of <paramref name="half"/> size fits right beside one end of surface <paramref name="s"/>,
    /// at its height: room to walk off that end, or to rise past it and step on.
    /// </summary>
    private static bool SideClear(MapLayout map, Surface s, bool right, Vector2 half)
    {
        float w = half.X * 2 + 2, x1 = right ? s.X2 : s.X1 - w, y1 = s.Y - half.Y * 2 - 2, y2 = s.Y + 6;
        foreach (var r in map.Solids)
            if (r.X < x1 + w && x1 < r.Right && r.Y < y2 && y1 < r.Bottom) return false;
        foreach (var sl in map.Slopes)
            if (sl.Overlaps(x1, y1, x1 + w, y2)) return false;
        return true;
    }

    /// <summary>
    /// True if the solid whose top is surface <paramref name="s"/> hangs over our head (its underside above us, and
    /// overlapping us sideways), so jumping straight up would hit it. Beside a tower, which reaches down to the
    /// floor, this is false: rising along its side and stepping on is fine.
    /// </summary>
    private static bool UnderPlatform(MapLayout map, Surface s, Vector2 me, Vector2 half)
    {
        foreach (var r in map.Solids)
            if (r.Y == s.Y && r.X < me.X + half.X + 2 && r.Right > me.X - half.X - 2 && r.Bottom <= me.Y - half.Y + 2
                && r.X < s.X2 && r.Right > s.X1)
                return true;
        return false;
    }

    /// <summary>
    /// The surface reachable from where we stand that's best for shooting at <paramref name="target"/>: in line
    /// of sight if possible, then closest. Used when the target's own surface can't be reached. Each surface is tried
    /// at a row of spots, nearest the target first, so a floor under a ledge offers the spot just out from under it
    /// rather than the one right below, with no shot. That spot's x is kept in <see cref="_vantageX"/>.
    /// </summary>
    private int VantageSurface(MapLayout map, Avatar self, Avatar target)
    {
        var reach = MapGenerator.ReachableFrom(map, _surface);
        var tp = target.GlobalPosition;
        int best = _surface;
        float bestScore = float.MaxValue, bestX = float.NaN, inset = self.HalfSize.X + 2;
        bool bestJump = false;
        // Most of a full jump's rise, in case we let go a little early.
        float jv = self.S[Stat.JumpVelocity], apex = 0.8f * jv * jv / (2 * self.S[Stat.Gravity]);
        for (int i = 0; i < map.Surfaces.Count; i++)
        {
            if (!reach[i]) continue;
            var s = map.Surfaces[i];
            float near = Inside(tp.X, s, inset), score = float.MaxValue, x = near;
            bool jump = false;
            // The spot nearest the target, then one step to either side, two steps, ... out to the surface's ends. A
            // spot counts if we have a shot from it, straight or lobbed, standing or from the top of a jump.
            for (int k = 0; k <= VantageSteps * 2; k++)
            {
                float cx = near + (k + 1) / 2 * VantageStep * (k % 2 == 0 ? 1 : -1);
                if (cx < s.X1 + inset - 0.5f || cx > s.X2 - inset + 0.5f) continue;
                var stand = new Vector2(cx, s.Y - self.HalfSize.Y);
                jump = !HasShot(self, stand, tp);
                if (jump && !HasShot(self, stand - new Vector2(0, apex), tp)) continue;
                score = stand.DistanceTo(tp) + (jump ? 40 : 0);
                x = cx;
                break;
            }
            if (score == float.MaxValue) { score = new Vector2(near, s.Y - self.HalfSize.Y).DistanceTo(tp) + 1000; jump = false; }
            if (i == _vantage) score -= 60; // stick with the last pick unless another is clearly better, or we flip-flop
            if (score < bestScore) { bestScore = score; best = i; bestX = x; bestJump = jump; }
        }
        _vantageX = bestX;
        _vantageJump = bestJump;
        return _vantage = best;
    }

    /// <summary>True if a shot from <paramref name="from"/> can reach <paramref name="to"/>: a clear line, or a clear lob.</summary>
    private bool HasShot(Avatar self, Vector2 from, Vector2 to)
        => HasLineOfSight(from, to) || LobAim(from, to, self.S[Stat.BulletSpeed], self.S[Stat.BulletGravity], out _);

    /// <summary>Spacing and count (each way) of the spots <see cref="VantageSurface"/> tries along a surface.</summary>
    private const float VantageStep = 24;
    private const int VantageSteps = 20;

    /// <summary>How far above the lava a surface must be for bots to feel safe on it.</summary>
    private const float LavaMargin = 80;

    /// <summary>
    /// The reachable surface to escape rising lava to: the nearest one to the target (to keep fighting) among those
    /// well above it, else simply the highest one we can get to.
    /// </summary>
    private int EscapeSurface(MapLayout map, Vector2 tp, float lava)
    {
        var reach = MapGenerator.ReachableFrom(map, _surface);
        int best = -1, highest = _surface;
        float bestScore = float.MaxValue;
        for (int i = 0; i < map.Surfaces.Count; i++)
        {
            if (!reach[i]) continue;
            var s = map.Surfaces[i];
            if (s.Y < map.Surfaces[highest].Y) highest = i;
            if (s.Y > lava - LavaMargin * 1.5f) continue;
            float score = new Vector2(Mathf.Clamp(tp.X, s.X1, s.X2), s.Y).DistanceTo(tp) - (lava - s.Y) * 0.3f; // higher buys more time
            if (score < bestScore) { bestScore = score; best = i; }
        }
        return best >= 0 ? best : highest;
    }

    /// <summary>
    /// Surface <paramref name="i"/> as it is right now: a moving platform's lane narrows to where the platform is, and
    /// a drifting platform's top is wherever it has been shoved.
    /// </summary>
    private Surface Live(MapLayout map, int i)
    {
        if (map.DriftOf.TryGetValue(i, out int di) && di < _game.Arena.Drifts.Count)
        {
            var s = map.Surfaces[i];
            var off = _game.Arena.Drifts[di].Off;
            int dx = Mathf.RoundToInt(off.X), dy = Mathf.RoundToInt(off.Y);
            return new Surface(s.X1 + dx, s.X2 + dx, s.Y + dy);
        }
        if (!map.MoverOf.TryGetValue(i, out int m)) return map.Surfaces[i];
        var mv = map.Movers[m];
        int x = Mathf.RoundToInt(mv.XAt(_game.Arena.Clock));
        return new Surface(x, x + mv.W, mv.Y);
    }

    /// <summary>
    /// Whether we can get from <paramref name="cur"/> to <paramref name="hop"/> right now (both as they are now, see
    /// <see cref="Live"/>). To or from a moving platform that means close: it won't wait for us while we jump.
    /// </summary>
    private bool InReach(MapLayout map, Surface cur, Surface hop)
    {
        // A drifting platform shoved out of jumping range: wait for it to ease back.
        if (map.DriftOf.ContainsKey(_surface) || map.DriftOf.ContainsKey(_hop))
            if (!map.MoverOf.ContainsKey(_surface) && !map.MoverOf.ContainsKey(_hop)) return MapGenerator.CanTraverse(map, cur, hop);
        if (!map.MoverOf.ContainsKey(_surface) && !map.MoverOf.ContainsKey(_hop)) return true;
        int gap = Mathf.Max(0, Mathf.Max(cur.X1, hop.X1) - Mathf.Min(cur.X2, hop.X2));
        return gap <= 12 && MapGenerator.CanTraverse(map, cur, hop);
    }

    /// <summary>The pool our feet are in, if any.</summary>
    private Hazard? PoolUnder(Vector2 feet, float hw)
    {
        foreach (var h in _game.Map.Hazards)
            if (Mathf.Abs(feet.Y - h.Y) < 3 && feet.X + hw > h.X1 + 2 && feet.X - hw < h.X2 - 2) return h;
        return null;
    }

    /// <summary>
    /// Falling: if we'd land in a pool on the way we're going, the way to its nearer side (-1 or 1); else 0. Only the
    /// first pool down is checked, and nothing in between.
    /// </summary>
    private float PoolLanding(Avatar self)
    {
        var v = self.Velocity;
        if (v.Y <= 0) return 0;
        var feet = self.GlobalPosition + new Vector2(0, self.HalfSize.Y);
        float g = self.S[Stat.Gravity], hw = self.HalfSize.X, bestY = float.MaxValue, side = 0;
        foreach (var h in _game.Map.Hazards)
        {
            float dy = h.Y - feet.Y;
            if (dy < 0 || h.Y >= bestY) continue;
            float t = (-v.Y + Mathf.Sqrt(v.Y * v.Y + 2 * g * dy)) / g;
            float x = feet.X + v.X * t;
            if (x + hw <= h.X1 + 2 || x - hw >= h.X2 - 2) continue;
            bestY = h.Y;
            side = x - h.X1 < h.X2 - x ? -1 : 1;
        }
        return side;
    }

    /// <summary>True if a sawblade will touch us within the next 0.3 s; <paramref name="at"/> is where it will be.</summary>
    private bool SawComing(Avatar self, out Vector2 at)
    {
        at = default;
        foreach (var s in _game.Map.Saws)
            for (float t = 0; t <= 0.3f; t += 0.1f)
            {
                var p = s.At(_game.Arena.Clock + t);
                var c = new Vector2(p.X, p.Y);
                var b = self.Bounds;
                b.Position += self.Velocity * t; // where we'll be by then, too
                var closest = new Vector2(Mathf.Clamp(c.X, b.Position.X, b.End.X), Mathf.Clamp(c.Y, b.Position.Y, b.End.Y));
                if (closest.DistanceTo(c) < s.Radius + 4) { at = c; return true; }
            }
        return false;
    }

    /// <summary>The surface whose top is at <paramref name="feet"/> (within <paramref name="tol"/> px), or -1.</summary>
    private int SurfaceAt(MapLayout map, Vector2 feet, float tol)
    {
        for (int i = 0; i < map.Surfaces.Count; i++)
        {
            var s = Live(map, i);
            if (Mathf.Abs(feet.Y - s.Y) < tol && feet.X >= s.X1 - 4 && feet.X <= s.X2 + 4) return i;
        }
        return -1;
    }

    /// <summary>The highest surface at or below a point (where someone in the air will land), or -1.</summary>
    private int SurfaceUnder(MapLayout map, Vector2 p)
    {
        int best = -1;
        for (int i = 0; i < map.Surfaces.Count; i++)
        {
            var s = Live(map, i);
            if (s.Y >= p.Y - 4 && p.X >= s.X1 - 4 && p.X <= s.X2 + 4 && (best < 0 || s.Y < map.Surfaces[best].Y)) best = i;
        }
        return best;
    }

    /// <summary>
    /// Where to shoot to cut a chain of a hanging platform that would come down on <paramref name="target"/> (it stands
    /// in the platform's drop, below it and above where it lands), along the exact arc onto it. False if no platform
    /// hangs over them, it would come down on us too, we'd be riding it down, or no chain is in range. A chain can hang
    /// higher than a bullet climbs from the ground: then <paramref name="jumpFirst"/> says the shot opens up from the top
    /// of a jump.
    /// </summary>
    private bool ChainShot(Avatar self, Avatar target, out Vector2 aim, out bool jumpFirst)
    {
        aim = default;
        jumpFirst = false;
        var me = self.GlobalPosition;
        float jv = self.S[Stat.JumpVelocity], apex = 0.8f * jv * jv / (2 * self.S[Stat.Gravity]);
        bool canJump = self.IsOnFloor();
        float best = float.MaxValue;
        foreach (var h in _game.Arena.Hangers)
        {
            if (h.FellAt >= 0 || h.Gone) continue;
            if (!InDrop(h, target.Bounds) || InDrop(h, self.Bounds)) continue;
            var feet = me.Y + self.HalfSize.Y;
            if (Mathf.Abs(feet - h.Y) < 4 && me.X > h.Spec.X - self.HalfSize.X && me.X < h.Spec.X + h.Spec.W + self.HalfSize.X) continue;
            var xs = h.ChainXs;
            for (int c = 0; c < xs.Length; c++)
            {
                if (h.ChainCut(c)) continue;
                // Any point on the chain will do: try from just over the platform (the easiest to reach) upward.
                for (float y = h.Y - 6; y > h.Anchors[c] + 4; y -= 16)
                {
                    var at = new Vector2(xs[c], y);
                    float d = at.DistanceTo(me);
                    if (d >= best || d > _tune.Range) break;
                    if (ChainDir(self, me, at, out var dir)) { best = d; aim = me + dir * d; jumpFirst = false; break; }
                    if (canJump && ChainDir(self, me - new Vector2(0, apex), at, out _)) { best = d; aim = at; jumpFirst = true; break; }
                }
            }
        }
        return best != float.MaxValue;
    }

    /// <summary>
    /// Where to stand for a shot at a chain of a platform hanging over <paramref name="target"/>: just off the platform's
    /// end nearest an uncut chain, so the arc comes in over the top. NaN if no platform hangs over them.
    /// </summary>
    private float ChainSpot(Avatar self, Avatar target)
    {
        float me = self.GlobalPosition.X, best = float.NaN;
        foreach (var h in _game.Arena.Hangers)
        {
            if (h.FellAt >= 0 || h.Gone || !InDrop(h, target.Bounds)) continue;
            var s = h.Spec;
            for (int c = 0; c < h.ChainXs.Length; c++)
            {
                if (h.ChainCut(c)) continue;
                float x = h.ChainXs[c] - s.X < s.X + s.W - h.ChainXs[c] ? s.X - self.HalfSize.X - 24 : s.X + s.W + self.HalfSize.X + 24;
                if (float.IsNaN(best) || Mathf.Abs(x - me) < Mathf.Abs(best - me)) best = x;
            }
        }
        return best;
    }

    /// <summary>
    /// The way to fire from <paramref name="from"/> to hit a chain at <paramref name="at"/>: the exact arc, clear of
    /// solids (a rough allowance for the drop misses something this thin), or a straight line for bullets that don't drop.
    /// </summary>
    private bool ChainDir(Avatar self, Vector2 from, Vector2 at, out Vector2 dir)
    {
        float g = self.S[Stat.BulletGravity];
        // Against the live solids: the platform itself, crates and the like stop a shot too.
        if (LobAim(from, at, self.S[Stat.BulletSpeed], g, out dir, _game.Arena.Solids)) return true;
        dir = (at - from).Normalized();
        return g < 1 && g > -1 && HasLineOfSight(from, at);
    }

    /// <summary>True if <paramref name="r"/> is under hanging platform <paramref name="h"/>, above where it lands.</summary>
    private static bool InDrop(HangBody h, Rect2 r)
    {
        var s = h.Spec;
        float landBottom = (h.LandY ?? MapLayout.Height) + s.H;
        return r.End.X > s.X && r.Position.X < s.X + s.W && r.End.Y > h.Y + s.H && r.Position.Y < landBottom;
    }

    /// <summary>
    /// The way to fire (a unit vector) for a bullet of speed <paramref name="v"/> and gravity <paramref name="g"/> to
    /// arc from <paramref name="from"/> to <paramref name="to"/> without touching a solid on the way: the flatter of the
    /// two arcs that reach it if that one is clear, else the high lob. False if neither is clear or it's out of range. Clear of the
    /// arena's fixed solids, or of <paramref name="solids"/> if given.
    /// </summary>
    private bool LobAim(Vector2 from, Vector2 to, float v, float g, out Vector2 dir, IReadOnlyList<RectI> solids = null)
    {
        dir = default;
        float dx = to.X - from.X, ax = Mathf.Abs(dx), up = from.Y - to.Y;
        if (g < 1 || ax < 4) return false;
        float v2 = v * v, disc = v2 * v2 - g * (g * ax * ax + 2 * up * v2);
        if (disc < 0) return false;
        float root = Mathf.Sqrt(disc);
        for (int k = 0; k < 2; k++)
        {
            float angle = Mathf.Atan((v2 + (k == 0 ? -root : root)) / (g * ax));
            var vel = new Vector2(Mathf.Sign(dx) * v * Mathf.Cos(angle), -v * Mathf.Sin(angle));
            if (!ArcClear(from, vel, g, ax / (v * Mathf.Cos(angle)), solids ?? _game.Map.Solids)) continue;
            dir = vel / v;
            return true;
        }
        return false;
    }

    /// <summary>True if a bullet fired at <paramref name="vel"/> under gravity <paramref name="g"/> meets none of <paramref name="solids"/> (nor a ramp) for most of <paramref name="time"/> seconds.</summary>
    private bool ArcClear(Vector2 from, Vector2 vel, float g, float time, IReadOnlyList<RectI> solids)
    {
        const int Steps = 12;
        var p = from;
        for (int i = 1; i <= Steps; i++)
        {
            // Stop just short of the end: the target stands on something, which the arc's last bit may graze.
            float t = time * 0.95f * i / Steps;
            var q = from + vel * t + new Vector2(0, 0.5f * g * t * t);
            var d = q - p;
            if (Sweep.Cast(new NVec(p.X, p.Y), new NVec(d.X, d.Y), 1, solids, _game.Map.Slopes) != null) return false;
            p = q;
        }
        return true;
    }

    /// <summary>
    /// True if a full jump from where we stand, running <paramref name="dir"/> (-1 or 1), comes down on something: a
    /// surface ahead, or ground below the edge. False over a bottomless drop or a gap too wide to clear.
    /// </summary>
    private bool JumpLands(Avatar self, float dir)
    {
        var p = self.GlobalPosition + new Vector2(0, self.HalfSize.Y);
        float vx = dir * self.S[Stat.MoveSpeed] * 0.9f, vy = -self.S[Stat.JumpVelocity], g = self.S[Stat.Gravity], hw = self.HalfSize.X * 0.6f;
        const float dt = 1f / 30;
        for (int i = 0; i < 90; i++)
        {
            var q = p + new Vector2(vx, vy) * dt;
            vy += g * dt;
            if (q.Y > MapLayout.KillY) return false;
            if (q.Y > p.Y && TopBetween(q.X, hw, p.Y, q.Y)) return true;
            p = q;
        }
        return false;
    }

    /// <summary>True if the top of a live solid or a ramp lies under x (± <paramref name="hw"/>) between heights y1 and y2.</summary>
    private bool TopBetween(float x, float hw, float y1, float y2)
    {
        foreach (var r in _game.Arena.Solids)
            if (r.X < x + hw && r.Right > x - hw && r.Y >= y1 - 1 && r.Y <= y2 + 1) return true;
        foreach (var s in _game.Map.Slopes)
            if (x >= s.X1 && x < s.X2 && s.YAt(x) >= y1 - 1 && s.YAt(x) <= y2 + 1) return true;
        return false;
    }

    private bool HasLineOfSight(Vector2 from, Vector2 to)
    {
        var d = to - from;
        var hit = Sweep.Cast(new NVec(from.X, from.Y), new NVec(d.X, d.Y), 1, _game.Map.Solids, _game.Map.Slopes);
        return hit == null;
    }

    private bool GroundAhead(Avatar self, float move)
    {
        if (Mathf.Abs(move) < 0.1f) return true;
        var probe = self.GlobalPosition + new Vector2(Mathf.Sign(move) * (self.HalfSize.X + 6), 0);
        return SolidInColumn(probe.X, probe.Y + self.HalfSize.Y, 50);
    }

    private bool GroundBelow(Avatar self, float depth)
        => SolidInColumn(self.GlobalPosition.X, self.GlobalPosition.Y + self.HalfSize.Y, depth);

    private bool SolidInColumn(float x, float y, float depth)
    {
        // The arena's live solids: moving platforms where they are now, and crates and barrels still standing.
        foreach (var r in _game.Arena.Solids)
            if (x >= r.X && x < r.Right && r.Bottom > y && r.Y < y + depth) return true;
        foreach (var s in _game.Map.Slopes)
            if (x >= s.X1 && x < s.X2 && s.Bottom > y && s.YAt(x) < y + depth) return true;
        return false;
    }

    private bool ThreatIncoming(Avatar self, out int bulletId)
    {
        bulletId = -1;
        var me = self.GlobalPosition;
        foreach (var b in _game.Bullets.Bullets)
        {
            if (b.Dead || (b.Owner == self.Player.Slot && !b.Bounced) || _game.Spared(b.Owner, self.Player.Slot)) continue;
            var rel = me - b.Pos;
            if (rel.LengthSquared() > 45 * 45) continue;
            if (b.Vel.Dot(rel) <= 0) continue;
            bulletId = b.Id;
            return true;
        }
        return false;
    }
}
