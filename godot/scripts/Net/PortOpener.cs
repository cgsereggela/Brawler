using System.Threading.Tasks;
using Brawler.Core;
using Brawler.Logic;
using Godot;

namespace Brawler.Net;

public enum PortState { Idle, Working, Opened, NoUpnp, Failed }

/// <summary>
/// Asks the home router (via UPnP) to forward the host's UDP port, and reads back the router's public address.
/// This runs in the background, because discovery blocks for a couple of seconds. Many routers have UPnP off, and some
/// ISPs put homes behind carrier-grade NAT, where no forward can work. Both cases are reported so the lobby can
/// point people at the online-play guide.
/// </summary>
public sealed class PortOpener
{
    public PortState State { get; private set; } = PortState.Idle;
    public string PublicIp { get; private set; } = "";
    /// <summary>The router's public address is itself private or shared, so the ISP uses CGNAT and forwarding can't help.</summary>
    public bool BehindCgnat { get; private set; }
    public string Detail { get; private set; } = "";

    private Upnp _upnp;
    private int _port;
    private int _generation;

    /// <summary>Starts a background attempt. <paramref name="done"/> runs on the main thread when it finishes.</summary>
    public void Open(int port, System.Action done)
    {
        Close();
        int gen = ++_generation;
        _port = port;
        State = PortState.Working;
        PublicIp = Detail = "";
        BehindCgnat = false;
        Task.Run(() =>
        {
            var upnp = new Upnp();
            var state = PortState.Failed;
            string ip = "", detail = "";
            int err = upnp.Discover(2000, 2, "InternetGatewayDevice");
            if (err != (int)Upnp.UpnpResult.Success || upnp.GetGateway() is not { } gw || !gw.IsValidGateway())
            {
                state = PortState.NoUpnp;
                detail = err != (int)Upnp.UpnpResult.Success ? ((Upnp.UpnpResult)err).ToString() : "no gateway";
            }
            else
            {
                ip = upnp.QueryExternalAddress() ?? "";
                int map = upnp.AddPortMapping(port, port, "Brawler", "UDP", 0);
                state = map == (int)Upnp.UpnpResult.Success ? PortState.Opened : PortState.Failed;
                detail = ((Upnp.UpnpResult)map).ToString();
            }
            Callable.From(() =>
            {
                if (gen != _generation) { if (state == PortState.Opened) Unmap(upnp, port); return; }
                _upnp = state == PortState.Opened ? upnp : null;
                State = state;
                PublicIp = ip;
                Detail = detail;
                BehindCgnat = NetAddress.IsShared(ip);
                Log.Info($"upnp: {state} ip={ip} ({detail}){(BehindCgnat ? " cgnat" : "")}");
                done?.Invoke();
            }).CallDeferred();
        });
    }

    /// <summary>Removes the forward if we made one. Safe to call any time.</summary>
    public void Close()
    {
        _generation++;
        if (_upnp != null)
        {
            var upnp = _upnp;
            int port = _port;
            _upnp = null;
            Task.Run(() => Unmap(upnp, port));
        }
        State = PortState.Idle;
    }

    /// <summary>Blocking variant for shutdown, so the forward doesn't outlive the game.</summary>
    public void CloseNow()
    {
        _generation++;
        if (_upnp != null) Unmap(_upnp, _port);
        _upnp = null;
        State = PortState.Idle;
    }

    /// <summary>Read-only check for troubleshooting: finds the router and reads its public address, without forwarding anything.</summary>
    public static string Probe()
    {
        var upnp = new Upnp();
        int err = upnp.Discover(2000, 2, "InternetGatewayDevice");
        if (err != (int)Upnp.UpnpResult.Success) return $"no UPnP router found ({(Upnp.UpnpResult)err})";
        if (upnp.GetGateway() is not { } gw || !gw.IsValidGateway()) return $"found {upnp.GetDeviceCount()} UPnP device(s) but no internet gateway";
        string ip = upnp.QueryExternalAddress() ?? "";
        return $"UPnP gateway found, public address {(ip == "" ? "unknown" : ip)}{(NetAddress.IsShared(ip) ? " (shared: ISP uses CGNAT, forwarding can't work)" : "")}";
    }

    private static void Unmap(Upnp upnp, int port)
    {
        int r = upnp.DeletePortMapping(port, "UDP");
        Log.Info($"upnp: removed forward for {port} ({(Upnp.UpnpResult)r})");
    }

}
