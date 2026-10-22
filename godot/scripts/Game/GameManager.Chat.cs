using System;
using System.Collections.Generic;
using System.Linq;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Logic;
using Brawler.Net;
using Godot;

namespace Brawler.Game;

/// <summary>Emotes, pings and ghosts.</summary>
public partial class GameManager
{
    // =====================================================================
    // Emotes, pings and ghosts
    // =====================================================================

    /// <summary>Every peer, after the host lets an emote or a ping through: (slot, emote id) and (slot, where).</summary>
    public event Action<int, int> EmoteShown;
    public event Action<int, Vector2> PingShown;

    private readonly EmoteLimiter[] _emoteGuard = NewGuards(), _pingGuard = NewGuards();
    private readonly EmoteLimiter[] _myEmotes = NewGuards(), _myPings = NewGuards(); // per slot, for the players at this machine
    private readonly Rng _chatRng = new((ulong)DateTime.Now.Ticks ^ 0x9e3779b97f4a7c15UL);

    private static EmoteLimiter[] NewGuards() => Enumerable.Range(0, LobbyState.MaxPlayers).Select(_ => new EmoteLimiter()).ToArray();

    /// <summary>Emotes and pings work while the arena is on screen, alive or as a ghost.</summary>
    public bool CanChat => Phase is Phase.Countdown or Phase.Fighting or Phase.Intermission && Map != null;

    /// <param name="who">Which player at this machine (default: the keyboard player).</param>
    public void LocalEmote(int id, MatchPlayer who = null)
    {
        who ??= LocalMatchPlayer;
        if (!CanChat || !IsLocalHuman(who) || !Emotes.Valid(id) || !_myEmotes[who.Slot].TryUse()) return;
        if (IsHost) HostEmote(who.Slot, id);
        else RpcId(1, MethodName.RequestEmote, who.Slot, id);
    }

    public void LocalPing(Vector2 at, MatchPlayer who = null)
    {
        who ??= LocalMatchPlayer;
        if (!CanChat || !IsLocalHuman(who) || !_myPings[who.Slot].TryUse()) return;
        if (IsHost) HostPing(who.Slot, at);
        else RpcId(1, MethodName.RequestPing, who.Slot, at);
    }

    private void HostEmote(int slot, int id)
    {
        if (!CanChat || !Emotes.Valid(id) || Slots[slot] is not { Left: false } || !_emoteGuard[slot].TryUse()) return;
        Rpc(MethodName.ShowEmote, slot, id);
    }

    private void HostPing(int slot, Vector2 at)
    {
        if (!CanChat || Slots[slot] is not { Left: false } || !_pingGuard[slot].TryUse()) return;
        at = new Vector2(Mathf.Clamp(at.X, 0, MapLayout.Width), Mathf.Clamp(at.Y, 0, MapLayout.Height));
        Rpc(MethodName.ShowPing, slot, at);
    }

    /// <summary>Host: a bot sometimes says something when it scores, dies or wins (rate limits apply as for anyone).</summary>
    private void HostBotChat(int slot, float chance, params int[] ids)
    {
        if (slot < 0 || Slots[slot] is not { } p || !p.Info.IsBot || !_chatRng.Chance(chance)) return;
        HostEmote(slot, ids[_chatRng.Range(0, ids.Length - 1)]);
    }

    private void TickChatGuards(float dt)
    {
        foreach (var g in _emoteGuard) g.Tick(dt);
        foreach (var g in _pingGuard) g.Tick(dt);
        foreach (var g in _myEmotes) g.Tick(dt);
        foreach (var g in _myPings) g.Tick(dt);
    }

    /// <summary>Reads the emote and ping keys (and buttons) for the people at this computer.</summary>
    private void ReadChatKeys()
    {
        if (!CanChat || AutopilotLocal) return;
        var me = LocalMatchPlayer;
        if (me != null && !LocalInput.Suppressed)
        {
            for (int i = 0; i < Emotes.All.Length; i++)
                if (Input.IsActionJustPressed(Emotes.Action(i))) LocalEmote(i);
            if (Input.IsActionJustPressed("ping")) LocalPing(GetGlobalMousePosition());
            foreach (int d in Pads.Unclaimed()) PadChat(me, d);
        }
        foreach (var g in LocalGuests)
            if (g.Input is PadInput pad) PadChat(g, pad.Device);
    }

    private void PadChat(MatchPlayer who, int device)
    {
        for (int i = 0; i < Emotes.All.Length && i < PadLayout.Emotes.Length; i++)
            if (Pads.JustPressed(device, PadLayout.Emotes[i])) LocalEmote(i, who);
        // A controller pings where its player aims.
        if (Pads.JustPressed(device, PadLayout.Ping) && who.Avatar is { } a && IsInstanceValid(a))
            LocalPing(who.Input switch
            {
                PadInput p => a.GlobalPosition + p.AimDir * PadReader.Reach,
                LocalInput l => a.GlobalPosition + l.PadAimDir * PadReader.Reach,
                _ => a.GlobalPosition,
            }, who);
    }

    /// <summary>Where a bot's ghost (or an autopiloted one) floats: circling over the nearest live player's head.</summary>
    private Vector2 BotGhostTarget(Avatar ghost)
    {
        Avatar near = null;
        float best = float.MaxValue;
        foreach (var a in LiveAvatars())
        {
            float d = a.GlobalPosition.DistanceSquaredTo(ghost.GlobalPosition);
            if (d < best) (best, near) = (d, a);
        }
        float t = _clock * 1.3f + ghost.Player.Slot * 1.7f;
        var center = near?.GlobalPosition ?? new Vector2(MapLayout.Width / 2f, MapLayout.Height / 3f);
        return center + new Vector2(Mathf.Cos(t) * 46, -46 + Mathf.Sin(t * 2) * 10);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestEmote(int slot, int id)
    {
        if (!IsHost) return;
        if (SenderOwns(slot) is { } p) HostEmote(p.Slot, id);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestPing(int slot, Vector2 at)
    {
        if (!IsHost) return;
        if (SenderOwns(slot) is { } p) HostPing(p.Slot, at);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ShowEmote(int slot, int id)
    {
        if (Slots[slot]?.Avatar is not { } a || !Emotes.Valid(id)) return;
        a.ShowEmote(id);
        Sfx.Play("emote", 0.8f);
        Log.Info($"emote: {NameOf(slot)} says {Emotes.Text(id, !a.Alive)}");
        EmoteShown?.Invoke(slot, id);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ShowPing(int slot, Vector2 at)
    {
        if (Slots[slot] == null || Map == null) return;
        Fx.Ping(at, Palette.Player(ColorOf(slot)), NameOf(slot), Emotes.PingTime);
        Sfx.Play("ping", 0.8f);
        Log.Info($"ping: {NameOf(slot)} at ({at.X:0}, {at.Y:0})");
        PingShown?.Invoke(slot, at);
    }
}
