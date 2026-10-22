using System;
using System.Linq;
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

    /// <summary>The build version (project setting application/config/version). Players must match to share a lobby.</summary>
    public static string Version => (string)ProjectSettings.GetSetting("application/config/version", "dev");

    /// <summary>Router port forwarding for the host. Off for automated test runs.</summary>
    public readonly PortOpener Ports = new();
    public bool UseUpnp = true;
    /// <summary>Test runs (--teams=N, --friendlyfire): the team mode for matches this run hosts (applied at match start).</summary>
    public int TestTeamSize;
    public bool TestFriendlyFire;
    /// <summary>--mode=ID: the game mode for matches this run hosts.</summary>
    public Brawler.Logic.GameMode? TestMode;
    /// <summary>--rounds=N: round wins to take the match, for matches this run hosts (0 = the lobby setting).</summary>
    public int TestRounds;
    /// <summary>--arena=CODE: the custom arena every battle of the matches this run hosts is played on (null = the lobby setting).</summary>
    public byte[] TestArena;

    public NetMode Mode { get; private set; } = NetMode.None;
    public LobbyState Lobby { get; private set; } = new();
    public bool InMatch { get; set; }
    public int Port { get; private set; } = DefaultPort;
    public string LocalName = "Player";
    public int LocalColor;
    public Appearance LocalLook = Appearance.Default();
    private readonly Rng _botLooks = new((ulong)System.DateTime.Now.Ticks);

    /// <summary>Names this game process to a host, so if the connection drops mid-match the host can give us our slot back.</summary>
    public readonly string RejoinKey = Guid.NewGuid().ToString("N");
    private readonly System.Collections.Generic.Dictionary<long, string> _keys = new(); // host: each client's rejoin key

    /// <summary>
    /// Host: asked when someone joins while a match is on, with their peer, rejoin key and name. Returns true if it put
    /// them back into a slot they dropped from; otherwise they're turned away.
    /// </summary>
    public Func<long, string, string, bool> TryRejoin;

    public bool IsHost => Mode is NetMode.Host or NetMode.Offline;
    public long LocalPeer => Multiplayer.GetUniqueId();
    public PlayerInfo LocalPlayer => Lobby.ByPeer(LocalPeer);

    public event Action LobbyChanged;
    public event Action<string> Status;       // status/error text for the menu
    public event Action<string> SessionEnded; // back to menu with a reason
    public event Action<long> PeerLeft;       // host: a client disconnected
    public event Action PortsChanged;         // host: the UPnP attempt finished

    public override void _Ready()
    {
        Multiplayer.PeerConnected += id => Log.Info($"peer connected {id}");
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
        Multiplayer.ConnectedToServer += OnConnectedToServer;
        Multiplayer.ConnectionFailed += () => EndSession("Could not connect to host.");
        Multiplayer.ServerDisconnected += () => EndSession(InMatch
            ? "Lost the connection to the host. If the match is still on, press Join to get back in."
            : "Host closed the game.");
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
        Log.Info($"hosting on {port} (version {Version})");
        if (UseUpnp) Ports.Open(port, () => PortsChanged?.Invoke());
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
        Status?.Invoke(StreamerMode.On ? "Connecting..." : $"Connecting to {address}:{port}...");
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
        _haveMaps.Clear();
        _mapsKey = "";
        Ports.Close();
        if (Multiplayer.MultiplayerPeer is ENetMultiplayerPeer enet) enet.Close();
        Multiplayer.MultiplayerPeer = new OfflineMultiplayerPeer();
        Mode = NetMode.None;
        Lobby = new LobbyState();
        InMatch = false;
        _keys.Clear();
        Pads.Couch.Clear();
        CustomCardLoader.RestoreLocal(); // a client may have been using the host's cards
    }

    private void AddHumanLocal()
    {
        Lobby.Players.Add(new PlayerInfo { Slot = 0, PeerId = 1, Name = LocalName, ColorIdx = LocalColor, Look = LocalLook.Clone() });
        LobbyChanged?.Invoke();
    }

    // ---------- host-side lobby edits ----------

    public void AddBot()
    {
        if (!IsHost || InMatch || !Lobby.HasRoom()) return;
        int slot = Lobby.FreeSlot();
        int color = Lobby.FreeColor();
        Lobby.Players.Add(new PlayerInfo
        {
            Slot = slot, PeerId = 1, IsBot = true, Name = $"Bot {Palette.Names[color]}", ColorIdx = color,
            Team = Lobby.NewcomerTeam(), Look = Appearance.Random(_botLooks),
        });
        BroadcastLobby();
    }

    /// <summary>Moves a player to the other team, if it has room. The host can move anyone; others only themselves and their couch guests.</summary>
    public void SwapTeam(int slot)
    {
        if (InMatch || !Lobby.TeamMode) return;
        if (IsHost) HostSwapTeam(slot);
        else if (Mode == NetMode.Client && Lobby.BySlot(slot) is { IsBot: false } p && p.PeerId == LocalPeer) RpcId(1, MethodName.RequestSwapTeam, slot);
    }

    private void HostSwapTeam(int slot)
    {
        var p = Lobby.BySlot(slot);
        if (p == null || InMatch || !Lobby.TeamMode) return;
        p.Team = Teams.Swap(Lobby.Players.Where(o => o != p).Select(o => o.Team), p.Team, Lobby.Settings.TeamSize);
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

    /// <summary>Host: switch between free-for-all and the team modes, dealing everyone onto fresh teams.</summary>
    public void SetTeamSize(int size)
    {
        if (!IsHost || InMatch) return;
        Lobby.Settings.TeamSize = size;
        if (Lobby.TeamMode) Lobby.DealTeams();
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

    /// <summary>
    /// Host: sends the lobby to every peer. The custom arenas go only to peers that don't have this list yet (it
    /// changed, or they just connected); everyone else gets the lobby without them, so the many small updates (names,
    /// colors, ready) stay small even with 12 big arenas picked.
    /// </summary>
    public void BroadcastLobby()
    {
        if (!IsHost) return;
        var key = string.Join(",", Lobby.Settings.Maps.Select(System.Convert.ToBase64String));
        if (key != _mapsKey) { _mapsKey = key; _haveMaps.Clear(); }
        byte[] full = null, light = null;
        foreach (int peer in Multiplayer.GetPeers())
        {
            bool has = !_haveMaps.Add(peer);
            RpcId(peer, MethodName.SyncLobby, has ? light ??= Lobby.Serialize(withMaps: false) : full ??= Lobby.Serialize());
        }
        SyncLobby(null);
    }

    private string _mapsKey = "";
    private readonly System.Collections.Generic.HashSet<int> _haveMaps = new(); // peers holding the arenas in _mapsKey


    // ---------- local co-op ----------

    /// <summary>
    /// A second (third, fourth) person on this machine joins with their own controller. Their slot is theirs for the
    /// session; the host lists them like anyone else. Returns false if there is no room or the controller is taken.
    /// </summary>
    public bool AddGuest(int device)
    {
        if (Mode == NetMode.None || InMatch || !Lobby.HasRoom()) return false;
        int guest = Pads.Couch.Add(device);
        if (guest < 0) return false;
        string name = GuestName(LocalName, guest);
        var look = Appearance.Random(_botLooks);
        Log.Info($"local player {guest + 1} joined on {Pads.Name(device)}");
        _guestSince[guest] = Time.GetTicksMsec();
        if (IsHost) HostAddGuest(LocalPeer, guest, name, look, RejoinKey);
        else RpcId(1, MethodName.RequestAddGuest, guest, name, look.ToBytes());
        return true;
    }

    private readonly System.Collections.Generic.Dictionary<int, ulong> _guestSince = new();

    /// <summary>Client: the host turned a guest away (the lobby filled up first), so free their controller. Requests still in flight get a few seconds.</summary>
    private void DropRefusedGuests()
    {
        foreach (var (guest, _) in new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<int, int>>(Pads.Couch.Guests))
            if (Lobby.Guest(LocalPeer, guest) == null && (!_guestSince.TryGetValue(guest, out ulong since) || Time.GetTicksMsec() - since > 3000))
                Pads.Couch.Remove(guest);
    }

    public void RemoveGuest(int guest)
    {
        if (InMatch || !Pads.Couch.Remove(guest)) return;
        if (IsHost) HostRemoveGuest(LocalPeer, guest);
        else if (Mode == NetMode.Client) RpcId(1, MethodName.RequestRemoveGuest, guest);
    }

    /// <summary>"Sam" with guest 1 is "Sam 2": the keyboard player is player 1 of their machine.</summary>
    public static string GuestName(string owner, int guest)
    {
        string suffix = $" {guest + 1}";
        owner = string.IsNullOrWhiteSpace(owner) ? "Player" : owner.Trim();
        return owner.Length + suffix.Length > 12 ? owner[..(12 - suffix.Length)] + suffix : owner + suffix;
    }

    private void HostAddGuest(long peer, int guest, string name, Appearance look, string key)
    {
        if (InMatch || !Lobby.HasRoom() || guest is < 1 or > CouchRoster.MaxGuests || Lobby.ByPeer(peer) == null || Lobby.Guest(peer, guest) != null) return;
        Lobby.Players.Add(new PlayerInfo
        {
            Slot = Lobby.FreeSlot(), PeerId = peer, Guest = guest, Name = name, ColorIdx = Lobby.FreeColor(), Look = look,
            Key = key, Team = Lobby.NewcomerTeam(),
        });
        BroadcastLobby();
    }

    private void HostRemoveGuest(long peer, int guest)
    {
        if (InMatch || guest < 1) return;
        Lobby.Players.RemoveAll(p => p.PeerId == peer && !p.IsBot && p.Guest == guest);
        BroadcastLobby();
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestAddGuest(int guest, string name, byte[] look)
    {
        if (!IsHost) return;
        long sender = Multiplayer.GetRemoteSenderId();
        name = string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim();
        if (name.Length > 12) name = name[..12];
        _keys.TryGetValue(sender, out var key);
        HostAddGuest(sender, guest, name, Appearance.FromBytes(look), key);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestRemoveGuest(int guest)
    {
        if (IsHost) HostRemoveGuest(Multiplayer.GetRemoteSenderId(), guest);
    }

    // ---------- connection events ----------

    private void OnConnectedToServer()
    {
        Status?.Invoke("Connected. Joining lobby...");
        // A separate call ahead of RequestJoin, so a host on another version can still read RequestJoin and say so.
        RpcId(1, MethodName.Identify, RejoinKey);
        RpcId(1, MethodName.RequestJoin, Version, LocalName, LocalColor, LocalLook.ToBytes());
    }

    private void OnPeerDisconnected(long id)
    {
        Log.Info($"peer disconnected {id}");
        if (!IsHost) return;
        _keys.Remove(id);
        if (Lobby.ByPeer(id) == null) return;
        PeerLeft?.Invoke(id);
        Lobby.Players.RemoveAll(p => p.PeerId == id && !p.IsBot);
        BroadcastLobby();
    }

    // ---------- RPCs ----------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestJoin(string version, string name, int color, byte[] look)
    {
        if (!IsHost) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (version != Version)
        {
            RpcId(sender, MethodName.Rejected, $"Version mismatch: host has {Version}, you have {version}. Download the same release.");
            return;
        }
        name = string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim();
        if (name.Length > 12) name = name[..12];
        _keys.TryGetValue(sender, out var key);
        if (InMatch && TryRejoin?.Invoke(sender, key, name) == true) return;
        if (InMatch || !Lobby.HasRoom())
        {
            RpcId(sender, MethodName.Rejected, InMatch ? "A match is already in progress." : "Lobby is full.");
            return;
        }
        if (Lobby.Players.Exists(p => p.ColorIdx == color)) color = Lobby.FreeColor();
        SendCards(sender);
        Lobby.Players.Add(new PlayerInfo { Slot = Lobby.FreeSlot(), PeerId = sender, Name = name, ColorIdx = color, Look = Appearance.FromBytes(look), Key = key, Team = Lobby.NewcomerTeam() });
        BroadcastLobby();
    }

    /// <summary>
    /// Host: everyone plays with the host's custom cards. Sent before the lobby (or a rejoin) on the same reliable
    /// channel, so the client has them long before any card pick.
    /// </summary>
    public void SendCards(long peer) => RpcId(peer, MethodName.SyncCards, CustomCards.Serialize(CardRegistry.Custom));

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Identify(string key)
    {
        if (IsHost) _keys[Multiplayer.GetRemoteSenderId()] = key;
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
    private void RequestSwapTeam(int slot)
    {
        if (!IsHost) return;
        // Themselves or one of their couch guests.
        if (Lobby.BySlot(slot) is { IsBot: false } p && p.PeerId == Multiplayer.GetRemoteSenderId()) HostSwapTeam(slot);
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

    /// <summary>Client: the host's custom card definitions replace ours until we leave (Reset restores them).</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SyncCards(byte[] data)
    {
        if (Mode != NetMode.Client) return;
        CustomCardLoader.UseHostCards(data);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SyncLobby(byte[] data)
    {
        if (!IsHost) Lobby = LobbyState.Deserialize(data, Lobby.Settings.Maps);
        if (Mode == NetMode.Client && !InMatch) DropRefusedGuests();
        LobbyChanged?.Invoke();
    }

    public static string[] LocalAddresses()
    {
        var list = new System.Collections.Generic.List<string>();
        foreach (var a in IP.GetLocalAddresses())
            if (a.Contains('.') && !a.StartsWith("127.") && !a.StartsWith("169.254.")) list.Add(a);
        return list.ToArray();
    }

    public override void _ExitTree() => Ports.CloseNow();
}
