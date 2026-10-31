using DfoServer.GameWorld.Items;
using System.Text.Json.Serialization;

namespace DfoServer.Game.Inventory.Models;

internal readonly record struct ChronicleOption(int Id, byte Job, byte GrowType, byte EquipmentType, byte Number);
internal readonly record struct RandomOption(byte Type, byte Value1, byte Value2);
// 尚未恢复效果语义的字段保留独立类型；普通创建置零，移动和保存必须完整保留。
internal readonly record struct ItemExtension(byte Flag, uint Value);
internal sealed record EpicExchangeOrigin(int Version, int SourceItemId);

internal sealed record ItemCore(int ItemId, ItemKind Kind, int Value)
{
    public byte Attr { get; init; }
    public ushort Durability { get; init; }
    public byte SealFlag { get; init; }
    public int EnchantCardId { get; init; }
    public byte EnchantUpgrade { get; init; }
    public byte AmplifyType { get; init; }
    public ushort AmplifyValue { get; init; }
    public int Marker16 { get; init; }
    public byte Extension26 { get; init; }
    public uint Extension27 { get; init; }
    public uint Extension31 { get; init; }
    public uint Extension35 { get; init; }
    public ChronicleOption Chronicle0 { get; init; }
    public ChronicleOption Chronicle1 { get; init; }
    public int ExpireTime { get; init; }
    public byte EmblemSocketCount { get; init; }
    public int Emblem1 { get; init; }
    public int Emblem2 { get; init; }
    public ushort Rune { get; init; }
    public RandomOption Random0 { get; init; }
    public RandomOption Random1 { get; init; }
    public RandomOption Random2 { get; init; }
    public byte RandomState { get; init; }
    public byte RandomChangedIndex { get; init; }
    public byte RandomChangeState { get; init; }
    public RandomOption RandomChange { get; init; }
    public byte GenuineUpgrade { get; init; }
    public byte EmancipateLevel { get; init; }
    public byte TradeRestriction { get; init; }
    public ushort Extension90 { get; init; }
    public byte Extension92 { get; init; }
    public byte Extension93 { get; init; }
    public byte RemainUseCount { get; init; }
    public byte SortLockFlag { get; init; }
    public byte EquipmentLockId { get; init; }
    public uint Extension97 { get; init; }
    public ulong GuardianGemKeys { get; init; }
    public byte Extension109 { get; init; }
    public uint Extension110 { get; init; }
    public ItemExtension Extension114 { get; init; }
    public byte Extension119 { get; init; }
    public byte Extension120 { get; init; }
    public ushort Extension121 { get; init; }
    // 租赁实例复用普通 Equipment 的持久化与穿脱链路；该标记只用于
    // S2C988 面板筛选和物品行期限投影，不占用客户端物品核心字段。
    public bool IsRental { get; init; }
    // 服务端装备实例来源；旧 JSON 为 null，不从模板或未知 wire 位推断。
    public EpicExchangeOrigin? EpicExchangeOrigin { get; init; }
    // 礼包创建时写入的服务端实例限制；旧实例不按模板反推，不占未知客户端位。
    public bool DisjointForbidden { get; init; }
    public bool DestructionSoulForbidden { get; init; }
    public EchonSet? EchonSet { get; init; }
    [JsonIgnore] public byte Upgrade => (byte)(Attr & 0x1F);
    [JsonIgnore] public bool IsStackable => Kind is ItemKind.Consumable or ItemKind.Material or ItemKind.Quest or
        ItemKind.CreatureConsumable or ItemKind.Emblem or ItemKind.Profession or ItemKind.GuardianGem or ItemKind.Rune;
    [JsonIgnore] public int Count => IsStackable ? Value : 1;
    public bool IsExpired(long utcSeconds) => ExpireTime > 0 && ExpireTime <= utcSeconds;
    public void Validate()
    {
        // 宠物复用 SealFlag 保存类型，不套用普通封装的布尔约束。
        if (ItemId <= 0 || !Enum.IsDefined(Kind) || Value < 0 || IsStackable && Value == 0 || EmblemSocketCount > 2 || SortLockFlag > 1 ||
            Kind != ItemKind.Creature && SealFlag > 1 || Chronicle0.Id == 0 && Chronicle1.Id != 0 ||
            Random0.Type == 0 && (Random1.Type != 0 || Random2.Type != 0) || Random1.Type == 0 && Random2.Type != 0 ||
            EpicExchangeOrigin is { } origin && (Kind != ItemKind.Equipment || origin.Version != 1 || origin.SourceItemId <= 0) ||
            DisjointForbidden && Kind != ItemKind.Equipment || DestructionSoulForbidden && !DisjointForbidden)
            throw new InvalidDataException("无效物品核心");
        if (Kind == ItemKind.Talisman)
            TalismanSocketColors.Read(this).Validate();
        if (EchonSet is { } set)
        {
            set.Validate();
            if (Kind != ItemKind.Equipment || EchonEquipmentState.Read(this).SetIndex != set.Index)
                throw new InvalidDataException("艾肯套装定义与装备索引不一致");
        }
    }
}
