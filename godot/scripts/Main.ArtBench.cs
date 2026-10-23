using System;
using System.Diagnostics;
using Brawler.Core;
using Brawler.Game;
using Brawler.Logic;
using Godot;

namespace Brawler;

public partial class Main
{
    /// <summary>
    /// --artbench: the C# cost of drawing one character. Draws 32 looks (random, from fixed seeds) in a spread of poses
    /// (standing, walking, in the air, on a wall, with and without the gun) 40 times a frame for 240 frames and prints
    /// the time and memory per character (ARTBENCH). Needs a window: headless runs don't draw.
    /// </summary>
    private void ArtBench()
    {
        _autoMode = "bench";
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        var looks = new Appearance[32];
        for (int i = 0; i < looks.Length; i++) looks[i] = Appearance.Random(new Rng((ulong)i + 1));
        var canvas = new Node2D { Name = "ArtBench" };
        AddChild(canvas);
        const int perFrame = 40, frames = 240, warmup = 30;
        int frame = 0;
        long ticks = 0, bytes = 0, draws = 0;
        bool hashed = false;
        AvatarPose Pose(int k) => new()
        {
            Body = Palette.Player(k % Palette.Players.Length),
            Look = looks[k % looks.Length],
            Facing = k % 2 == 0 ? 1 : -1,
            Aim = Vector2.FromAngle(k * 0.37f),
            Scale = 3,
            Time = k * 0.016f,
            WalkPhase = k * 0.21f,
            Grounded = k % 5 != 3,
            Moving = k % 3 != 0,
            Walk = k % 3 != 0 ? 1 : 0,
            MoveDir = k % 2 == 0 ? 1 : -1,
            Air = k % 5 == 3 ? 1 : 0,
            VelY = k % 5 == 3 ? -200 : 0,
            Wall = k % 11 == 4 ? 1 : 0,
            WallSide = 1,
            Blink = k % 17 == 0,
            Hurt = k % 13 == 0,
            Gun = k % 4 != 1,
            Kick = k % 7 == 0 ? 0.6f : 0,
        };
        canvas.Draw += () =>
        {
            frame++;
            if (frame > frames + warmup)
            {
                // Untimed: every triangle of 2,000 draws, hashed, so a speed-up can be checked to draw exactly the same.
                Fx.TriBatch.Hash = 14695981039346656037UL;
                Fx.TriBatch.Hashing = true;
                for (int k = 0; k < 2000; k++) AvatarArt.Draw(canvas, new Vector2(60 + k % 10 * 90, 80 + k / 10 % 5 * 110), Pose(k));
                Fx.TriBatch.Hashing = false;
                hashed = true;
                return;
            }
            long b0 = GC.GetAllocatedBytesForCurrentThread();
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < perFrame; i++)
            {
                int k = frame * perFrame + i;
                AvatarArt.Draw(canvas, new Vector2(60 + i % 10 * 90, 80 + i / 10 * 110), Pose(k));
            }
            if (frame <= warmup) return; // JIT and first-use buffers
            ticks += Stopwatch.GetTimestamp() - t0;
            bytes += GC.GetAllocatedBytesForCurrentThread() - b0;
            draws += perFrame;
        };
        void Next()
        {
            if (!hashed)
            {
                canvas.QueueRedraw();
                return;
            }
            GetTree().ProcessFrame -= Next;
            double us = ticks * 1e6 / Stopwatch.Frequency / draws;
            Log.Info($"ARTBENCH {us / 1000:0.000} ms per character, {bytes / (double)draws:0} bytes allocated per character ({draws} draws); drawing hash {Fx.TriBatch.Hash:x16}");
            GetTree().Quit(0);
        }
        GetTree().CreateTimer(0.5).Timeout += () => GetTree().ProcessFrame += Next;
    }
}
