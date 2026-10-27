using System.Security.Cryptography;
using System.Buffers.Binary;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Marriage;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

// 由共享副本owner在同一清图操作中冻结；既不是客户端队员列表，也不是可变城镇队伍快照。
internal sealed record QuestMarriagePartyClear(Guid SharedInstanceId, Guid MarriageId, ushort FirstId, ushort SecondId);

internal static class QuestMarriageProgress
{
    public static bool Preparation(StoryQuest quest) => quest is
        { Id: 20737, Grade: "[wedding]", Condition: "[condition under clear]", SubType: 22, IntData: [-1, -1, > 0] };

    public static bool Supports(StoryQuest quest) => Preparation(quest) || quest is
        { Id: 20738, Grade: "[wedding]", Condition: "[seeking]", SubType: -1, IntData: [> 0, > 0] };

    public static bool Eligible(Character character, StoryQuest quest) => Supports(quest) &&
        character.Marriage is { State: >= 1 and <= 3 } record && record.IsMember(character.Id);

    public static bool NormalDungeon(DungeonEntryPlan entry) => entry.IsOrdinary && !entry.IsPractice && !entry.IsQuestReplay &&
        !entry.IsTrainingRoom && !entry.IsClassExperience && !entry.IsTournament && !entry.IsAltar && !entry.IsDespair &&
        !entry.Maze.IsGriefTower && !entry.IsTagSystem && entry.Hope is null;

    public static bool Matches(DungeonEntryPlan entry, SinglePlayerDungeonRun? run) => NormalDungeon(entry) &&
        run?.MarriagePartyClear is { } party && party.SharedInstanceId != Guid.Empty && party.MarriageId != Guid.Empty &&
        party.FirstId != 0 && party.SecondId != 0 && party.FirstId != party.SecondId &&
        (entry.CharacterId == party.FirstId || entry.CharacterId == party.SecondId);

    public static bool Matches(MarriageRecord record, QuestMarriagePartyClear party, Guid instanceId) =>
        record.State is >= 1 and <= 3 && party.SharedInstanceId == instanceId && party.MarriageId == record.Id &&
        party.FirstId != party.SecondId && record.IsMember(party.FirstId) && record.IsMember(party.SecondId);

    // 聚合去重键区分共同准备次数与各自胶囊计数，防止同一共享死亡只给首位角色计数。
    public static Guid EventKey(Guid instanceId, ushort actorId)
    {
        Span<byte> input = stackalloc byte[19];
        instanceId.TryWriteBytes(input);
        BinaryPrimitives.WriteUInt16LittleEndian(input[16..], actorId);
        input[18] = actorId == 0 ? (byte)1 : (byte)2;
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        return new Guid(digest[..16]);
    }
}
