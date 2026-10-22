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
        // Dim the arena behind, and make the panel nearly solid, so the fight behind doesn't show through the standings.
        var dim = new ColorRect { Color = new Color(0.02f, 0.01f, 0.05f, 0.6f), MouseFilter = MouseFilterEnum.Ignore };
        AddChild(dim);
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        (_, _body) = UiKit.CenteredPanel(this, 460);
        var panel = (PanelContainer)_body.GetParent();
        var solid = (StyleBoxFlat)panel.GetThemeStylebox("panel").Duplicate();
        solid.BgColor = solid.BgColor with { A = 0.97f };
        panel.AddThemeStyleboxOverride("panel", solid);
        _game.MatchEnded += Build;
        _game.RematchVotesChanged += ShowVotes;
        _game.ScoresChanged += ShowVotes; // someone left
    }

    private void Build(int winner)
    {
        UiKit.ClearChildren(_body);
        // Five or more players: the standings go in two columns on a wider panel, so the list still fits on screen.
        // Team modes: the winning team first, then the rest; both share a place.
        int champTeam = _game.TeamOf(winner);
        var players = _game.Slots.Where(p => p != null)
            .OrderBy(p => champTeam >= 0 && _game.TeamOf(p.Slot) != champTeam ? 1 : 0)
            .ThenByDescending(p => p.RoundWins).ToList();
        bool crowded = players.Count > 4;
        ((Control)_body.GetParent()).CustomMinimumSize = new Vector2(crowded ? 760 : _game.MatchAwards.Count > 0 ? 540 : 460, 0);
        var champCol = winner >= 0 ? _game.WinnerColor(winner) : Palette.Text;
        _body.AddChild(new TitleText(winner >= 0 ? $"{_game.WinnerName(winner).ToUpperInvariant()} WINS!" : "MATCH OVER", 34, 2.5f,
            champCol, champCol.Lightened(0.3f), Palette.Accent));
        // The champion on a pedestal, with the awards either side of them.
        var awards = _game.MatchAwards;
        var stage = UiKit.HBox(12);
        var left = UiKit.VBox(10);
        var right = UiKit.VBox(10);
        foreach (var col in new[] { left, right })
        {
            col.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            col.Alignment = BoxContainer.AlignmentMode.Center;
        }
        for (int i = 0; i < awards.Count; i++)
            (i % 2 == 0 ? left : right).AddChild(AwardCard(awards[i], i % 2 == 0 ? HorizontalAlignment.Right : HorizontalAlignment.Left));
        stage.AddChild(left);
        if (winner >= 0 && _game.Slots[winner] != null)
        {
            // The champion (or the whole winning team, smaller), wider than the figure so the gun it holds out never
            // reaches the awards beside it.
            var champs = _game.SideOf(winner).ToList();
            float zoom = (crowded ? 2.2f : 3.2f) * (champs.Count > 1 ? 0.6f : 1);
            var podium = UiKit.HBox(0);
            podium.Alignment = BoxContainer.AlignmentMode.Center;
            foreach (var champ in champs)
            {
                var spot = new CenterContainer { CustomMinimumSize = new Vector2(56 * zoom, 0) };
                spot.AddChild(new AvatarPreview(champ.Info.Look, champ.Info.ColorIdx, zoom, alive: true) { Pedestal = true });
                podium.AddChild(spot);
            }
            stage.AddChild(podium);
        }
        stage.AddChild(right);
        if (awards.Count > 0 || stage.GetChildCount() > 2) _body.AddChild(stage);

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
            int team = _game.TeamOf(p.Slot);
            int place = team >= 0 && champTeam >= 0 ? (team == champTeam ? 1 : 2) : idx + 1;
            var row = UiKit.HBox(10);
            var placeCol = team >= 0 ? Palette.Team(team) : place == 1 ? Palette.Accent : Palette.TextDim;
            row.AddChild(UiKit.Label($"#{place}", 18, placeCol, font: Fonts.Heavy));
            row.AddChild(new AvatarPreview(p.Info.Look, p.Info.ColorIdx, 1.1f) { CustomMinimumSize = new Vector2(36, 34), ShowGun = false });
            var name = UiKit.Label($"{p.Info.Name}{(p.Left ? "  (left)" : "")}", UiKit.Body, p.Color.Lightened(0.2f), font: Fonts.Bold);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            name.VerticalAlignment = VerticalAlignment.Center;
            row.AddChild(name);
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

    /// <summary>One award: its title, who got it, and the number behind it.</summary>
    private Control AwardCard(Award a, HorizontalAlignment align)
    {
        var box = UiKit.VBox(0);
        var title = UiKit.Label(a.Title.ToUpperInvariant(), UiKit.Small, Palette.Accent, align, Fonts.Heavy);
        title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(title);
        box.AddChild(UiKit.Label(_game.NameOf(a.Slot), UiKit.Small, Palette.Player(_game.ColorOf(a.Slot)).Lightened(0.2f), align, Fonts.Bold));
        var blurb = UiKit.Label(a.Blurb, UiKit.Tiny, Palette.TextDim, align);
        blurb.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(blurb);
        return box;
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
