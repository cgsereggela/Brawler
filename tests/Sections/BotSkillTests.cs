using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Brawler.Logic;
using static Brawler.Tests.Harness;

namespace Brawler.Tests;

static class BotSkillTests
{
    public static void Run()
    {
        var (easy, normal, hard) = (BotTuning.Of(BotSkill.Easy), BotTuning.Of(BotSkill.Normal), BotTuning.Of(BotSkill.Hard));
        Check(normal == new BotTuning(1f, 0.8f, 520, 0f, 0.55f, 140, 240), "Bot skill: Normal plays as bots always did (card stats come from it)");
        Check(easy.AimError > normal.AimError && normal.AimError > hard.AimError && easy.Reaction > normal.Reaction && easy.BlockChance < normal.BlockChance
              && normal.BlockChance < hard.BlockChance && easy.Range < normal.Range && normal.Range < hard.Range && easy.Lead < hard.Lead,
              "Bot skill: Easy is worse than Normal at everything, Hard better");
        Check(BotTuning.Next(BotSkill.Easy) == BotSkill.Normal && BotTuning.Next(BotSkill.Normal) == BotSkill.Hard && BotTuning.Next(BotSkill.Hard) == BotSkill.Easy,
              "Bot skill: the lobby button cycles Easy, Normal, Hard");
    }
}
