using System;
using System.Linq;
using Brawler.Core;
using Brawler.Fx;
using Brawler.Game;
using Brawler.Logic;
using Brawler.Net;
using Godot;

namespace Brawler.UI;

public partial class MatchOverUI : Control
{
    public event Action BackToLobby, LeavePressed;

    private readonly GameManager _game;
    private readonly NetworkManager _net;
    private VBoxContainer _body;

    public MatchOverUI(GameManager game, NetworkManager net)
    {
        _game = game;
        _net = net;
    }

    public MatchOverUI() { }

    public override void _Ready()
    {
        (_, _body) = UiKit.CenteredPanel(this, 460);
        _game.MatchEnded += Build;
        _game.RematchVotesChanged += ShowVotes;
        _game.ScoresChanged += ShowVotes; // someone left
    }

    private void Build(int winner)
    {
        UiKit.ClearChildren(_body);
        // Five or more players: the standings go in two columns on a wider panel, so the list still fits on screen.
        var players = _game.Slots.Where(p => p != null).OrderByDescending(p => p.RoundWins).ToList();
        bool crowded = players.Count > 4;
        ((Control)_body.GetParent()).CustomMinimumSize = new Vector2(crowded ? 760 : 460, 0);
        var champCol = winner >= 0 ? Palette.Player(_game.ColorOf(winner)) : Palette.Text;
        _body.AddChild(new TitleText(winner >= 0 ? $"{_game.NameOf(winner).ToUpperInvariant()} WINS!" : "MATCH OVER", 34, 2.5f,
            champCol, champCol.Lightened(0.3f), Palette.Accent));
        if (winner >= 0 && _game.Slots[winner] is { } champ)
        {
            var podium = new CenterContainer();
            podium.AddChild(new AvatarPreview(champ.Info.Look, champ.Info.ColorIdx, crowded ? 2.2f : 3.2f, alive: true) { Pedestal = true });
            _body.AddChild(podium);
        }

        Container standings = _body;
        if (crowded)
        {
            var grid = new GridContainer { Columns = 2 };
            grid.AddThemeConstantOverride("h_separation", 18);
            grid.AddThemeConstantOverride("v_separation", 6);
            _body.AddChild(grid);
            standings = grid;
        }
        // Two columns read top to bottom: places 1-4 on the left, the rest on the right.
        int rows = crowded ? (players.Count + 1) / 2 : players.Count;
        for (int cell = 0; cell < players.Count; cell++)
        {
            int idx = crowded ? cell % 2 * rows + cell / 2 : cell;
            var p = players[idx];
            int place = idx + 1;
            var row = UiKit.HBox(10);
            row.AddChild(UiKit.Label($"#{place}", 18, place == 1 ? Palette.Accent : Palette.TextDim, font: Fonts.Heavy));
            row.AddChild(new AvatarPreview(p.Info.Look, p.Info.ColorIdx, 1.1f) { CustomMinimumSize = new Vector2(36, 34), ShowGun = false });
            var info = UiKit.VBox(0);
            info.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            info.AddChild(UiKit.Label($"{p.Info.Name}{(p.Left ? "  (left)" : "")}", UiKit.Body, p.Color.Lightened(0.2f), font: Fonts.Bold));
            var cards = string.Join("  ·  ", CardRegistry.Resolve(p.Cards).Select(c => c.Name));
            var l = UiKit.Label(cards.Length > 0 ? cards : "no cards", UiKit.Tiny, Palette.TextDim);
            l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            if (crowded) { l.MaxLinesVisible = 2; l.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; }
            info.AddChild(l);
            row.AddChild(info);
            row.AddChild(UiKit.Label($"{p.RoundWins}", 22, Colors.White, font: Fonts.Heavy));
            row.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            standings.AddChild(row);
        }

        _votes = UiKit.Label("", UiKit.Small, Palette.TextDim, HorizontalAlignment.Center);
        _votes.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _body.AddChild(_votes);

        var buttons = UiKit.HBox(8);
        _rematch = UiKit.Primary("REMATCH", () => _game.LocalVoteRematch(!_game.RematchVotes.Contains(_game.LocalMatchPlayer?.Slot ?? -1)));
        _rematch.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        buttons.AddChild(_rematch);
        if (_net.IsHost) buttons.AddChild(UiKit.Button("Lobby", () => BackToLobby?.Invoke()));
        buttons.AddChild(UiKit.Button("Leave", () => LeavePressed?.Invoke()));
        _body.AddChild(buttons);
        ShowVotes();
    }

    private Label _votes;
    private Button _rematch;

    /// <summary>Who is in for a rematch. It starts once every human still here votes; bots always are.</summary>
    private void ShowVotes()
    {
        if (_rematch == null || !IsInstanceValid(_rematch)) return;
        var voters = _game.RematchVoters().ToList();
        bool mine = _game.LocalMatchPlayer is { } me && _game.RematchVotes.Contains(me.Slot);
        _rematch.Disabled = !_game.CanRematch || _game.LocalMatchPlayer == null;
        _rematch.Text = mine ? "READY! (CLICK TO CANCEL)" : "REMATCH";
        if (!_game.CanRematch)
            _votes.Text = _net.IsHost ? "Not enough players left for a rematch." : "Not enough players left. Waiting for host...";
        else if (voters.Count > 0 && voters.All(p => _game.RematchVotes.Contains(p.Slot)))
            _votes.Text = "Everyone's in. Here we go!";
        else
        {
            var waiting = voters.Where(p => !_game.RematchVotes.Contains(p.Slot)).Select(p => p.Info.Name);
            _votes.Text = voters.Count <= 1 ? "" : $"Rematch: {voters.Count(p => _game.RematchVotes.Contains(p.Slot))}/{voters.Count} ready  ·  waiting for {string.Join(", ", waiting)}";
        }
        _votes.Visible = _votes.Text.Length > 0;
    }
}
