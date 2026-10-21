# Playing online: friends on different home networks

One player **hosts** and everyone else **joins** the host. The joiners' computers must be able to reach the
host's computer on **UDP port 24565**. Home routers block that by default, so you need one of these options:

| Option | Setup | Works when | Best for |
|---|---|---|---|
| **A. Tailscale** (recommended) | Everyone installs a free app (5 min) | Always, even behind strict ISPs | Most groups, and the easiest way to get going |
| **B. Automatic port opening** | Nothing to set up. Just click Host | The host's router has UPnP on | Quick games, if your router allows it |
| **C. Manual port forwarding** | The host changes one router setting | The host's ISP gives a real public IP | Regular host with router access |

Only the **host** has to do anything for B and C. For A, everyone installs Tailscale. Pick whoever has the best
connection (wired beats Wi-Fi) as host. The host plays with no lag at all and everyone else plays with their
ping to the host.

Before you start, check that **everyone has the same game version** (bottom-right of the main menu).

---

## Option A: Tailscale (a private network between your computers)

Tailscale links your computers as if they were on the same Wi-Fi, over the internet and encrypted. It doesn't
need router changes and works with any ISP. It's free for personal use.

**Host (do this once):**
1. Go to <https://tailscale.com/download>, install Tailscale, and sign in (Google, Microsoft, GitHub or Apple account).
2. Invite your friends. On <https://login.tailscale.com/admin/machines>, find your computer, click **⋯ → Share...**,
   and send the invite link to each friend. They will see only your computer, nothing else on your network.

**Each friend (do this once):**
1. Install Tailscale from the same page and sign in with any account.
2. Open the share link the host sent and click **Accept**.

**Every time you play:**
1. Make sure Tailscale shows **Connected** on everyone's computer.
2. The host starts Brawler and clicks **Host Game**. The lobby line *"Same network or VPN: …"* lists an address
   starting with **100.** (also shown in the Tailscale app as the computer's IP). That's the host's Tailscale address.
3. The host sends that `100.x.y.z` address to the friends.
4. Each friend types it into **Join a Friend** and clicks **Join**.

> **Alternatives that work the same way:** [ZeroTier](https://www.zerotier.com/) (create a network at
> my.zerotier.com, everyone joins its network ID, and the host ticks **Auth** for each member) or
> [Radmin VPN](https://www.radmin-vpn.com/) (Windows only, where one person creates a network and the others join it by name
> and password). Then join the host's ZeroTier or Radmin address as above.

---

## Option B: automatic port opening (UPnP)

When you click **Host Game**, Brawler asks your router to open UDP port 24565 for you. After a couple
of seconds the lobby shows one of these:

| Lobby says | What to do |
|---|---|
| **Internet: 203.0.113.7:24565 (port opened automatically)** | It worked. Click **Copy** and send the address to your friends. They paste it into **Join a Friend**. |
| **Internet: the router didn't open the port automatically...** | UPnP is off on your router. Turn it on in the router settings (usually under *Advanced → UPnP*), or use Option A or C. |
| **Internet: your ISP shares your public address (CGNAT)...** | Port forwarding can't work on your connection. Use **Option A**. |

The game closes the port again when you leave the lobby or quit.

Streaming? Turn on **Options → Online → Streamer mode** first. The lobby then says *port opened automatically* without
the address, and **Copy invite** gives a code like `J71P-Z4HM-V2T0` to send privately; friends paste it into **Join a
Friend** just like an address.

---

## Option C: manual port forwarding (host only)

### 1. Find your computer's local address
Click **Host Game**. The lobby line *"Same network or VPN: …"* shows an address like `192.168.1.20`. That's
your computer on your home network. (On Windows you can also run `ipconfig` and read *IPv4 Address*.)

### 2. Give your computer a fixed local address
Routers can hand out a different local address after a restart, which would break the forward. In the router's
settings, find **DHCP reservation** (also called *Address reservation* or *Static lease*) and reserve the
address from step 1 for your computer. Skip this if you can't find it, and recheck step 1 whenever forwarding stops working.

### 3. Add the forward in your router
1. Open the router's page in a browser. It's usually <http://192.168.1.1> or <http://192.168.0.1>, and the address
   and password are often on a sticker on the router. ISP apps (Xfinity, Spectrum, BT, etc.) also have this under *Port forwarding*.
2. Find **Port Forwarding** (also called *Virtual Server*, *NAT* or *Applications & Gaming*).
3. Add a rule:

   | Field | Value |
   |---|---|
   | Name / Service | Brawler |
   | Protocol | **UDP** (or *Both*) |
   | External / WAN port | **24565** |
   | Internal / LAN port | **24565** |
   | Internal IP / Device | your address from step 1 |

4. Save, and restart the router if it asks.

### 4. Find your public address and send it
Search "what is my IP" in any browser, or open <https://ifconfig.me>. Send that address (for example `203.0.113.7`)
to your friends. They type it into **Join a Friend**.

### 5. Check for CGNAT
If friends still can't connect, compare the public address from step 4 with the **WAN / Internet IP** on your
router's status page. If they differ, or the router's WAN IP starts with `100.64`–`100.127`, `10.` or `192.168.`,
your ISP shares one public address among many customers (CGNAT) and forwarding can't work. Use **Option A**,
or ask your ISP for a public IP (some give one free on request).

Using a different port is fine. The host types it in the port box next to **Join** before clicking **Host Game**,
forwards that port instead, and friends join with `address:port`.

---

## Firewall

The host's computer must allow Brawler through its firewall. Joiners usually don't need to change anything.

- **Windows:** the first time you host, Windows asks. Tick **Private and Public** and click **Allow**. If you
  missed it, open **Windows Security → Firewall & network protection → Allow an app through firewall →
  Change settings**, find Brawler (or click *Allow another app...* and pick `Brawler.exe`), and tick both boxes.
- **macOS:** click **Allow** when asked about incoming connections. Otherwise go to **System Settings →
  Network → Firewall → Options** and set Brawler to *Allow incoming connections*.
- **Linux:** if you use ufw, run `sudo ufw allow 24565/udp`.

Third-party antivirus suites (Norton, McAfee, Avast, ...) have their own firewall, where you allow Brawler the same way.

---

## Troubleshooting

| Problem | Fix |
|---|---|
| **"Could not connect to host."** | Check that the host is in the lobby (not mid-match) and that the address is right. Try each option's checks: is Tailscale connected on both ends? Is the firewall rule there? Is the forward set to **UDP**? Is the host behind CGNAT? |
| **"Version mismatch"** | Everyone downloads the same release. The version is in the bottom-right of the menu. |
| **"A match is already in progress."** | The host must finish or leave the current match. Joining happens in the lobby. |
| **"Lobby is full."** | A lobby holds 4 players. The host can remove a bot to free a slot. |
| **"Could not host on port 24565"** | Another copy of Brawler (or another program) is using the port. Close it, or host on a different port. |
| **Works on the same Wi-Fi but not over the internet** | The firewall is fine, so it's the router. Use Option A, or recheck Option C. |
| **Laggy** | Make the player with the best (ideally wired) connection the host, and close downloads or streams. With Tailscale, check that `tailscale status` shows a *direct* connection rather than *relay*. |

If you're stuck, the host's log in the `logs` folder (see *Updating* in the install guide) has lines like `hosting on 24565`,
`upnp: Opened ip=…` and `peer connected …` that show how far things got.

**Safety:** only share your public address with people you trust. A forwarded port exposes only Brawler while it's
hosting. Remove the router rule when you no longer need it. With Tailscale, unshare your machine from the admin page.
