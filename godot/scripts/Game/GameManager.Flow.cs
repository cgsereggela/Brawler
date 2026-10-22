using System;
using System.Collections.Generic;
using System.Linq;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Logic;
using Brawler.Net;
using Godot;

namespace Brawler.Game;

/// <summary>Match flow: starting matches and battles, resolving them, the kill cam, card picks, rematches and
/// players leaving or rejoining. The host drives it; the RPCs here carry each step to everyone.</summary>
public partial class GameManager
{
    // =====================================================================
    // Host: match flow
    // =====================================================================

    public void HostStartMatch()
    {
        if (!IsHost) return;
        if (Net.TestTeamSize > 0 && Net.Lobby.Settings.TeamSize != Net.TestTeamSize)
        {
            Net.Lobby.Settings.TeamSize = Net.TestTeamSize; // --teams=N: deal the test lobby onto teams
            Net.Lobby.DealTeams();
        }
        if (Net.TestFriendlyFire) Net.Lobby.Settings.FriendlyFire = true;
        if (Net.TestDraft) Net.Lobby.Settings.Draft = true;
        if (Net.TestRounds > 0) Net.Lobby.Settings.RoundsToWin = Net.TestRounds; // --rounds=N: a shorter test match
        if (Net.TestArena != null) Net.Lobby.Settings.Maps = new() { Net.TestArena }; // --arena=CODE
        if (Net.TestMode is { } mode)
        {
            Net.Lobby.Settings.Mode = mode; // --mode=ID
            if (GameModes.Problem(mode, Net.Lobby.Settings.TeamSize) != null)
            {
                Net.Lobby.Settings.TeamSize = Math.Clamp((Net.Lobby.Players.Count + 1) / 2, 2, 4); // Capture the Flag without --teams: even teams
                Net.Lobby.DealTeams();
            }
        }
        if (Net.Lobby.StartProblem() is { } problem)
        {
            Log.Info($"can't start: {problem}");
            return;
        }
        Net.InMatch = true;
        if (Stats.Mode != "sim") Stats.Mode = Net.Mode == NetMode.Host ? "host" : "offline";
        if (LavaOverride >= 0) Net.Lobby.Settings.LavaAfter = LavaOverride;
        if (RareOverride >= 0) Net.Lobby.Settings.RareChance = RareOverride;
        Rpc(MethodName.BeginMatch, Net.Lobby.Serialize());
        _arenaTurn = 0;
        if (Settings.Draft && Mode is not (GameMode.GunGame or GameMode.Survival)) HostBeginDraft(); // Gun Game hands out its own cards, Survival deals the bots theirs
        else HostStartBattle();
    }

    private void HostStartBattle()
    {
        uint seed = HostRng.NextUInt();
        // Custom arenas take turns: the seed says whose turn it is (see LayoutFor), so the battle RPC stays the same.
        uint n = (uint)Settings.Maps.Count;
        if (n > 0)
        {
            seed = seed / n * n;
            if (seed > uint.MaxValue - n) seed -= n;
            seed += (uint)(_arenaTurn++ % n);
        }
        Rpc(MethodName.StartBattle, (int)seed, ScoresBytes());
    }

    private int _arenaTurn; // host: battles started this match, for the custom arenas' turns

    /// <summary>
    /// A battle's arena on every peer: with custom arenas in the lobby, the one whose turn the seed carries (a code that
    /// doesn't read falls back to a generated arena); otherwise the seed's generated arena.
    /// </summary>
    public MapLayout LayoutFor(int seed, int players, out string name, out MapDesign custom)
    {
        var maps = Settings.Maps;
        name = null;
        custom = null;
        if (maps.Count > 0 && MapCode.Unpack(maps[(int)((uint)seed % (uint)maps.Count)]) is { } design)
        {
            custom = design;
            name = design.Name != "" ? design.Name : null;
            return design.Build(players);
        }
        return MapGenerator.Generate(seed, players);
    }

    /// <summary>What the HUD calls this battle's arena: a custom arena's name, else its theme.</summary>
    public string ArenaName { get; private set; } = "";

    /// <summary>This battle's custom arena as a map code (to save or share from the overlay), or null for a generated one.</summary>
    public string ArenaCode { get; private set; }

    /// <summary>
    /// The level editor's framing: the camera fixed on the whole arena drawn at <c>Zoom</c> with its top-left corner at
    /// <c>Corner</c> on screen (base viewport pixels), or null for the match camera.
    /// </summary>
    public (Vector2 Corner, float Zoom)? EditorView
    {
        get => _editorView;
        set
        {
            _editorView = value;
            Visible = value != null || Phase != Phase.Idle;
        }
    }
    private (Vector2 Corner, float Zoom)? _editorView;

    private void HostResolveBattle()
    {
        int winner = _pendingWinner;
        bool roundWon = false, matchWon = false;
        // Survival: unless the humans cleared the wave, the bots take it (everyone falling at once included).
        if (Mode == GameMode.Survival && (winner < 0 || TeamOf(winner) != 0))
            winner = Slots.FirstOrDefault(p => p != null && p.Info.IsBot)?.Slot ?? winner;
        if (winner >= 0 && Slots[winner] != null)
        {
            var w = Slots[winner];
            var side = SideOf(winner).ToList(); // in team modes the whole team scores together
            if (Mode == GameMode.Survival)
            {
                // Each wave cleared is a point for every human; clearing the last wins, and losing one ends the run.
                roundWon = true;
                if (TeamOf(winner) == 0) foreach (var p in side) p.RoundWins++;
                matchWon = TeamOf(winner) != 0 || w.RoundWins >= (Net.TestRounds > 0 ? Net.TestRounds : GameModes.SurvivalWaves); // --rounds=N: a shorter test run
            }
            else if (Mode == GameMode.GunGame)
            {
                // Finishing the ladder wins the whole match.
                w.RoundWins++;
                roundWon = matchWon = true;
            }
            else
            {
                foreach (var p in side) p.BattleWins++;
                if (w.BattleWins >= Settings.BattlesPerRound)
                {
                    roundWon = true;
                    foreach (var p in side) p.RoundWins++;
                    foreach (var p in Slots) if (p != null) p.BattleWins = 0;
                    if (w.RoundWins >= Settings.RoundsToWin) matchWon = true;
                }
            }
        }
        Stats.BattleEnd(winner, roundWon, Active());
        Log.Info($"arena: {Arena.Summary}");
        if (matchWon) HostSendAwards(); // ahead of BattleOver, so the match-over screen has them
        Rpc(MethodName.BattleOver, winner, ScoresBytes(), roundWon, matchWon);
        if (matchWon)
        {
            Stats.MatchEnd(winner, true, Slots.Where(p => p != null));
            return;
        }
        if (roundWon) HostAfter(1.6f, () => HostBeginCardPick(winner));
        else HostAfter(1.4f, HostStartBattle);
    }

    private void HostBeginCardPick(int roundWinner)
    {
        var w = new Wire();
        var entries = new List<PickEntry>();
        foreach (var p in Active())
        {
            if (Mode == GameMode.Survival && p.Info.IsBot) continue; // the next wave deals the bots their cards
            bool won = p.Slot == roundWinner || Allies(p.Slot, roundWinner);
            int count = Mode == GameMode.Survival ? Settings.LoserChoices : won ? Settings.WinnerChoices : Settings.LoserChoices;
            var ids = count > 0 ? CardRegistry.RollOptions(HostRng, count, Settings.DisabledCards, Settings.RareChance / 100f) : null;
            if (ids is not { Count: > 0 }) continue;
            var deal = Settings.CursedDeals ? CardRegistry.RollDeal(HostRng, Settings.DisabledCards, ids, Settings.RareChance / 100f) : null;
            entries.Add(new PickEntry(p.Slot, ids, deal));
        }
        if (entries.Count == 0)
        {
            HostStartBattle();
            return;
        }
        WritePicks(w, entries);
        _hostPicked.Clear();
        _botPickT.Clear();
        _pickTimeoutT = PickTimeout;
        int completedRounds = Slots.Where(p => p != null).Sum(p => p.RoundWins);
        foreach (var (slot, ids, deal) in entries)
        {
            bool bot = IsBotPlayer(Slots[slot]);
            if (bot) _botPickT[slot] = HostRng.Range(0.5f, 1.5f);
            Stats.PickOffered(completedRounds, Slots[slot], bot, slot == roundWinner || Allies(slot, roundWinner), ids, deal);
        }
        Rpc(MethodName.BeginCardPick, w.Done());
    }

    /// <param name="deal">Take the slot's cursed deal: <paramref name="cardId"/> must be its rare card, and its curse comes too.</param>
    private void HostApplyPick(int slot, int cardId, bool deal = false, bool timedOut = false)
    {
        if (Phase != Phase.CardPick || _hostPicked.Contains(slot)) return;
        int curse = -1;
        if (deal)
        {
            if (!PickDeals.TryGetValue(slot, out var d) || d.Rare != cardId) return;
            curse = d.Curse;
        }
        else if (!PickOptions.TryGetValue(slot, out var opts) || !opts.Contains(cardId)) return;
        _hostPicked.Add(slot);
        if (_draftPool != null) _draftPool.Remove(cardId); // drafted picks stay out of the card stats: nobody was offered a normal hand
        else Stats.Picked(slot, cardId, timedOut, curse);
        Rpc(MethodName.CardPicked, slot, cardId, curse);
        HostCheckPicksDone();
    }

    private bool _lastPickCombo;

    private void HostCheckPicksDone()
    {
        bool done = PickOptions.Keys.All(s => _hostPicked.Contains(s) || Slots[s] == null || Slots[s].Left);
        if (done && _draftPool != null && _hostNext == null)
        {
            HostAfter(_lastPickCombo ? 1.8f : 0.8f, HostDraftTurn);
            return;
        }
        // A little longer when the last pick found a combo, so its popup can be read.
        if (done && _hostNext == null) HostAfter(_lastPickCombo ? 1.8f : 0.9f, HostStartBattle);
    }

    // ---------- draft mode ----------

    private List<int> _draftPool;     // host, while drafting: the cards still in the shared pool
    private Queue<int> _draftOrder;   // host, while drafting: whose turn comes next

    /// <summary>True while the players draft their opening cards (a card pick before the first battle).</summary>
    public bool Drafting => Phase == Phase.CardPick && BattleNumber == 0;

    /// <summary>Host: rolls the shared pool and starts the first turn. The turn order is shuffled, then snakes (see Draft).</summary>
    private void HostBeginDraft()
    {
        var slots = Active().Where(p => !p.Left).Select(p => p.Slot).ToList();
        for (int i = slots.Count - 1; i > 0; i--)
        {
            int j = HostRng.Range(0, i);
            (slots[i], slots[j]) = (slots[j], slots[i]);
        }
        _draftPool = CardRegistry.RollOptions(HostRng, Draft.PoolSize(slots.Count), Settings.DisabledCards, Settings.RareChance / 100f);
        _draftOrder = new Queue<int>(Draft.Order(slots, Draft.PicksEach(slots.Count)));
        Log.Info($"draft: {_draftPool.Count} cards, {_draftOrder.Count} picks");
        HostDraftTurn();
    }

    /// <summary>Host: offers the whole remaining pool to the next drafter, or starts the first battle once everyone has drafted.</summary>
    private void HostDraftTurn()
    {
        while (_draftOrder.Count > 0 && Slots[_draftOrder.Peek()] is not { Left: false }) _draftOrder.Dequeue();
        if (_draftOrder.Count == 0 || _draftPool.Count == 0)
        {
            _draftPool = null;
            HostStartBattle();
            return;
        }
        int slot = _draftOrder.Dequeue();
        var w = new Wire();
        WritePicks(w, new[] { new PickEntry(slot, new List<int>(_draftPool), null) });
        _hostPicked.Clear();
        _botPickT.Clear();
        _pickTimeoutT = Draft.TurnTime;
        if (IsBotPlayer(Slots[slot])) _botPickT[slot] = HostRng.Range(0.6f, 1.4f);
        Rpc(MethodName.BeginCardPick, w.Done());
    }

    public void HostReturnToLobby()
    {
        if (!IsHost) return;
        Rpc(MethodName.EndMatch);
        Net.BroadcastLobby();
    }

    // ---------- rematch vote (match-over screen) ----------

    /// <summary>Slots that voted for a rematch. Bots don't vote; the rematch starts once every human still here has.</summary>
    public readonly HashSet<int> RematchVotes = new();

    /// <summary>The humans whose vote a rematch waits on.</summary>
    public IEnumerable<MatchPlayer> RematchVoters() => Active().Where(p => !p.Info.IsBot);

    public bool CanRematch => Phase == Phase.MatchOver && Active().Count() >= 2 && Net.Lobby.StartProblem() == null;

    public void LocalVoteRematch(bool yes)
    {
        if (LocalMatchPlayer == null || Phase != Phase.MatchOver) return;
        // Couch guests vote with the keyboard player: they share the screen and its buttons.
        foreach (var p in LocalHumans().ToList())
            if (IsHost) HostVoteRematch(p.Slot, yes);
            else RpcId(1, MethodName.RequestRematch, p.Slot, yes);
    }

    private void HostVoteRematch(int slot, bool yes)
    {
        if (Phase != Phase.MatchOver || _hostNext != null) return;
        if (yes) RematchVotes.Add(slot);
        else RematchVotes.Remove(slot);
        HostSyncRematch();
    }

    /// <summary>Host: share the votes, and start the rematch once everyone still here is in.</summary>
    private void HostSyncRematch()
    {
        RematchVotes.RemoveWhere(s => Slots[s] == null || Slots[s].Left);
        var w = new Wire().U8(RematchVotes.Count);
        foreach (var s in RematchVotes) w.U8(s);
        Rpc(MethodName.SyncRematchVotes, w.Done());
        if (CanRematch && RematchVoters().All(p => RematchVotes.Contains(p.Slot)))
        {
            Log.Info("rematch: everyone is in");
            HostAfter(1.0f, HostStartMatch); // a beat to see the last vote land
        }
    }

    private void HostAfter(float seconds, Action a)
    {
        _hostNext = a;
        _hostNextT = seconds;
    }

    private void OnPeerLeft(long peer)
    {
        if (Phase == Phase.Idle) return;
        // Their couch guests go with them.
        var gone = Slots.Where(p => p != null && !p.Info.IsBot && !p.Left && p.Info.PeerId == peer).ToList();
        if (gone.Count == 0) return;
        if (KillCam.Playing) Rpc(MethodName.EndKillCam); // back to the real state before anything changes
        foreach (var mp in gone)
        {
            if (mp.Avatar != null && mp.Avatar.Alive) HostKill(mp.Avatar, KillCause.Left);
            Rpc(MethodName.PlayerLeft, mp.Slot);
        }
        if (Phase == Phase.CardPick) HostCheckPicksDone();
        if (Phase == Phase.MatchOver) HostSyncRematch(); // their vote goes, and the rest may now all be in

        // Too few left to play on: hold the match (see HostRejoinWaitTick) so whoever dropped can rejoin.
        if (Active().Count() < 2 && Phase != Phase.MatchOver && _rejoinWaitT <= 0)
        {
            _rejoinWaitT = RejoinWait;
            _rejoinWaitShown = -1;
        }
    }

    /// <summary>
    /// Host: someone joined while the match is on. If they dropped out of it, they get their slot back with its cards and
    /// score: matched by their game's rejoin key, or by name when exactly one dropped player has it (they restarted the
    /// game). They sit out the rest of a battle already under way and play from the next one, or pick now if a card
    /// pick is on.
    /// </summary>
    private bool HostTryRejoin(long peer, string key, string name)
    {
        if (Phase is Phase.Idle or Phase.MatchOver) return false;
        var dropped = Slots.Where(p => p != null && p.Left && !p.Info.IsBot).ToList();
        var mp = dropped.FirstOrDefault(p => key != null && p.Info.Key == key && p.Info.Guest == 0);
        if (mp == null && dropped.Count(p => p.Info.Name == name && p.Info.Guest == 0) == 1) mp = dropped.First(p => p.Info.Name == name && p.Info.Guest == 0);
        if (mp == null) return false;

        // Their couch guests (who share the rejoin key) come back with them.
        var back = new List<MatchPlayer> { mp };
        if (key != null) back.AddRange(dropped.Where(p => p != mp && p.Info.Guest > 0 && p.Info.Key == key));
        foreach (var p in back)
        {
            p.Info.PeerId = peer;
            p.Info.Key = key;
            p.Left = false;
            p.Input = new RemoteInput();
        }
        Net.SendCards(peer);
        RpcId(peer, MethodName.Rejoined, RejoinBytes());
        foreach (var p in back)
        {
            Rpc(MethodName.PlayerBack, p.Slot, peer);
            // Back in the lobby list too, for after the match.
            if (Net.Lobby.BySlot(p.Slot) == null)
                Net.Lobby.Players.Add(new PlayerInfo { Slot = p.Slot, PeerId = peer, Guest = p.Info.Guest, Name = p.Info.Name, ColorIdx = p.Info.ColorIdx, Look = p.Info.Look.Clone(), Key = key });
        }
        Net.Lobby.Players.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        Net.BroadcastLobby();
        Log.Info($"{mp.Info.Name} rejoined slot {mp.Slot} with {mp.Cards.Count} cards");
        return true;
    }

    /// <summary>Host, while a match is short-handed: count down, and end the match if nobody is back in time.</summary>
    private void HostRejoinWaitTick(float dt)
    {
        if (Active().Count() >= 2 || Phase == Phase.MatchOver)
        {
            _rejoinWaitT = 0;
            return;
        }
        _rejoinWaitT -= dt;
        if (_rejoinWaitT <= 0)
        {
            _rejoinWaitT = 0;
            _hostNext = null;
            var remaining = Active().ToList();
            int winner = remaining.Count == 1 ? remaining[0].Slot : -1;
            Stats.MatchEnd(winner, false, Slots.Where(p => p != null));
            HostSendAwards();
            Rpc(MethodName.BattleOver, winner, ScoresBytes(), true, true);
            return;
        }
        int secs = Mathf.CeilToInt(_rejoinWaitT);
        if (secs != _rejoinWaitShown)
        {
            _rejoinWaitShown = secs;
            Rpc(MethodName.WaitingForRejoin, secs);
        }
    }

    // =====================================================================
    // Local actions (from UI)
    // =====================================================================

    /// <param name="deal">Take the cursed deal (its rare card is <paramref name="cardId"/>) instead of an ordinary card.</param>
    /// <param name="who">Which player at this machine is picking (default: the keyboard player).</param>
    public void LocalPick(int cardId, bool deal = false, MatchPlayer who = null)
    {
        who ??= LocalMatchPlayer;
        if (!IsLocalHuman(who)) return;
        if (IsHost) HostApplyPick(who.Slot, cardId, deal);
        else RpcId(1, MethodName.RequestPick, who.Slot, cardId, deal);
    }

    // ---------- kill cam ----------

    private int _lastVictim = -1, _lastKiller = -1;
    private bool _killCamWait; // host: a kill cam is on; the battle resolves once it ends
    private readonly HashSet<int> _killCamSkips = new();
    /// <summary>This peer asked to skip the kill cam on screen.</summary>
    public bool SkipAsked { get; private set; }
    /// <summary>How many have asked to skip the kill cam on screen (it ends once every human has).</summary>
    public int KillCamSkipVotes { get; private set; }

    /// <summary>Host: replay the battle's final kill everywhere, then resolve the battle (straight away if there's none).</summary>
    private void HostKillCamOrResolve()
    {
        // Not in the respawn modes: the last kill there rarely decided anything.
        if (Settings.KillCam && !DisableKillCam && _lastVictim >= 0 && !GameModes.Respawns(Mode))
        {
            _killCamSkips.Clear();
            Rpc(MethodName.PlayKillCam, _lastVictim, _lastKiller);
            if (KillCam.Playing)
            {
                _killCamWait = true;
                return;
            }
        }
        HostResolveBattle();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PlayKillCam(int victim, int killer)
    {
        SkipAsked = false;
        KillCamSkipVotes = 0;
        if (!KillCam.Start(victim, killer)) return;
        Log.Info($"kill cam: {NameOf(victim)}{(killer >= 0 ? $" by {NameOf(killer)}" : "")}");
        KillCamStarted?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void EndKillCam()
    {
        if (KillCam.Playing) Log.Info($"kill cam ended early at {KillCam.Progress:P0}");
        KillCam.Stop();
    }

    /// <summary>The local player wants to skip the kill cam.</summary>
    public void SkipKillCam()
    {
        if (!KillCam.Playing || SkipAsked || LocalMatchPlayer == null) return;
        SkipAsked = true;
        // One skip speaks for everyone at this machine.
        if (IsHost) foreach (var p in LocalHumans().ToList()) HostSkipKillCam(p.Slot);
        else RpcId(1, MethodName.RequestSkipKillCam);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestSkipKillCam()
    {
        if (!IsHost) return;
        long sender = Multiplayer.GetRemoteSenderId();
        foreach (var p in Slots.Where(s => s != null && !s.Info.IsBot && !s.Left && s.Info.PeerId == sender).ToList())
            HostSkipKillCam(p.Slot);
    }

    private void HostSkipKillCam(int slot)
    {
        if (!_killCamWait) return;
        _killCamSkips.Add(slot);
        Rpc(MethodName.KillCamSkipped, _killCamSkips.Count);
        HostCheckKillCamSkips();
    }

    private void HostCheckKillCamSkips()
    {
        var humans = Active().Where(p => !p.Info.IsBot).ToList();
        if (KillCam.Playing && humans.Count > 0 && humans.All(p => _killCamSkips.Contains(p.Slot))) Rpc(MethodName.EndKillCam);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void KillCamSkipped(int votes) => KillCamSkipVotes = votes;

    /// <summary>Humans who could skip the kill cam (the HUD shows votes out of this).</summary>
    public int KillCamVoters => Active().Count(p => !p.Info.IsBot);

    private float _camZoom = 1;
    private Vector2 _camFocus = new Vector2(MapLayout.Width, MapLayout.Height) / 2;

    /// <summary>The camera closes in on the kill while the kill cam plays and eases back out after.</summary>
    private void FrameKillCam(float dt)
    {
        var center = new Vector2(MapLayout.Width, MapLayout.Height) / 2;
        var focus = center;
        float zoom = 1;
        if (KillCam.Playing)
        {
            var (at, span) = KillCam.Framing();
            focus = at;
            zoom = Mathf.Clamp(Mathf.Min(MapLayout.Width / (span.X + 320), MapLayout.Height / (span.Y + 200)), 1.35f, 2f);
        }
        // Real time (the replay slows delta down), so the camera keeps up at any speed.
        float real = dt / Mathf.Max(0.05f, (float)Engine.TimeScale);
        float k = 1f - Mathf.Exp(-(KillCam.Playing ? 5f : 7f) * real);
        _camZoom = Mathf.Lerp(_camZoom, zoom, k);
        _camFocus = _camFocus.Lerp(focus, k);
        // Keep the view inside the arena.
        var half = new Vector2(MapLayout.Width, MapLayout.Height) / (2 * _camZoom);
        _cam.Position = new Vector2(Mathf.Clamp(_camFocus.X, half.X, MapLayout.Width - half.X), Mathf.Clamp(_camFocus.Y, half.Y, MapLayout.Height - half.Y));
    }

    private void HostPickTick(float dt)
    {
        _pickTimeoutT -= dt;
        foreach (var slot in _botPickT.Keys.ToList())
        {
            _botPickT[slot] -= dt;
            if (_botPickT[slot] <= 0)
            {
                _botPickT.Remove(slot);
                var (card, deal) = BotChooseCard(slot, PickOptions[slot]);
                HostApplyPick(slot, card, deal);
            }
        }
        if (_pickTimeoutT <= 0)
        {
            foreach (var (slot, opts) in PickOptions)
                if (!_hostPicked.Contains(slot)) HostApplyPick(slot, opts[HostRng.Range(0, opts.Count - 1)], timedOut: true);
            _pickTimeoutT = float.MaxValue;
        }
    }

    private float _autoPickT = -1;

    private void ClientAutopilotPick(float dt)
    {
        if (!AutopilotLocal) return;
        // Everyone at this machine in turn: the keyboard player, then the couch guests.
        var me = LocalHumans().FirstOrDefault(p => !Picked.Contains(p.Slot) && PickOptions.ContainsKey(p.Slot));
        if (me == null) return;
        var opts = PickOptions[me.Slot];
        if (_autoPickT < 0) _autoPickT = 0.8f;
        _autoPickT -= dt;
        if (_autoPickT > 0) return;
        _autoPickT = -1;
        // Like a bot: a combo card when there is one, mostly, so autotests see combos found.
        var combos = opts.Where(id => Combos.Unlocks(me.Cards, id).Count > 0).ToList();
        var from = combos.Count > 0 && GD.Randf() < 0.75f ? combos : opts;
        // Now and then the cursed deal, so autotests take curses too.
        if (combos.Count == 0 && PickDeals.TryGetValue(me.Slot, out var deal) && GD.Randf() < 0.3f) LocalPick(deal.Rare, deal: true, me);
        else LocalPick(from[(int)(GD.Randi() % (uint)from.Count)], who: me);
    }

    /// <summary>
    /// Bots usually take a card that completes a combo when one is offered; otherwise they sometimes gamble on the
    /// cursed deal, and take any card the rest of the time.
    /// </summary>
    private (int Card, bool Deal) BotChooseCard(int slot, List<int> opts)
    {
        var combos = opts.Where(id => Combos.Unlocks(Slots[slot].Cards, id).Count > 0).ToList();
        if (combos.Count > 0 && HostRng.Range(0f, 1f) < 0.75f) return (combos[HostRng.Range(0, combos.Count - 1)], false);
        if (PickDeals.TryGetValue(slot, out var deal) && HostRng.Range(0f, 1f) < BotDealChance) return (deal.Rare, true);
        return (opts[HostRng.Range(0, opts.Count - 1)], false);
    }

    /// <summary>How often a bot without a combo to chase takes the cursed deal.</summary>
    private const float BotDealChance = 0.3f;

    // =====================================================================
    // RPCs: host → everyone
    // =====================================================================

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BeginMatch(byte[] lobbyData)
    {
        var lobby = LobbyState.Deserialize(lobbyData);
        Settings = lobby.Settings;
        Array.Clear(Slots);
        LocalMatchPlayer = null;
        LocalGuests.Clear();
        foreach (var info in lobby.Players)
        {
            if (Settings.Mode == GameMode.Survival) info.Team = info.IsBot ? 1 : 0; // the humans against the bots
            var mp = new MatchPlayer { Info = info };
            if (IsHost) info.Key = Net.Lobby.BySlot(info.Slot)?.Key; // keys aren't sent, so take them from our own lobby
            if (AttachLocal(mp)) { }
            else if (IsHost)
            {
                mp.Input = info.IsBot ? new BotBrain(this, info.Skill) : new RemoteInput();
            }
            Slots[info.Slot] = mp;
        }
        BattleNumber = 0;
        MatchWinner = -1;
        RematchVotes.Clear();
        MatchAwards.Clear();
        Book.Reset();
        Net.InMatch = true;
        Visible = true;
        if (IsHost) Stats.MatchStart(Settings, Active(), IsBotPlayer);
        Log.Info($"match begins: {lobby.Players.Count} players, {GameModes.Name(Mode)}, {Teams.ModeName(Settings.TeamSize)}{(TeamMode && Settings.FriendlyFire ? " (friendly fire)" : "")}, battles/round {Settings.BattlesPerRound}, rounds to win {Settings.RoundsToWin}");
        CareerBook.MatchBegan();
        MatchBegan?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void StartBattle(int seed, byte[] scores)
    {
        ApplyScores(scores);
        BattleNumber++;
        // Gun Game: everyone starts the match on the ladder's first card, and holds only that.
        if (Mode == GameMode.GunGame && BattleNumber == 1)
            foreach (var p in Slots) if (p != null) SetGunLevel(p, 0);
        foreach (var c in _avatars.GetChildren()) c.QueueFree();
        foreach (var p in Slots) if (p != null) p.Avatar = null;
        Bullets.Clear();
        Fx.Clear();
        if (DebugCards != null && !Networked)
            foreach (var p in Active())
            {
                p.Cards.Clear();
                p.Cards.AddRange(DebugCards(p.Slot));
                p.RecomputeStats();
            }

        if (Mode == GameMode.Survival) SetupWave(seed);
        var active = Active().Where(p => !p.Benched).OrderBy(p => p.Slot).ToList();
        var layout = LayoutFor(seed, active.Count, out var arenaName, out var custom);
        MapNode.Build(layout);
        ArenaName = arenaName ?? MapNode.Theme.Name;
        ArenaCode = custom != null ? MapCode.Encode(custom) : null;
        Arena.Build(layout);

        // Seeded shuffle so spawn positions rotate fairly but identically on all peers.
        var order = Enumerable.Range(0, layout.Spawns.Count).ToList();
        var rng = new Rng((ulong)(uint)seed + 77);
        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = rng.Range(0, i);
            (order[i], order[j]) = (order[j], order[i]);
        }
        // Teams start on opposite sides (swapping each battle): Red takes spawns from one end, Blue from the other.
        var byX = Enumerable.Range(0, layout.Spawns.Count).OrderBy(k => layout.Spawns[k].X).ToList();
        if (BattleNumber % 2 == 0) byX.Reverse();
        if (TeamMode && order.Count >= active.Count)
        {
            int lo = 0, hi = byX.Count - 1;
            for (int i = 0; i < active.Count; i++) order[i] = active[i].Info.Team == 0 ? byX[lo++] : byX[hi--];
        }
        for (int i = 0; i < active.Count; i++)
        {
            var p = active[i];
            var sp = layout.Spawns[order[i % order.Count]];
            SpawnAvatar(p, new Vector2(sp.X, sp.Y));
        }
        SetupObjectives(layout, seed, active, byX);
        if (IsHost)
        {
            foreach (var p in active)
                foreach (var e in p.Effects) e.OnBattleStart(p.Avatar, this);
            Stats.BattleStart(BattleNumber, Slots.Where(p => p != null).Sum(p => p.RoundWins) + 1, seed, active);
        }

        Phase = Phase.Countdown;
        CountdownLeft = CountdownTime;
        _lastCountdownTick = -1;
        System.Array.Clear(_battleKills);
        _endCheckT = -1;
        _lastVictim = _lastKiller = -1;
        _killCamWait = false;
        KillCam.Begin();
        PickOptions.Clear();
        PickDeals.Clear();
        Log.Info($"battle {BattleNumber} start (seed {seed}, {(Settings.Maps.Count > 0 ? $"custom arena \"{ArenaName}\", {layout.Solids.Count} solids, " : "")}map attempts {layout.Attempts}{(layout.UsedFallback ? ", fallback" : "")})");
        BattleStarted?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BattleOver(int winner, byte[] scores, bool roundWon, bool matchWon)
    {
        KillCam.End();
        ApplyScores(scores);
        string name = WinnerName(winner);
        Color col = WinnerColor(winner);
        Log.Info($"battle over: winner {winner} ({(winner >= 0 ? name : "draw")}) round={roundWon} match={matchWon} | {ScoreLine()}");

        if (matchWon)
        {
            Phase = Phase.MatchOver;
            MatchWinner = winner;
            Net.InMatch = false;
            Banner?.Invoke(Mode == GameMode.Survival ? MatchTitle(winner) : winner >= 0 ? $"{name} WINS THE MATCH!" : "MATCH OVER", col, 3f);
            Sfx.Play("win");
            CareerBook.MatchEnded(this, winner); // ahead of the match-over screen, which shows what it unlocked
            MatchEnded?.Invoke(winner);
            return;
        }
        Phase = Phase.Intermission;
        if (Mode == GameMode.Survival) { Banner?.Invoke($"WAVE {BattleNumber} CLEARED!", col, 1.5f); Sfx.Play("win", 0.7f); }
        else if (winner < 0) Banner?.Invoke("DRAW", col, 1.2f);
        else if (roundWon) { Banner?.Invoke($"{name} TAKES THE ROUND", col, 1.5f); Sfx.Play("win", 0.7f); }
        else Banner?.Invoke($"{name} wins the battle", col, 1.2f);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BeginCardPick(byte[] data)
    {
        ReadPicks(data);
        Picked.Clear();
        Phase = Phase.CardPick;
        Bullets.Clear();
        CardPickBegan?.Invoke();
    }

    /// <summary>To a rejoining client only: the match as it stands (see <see cref="RejoinBytes"/>).</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Rejoined(byte[] data)
    {
        var r = new WireReader(data);
        var lobby = LobbyState.Deserialize(r.Blob());
        Settings = lobby.Settings;
        Array.Clear(Slots);
        LocalMatchPlayer = null;
        LocalGuests.Clear();
        foreach (var info in lobby.Players)
        {
            if (Settings.Mode == GameMode.Survival) info.Team = info.IsBot ? 1 : 0;
            var mp = new MatchPlayer { Info = info, Left = r.Bool(), RoundWins = r.U8(), BattleWins = r.U8() };
            int cards = r.U8();
            for (int k = 0; k < cards; k++) mp.Cards.Add(r.I16());
            mp.RecomputeStats();
            AttachLocal(mp);
            Slots[info.Slot] = mp;
        }
        BattleNumber = r.I16();
        MatchWinner = -1;
        Phase = Phase.Idle; // until the next StartBattle, unless a card pick is on
        Net.InMatch = true;
        Visible = true;
        Log.Info($"rejoined the match at battle {BattleNumber} with {LocalMatchPlayer?.Cards.Count ?? 0} cards | {ScoreLine()}");
        MatchBegan?.Invoke();
        ScoresChanged?.Invoke();
        if (r.Bool())
        {
            ReadPicks(r.Blob());
            Picked.Clear();
            int n = r.U8();
            for (int i = 0; i < n; i++) Picked.Add(r.U8());
            Phase = Phase.CardPick;
            CardPickBegan?.Invoke();
        }
        else Banner?.Invoke("REJOINED! YOU'RE IN FROM THE NEXT BATTLE", Palette.Accent, 2.5f);
    }

    /// <summary>Gives a player at this machine their input: the keyboard player, or a couch guest's controller. False if they play elsewhere.</summary>
    private bool AttachLocal(MatchPlayer mp)
    {
        var info = mp.Info;
        if (info.IsBot || info.PeerId != Net.LocalPeer) return false;
        if (info.Guest == 0)
        {
            LocalMatchPlayer = mp;
            mp.Input = AutopilotLocal ? new BotBrain(this) : new LocalInput(this);
        }
        else
        {
            LocalGuests.Add(mp);
            int device = Pads.Couch.DeviceOf(info.Guest);
            // A guest whose controller is gone (a rejoin after restarting the game) stands still until the next lobby.
            mp.Input = AutopilotLocal ? new BotBrain(this) : device >= 0 ? new PadInput(device) : null;
        }
        return true;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PlayerBack(int slot, long peer)
    {
        var p = Slots[slot];
        if (p == null) return;
        p.Info.PeerId = peer;
        p.Left = false;
        if (p != LocalMatchPlayer) Banner?.Invoke($"{p.Info.Name} is back", Palette.Player(p.Info.ColorIdx), 1.5f);
        ScoresChanged?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void WaitingForRejoin(int seconds)
    {
        Banner?.Invoke($"WAITING FOR PLAYERS TO REJOIN  {seconds}", Palette.TextDim, 1.2f);
    }

    /// <param name="curseId">The curse that came with a cursed deal, or -1 for an ordinary pick.</param>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CardPicked(int slot, int cardId, int curseId)
    {
        var p = Slots[slot];
        if (p == null) return;
        Picked.Add(slot);
        var unlocked = Combos.Unlocks(p.Cards, cardId);
        _lastPickCombo = unlocked.Count > 0;
        p.Cards.Add(cardId);
        if (curseId >= 0) p.Cards.Add(curseId);
        p.RecomputeStats();
        Sfx.Play("pick", 0.6f);
        if (curseId >= 0) Log.Info($"{p.Info.Name} took a cursed deal: {CardRegistry.Get(cardId)?.Name} with {CardRegistry.Get(curseId)?.Name}");
        else Log.Info($"{p.Info.Name} picked {CardRegistry.Get(cardId)?.Name}");
        CardWasPicked?.Invoke(slot, cardId);
        if (curseId >= 0) CurseTaken?.Invoke(slot, curseId);
        foreach (var combo in unlocked)
        {
            Log.Info($"combo: {p.Info.Name} found {combo.Name}");
            if (IsLocalHuman(p)) Sfx.Play("combo", 0.7f);
            ComboFound?.Invoke(slot, combo);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SyncRematchVotes(byte[] data)
    {
        var r = new WireReader(data);
        RematchVotes.Clear();
        int n = r.U8();
        for (int i = 0; i < n; i++) RematchVotes.Add(r.U8());
        RematchVotesChanged?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void EndMatch()
    {
        ResetWorld();
        Net.InMatch = false;
        ReturnedToLobby?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PlayerLeft(int slot)
    {
        if (Slots[slot] == null) return;
        Slots[slot].Left = true;
        _respawnAt[slot] = -1; // if they rejoin, they sit out the rest of this battle
        if (Slots[slot].Avatar is { } a)
        {
            a.Alive = false;
            a.SyncGhost();
        }
        Banner?.Invoke($"{Slots[slot].Info.Name} left", Palette.TextDim, 1.2f);
        ScoresChanged?.Invoke();
    }
}
