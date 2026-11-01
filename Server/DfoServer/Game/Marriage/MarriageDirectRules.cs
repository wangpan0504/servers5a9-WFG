namespace DfoServer.Game.Marriage;

// GM直接建立已婚关系；普通求婚、任务与婚礼流程仍走原有规则。
internal static class MarriageDirectRules
{
    public static bool Male(string job) => job switch
    {
        "swordman" or "gunner" or "priest" or "at fighter" or "at mage" or
            "demonic swordman" or "demonic lancer" or "gun blader" => true,
        "fighter" or "mage" or "at gunner" or "thief" or "creator mage" or
            "at swordman" or "knight" or "at priest" => false,
        _ => throw new InvalidDataException("当前职业的性别无法确认，未建立婚姻关系")
    };

    public static MarriageRecord Create(Guid id, uint context, ushort firstId, ushort secondId,
        bool firstMale, bool secondMale, long now)
    {
        if (firstMale == secondMale) throw new InvalidDataException("请选择异性角色结婚");
        return MarriageWeddingRules.CreateEngagement(id, context, firstId, secondId, now) with
        {
            State = MarriageWeddingRules.Married, MarriedAt = now
        };
    }
}
