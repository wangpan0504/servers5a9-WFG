using System.Collections.Immutable;
using System.Text;
using DfoServer.Game.Characters;

namespace DfoServer.Game.Inventory.Models;

// 装扮预设：容量由「装扮预设橱窗扩展券」（CMD515 action 216）累加，默认 1、上限 6。
// 客户端槽数组固定 6 项，槽位是身份、允许空洞：只保存实际有内容的槽。
// ActiveSlot 是玩家当前选中的橱窗：0x063A 选中后写入，之后的穿戴变化实时回写该槽；未选中为空。
internal sealed record AvatarPresetState(byte Capacity = AvatarPresetState.DefaultCapacity,
    ImmutableArray<AvatarPreset> Presets = default, byte? ActiveSlot = null)
{
    public const byte DefaultCapacity = 1;
    public const byte MaximumCapacity = 6;
    // 客户端名字读取器FUN_03be25f0按u32字节数复制原始字节，长度必须小于0x100：超限时整条记录回退成空名
    // 且后续部位错位；名字按CP936上网，127个字符最多254字节，仍在限内。
    public const int MaximumNameLength = 127;

    public static AvatarPresetState Empty { get; } = new(DefaultCapacity, []);

    public AvatarPreset? Find(byte slot) =>
        Presets.IsDefault ? null : Presets.FirstOrDefault(preset => preset.Slot == slot);

    public AvatarPresetState With(AvatarPreset preset) => this with
    {
        Presets = (Presets.IsDefault ? [] : Presets).Where(row => row.Slot != preset.Slot)
            .Append(preset).OrderBy(row => row.Slot).ToImmutableArray()
    };

    public AvatarPresetState Without(byte slot) => this with
    {
        Presets = (Presets.IsDefault ? [] : Presets).Where(row => row.Slot != slot).ToImmutableArray()
    };

    public bool Same(AvatarPresetState other) => Capacity == other.Capacity && ActiveSlot == other.ActiveSlot &&
        (Presets.IsDefault ? [] : Presets).SequenceEqual(other.Presets.IsDefault ? [] : other.Presets);

    // 投影层据此补发 0x063F：槽位是身份、允许空洞，只比记录内容（名称与部位），
    // 容量与当前橱窗另有整表/选择回包。记录里的 ImmutableArray 默认按底层数组引用比较，
    // 同一套外观重新采样后必然不等，必须走 SameContent 逐字段比。
    public ImmutableArray<byte> ContentChangedSlots(AvatarPresetState after)
    {
        var current = Presets.IsDefault ? ImmutableArray<AvatarPreset>.Empty : Presets;
        var next = after.Presets.IsDefault ? ImmutableArray<AvatarPreset>.Empty : after.Presets;
        // 绝大多数库存变化不碰预设：两个状态共用同一份不可变数组，直接跳过逐槽逐字段比较。
        if (current.Equals(next)) return ImmutableArray<byte>.Empty;
        var changed = ImmutableArray.CreateBuilder<byte>();
        foreach (var slot in current.Select(row => row.Slot).Concat(next.Select(row => row.Slot)).Distinct().Order())
        {
            var before = Find(slot);
            var committed = after.Find(slot);
            if (before is null || committed is null || !AvatarPreset.SameContent(before, committed)) changed.Add(slot);
        }
        return changed.ToImmutable();
    }

    public void Validate()
    {
        if (Capacity > MaximumCapacity || Presets.IsDefault || Presets.Length > MaximumCapacity)
            throw new InvalidDataException("装扮预设容量或数量无效");
        if (ActiveSlot is { } active && active >= Capacity)
            throw new InvalidDataException("当前选中装扮预设槽位超出容量");
        if (Presets.Select(row => row.Slot).Distinct().Count() != Presets.Length ||
            Presets.Any(row => row.Slot >= Capacity))
            throw new InvalidDataException("装扮预设槽位超出容量或重复");
        foreach (var preset in Presets)
            preset.Validate();
    }
}

internal sealed record AvatarPreset(byte Slot, string Name, ImmutableArray<AvatarPresetPart> Parts)
{
    // Record 生成的 Equals 只按 ImmutableArray 的底层数组引用比较部位，重采样后同内容也会判成不同；
    // 投影层判断是否需要补发 0x063F 必须用这个逐字段比较。
    public static bool SameContent(AvatarPreset left, AvatarPreset right) =>
        left.Slot == right.Slot && left.Name == right.Name && AvatarPresetPart.SameContent(left.Parts, right.Parts);

    // 空名是合法状态：客户端把长度0的记录显示成自己的默认标签（字符串0xB743），服务端不自造名字。
    // 非空名按CP936上网，客户端显示侧按CP936解码；编码器用异常回退，编不出的字符入库前必须拒绝。
    public void Validate()
    {
        if (Name != Name.Trim() || Name.Length > AvatarPresetState.MaximumNameLength || Name.Any(char.IsControl))
            throw new InvalidDataException("装扮预设名字无效");
        if (Name.Length > 0)
        {
            try
            {
                if (CharacterName.Encoding.GetByteCount(Name) >= 0x100)
                    throw new InvalidDataException("装扮预设名字过长");
            }
            catch (EncoderFallbackException) { throw new InvalidDataException("装扮预设名字编码无效"); }
        }
        if (Parts.IsDefault || Parts.Length > 12 ||
            Parts.Select(part => part.Slot).Distinct().Count() != Parts.Length)
            throw new InvalidDataException("装扮预设部位无效");
        foreach (var part in Parts)
            part.Validate();
    }
}

// ItemUid 是服务端内部索引：穿戴该预设时按实例定位，客户端只感知 ItemId 与外观字段。
// ClearAvatarSourceUid 是克隆外观（含转职光环装扮、梦幻克隆武器）在装扮背包中的原件实例；
// 旧存档没有该字段时按 0 读取，应用预设时保留物品自身仍然有效的来源。
internal sealed record AvatarPresetPart(byte Slot, long ItemUid, uint ItemId, uint ClearAvatarId,
    ushort Color1, ushort Color2, ImmutableArray<AvatarSocket> Sockets, long ClearAvatarSourceUid = 0)
{
    public static bool SameContent(ImmutableArray<AvatarPresetPart> left, ImmutableArray<AvatarPresetPart> right)
    {
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
            if (!SameContent(left[index], right[index])) return false;
        return true;
    }

    // 孔位是值类型 record struct，按值比较；其余字段逐个比，ItemUid 参与是因为应用预设按实例定位。
    public static bool SameContent(AvatarPresetPart left, AvatarPresetPart right) =>
        left.Slot == right.Slot && left.ItemUid == right.ItemUid && left.ItemId == right.ItemId &&
        left.ClearAvatarId == right.ClearAvatarId && left.Color1 == right.Color1 && left.Color2 == right.Color2 &&
        left.ClearAvatarSourceUid == right.ClearAvatarSourceUid &&
        left.Sockets.AsSpan().SequenceEqual(right.Sockets.AsSpan());

    public void Validate()
    {
        if (ItemUid <= 0 || ItemId == 0 || ClearAvatarId > int.MaxValue || ClearAvatarSourceUid < 0 ||
            Sockets.IsDefault || Sockets.Length != 5 || Sockets.Any(socket => socket.EmblemId < -1))
            throw new InvalidDataException("装扮预设部位详情无效");
    }
}
