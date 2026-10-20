using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Skills;
using System.Collections.Immutable;
namespace DfoServer.Game.Characters;

internal sealed record Character(ushort Id, ushort Slot, string Name, long Revision, Location Position, Location? Return)
{
    public byte Job { get; init; }
    public byte GrowType { get; init; }
    public byte EquipmentSlotFlags { get; init; }
    // 来自角色设置的场景显示位，不改写位置、进度或资产revision。
    public byte VisibilityFlags { get; init; } = 6;
    public ushort Emotion { get; init; }
    // 被其他角色拉黑后的有效计分，不是本人黑名单条目数；客户端据此选择惩罚称号。
    public int BlacklistCount { get; init; }
    public byte Level { get; init; } = 1;
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DfoServer.Game.Guild.GuildMembership? Guild { get; init; }
    public DfoServer.Game.Tutorials.CharacterTutorialState? Tutorial { get; init; }
    public uint AcceptedStoryLevel { get; init; }
    public int[] CompletedQuests { get; init; } = [];
    public ImmutableDictionary<int, DfoServer.Game.Titles.TitleProgress> TitleProgress { get; init; } = ImmutableDictionary<int, DfoServer.Game.Titles.TitleProgress>.Empty;
    public ImmutableHashSet<int> TitleSections { get; init; } = ImmutableHashSet<int>.Empty;
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
    public SkillSwitchInventoryState SkillSwitchInventory { get; init; } = SkillSwitchInventoryState.Empty;
    public DfoServer.Game.CollectBox.CollectBoxState CollectBox { get; init; } = DfoServer.Game.CollectBox.CollectBoxState.Empty;
    public CharacterDungeonState? DungeonState { get; init; }
    public CharacterFatigueState? Fatigue { get; init; }
    public DfoServer.Game.Town.TeleportCooldowns TeleportCooldowns { get; init; } = new();
    // 远古精灵秘药的限时经验加成。Unix 秒截止；0 表示没有。死亡、回城和重登都保留。
    public long ExpBonusUntil { get; init; }
    public int ExpBonusPermille { get; init; }
    // 副职业资格。0 表示没有；1 附魔、2 炼金、3 分解、4 控偶。
    // USERINFO Actor 经 026777A0 写入局部 DTO+3B0，再由 02D8B110/015034E0 装回活 I；TownPackets.Actor 投影该字节。
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public byte ExpertJobType { get; init; }
    // 副职业累计熟练度。Actor 在类型字节后写 u32，客户端装回 I+3B4，按 [expertness exp] 阈值自己显示等级。
    public int ExpertJobExperience { get; init; }
    // 放弃次数档。存在 character_expert_meta，删除 character_expert_job 后仍保留。
    public int ExpertGiveUpState { get; init; }
    // 分解机当前档；脚本 [upgrade cost] 首键为 1。
    public int ExpertMachineGrade { get; init; } = 1;
    // [endurance initial value] 300。
    public int ExpertMachineEndurance { get; init; } = 300;
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public long BlackDiamondExpiresAt => Inventory.AccountWallet.ContractExpiry(56);
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
    public DfoServer.Game.Tutorials.CharacterTutorialState? Tutorial { get; init; }
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
        Tutorial?.Validate();
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
