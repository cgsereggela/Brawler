using System;
using Brawler.Core;
using Brawler.Logic;
using Godot;

namespace Brawler.Net;

public enum NetMode { None, Offline, Host, Client }

/// <summary>
/// Owns the connection (ENet or offline) and the lobby. The host is the single source of truth:
/// clients request changes, the host applies them and rebroadcasts the full lobby state.
/// </summary>
public partial class NetworkManager : Node
{
    public const int DefaultPort = 24565;

    public NetMode Mode { get; private set; } = NetMode.None;
    public LobbyState Lobby { get; private set; } = new();
    public bool InMatch { get; set; }
    public int Port { get; private set; } = DefaultPort;
    public string LocalName = "Player";
    public int LocalColor;
    public Appearance LocalLook = Appearance.Default();
    private readonly Rng _botLooks = new((ulong)System.DateTime.Now.Ticks);

    public bool IsHost => Mode is NetMode.Host or NetMode.Offline;
    public long LocalPeer => Multiplayer.GetUniqueId();
    public PlayerInfo LocalPlayer => Lobby.ByPeer(LocalPeer);

    public event Action LobbyChanged;
    public event Action<string> Status;       // status/error text for the menu
    public event Action<string> SessionEnded; // back to menu with a reason
    public event Action<long> PeerLeft;       // host: a client disconnected

    public override void _Ready()
    {
        Multiplayer.PeerConnected += id => Log.Info($"peer connected {id}");
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
        Multiplayer.ConnectedToServer += OnConnectedToServer;
        Multiplayer.ConnectionFailed += () => EndSession("Could not connect to host.");
        Multiplayer.ServerDisconnected += () => EndSession("Host closed the game.");
    }

    // ---------- session control ----------

    public void StartOffline()
    {
        Reset();
        Multiplayer.MultiplayerPeer = new OfflineMultiplayerPeer();
        Mode = NetMode.Offline;
        AddHumanLocal();
    }

    public bool Host(int port)
    {
        Reset();
        var peer = new ENetMultiplayerPeer();
        var err = peer.CreateServer(port, LobbyState.MaxPlayers);
        if (err != Error.Ok)
        {
            Status?.Invoke($"Could not host on port {port}: {err}");
            return false;
        }
        Multiplayer.MultiplayerPeer = peer;
        Mode = NetMode.Host;
        Port = port;
        AddHumanLocal();
        Log.Info($"hosting on {port}");
        return true;
    }

    public bool Join(string address, int port)
    {
        Reset();
        var peer = new ENetMultiplayerPeer();
        var err = peer.CreateClient(address, port);
        if (err != Error.Ok)
        {
            Status?.Invoke($"Could not connect: {err}");
            return false;
        }
        Multiplayer.MultiplayerPeer = peer;
        Mode = NetMode.Client;
        Status?.Invoke($"Connecting to {address}:{port}...");
        Log.Info($"joining {address}:{port}");
        return true;
    }

    public void Leave() => EndSession(null);

    private void EndSession(string reason)
    {
        bool wasActive = Mode != NetMode.None;
        Reset();
        if (wasActive) SessionEnded?.Invoke(reason);
    }

    private void Reset()
    {
        if (Multiplayer.MultiplayerPeer is ENetMultiplayerPeer enet) enet.Close();
        Multiplayer.MultiplayerPeer = new OfflineMultiplayerPeer();
        Mode = NetMode.None;
        Lobby = new LobbyState();
        InMatch = false;
    }

    private void AddHumanLocal()
    {
        Lobby.Players.Add(new PlayerInfo { Slot = 0, PeerId = 1, Name = LocalName, ColorIdx = LocalColor, Look = LocalLook.Clone() });
        LobbyChanged?.Invoke();
    }

    // ---------- host-side lobby edits ----------

    public void AddBot()
    {
        if (!IsHost || InMatch) return;
        int slot = Lobby.FreeSlot();
        if (slot < 0) return;
        int color = Lobby.FreeColor();
        Lobby.Players.Add(new PlayerInfo { Slot = slot, PeerId = 1, IsBot = true, Name = $"Bot {Palette.Names[color]}", ColorIdx = color, Look = Appearance.Random(_botLooks) });
        BroadcastLobby();
    }

    public void RemoveBot(int slot)
    {
        if (!IsHost || InMatch) return;
        Lobby.Players.RemoveAll(p => p.Slot == slot && p.IsBot);
        BroadcastLobby();
    }

    public void SetSettings(MatchSettings s)
    {
        if (!IsHost) return;
        Lobby.Settings = s;
        BroadcastLobby();
    }

    public void SetLocalColor(int color)
    {
        LocalColor = color;
        if (IsHost)
        {
            var me = LocalPlayer;
            if (me != null) me.ColorIdx = color;
            BroadcastLobby();
        }
        else if (Mode == NetMode.Client)
        {
            RpcId(1, MethodName.RequestColor, color);
        }
    }

    public void SetLocalLook(Appearance look)
    {
        LocalLook = look.Clone();
        if (IsHost)
        {
            var me = LocalPlayer;
            if (me == null) return;
            me.Look = look.Clone();
            BroadcastLobby();
        }
        else if (Mode == NetMode.Client)
        {
            RpcId(1, MethodName.RequestLook, look.ToBytes());
        }
    }

    public void BroadcastLobby()
    {
        if (!IsHost) return;
        Rpc(MethodName.SyncLobby, Lobby.Serialize());
    }

    // ---------- connection events ----------

    private void OnConnectedToServer()
    {
        Status?.Invoke("Connected. Joining lobby...");
        RpcId(1, MethodName.RequestJoin, LocalName, LocalColor, LocalLook.ToBytes());
    }

    private void OnPeerDisconnected(long id)
    {
        Log.Info($"peer disconnected {id}");
        if (!IsHost) return;
        if (Lobby.ByPeer(id) == null) return;
        PeerLeft?.Invoke(id);
        Lobby.Players.RemoveAll(p => p.PeerId == id && !p.IsBot);
        BroadcastLobby();
    }

    // ---------- RPCs ----------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestJoin(string name, int color, byte[] look)
    {
        if (!IsHost) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (InMatch || Lobby.FreeSlot() < 0)
        {
            RpcId(sender, MethodName.Rejected, InMatch ? "A match is already in progress." : "Lobby is full.");
            return;
        }
        if (Lobby.Players.Exists(p => p.ColorIdx == color)) color = Lobby.FreeColor();
        name = string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim();
        if (name.Length > 12) name = name[..12];
        Lobby.Players.Add(new PlayerInfo { Slot = Lobby.FreeSlot(), PeerId = sender, Name = name, ColorIdx = color, Look = Appearance.FromBytes(look) });
        BroadcastLobby();
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestColor(int color)
    {
        if (!IsHost) return;
        var p = Lobby.ByPeer(Multiplayer.GetRemoteSenderId());
        if (p == null) return;
        p.ColorIdx = color;
        BroadcastLobby();
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestLook(byte[] look)
    {
        if (!IsHost || InMatch) return;
        var p = Lobby.ByPeer(Multiplayer.GetRemoteSenderId());
        if (p == null) return;
        p.Look = Appearance.FromBytes(look);
        BroadcastLobby();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Rejected(string reason)
    {
        EndSession(reason);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SyncLobby(byte[] data)
    {
        if (!IsHost) Lobby = LobbyState.Deserialize(data);
        LobbyChanged?.Invoke();
    }

    public static string[] LocalAddresses()
    {
        var list = new System.Collections.Generic.List<string>();
        foreach (var a in IP.GetLocalAddresses())
            if (a.Contains('.') && !a.StartsWith("127.") && !a.StartsWith("169.254.")) list.Add(a);
        return list.ToArray();
    }
}
