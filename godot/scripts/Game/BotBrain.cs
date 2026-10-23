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
            // Reaction time: a target has to stay in sight a moment before the first shot.
            if (!sight || target != _seen) _seenT = 0;
            else _seenT += dt;
            _seen = sight ? target : null;
            f.Shoot = sight && _seenT >= _tune.Reaction && tDist < _tune.Range && (self.Ammo > 1 || tDist < 180 || _rng.Randf() < 0.05f);
            f.Reload = !sight && lowAmmo;
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

        bool wantJump = false;
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
                if (_hop < 0 && goal == null) _hop = MapGenerator.NextHop(map, _surface, VantageSurface(map, self, target));
            }
            navigating = _hop >= 0 && _hop != _surface;
        }
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
        if (onFloor && !dropping && !riding && !GroundAhead(self, move)) wantJump = true; // edge or pit ahead
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
        if (!navigating && !onFloor && self.Velocity.Y > 60 && !GroundBelow(self, 175)) move = -move; // falling into a pit: steer back
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
            _jumpHold = jet && !onFloor ? _rng.RandfRange(0.5f, 0.8f) : _rng.RandfRange(0.15f, 0.35f);
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
    private float _navT, _stuckT, _wanderT, _wanderDir = 1, _airT;
    private Vector2 _stuckFrom;

    public string DebugState => $"surface {_surface} hop {_hop} stuck {_stuckT:0.0}s wander {_wanderT:0.0}s trail {string.Join(" ", _trail)}";
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
    /// of sight if possible, then closest. Used when the target's own surface can't be reached.
    /// </summary>
    private int VantageSurface(MapLayout map, Avatar self, Avatar target)
    {
        var reach = MapGenerator.ReachableFrom(map, _surface);
        var tp = target.GlobalPosition;
        int best = _surface;
        float bestScore = float.MaxValue;
        for (int i = 0; i < map.Surfaces.Count; i++)
        {
            if (!reach[i]) continue;
            var s = map.Surfaces[i];
            var stand = new Vector2(Inside(tp.X, s, self.HalfSize.X + 2), s.Y - self.HalfSize.Y);
            float score = stand.DistanceTo(tp) + (HasLineOfSight(stand, tp) ? 0 : 1000);
            if (i == _vantage) score -= 60; // stick with the last pick unless another is clearly better, or we flip-flop
            if (score < bestScore) { bestScore = score; best = i; }
        }
        return _vantage = best;
    }

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
