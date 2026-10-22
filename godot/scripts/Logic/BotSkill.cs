namespace Brawler.Logic;

/// <summary>How well a bot plays, chosen per bot in the lobby.</summary>
public enum BotSkill : byte { Easy, Normal, Hard }

/// <summary>
/// What each <see cref="BotSkill"/> changes in a bot's play. Normal is the bot as it always was (the card balance
/// stats come from it); Easy aims loosely, reacts slowly, rarely blocks and keeps its distance; Hard leads its shots
/// fully, aims tight, shoots from further off and blocks almost everything it sees coming.
/// </summary>
public readonly record struct BotTuning(
    float AimError,     // times the normal aim wobble
    float Lead,         // how much of the target's movement it aims ahead of (1 = all of it)
    float Range,        // farthest it shoots from, px
    float Reaction,     // seconds a target must be in sight before it fires
    float BlockChance,  // chance to block a bullet it sees coming
    float PrefMin, float PrefMax) // the fighting distance it keeps, px
{
    public static BotTuning Of(BotSkill s) => s switch
    {
        BotSkill.Easy => new(2.6f, 0.35f, 360, 0.6f, 0.15f, 200, 300),
        BotSkill.Hard => new(0.4f, 1.0f, 620, 0.05f, 0.9f, 120, 200),
        _ => new(1f, 0.8f, 520, 0f, 0.55f, 140, 240),
    };

    public static string Name(BotSkill s) => s switch { BotSkill.Easy => "Easy", BotSkill.Hard => "Hard", _ => "Normal" };

    /// <summary>The next level up, wrapping from Hard back to Easy (the lobby's button cycles through them).</summary>
    public static BotSkill Next(BotSkill s) => s == BotSkill.Hard ? BotSkill.Easy : s + 1;
}
