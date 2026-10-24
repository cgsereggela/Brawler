using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class EmotesTests
{
    public static void Run()
    {
        var g = new EmoteLimiter(burst: 3, refill: 1.5f);
        int burst = Enumerable.Range(0, 10).Count(_ => g.TryUse());
        Check(burst == 3, "Emote limiter allows a burst of 3, then refuses", $"{burst}");
        g.Tick(1.4f);
        Check(!g.TryUse(), "No new emote before a refill period passes");
        g.Tick(0.2f);
        Check(g.TryUse() && !g.TryUse(), "One emote back per refill period");
        g.Tick(100);
        Check(Math.Abs(g.Tokens - 3) < 1e-4, "Refill stops at the burst size", $"{g.Tokens}");
        // Spamming every frame for 30 s gets through at most the burst plus one per refill period.
        var spam = new EmoteLimiter();
        int sent = 0;
        for (int i = 0; i < 30 * 60; i++) { spam.Tick(1 / 60f); if (spam.TryUse()) sent++; }
        Check(sent <= 3 + 30 / 1.5f + 1 && sent >= 20, "Spamming is held to about one emote per 1.5 s", $"{sent}");
        Check(Emotes.All.All(e => e.Text.Length is > 0 and <= 12 && e.GhostText.Length is > 0 and <= 12), "Emote texts are short enough for a bubble");
        Check(Emotes.Text(1, ghost: true) == "BOO!" && Emotes.Text(1, ghost: false) == "HAHA!" && Emotes.Text(99, false) == "", "Ghosts say their own line; bad ids say nothing");
        Check(Enumerable.Range(0, Emotes.All.Length).All(i => BindTable.Actions.Contains(Emotes.Action(i))) && BindTable.Actions.Contains("ping"),
            "Every emote and the ping have a bindable action");
        Check(Emotes.Text(4, ghost: false) == "UNLUCKY" && Emotes.Action(4) == "emote_5", "UNLUCKY is the fifth emote, on emote_5");
    }
}
