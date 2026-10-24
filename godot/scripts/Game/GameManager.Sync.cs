using System;
using System.Collections.Generic;
using System.Linq;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Logic;
using Brawler.Net;
using Godot;

namespace Brawler.Game;

/// <summary>The wire: snapshots, the client → host requests and the byte formats they use.</summary>
public partial class GameManager
{
    // =====================================================================
    // RPCs: snapshots, host → everyone
    // =====================================================================

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void Snapshot(byte[] data)
    {
        if (IsHost || Map == null) return;
        var r = new WireReader(data);
        int n = r.U8();
        for (int i = 0; i < n; i++)
        {
            var s = r.Snap();
            var a = Slots[s.Slot]?.Avatar;
            if (a == null || _clock - a.BornAt < 0.25f) continue; // just respawned: older snapshots still show the dead body
            bool mine = IsLocalHuman(s.Slot);
            if (a.Alive && !s.Alive) a.Alive = false;
            a.Hp = s.Hp;
            a.Ammo = s.Ammo;
            a.Reloading = s.Reloading;
            a.ReloadFrac = s.ReloadFrac;
            a.BlockCd = s.BlockCd;
            a.BlockActive = s.BlockActive;
            if (!mine) a.AimDir = Vector2.FromAngle(s.Aim);
            a.SetNetTarget(new Vector2(s.Pos.X, s.Pos.Y), new Vector2(s.Vel.X, s.Vel.Y), predicted: mine);
            a.QueueRedraw();
        }
        if (r.More) Arena.Read(r);
        if (r.More) ReadObjectives(r);
    }

    // =====================================================================
    // RPCs: client → host
    // =====================================================================

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void SubmitInput(int slot, byte[] data)
    {
        if (!IsHost) return;
        if (SenderOwns(slot, allowLeft: true) is { Input: RemoteInput ri }) ri.Latest = InputFrame.FromBytes(data);
    }

    /// <summary>Host: the player in <paramref name="slot"/> if the peer sending this RPC plays it (itself or a couch guest of its), else null.</summary>
    private MatchPlayer SenderOwns(int slot, bool allowLeft = false)
    {
        long sender = Multiplayer.GetRemoteSenderId();
        return slot >= 0 && slot < Slots.Length && Slots[slot] is { } p && !p.Info.IsBot && p.Info.PeerId == sender && (allowLeft || !p.Left) ? p : null;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestPick(int slot, int cardId, bool deal)
    {
        if (!IsHost) return;
        if (SenderOwns(slot, allowLeft: true) is { } p) HostApplyPick(p.Slot, cardId, deal);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestRematch(int slot, bool yes)
    {
        if (!IsHost) return;
        if (SenderOwns(slot) is { } p) HostVoteRematch(p.Slot, yes);
    }

    // =====================================================================
    // Serialization helpers
    // =====================================================================

    private byte[] ScoresBytes()
    {
        var w = new Wire();
        foreach (var p in Slots)
        {
            w.Bool(p != null);
            if (p != null) w.U8(p.RoundWins).U8(p.BattleWins);
        }
        return w.Done();
    }

    private void ApplyScores(byte[] data)
    {
        var r = new WireReader(data);
        foreach (var p in Slots)
        {
            if (!r.Bool()) continue;
            int rw = r.U8(), bw = r.U8();
            if (p == null) continue;
            p.RoundWins = rw;
            p.BattleWins = bw;
        }
        ScoresChanged?.Invoke();
    }

    /// <summary>One slot's offer in a card pick: its cards, and the cursed deal if it has one.</summary>
    private readonly record struct PickEntry(int Slot, List<int> Ids, (int Rare, int Curse)? Deal);

    private static void WritePicks(Wire w, IReadOnlyCollection<PickEntry> entries)
    {
        w.U8(entries.Count);
        foreach (var (slot, ids, deal) in entries)
        {
            w.U8(slot).U8(ids.Count);
            foreach (int id in ids) w.I16(id); // 16 bits: custom card ids start at 1000
            w.Bool(deal != null);
            if (deal is { } d) w.I16(d.Rare).I16(d.Curse);
        }
    }

    private void ReadPicks(byte[] data)
    {
        var r = new WireReader(data);
        PickOptions = new Dictionary<int, List<int>>();
        PickDeals = new Dictionary<int, (int Rare, int Curse)>();
        int n = r.U8();
        for (int i = 0; i < n; i++)
        {
            int slot = r.U8();
            int count = r.U8();
            var ids = new List<int>();
            for (int k = 0; k < count; k++) ids.Add(r.I16());
            PickOptions[slot] = ids;
            if (r.Bool()) PickDeals[slot] = (r.I16(), r.I16());
        }
    }

    /// <summary>
    /// The match as it stands, for a rejoining client: every slot's player, whether they've left, their score and cards,
    /// the battle number, and the card pick if one is on (offers and who has picked).
    /// </summary>
    private byte[] RejoinBytes()
    {
        var players = Slots.Where(p => p != null).ToList();
        var lobby = new LobbyState { Settings = Settings };
        lobby.Players.AddRange(players.Select(p => p.Info));
        var w = new Wire();
        w.Blob(lobby.Serialize());
        foreach (var p in players)
        {
            w.Bool(p.Left).U8(p.RoundWins).U8(p.BattleWins).U8(p.Cards.Count);
            foreach (int id in p.Cards) w.I16(id);
        }
        w.I16(BattleNumber);
        bool picking = Phase == Phase.CardPick;
        w.Bool(picking);
        if (picking)
        {
            var picks = new Wire();
            WritePicks(picks, PickOptions.Select(kv => new PickEntry(kv.Key, kv.Value, PickDeals.TryGetValue(kv.Key, out var d) ? d : null)).ToList());
            w.Blob(picks.Done());
            w.U8(Picked.Count);
            foreach (int s in Picked) w.U8(s);
        }
        return w.Done();
    }

    private byte[] SnapshotBytes()
    {
        var w = new Wire();
        var list = Slots.Where(p => p?.Avatar != null).ToList();
        w.U8(list.Count);
        foreach (var p in list)
        {
            var a = p.Avatar;
            w.Snap(new AvatarSnap(p.Slot, a.Alive, a.Reloading, new(a.GlobalPosition.X, a.GlobalPosition.Y), new(a.Velocity.X, a.Velocity.Y),
                a.Hp, a.Ammo, a.ReloadFrac, a.BlockCd, a.BlockActive, a.AimDir.Angle()));
        }
        Arena.Write(w);
        WriteObjectives(w);
        return w.Done();
    }
}
