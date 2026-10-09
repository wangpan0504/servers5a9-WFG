using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Game.Inventory.Models;
using System.Collections.Immutable;
namespace DfoServer.Game.Characters;

internal sealed record Character(ushort Id, ushort Slot, string Name, long Revision, Location Position, Location? Return)
{
    public byte Job { get; init; }
    public byte GrowType { get; init; }
    public byte EquipmentSlotFlags { get; init; }
    // 来自角色设置的场景显示位，不改写位置、进度或资产revision。
    public byte VisibilityFlags { get; init; } = 6;
    public byte Level { get; init; } = 1;
    public uint AcceptedStoryLevel { get; init; }
    public int[] CompletedQuests { get; init; } = [];
    public ImmutableDictionary<int,byte> QuestAnswers { get; init; } = ImmutableDictionary<int,byte>.Empty;
    public int QuestCycleDay { get; init; }
    public ImmutableDictionary<int, int> QuestDailyClaims { get; init; } = ImmutableDictionary<int, int>.Empty;
    public int[] AccountCompletedQuests { get; init; } = [];
    public ImmutableDictionary<int,byte> AccountQuestAnswers { get; init; } = ImmutableDictionary<int,byte>.Empty;
    public ImmutableDictionary<int, int> AccountQuestDailyClaims { get; init; } = ImmutableDictionary<int, int>.Empty;
    public CharacterSkill[] Skills { get; init; } = [];
    // 进行中的主要任务。槽位 0–29 对应 ACK4 的 30 组；空槽不入库。
    public ActiveMainQuest[] ActiveMainQuests { get; init; } = [];
    public CharacterLearningState Learning { get; init; } = CharacterLearningState.Uninitialized;
    public InventorySnapshot Inventory { get; init; } = InventorySnapshot.Empty;
    public CharacterDungeonState? DungeonState { get; init; }
    public CharacterFatigueState? Fatigue { get; init; }
}

internal sealed record ActiveMainQuest(byte Slot, ushort QuestId, uint ConditionState)
{
    // 同一任务重新接取必须拥有新身份，旧副本事实不能推进新的任务槽。
    public Guid AcceptanceId { get; init; }
}

// 创建所需的领域数据；数据库不决定出生点，也不区分普通角色和工具准备的角色。
internal sealed record NewCharacter(string Name, Location Position, Location? Return)
{
    public byte Job { get; init; }
    public byte GrowType { get; init; }
    public byte EquipmentSlotFlags { get; init; }
    public byte Level { get; init; } = 1;
    public uint AcceptedStoryLevel { get; init; }
    public int[] CompletedQuests { get; init; } = [];
    public CharacterSkill[] Skills { get; init; } = [];
    public ActiveMainQuest[] ActiveMainQuests { get; init; } = [];
    public CharacterLearningState InitialLearning { get; init; } = CharacterLearningState.Uninitialized;
    public InventorySnapshot Inventory { get; init; } = InventorySnapshot.Empty;
    public CharacterDungeonState? DungeonState { get; init; }

    public void Validate()
    {
        if (!CharacterName.IsValid(Name))
            throw new InvalidDataException("Invalid CP936 character name");
        if (Job > 15 || GrowType > 127 || Level is < 1 or > 95 || AcceptedStoryLevel > 95)
            throw new InvalidDataException("Invalid character identity");
        if (CompletedQuests.Any(id => id is <= 0 or >= 30000) || CompletedQuests.Distinct().Count() != CompletedQuests.Length)
            throw new InvalidDataException("Invalid completed quests");
        if (Skills.Length > 255 || Skills.Any(skill => skill.Level == 0) || Skills.Select(skill => skill.Id).Distinct().Count() != Skills.Length)
            throw new InvalidDataException("Invalid character skills");
        if (ActiveMainQuests.Length > 30 || ActiveMainQuests.Any(quest => quest.Slot > 29 || quest.QuestId is <= 0 or >= 30000) ||
            ActiveMainQuests.Select(quest => quest.Slot).Distinct().Count() != ActiveMainQuests.Length ||
            ActiveMainQuests.Where(quest => quest.AcceptanceId != Guid.Empty).Select(quest => quest.AcceptanceId).Distinct().Count() !=
                ActiveMainQuests.Count(quest => quest.AcceptanceId != Guid.Empty))
            throw new InvalidDataException("Invalid active main quests");
        InitialLearning.Validate();
        DungeonState?.Validate();
        Inventory.Validate(allocating: true);
        if (!Inventory.AccountWallet.Same(DfoServer.Game.Currency.AccountWallet.Empty))
            throw new InvalidDataException("建角只初始化角色资产，账号余额由当前账号装载");
        if (Inventory.AccountCargo != new AccountCargoState() || Inventory.Items.Any(item => item.Position.List == InventoryListType.AccountCargo))
            throw new InvalidDataException("建角不能初始化或覆盖账号仓库");
        if (!Inventory.EpicPieces.Same(EpicPieceBook.Empty))
            throw new InvalidDataException("建角不能初始化或覆盖账号史诗图鉴");
    }
}
