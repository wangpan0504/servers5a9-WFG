using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

// 第四步/分解末步只认匹配客户端硬编码的任务号与 QST 形状。
// 11009：ACK 601 0234FECF event17；11015/11018：ACK 238 0236F904..0236F961；10021：event14→0202BE90 类型17。
internal static class QuestExpertJobProgress
{
    public const ushort Enchanter = 11009, Alchemist = 11015, Doll = 11018, Disjointer = 10021;

    public static bool Craft(StoryQuest quest) => QuestExpertJobReward.Chain(quest) &&
        quest.Condition == "[custom quest]" && quest.SubType == 1 && quest.IntData is [1, 0, 1] &&
        quest.Id is Enchanter or Alchemist or Doll;

    public static bool SelfDisjoint(StoryQuest quest) => QuestExpertJobReward.Chain(quest) &&
        quest.Condition == "[self disjoint item]" && quest.SubType == 0 && quest.IntData is [-2, -1, -1] &&
        quest.Id == Disjointer;

    public static bool Tutorial(StoryQuest quest) => Craft(quest) || SelfDisjoint(quest);

    public static byte NativeType(StoryQuest quest) => Craft(quest) ? (byte)19 : SelfDisjoint(quest) ? (byte)17 :
        throw new InvalidDataException("不是已闭合的副职业教程目标");

    public static byte RequiredType(ushort questId) => questId switch
    {
        Enchanter => 1,
        Alchemist => 2,
        Disjointer => 3,
        Doll => 4,
        _ => 0
    };

    public static byte RequiredType(StoryQuest quest) => RequiredType((ushort)quest.Id);

    public static ushort? TutorialQuest(byte expertType) => expertType switch
    {
        1 => Enchanter,
        2 => Alchemist,
        3 => Disjointer,
        4 => Doll,
        _ => null
    };

    // [expertjob level] 只要求已有 1–4 资格；档位累计 I+3B4 尚未落库，不按第二整数挡教程。
    public static bool MatchesLevel(Character character, StoryQuest quest) =>
        character.ExpertJobType is >= 1 and <= 4 &&
        (quest.Definition?.ExpertJobLevel.Length == 0 || character.ExpertJobType == RequiredType(quest));
}
