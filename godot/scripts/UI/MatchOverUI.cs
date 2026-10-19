using System;
using System.Linq;
using Brawler.Core;
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
        (_, _body) = UiKit.CenteredPanel(this, 280);
        _game.MatchEnded += Build;
    }

    private void Build(int winner)
    {
        UiKit.ClearChildren(_body);
        var title = winner >= 0 ? $"{_game.NameOf(winner)} WINS!" : "MATCH OVER";
        _body.AddChild(UiKit.Label(title, 20, winner >= 0 ? Palette.Player(_game.ColorOf(winner)) : Palette.Text, HorizontalAlignment.Center));
        if (winner >= 0 && _game.Slots[winner] is { } champ)
        {
            var podium = new CenterContainer();
            podium.AddChild(new AvatarPreview(champ.Info.Look, champ.Info.ColorIdx, 3, alive: true));
            _body.AddChild(podium);
        }

        foreach (var p in _game.Slots.Where(p => p != null).OrderByDescending(p => p.RoundWins))
        {
            var row = UiKit.HBox(6);
            row.AddChild(new AvatarPreview(p.Info.Look, p.Info.ColorIdx) { CustomMinimumSize = new Vector2(30, 28), ShowGun = false });
            row.AddChild(UiKit.Label($"{p.Info.Name}  -  {p.RoundWins} round{(p.RoundWins == 1 ? "" : "s")}{(p.Left ? " (left)" : "")}"));
            _body.AddChild(row);
            var cards = string.Join(", ", CardRegistry.Resolve(p.Cards).Select(c => c.Name));
            var l = UiKit.Label(cards.Length > 0 ? cards : "no cards", 8, Palette.TextDim);
            l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _body.AddChild(l);
        }

        var buttons = UiKit.HBox();
        if (_net.IsHost) buttons.AddChild(UiKit.Button("Back to Lobby", () => BackToLobby?.Invoke()));
        else buttons.AddChild(UiKit.Label("Waiting for host...", 10, Palette.TextDim));
        buttons.AddChild(UiKit.Button("Leave", () => LeavePressed?.Invoke()));
        _body.AddChild(buttons);
    }
}
