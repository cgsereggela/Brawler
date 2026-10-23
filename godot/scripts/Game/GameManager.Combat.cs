using System;
using System.Collections.Generic;
using System.Linq;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Logic;
using Brawler.Net;
using Godot;

namespace Brawler.Game;

/// <summary>Combat: the host resolves bullets, damage, blocks, kills and explosions, and every peer plays the effects.</summary>
public partial class GameManager
{
    // =====================================================================
    // Host: combat resolution (called by Avatar / BulletSystem)
    // =====================================================================

    public void HostSpawnBullet(Bullet b, bool queued = false)
    {
        b.Id = ++_bulletIds;
        if (queued) Bullets.AddQueued(b);
        else Bullets.Add(b);
        if (Net.Mode == NetMode.Host) Rpc(MethodName.SpawnBullet, b.Serialize());
        if (!queued)
        {
            OnShotFx(b);
            Stats.Shot(b.Owner);
            Book.Add(b.Owner, AwardStat.Shots);
        }
    }

    public void HostBulletDestroyed(Bullet b, bool impact)
    {
        ImpactFx(b.Pos, b.Owner, impact);
        if (Net.Mode == NetMode.Host) Rpc(MethodName.BulletGone, b.Id, b.Pos, impact);
    }

    /// <param name="reflected">Thorns damage: never reflected again (so two Thorns players can't loop).</param>
    public void HostDamage(Avatar victim, float damage, Vector2 knockback, int attackerSlot, Vector2 at, bool reflected = false)
    {
        if (victim.Guarded) return; // just respawned
        if (Spared(attackerSlot, victim.Player.Slot)) return; // teammates, friendly fire off: blasts, bashes and arcs pass harmlessly
        float hpBefore = victim.Hp;
        damage *= 1f - victim.S[Stat.Armor];
        knockback *= victim.S[Stat.KnockbackTaken];
        if (!victim.TakeHit(damage, knockback))
        {
            if (victim.Alive) { Stats.HitBlocked(victim.Player.Slot); Book.Add(victim.Player.Slot, AwardStat.HitsBlocked); }
            HostBlocked(victim, at);
            HostBlockedHit(victim, damage, at, attackerSlot, reflected);
            return;
        }
        if (victim.Hp <= 0 && victim.TryRevive())
            HostFx(FxKind.Revive, victim.GlobalPosition, 0, victim.Player.Slot, victim.GlobalPosition);
        if (attackerSlot >= 0 && attackerSlot != victim.Player.Slot)
        {
            victim.LastHitBy = attackerSlot;
            victim.LastHitAt = _clock;
        }
        float dealt = Mathf.Clamp(damage, 0, Mathf.Max(0, hpBefore));
        Stats.Damage(attackerSlot, victim.Player.Slot, dealt);
        Book.Add(victim.Player.Slot, AwardStat.DamageTaken, dealt);
        if (attackerSlot >= 0 && attackerSlot != victim.Player.Slot)
        {
            Book.Add(attackerSlot, AwardStat.Hits);
            Book.Add(attackerSlot, AwardStat.DamageDealt, dealt);
        }
        var attacker =attackerSlot >= 0 ? Slots[attackerSlot]?.Avatar : null;
        if (attacker != null && attacker != victim && attacker.Alive)
        {
            float steal = attacker.S[Stat.Lifesteal];
            if (steal > 0)
            {
                float before = attacker.Hp;
                attacker.Heal(damage * steal);
                HostHealed(attacker.Player.Slot, attacker.Hp - before);
            }
            foreach (var e in attacker.Player.Effects) e.OnDealDamage(attacker, victim, damage, this);
        }
        Rpc(MethodName.Damaged, victim.Player.Slot, victim.Hp, knockback, at, damage, attackerSlot);
        if (victim.Hp <= 0) HostKill(victim, KillCause.Shot, attackerSlot);

        float thorns = victim.S[Stat.Thorns];
        if (!reflected && thorns > 0 && attacker != null && attacker != victim && attacker.Alive)
            HostDamage(attacker, damage * thorns, Vector2.Zero, victim.Player.Slot, attacker.GlobalPosition, reflected: true);
    }

    /// <summary>Chain Lightning: a hit arcs to the nearest other enemy.</summary>
    public void HostChain(Avatar victim, float damage, int ownerSlot)
    {
        var owner = ownerSlot >= 0 && ownerSlot < Slots.Length ? Slots[ownerSlot] : null;
        float chain = owner?.Stats[Stat.Chain] ?? 0;
        if (chain <= 0) return;
        Avatar next = null;
        float best = 150f;
        foreach (var a in LiveAvatars())
        {
            if (a == victim || a.Player.Slot == ownerSlot || Allies(a.Player.Slot, ownerSlot)) continue;
            float d = a.GlobalPosition.DistanceTo(victim.GlobalPosition);
            if (d < best) { best = d; next = a; }
        }
        if (next == null) return;
        HostFx(FxKind.Zap, victim.GlobalPosition, 0, ownerSlot, next.GlobalPosition);
        var kb = (next.GlobalPosition - victim.GlobalPosition).Normalized() * 60;
        HostDamage(next, damage * chain, kb, ownerSlot, next.GlobalPosition);
    }

    /// <summary>Warp Rounds: move the shooter to where the bullet landed, if there's room for them.</summary>
    public void HostWarp(int ownerSlot, Vector2 at, Vector2 bulletVel)
    {
        var a = AvatarOf(ownerSlot);
        if (a == null || a.S[Stat.Warp] <= 0) return;
        var back = -bulletVel.Normalized();
        if (back == Vector2.Zero) back = Vector2.Up;
        var hs = a.HalfSize + new Vector2(1, 1);
        for (float d = hs.Length(); d < 48; d += 4)
        {
            var p = at + back * d;
            if (p.X < hs.X || p.X > MapLayout.Width - hs.X || p.Y < hs.Y || p.Y > MapLayout.KillY - 20) continue;
            var box = new Rect2(p - hs, hs * 2);
            bool clear = true;
            foreach (var r in Map.Solids)
                if (box.Intersects(new Rect2(r.X, r.Y, r.W, r.H))) { clear = false; break; }
            foreach (var s in Map.Slopes)
                if (s.Overlaps(box.Position.X, box.Position.Y, box.End.X, box.End.Y)) clear = false;
            if (!clear) continue;
            var from = a.GlobalPosition;
            a.GlobalPosition = p;
            a.Velocity = Vector2.Zero;
            HostFx(FxKind.Blink, from, 0, ownerSlot, p);
            return;
        }
    }

    /// <summary>Leech Field: drain enemies near the avatar (called a few times a second).</summary>
    public void HostLeech(Avatar a, float amount)
    {
        float radius = 44 * a.S[Stat.Size] + 10;
        foreach (var e in LiveAvatars().ToList())
        {
            if (e == a || Allies(e.Player.Slot, a.Player.Slot) || e.GlobalPosition.DistanceTo(a.GlobalPosition) > radius) continue;
            float before = e.Hp;
            HostDamage(e, amount, Vector2.Zero, a.Player.Slot, e.GlobalPosition);
            float dealt = Mathf.Max(0, before - Mathf.Max(0, e.Hp));
            if (dealt > 0 && a.Alive)
            {
                float hp = a.Hp;
                a.Heal(dealt);
                HostHealed(a.Player.Slot, a.Hp - hp);
            }
        }
    }

    public enum KillCause { Shot, Fall, Left, Hazard }

    public enum HazardHit { Magma, Poison, Saw, Crush }

    /// <summary>Last hazard that hurt each slot (every peer), so a hazard kill gets the right call-out.</summary>
    private readonly HazardHit[] _lastHazard = new HazardHit[LobbyState.MaxPlayers];

    /// <summary>
    /// Host: damage from the arena itself (pools, saws). Armor and block still count. A kill is credited to whoever
    /// hit the victim in the last few seconds, as with falls.
    /// </summary>
    public void HostHazardDamage(Avatar victim, float damage, Vector2 knockback, Vector2 at, HazardHit kind)
    {
        if (!victim.Alive) return;
        if (victim.Guarded) return;
        HazardDamaged?.Invoke(victim.Player.Slot, kind, damage);
        float hpBefore = victim.Hp;
        damage *= 1f - victim.S[Stat.Armor];
        knockback *= victim.S[Stat.KnockbackTaken];
        if (!victim.TakeHit(damage, knockback))
        {
            if (kind == HazardHit.Saw)
            {
                HostBlocked(victim, at);
                HostBlockedHit(victim, damage, at, -1);
            }
            return;
        }
        if (victim.Hp <= 0 && victim.TryRevive())
            HostFx(FxKind.Revive, victim.GlobalPosition, 0, victim.Player.Slot, victim.GlobalPosition);
        Arena.HazardDamage += damage;
        float dealt = Mathf.Clamp(damage, 0, Mathf.Max(0, hpBefore));
        Stats.Damage(-1, victim.Player.Slot, dealt);
        Book.Add(victim.Player.Slot, AwardStat.DamageTaken, dealt);
        Rpc(MethodName.HazardHurt, victim.Player.Slot, victim.Hp, knockback, at, damage, (int)kind);
        if (victim.Hp > 0) return;
        int slot = victim.Player.Slot;
        bool recent = victim.LastHitBy >= 0 && victim.LastHitBy != slot && _clock - victim.LastHitAt < 4f;
        HostKill(victim, KillCause.Hazard, recent ? victim.LastHitBy : -1);
    }

    public void HostPropBroken(int index, Vector2 at, bool fell) => Rpc(MethodName.PropBroken, index, at, fell);

    public void HostHangerCut(int index, int mask, float at, int by) => Rpc(MethodName.HangerCut, index, mask, at, by);

    /// <summary>Host: <paramref name="by"/> counts as having just hit <paramref name="victim"/>, for the kill credit.</summary>
    public void CreditHit(Avatar victim, int by)
    {
        victim.LastHitBy = by;
        victim.LastHitAt = _clock;
    }

    /// <summary>Host: pick the end-of-match awards from everyone who played (even if they left) and share them.</summary>
    private void HostSendAwards()
    {
        var awards = Awards.Pick(Book, Slots.Where(p => p != null).Select(p => p.Slot), HostRng);
        var w = new Wire().U8(awards.Count);
        foreach (var a in awards) w.U8(a.Slot).U8(a.Id).F(a.Value);
        Rpc(MethodName.SyncAwards, w.Done());
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SyncAwards(byte[] data)
    {
        var r = new WireReader(data);
        MatchAwards.Clear();
        int n = r.U8();
        for (int i = 0; i < n; i++) MatchAwards.Add(new Award(r.U8(), r.U8(), r.F()));
        Log.Info($"awards: {string.Join("; ", MatchAwards.Select(a => $"{a.Title} = {NameOf(a.Slot)} ({a.Blurb})"))}");
    }

    private void HostHealed(int slot, float amount)
    {
        Stats.Healed(slot, amount);
        Book.Add(slot, AwardStat.Healed, amount);
    }

    public void HostKill(Avatar a, KillCause cause = KillCause.Shot, int killer = -1)
    {
        if (!a.Alive) return;
        if (cause != KillCause.Left) Stats.Death(a.Player.Slot, fell: cause == KillCause.Fall);
        a.Alive = false;
        if (killer == a.Player.Slot) killer = -1;
        if (Allies(killer, a.Player.Slot)) Log.Info($"team kill: {NameOf(killer)} took out teammate {NameOf(a.Player.Slot)}");
        if (cause != KillCause.Left)
        {
            Book.Add(killer >= 0 ? killer : a.Player.Slot, killer >= 0 ? AwardStat.Kills : AwardStat.SelfKills);
            if (cause == KillCause.Fall) Book.Add(a.Player.Slot, AwardStat.Falls);
        }
        HostObjectiveKill(a, killer, cause);
        Rpc(MethodName.Died, a.Player.Slot, a.GlobalPosition, killer, (int)cause);
        _lastVictim = cause == KillCause.Left ? -1 : a.Player.Slot;
        _lastKiller = killer;
        if (cause != KillCause.Left)
        {
            HostBotChat(killer, 0.3f, 1, 3, 4);
            HostBotChat(a.Player.Slot, 0.3f, 2, 3, 4);
        }
        // Kamikaze: go out with a bang (not when leaving the game or falling into the void).
        float boom = a.S[Stat.Kamikaze];
        if (boom > 0 && cause is KillCause.Shot or KillCause.Hazard) HostExplode(a.GlobalPosition, boom, 70, a.Player.Slot, 480);
    }

    public void HostExplode(Vector2 at, float radius, float damage, int ownerSlot, float knockback)
    {
        HostFx(FxKind.Explosion, at, radius, ownerSlot, at);
        Arena.HostExplosion(at, radius, damage, ownerSlot, knockback);
        foreach (var a in LiveAvatars().ToList())
        {
            var r = a.Bounds;
            var closest = new Vector2(Mathf.Clamp(at.X, r.Position.X, r.End.X), Mathf.Clamp(at.Y, r.Position.Y, r.End.Y));
            float d = closest.DistanceTo(at);
            if (d > radius) continue;
            float falloff = 1f - 0.5f * d / radius;
            float dmg = damage * falloff * (a.Player.Slot == ownerSlot ? 0.5f : 1f);
            var dir = (a.GlobalPosition - at).Normalized();
            if (dir == Vector2.Zero) dir = Vector2.Up;
            HostDamage(a, dmg, dir * knockback * falloff, ownerSlot, closest);
        }
    }

    public void HostBlocked(Avatar a, Vector2 at) => HostFx(FxKind.Blocked, at, 0, a.Player.Slot, at);

    public void HostOnBlock(Avatar a)
    {
        Stats.BlockUsed(a.Player.Slot);
        Book.Add(a.Player.Slot, AwardStat.Blocks);
        HostFx(FxKind.Block, a.GlobalPosition, 0, a.Player.Slot, a.GlobalPosition);

        // Shockwave: starting a block erases enemy bullets around you.
        int slot = a.Player.Slot;
        var pos = a.GlobalPosition;
        float body = Mathf.Max(a.HalfSize.X, a.HalfSize.Y);
        float wave = a.S[Stat.Shockwave];
        if (wave > 0)
        {
            Bullets.HostErase(pos, body + wave, slot);
            HostFx(FxKind.Shockwave, pos, body + wave, slot, pos);
        }

        // Shield Bash: starting a block hits and shoves enemies touching your shield.
        float bash = a.S[Stat.ShieldBash];
        if (bash > 0)
        {
            float reach = body + BashReach;
            foreach (var e in LiveAvatars().ToList())
            {
                if (e == a) continue;
                var r = e.Bounds;
                var closest = new Vector2(Mathf.Clamp(pos.X, r.Position.X, r.End.X), Mathf.Clamp(pos.Y, r.Position.Y, r.End.Y));
                if (closest.DistanceTo(pos) > reach) continue;
                var dir = (e.GlobalPosition - pos).Normalized();
                if (dir == Vector2.Zero) dir = Vector2.Up;
                HostDamage(e, bash, dir * 330 + new Vector2(0, -90), slot, closest);
            }
            HostFx(FxKind.Bash, pos, reach, slot, pos);
        }
    }

    /// <summary>How far past your body Shield Bash reaches.</summary>
    public const float BashReach = 20f;
    /// <summary>How long Riposte's bonus damage lasts after a blocked hit.</summary>
    public const float CounterTime = 2f;

    /// <summary>Host: a hit (bullet, blast, saw) landed on an active block. Block cards react here.</summary>
    /// <param name="reflected">The hit was itself spike or thorns damage: no spikes back (two Spiked Shields can't loop).</param>
    public void HostBlockedHit(Avatar a, float damage, Vector2 at, int attackerSlot, bool reflected = false)
    {
        if (!a.Alive) return;
        var s = a.S;
        var attacker = attackerSlot >= 0 && attackerSlot < Slots.Length ? Slots[attackerSlot]?.Avatar : null;
        if (!reflected && s[Stat.BlockSpikes] > 0 && damage > 0 && attacker != null && attacker != a && attacker.Alive)
        {
            HostFx(FxKind.Zap, a.GlobalPosition, 0, a.Player.Slot, attacker.GlobalPosition);
            HostDamage(attacker, damage * s[Stat.BlockSpikes], Vector2.Zero, a.Player.Slot, attacker.GlobalPosition, reflected: true);
        }
        if (s[Stat.BlockHeal] > 0 && damage > 0)
        {
            float before = a.Hp;
            a.Heal(damage * s[Stat.BlockHeal]);
            HostHealed(a.Player.Slot, a.Hp - before);
        }
        if (s[Stat.BlockReload] > 0) a.RefillAmmo();
        if (s[Stat.BlockRefund] > 0) a.BlockCd = Mathf.Max(0, a.BlockCd - s[Stat.BlockCooldown] * s[Stat.BlockRefund]);
        if (s[Stat.Counter] > 0)
        {
            if (a.CounterT <= 0) HostFx(FxKind.Counter, a.GlobalPosition, 0, a.Player.Slot, a.GlobalPosition);
            a.CounterT = CounterTime;
        }
    }

    public void HostFx(FxKind kind, Vector2 pos, float value, int slot, Vector2 pos2)
        => Rpc(MethodName.FxEvent, (int)kind, pos, value, slot, pos2);

    public void OnJump(Avatar a, bool air = false)
    {
        var feet = a.GlobalPosition + new Vector2(0, a.HalfSize.Y);
        Fx.Emit(feet, PKind.Smoke, new Color(1, 1, 1, 0.5f), 4, 50, 0.35f, 2.2f, 0, Vector2.Up, 2.6f, 5);
        if (air) Fx.RingFx(feet, 2, 12 * a.S[Stat.Size], Palette.Player(a.Player.Info.ColorIdx).Lightened(0.4f), 0.25f, 1.5f);
        if (IsLocalHuman(a.Player)) Sfx.Play("jump", 0.6f);
    }

    private void OnShotFx(Bullet b)
    {
        var st = StyleOf(b.Owner);
        var col = Palette.Player(ColorOf(b.Owner));
        var dir = b.Vel.Normalized();
        Fx.Emit(b.Pos, PKind.Streak, col.Lightened(0.4f), 3 + st.Tier, 160, 0.16f, 0.8f, 0, dir, 0.9f);
        if (st.Tier >= 2) Fx.Flash(b.Pos, 8 + st.Tier * 2, col, 0.1f);
        if (st.Has(Flavor.Fire)) Fx.Emit(b.Pos, PKind.Ember, new Color("#ffa33b"), 2, 60, 0.4f, 1.2f, -40, dir, 1.2f);
        var gun = AvatarOf(b.Owner);
        Sfx.Play(gun != null ? Sfx.ShotFor(gun.S) : "shoot", 0.7f);
        if (IsLocalHuman(b.Owner)) Shake(0.12f);
        if (b.Child) return;
        var shooter = AvatarOf(b.Owner);
        if (shooter == null) return;
        shooter.OnFired();
        // Recoil Jet: pushed on the host and in the shooter's own client prediction.
        if (IsHost || IsLocalHuman(shooter.Player)) shooter.ApplyRecoil(dir);
    }

    // =====================================================================
    // Effects (every peer, cosmetic). They grow with the attacker's card count and take on card flavors.
    // =====================================================================

    public FxStyle StyleOf(int slot) => slot >= 0 && slot < Slots.Length && Slots[slot] != null ? Slots[slot].Fx : FxStyle.None;

    private Vector2 PosOf(int slot, Vector2 fallback) =>
        slot >= 0 && slot < Slots.Length && Slots[slot]?.Avatar is { } a ? a.GlobalPosition : fallback;

    /// <summary>A bullet hit a wall (or expired).</summary>
    private void ImpactFx(Vector2 at, int owner, bool impact)
    {
        var st = StyleOf(owner);
        var col = Palette.Player(ColorOf(owner));
        if (!impact)
        {
            Fx.Emit(at, PKind.Dot, col, 2, 40, 0.2f, 0.8f);
            return;
        }
        Fx.Spark(at, col.Lightened(0.3f), 4 + st.Tier * 2);
        Fx.Flash(at, 8 + st.Tier * 3, col, 0.12f);
        if (st.Tier >= 2) Fx.RingFx(at, 1, 8 + st.Tier * 3, col, 0.2f, 1.2f);
        if (st.Has(Flavor.Fire)) Fx.Emit(at, PKind.Ember, new Color("#ff8a2a"), 3 + st.Tier, 70, 0.6f, 1.2f, -60);
        if (st.Has(Flavor.Homing)) Fx.Emit(at, PKind.Star, col.Lightened(0.5f), 2, 50, 0.4f, 2.2f);
        if (st.Has(Flavor.Glass)) Fx.Emit(at, PKind.Shard, new Color("#c8f4ff"), 3, 90, 0.5f, 2f, 300);
    }

    private void HitFx(int victim, int attacker, Vector2 at, Vector2 knock, float damage)
    {
        var st = StyleOf(attacker);
        int tier = st.Tier;
        float I = st.Intensity;
        var vc = Palette.Player(ColorOf(victim));
        var ac = attacker >= 0 ? Palette.Player(ColorOf(attacker)) : Colors.White;
        var dir = knock.LengthSquared() > 1 ? knock.Normalized() : Vector2.Up;

        Fx.Emit(at, PKind.Streak, vc.Lightened(0.3f), Mathf.RoundToInt(7 * I), 200 * Mathf.Sqrt(I), 0.35f, 1.1f, 250, dir, 2.2f);
        Fx.Emit(at, PKind.Dot, Colors.White, 3 + tier * 2, 110, 0.3f, 0.9f);
        Fx.Emit(at, PKind.Chunk, vc, 3 + tier, 120, 0.6f, 2f, 500, dir, 2f);
        Fx.Flash(at, 12 + tier * 5, ac, 0.18f);
        Fx.RingFx(at, 2, 12 + tier * 5, ac, 0.25f, 1.6f);
        if (tier >= 2) Fx.RingFx(at, 2, 20 + tier * 6, Colors.White, 0.3f, 1f, 0.05f);
        if (tier >= 3) Fx.RaysFx(at, ac, 6, 22 + tier * 5, 0.25f);
        if (tier >= 4) Fx.ScreenFlash(ac with { A = 0.07f }, 0.08f);

        if (st.Has(Flavor.Fire))
        {
            Fx.Emit(at, PKind.Ember, new Color("#ff9a2e"), 5 + tier * 2, 120, 0.7f, 1.4f, -80);
            Fx.Emit(at, PKind.Smoke, new Color(0.15f, 0.1f, 0.1f, 0.8f), 2 + tier, 30, 0.8f, 3.5f, -20);
        }
        if (st.Has(Flavor.Homing)) Fx.Emit(at, PKind.Star, ac.Lightened(0.5f), 3 + tier, 90, 0.5f, 2.8f);
        if (st.Has(Flavor.Bouncy)) Fx.RingFx(at, 1, 10 + tier * 3, ac.Lightened(0.4f), 0.25f, 1f, 0.1f);
        if (st.Has(Flavor.Pierce)) Fx.Emit(at, PKind.Streak, Colors.White, 4, 520, 0.2f, 1.4f, 0, dir, 0.12f, 1f);
        if (st.Has(Flavor.Split)) Fx.Emit(at, PKind.Shard, ac, 4 + tier, 160, 0.6f, 2.4f, 350);
        if (st.Has(Flavor.Glass)) Fx.Emit(at, PKind.Shard, new Color("#d6f6ff"), 5 + tier, 180, 0.6f, 2.4f, 400);
        if (st.Has(Flavor.Feather)) Fx.Emit(at, PKind.Confetti, new Color(1, 1, 1, 0.9f), 3, 70, 1.2f, 3f, 40, null, Mathf.Tau, 3.5f);
        if (st.Has(Flavor.Vampire) && attacker >= 0)
            Fx.EmitHoming(at, PosOf(attacker, at), new Color("#ff2a4a"), 4 + tier * 2, 120, 0.9f, 1.1f);

        float size = 11 + Mathf.Clamp(damage / 5f, 0, 14) + tier;
        string txt = Mathf.RoundToInt(damage).ToString();
        if (st.Has(Flavor.Glass) && damage >= 40) txt += "!";
        Fx.Popup(at + new Vector2(0, -10), txt, Colors.White.Lerp(ac, 0.35f), size, 0.75f);
    }

    private static readonly string[][] KillWords =
    {
        new[] { "KO" },
        new[] { "KO!", "POP!" },
        new[] { "SMASHED!", "BONK!", "SPLAT!" },
        new[] { "WRECKED!", "DEMOLISHED!", "KABOOM!" },
        new[] { "OBLITERATED!", "ANNIHILATED!", "ERASED!" },
        new[] { "DELETED!!", "VAPORIZED!!", "SUPERNOVA!!" },
    };

    private readonly int[] _battleKills = new int[LobbyState.MaxPlayers];

    /// <summary>Test hook (offline only): replaces every player's cards at the start of each battle.</summary>
    public Func<int, List<int>> DebugCards;

    /// <summary>Test hook (host): starts a battle on a chosen map seed right away.</summary>
    public void DebugStartBattle(int seed)
    {
        if (!IsHost) return;
        _hostNext = null;
        Rpc(MethodName.StartBattle, seed, ScoresBytes());
    }

    /// <summary>
    /// --fxshots: freezes everyone and plays a hit (left) and a kill (right) as if slot 0 held
    /// <paramref name="power"/> cards with <paramref name="flavors"/>, so effect scaling can be screenshotted.
    /// </summary>
    public void DebugShowcase(int power, Flavor flavors)
    {
        if (Slots[0] == null || Slots[1] == null) return;
        foreach (var a in LiveAvatars()) a.Frozen = true;
        Bullets.Clear();
        Slots[0].Fx = new FxStyle(power, flavors);
        var mid = new Vector2(MapLayout.Width, MapLayout.Height) / 2;
        HitFx(1, 0, mid + new Vector2(-170, 0), new Vector2(140, -40), 25 + power * 4);
        Array.Clear(_battleKills);
        if (power >= 9) _battleKills[0] = 1; // show a streak call-out too
        KillFx(1, 0, mid + new Vector2(150, 10), KillCause.Shot);
    }

    private void KillFx(int victim, int killer, Vector2 at, KillCause cause)
    {
        var st = StyleOf(killer);
        int tier = st.Tier;
        float I = st.Intensity;
        var vc = Palette.Player(ColorOf(victim));
        var kc = killer >= 0 ? Palette.Player(ColorOf(killer)) : Colors.White;
        bool fell = cause == KillCause.Fall;
        if (fell) at = new Vector2(Mathf.Clamp(at.X, 20, MapLayout.Width - 20), MapLayout.Height - 4);
        Vector2? up = fell ? Vector2.Up : null;
        float spread = fell ? 1.4f : Mathf.Tau;

        Fx.Emit(at, PKind.Chunk, vc, Mathf.RoundToInt(16 * I), 260 * Mathf.Sqrt(I), 1.1f, 3f, 520, up, spread, 1.2f);
        Fx.Emit(at, PKind.Streak, vc.Lightened(0.4f), Mathf.RoundToInt(12 * I), 320, 0.45f, 1.4f, 200, up, spread);
        Fx.Emit(at, PKind.Dot, Colors.White, 8 + tier * 4, 200, 0.5f, 1.3f, 0, up, spread);
        Fx.Flash(at, 40 + tier * 12, vc, 0.35f);
        Fx.Flash(at, 18 + tier * 4, Colors.White, 0.15f);
        Fx.RingFx(at, 4, 50 + tier * 16, vc, 0.45f, 3f);
        Fx.RingFx(at, 2, 34 + tier * 10, Colors.White, 0.35f, 1.5f, 0.06f);
        if (tier >= 1) Fx.Emit(at, PKind.Smoke, new Color(0.12f, 0.08f, 0.16f, 0.9f), 3 + tier * 2, 70, 0.9f, 4f, -25);
        if (tier >= 2)
        {
            Fx.RaysFx(at, kc, 8 + tier * 2, 70 + tier * 22, 0.6f);
            Fx.Emit(at, PKind.Confetti, kc, 10 + tier * 6, 260, 1.6f, 3.4f, 260, up, spread, 2.2f);
            Fx.Emit(at, PKind.Confetti, vc.Lightened(0.3f), 6 + tier * 4, 240, 1.6f, 3.4f, 260, up, spread, 2.2f);
        }
        if (tier >= 3)
        {
            Fx.RingFx(at, 6, 90 + tier * 20, kc, 0.6f, 4f, 0.12f);
            Fx.ScreenFlash(kc with { A = 0.16f }, 0.16f);
            _zoomPunch = Mathf.Max(_zoomPunch, 0.5f + tier * 0.1f);
        }
        if (tier >= 4) Fx.Emit(at, PKind.Star, Colors.White, 6 + tier * 2, 260, 0.8f, 4f, 0, up, spread, 1.6f);

        if (st.Has(Flavor.Fire))
        {
            Fx.Emit(at, PKind.Ember, new Color("#ffae3b"), 18 + tier * 6, 260, 1.1f, 1.8f, -90);
            Fx.Emit(at, PKind.Smoke, new Color(0.18f, 0.1f, 0.08f, 0.9f), 6 + tier * 2, 90, 1.2f, 5f, -30);
            Fx.Flash(at, 70 + tier * 14, new Color("#ff7a22"), 0.4f);
        }
        if (st.Has(Flavor.Homing)) Fx.Emit(at, PKind.Star, kc.Lightened(0.5f), 10 + tier * 3, 200, 0.9f, 3.4f);
        if (st.Has(Flavor.Bouncy))
            for (int i = 0; i < 3; i++) Fx.RingFx(at, 4, 30 + i * 18, kc.Lightened(0.3f), 0.35f, 1.6f, 0.08f * (i + 1));
        if (st.Has(Flavor.Pierce))
        {
            var d = Vector2.FromAngle((float)GD.RandRange(0, Mathf.Pi));
            Fx.Emit(at, PKind.Streak, Colors.White, 6, 700, 0.25f, 2f, 0, d, 0.06f, 1f);
            Fx.Emit(at, PKind.Streak, Colors.White, 6, 700, 0.25f, 2f, 0, -d, 0.06f, 1f);
        }
        if (st.Has(Flavor.Split) || st.Has(Flavor.Glass))
            Fx.Emit(at, PKind.Shard, st.Has(Flavor.Glass) ? new Color("#d6f6ff") : kc, 14 + tier * 4, 300, 1f, 3.2f, 450, up, spread);
        if (st.Has(Flavor.Feather)) Fx.Emit(at, PKind.Confetti, Colors.White, 10, 160, 2f, 3.6f, 50, up, spread, 3f);
        if (st.Has(Flavor.Vampire) && killer >= 0)
            Fx.EmitHoming(at, PosOf(killer, at), new Color("#ff2a4a"), 14 + tier * 3, 200, 1.2f, 1.4f);

        // Words: how hard it was, plus a streak call-out.
        string word = fell && killer < 0 ? "OUT!" : KillWords[tier][(int)(GD.Randi() % (uint)KillWords[tier].Length)];
        // Blown up by your own shot (or nobody's): no one to credit.
        if (cause == KillCause.Shot && killer < 0) word = "UNLUCKY!";
        if (cause == KillCause.Hazard && victim >= 0)
            word = _lastHazard[victim] switch { HazardHit.Saw => "SHREDDED!", HazardHit.Magma => "MELTED!", HazardHit.Crush => "CRUSHED!", _ => "POISONED!" };
        Fx.Popup(at + new Vector2(0, fell ? -40 : -18), word, kc.Lightened(0.2f), 16 + tier * 3, 1.1f);
        if (killer >= 0 && killer < _battleKills.Length)
        {
            int k = ++_battleKills[killer];
            string streak = k switch { 2 => "DOUBLE KILL", 3 => "TRIPLE KILL", >= 4 => "RAMPAGE", _ => null };
            if (streak != null)
            {
                Fx.Popup(PosOf(killer, at) + new Vector2(0, -38), streak, Palette.Accent, 18 + k * 2, 1.4f);
                Fx.RaysFx(PosOf(killer, at), Palette.Accent, 10, 60, 0.5f);
            }
        }

        Sfx.Play("death");
        if (tier >= 3 || st.Has(Flavor.Fire)) Sfx.Play("explode", 0.6f);
        Shake(0.7f + tier * 0.08f);
        HitStop(70 + Mathf.Min(tier, 4) * 20);
    }

    // =====================================================================
    // RPCs: combat events, host → everyone
    // =====================================================================

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SpawnBullet(byte[] data)
    {
        if (Map == null) return;
        var b = Bullet.Deserialize(data);
        Bullets.Add(b);
        OnShotFx(b);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BulletGone(int id, Vector2 pos, bool impact)
    {
        var b = Bullets.Kill(id);
        ImpactFx(pos, b?.Owner ?? -1, impact);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Damaged(int slot, float newHp, Vector2 knockback, Vector2 at, float damage, int attacker)
    {
        var a = Slots[slot]?.Avatar;
        if (a == null) return;
        if (!IsHost)
        {
            a.Hp = newHp;
            if (IsLocalHuman(slot)) a.Velocity += knockback; // predicted avatar feels the hit
        }
        a.OnHurtFx();
        HitFx(slot, attacker, at, knockback, damage);
        Sfx.Play("hit");
        float tierShake = StyleOf(attacker).Tier * 0.04f;
        Shake((IsLocalHuman(slot) ? 0.45f : 0.2f) + tierShake);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void HazardHurt(int slot, float newHp, Vector2 knockback, Vector2 at, float damage, int kind)
    {
        var a = Slots[slot]?.Avatar;
        if (a == null) return;
        if (!IsHost)
        {
            a.Hp = newHp;
            if (IsLocalHuman(slot)) a.Velocity += knockback;
        }
        var k = (HazardHit)kind;
        _lastHazard[slot] = k;
        a.OnHurtFx();
        bool mine = IsLocalHuman(slot);
        switch (k)
        {
            case HazardHit.Saw:
                var dir = knockback.LengthSquared() > 1 ? knockback.Normalized() : Vector2.Up;
                Fx.Emit(at, PKind.Streak, new Color("#fff1c2"), 12, 260, 0.25f, 1f, 300, dir, 2.4f);
                Fx.Emit(at, PKind.Chunk, Palette.Player(ColorOf(slot)), 4, 140, 0.6f, 2f, 500, dir, 2f);
                Fx.Flash(at, 16, Colors.White, 0.12f);
                Fx.Popup(at + new Vector2(0, -10), Mathf.RoundToInt(damage).ToString(), new Color("#ffe1e1"), 13, 0.7f);
                Sfx.Play("saw");
                Shake(mine ? 0.45f : 0.2f);
                break;
            case HazardHit.Crush:
                Fx.Emit(at, PKind.Chunk, Palette.Player(ColorOf(slot)), 6, 160, 0.6f, 2f, 500, Vector2.Up, 2f);
                Fx.Popup(at + new Vector2(0, -14), Mathf.RoundToInt(damage).ToString(), new Color("#ffe1e1"), 13, 0.7f);
                Sfx.Play("hit");
                Shake(mine ? 0.5f : 0.25f);
                break;
            case HazardHit.Magma:
                Fx.Emit(at, PKind.Ember, new Color("#ffb13b"), 5, 90, 0.6f, 1.3f, -70, Vector2.Up, 1.6f);
                Fx.Emit(at, PKind.Smoke, new Color(0.25f, 0.15f, 0.12f, 0.6f), 1, 20, 0.6f, 3f, -30);
                Fx.Popup(at + new Vector2(0, -26), Mathf.RoundToInt(damage).ToString(), new Color("#ffb13b"), 9, 0.5f);
                Sfx.Play("sizzle", 0.55f);
                if (mine) Shake(0.08f);
                break;
            case HazardHit.Poison:
                Fx.Emit(at, PKind.Dot, new Color("#9dff4a"), 4, 40, 0.6f, 1.2f, -40);
                Fx.Popup(at + new Vector2(0, -20), Mathf.RoundToInt(damage).ToString(), new Color("#9dff4a"), 9, 0.5f);
                Sfx.Play("sizzle", 0.3f);
                break;
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void HangerCut(int index, int mask, float at, int by)
    {
        if (Map == null) return;
        Arena.CutChains(index, mask, at, by);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PropBroken(int index, Vector2 at, bool fell)
    {
        if (Map == null) return;
        Arena.Break(index, at, fell);
        if (!fell) Shake(0.1f);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Died(int slot, Vector2 at, int killer, int cause)
    {
        var a = Slots[slot]?.Avatar;
        if (a == null) return;
        a.Alive = false;
        a.CollisionLayer = 0;
        a.QueueRedraw();
        KillCam.NoteDeath(slot, _clock);
        NoteRespawn(slot, (KillCause)cause);
        KillFx(slot, killer, at, (KillCause)cause);
        CareerBook.Died(this, slot, killer);
        Log.Info($"{NameOf(slot)} died{(killer >= 0 ? $" (by {NameOf(killer)})" : "")}");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void FxEvent(int kind, Vector2 pos, float value, int slot, Vector2 pos2)
    {
        var col = Palette.Player(ColorOf(slot));
        var st = StyleOf(slot);
        switch ((FxKind)kind)
        {
            case FxKind.Explosion:
            {
                var fire = new Color("#ffa33b");
                Fx.Flash(pos, value * 2.2f, fire, 0.3f);
                Fx.Flash(pos, value * 0.9f, Colors.White, 0.12f);
                Fx.RingFx(pos, 2, value * 1.1f, col.Lightened(0.3f), 0.3f, 3f);
                Fx.RingFx(pos, 2, value * 1.6f, fire, 0.4f, 1.5f, 0.05f);
                Fx.Emit(pos, PKind.Ember, fire, 14 + st.Tier * 4, value * 6, 0.8f, 1.6f, -60);
                Fx.Emit(pos, PKind.Streak, col.Lightened(0.4f), 10 + st.Tier * 2, value * 8, 0.35f, 1.4f, 100);
                Fx.Emit(pos, PKind.Smoke, new Color(0.16f, 0.1f, 0.1f, 0.9f), 6 + st.Tier, value * 2, 1f, 4.5f, -30);
                if (st.Tier >= 3) Fx.RaysFx(pos, fire, 8, value * 2.4f, 0.35f);
                Sfx.Play("explode");
                Shake(0.4f + st.Tier * 0.04f);
                break;
            }
            case FxKind.Block:
                Fx.RingFx(pos, 8, 22, Colors.White, 0.25f, 2f);
                Fx.Flash(pos, 22, col, 0.2f);
                Sfx.Play("block", 0.7f);
                break;
            case FxKind.Blocked:
                Fx.Emit(pos, PKind.Streak, Colors.White, 10, 180, 0.3f, 1f);
                Fx.Emit(pos, PKind.Star, col.Lightened(0.5f), 3, 80, 0.4f, 2.5f);
                Fx.RingFx(pos, 2, 14, Colors.White, 0.2f, 1.5f);
                Fx.Popup(pos + new Vector2(0, -10), "BLOCKED", Colors.White, 11, 0.7f);
                Sfx.Play("blocked");
                break;
            case FxKind.Shockwave:
                Fx.RingFx(pos, 6, value, col.Lightened(0.5f), 0.32f, 2.5f);
                Fx.RingFx(pos, 4, value * 0.7f, Colors.White, 0.22f, 1.2f, 0.04f);
                Fx.Flash(pos, value * 0.8f, col, 0.15f);
                Fx.Emit(pos, PKind.Dot, col.Lightened(0.4f), 12, value * 3, 0.35f, 1.2f);
                Sfx.Play("blink", 0.6f);
                break;
            case FxKind.Bash:
                Fx.RingFx(pos, 6, value + 4, Colors.White, 0.2f, 3f);
                Fx.Emit(pos, PKind.Streak, col.Lightened(0.5f), 14, 220, 0.25f, 1.2f);
                Fx.Flash(pos, value * 1.4f, col, 0.18f);
                Sfx.Play("blocked", 0.9f);
                Shake(0.22f);
                break;
            case FxKind.Counter:
                Fx.Popup(pos + new Vector2(0, -20), "COUNTER!", col.Lightened(0.45f), 11, 0.8f);
                Fx.RingFx(pos, 20, 4, col.Lightened(0.3f), 0.3f, 2f);
                break;
            case FxKind.Blink:
                foreach (var p in new[] { pos, pos2 })
                {
                    Fx.Emit(p, PKind.Dot, col.Lightened(0.3f), 14, 110, 0.45f, 1.2f);
                    Fx.Emit(p, PKind.Star, Colors.White, 4, 60, 0.4f, 2.4f);
                    Fx.RingFx(p, 18, 2, col, 0.25f, 2f);
                }
                Sfx.Play("blink");
                break;
            case FxKind.Zap:
            {
                var bolt = new Color("#8fe9ff");
                Fx.Bolt(pos, pos2, bolt);
                Fx.Bolt(pos, pos2, col, 0.18f);
                Fx.Flash(pos2, 18, bolt, 0.18f);
                Fx.Emit(pos2, PKind.Streak, bolt, 6, 150, 0.2f, 0.8f);
                Sfx.Play("blink", 0.5f);
                break;
            }
            case FxKind.Revive:
            {
                var gold = new Color("#ffe066");
                Fx.RingFx(pos, 4, 40, gold, 0.45f, 3f);
                Fx.RaysFx(pos, gold, 10, 50, 0.5f);
                Fx.Emit(pos, PKind.Star, gold, 12, 120, 0.6f, 2.4f, -40);
                Fx.Popup(pos + new Vector2(0, -18), "SECOND WIND!", gold, 14, 1f);
                Sfx.Play("pad");
                break;
            }
        }
    }
}
