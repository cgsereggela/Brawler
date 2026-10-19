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
    }

    private void Build(int winner)
    {
        UiKit.ClearChildren(_body);
        var champCol = winner >= 0 ? Palette.Player(_game.ColorOf(winner)) : Palette.Text;
        _body.AddChild(new TitleText(winner >= 0 ? $"{_game.NameOf(winner).ToUpperInvariant()} WINS!" : "MATCH OVER", 34, 2.5f,
            champCol, champCol.Lightened(0.3f), Palette.Accent));
        if (winner >= 0 && _game.Slots[winner] is { } champ)
        {
            var podium = new CenterContainer();
            podium.AddChild(new AvatarPreview(champ.Info.Look, champ.Info.ColorIdx, 3.2f, alive: true) { Pedestal = true });
            _body.AddChild(podium);
        }

        int place = 0;
        foreach (var p in _game.Slots.Where(p => p != null).OrderByDescending(p => p.RoundWins))
        {
            place++;
            var row = UiKit.HBox(10);
            row.AddChild(UiKit.Label($"#{place}", 18, place == 1 ? Palette.Accent : Palette.TextDim, font: Fonts.Heavy));
            row.AddChild(new AvatarPreview(p.Info.Look, p.Info.ColorIdx, 1.1f) { CustomMinimumSize = new Vector2(36, 34), ShowGun = false });
            var info = UiKit.VBox(0);
            info.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            info.AddChild(UiKit.Label($"{p.Info.Name}{(p.Left ? "  (left)" : "")}", UiKit.Body, p.Color.Lightened(0.2f), font: Fonts.Bold));
            var cards = string.Join("  ·  ", CardRegistry.Resolve(p.Cards).Select(c => c.Name));
            var l = UiKit.Label(cards.Length > 0 ? cards : "no cards", UiKit.Tiny, Palette.TextDim);
            l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            info.AddChild(l);
            row.AddChild(info);
            row.AddChild(UiKit.Label($"{p.RoundWins}", 22, Colors.White, font: Fonts.Heavy));
            _body.AddChild(row);
        }

        var buttons = UiKit.HBox(8);
        if (_net.IsHost)
        {
            var back = UiKit.Primary("BACK TO LOBBY", () => BackToLobby?.Invoke());
            back.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            buttons.AddChild(back);
        }
        else
        {
            var wait = UiKit.Label("Waiting for host...", UiKit.Body, Palette.TextDim);
            wait.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            buttons.AddChild(wait);
        }
        buttons.AddChild(UiKit.Button("Leave", () => LeavePressed?.Invoke()));
        _body.AddChild(buttons);
    }
}
