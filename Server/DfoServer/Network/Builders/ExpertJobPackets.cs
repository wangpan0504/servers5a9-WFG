namespace DfoServer.Network.Builders;

internal static class ExpertJobPackets
{
    public static byte[] Fail(ushort error)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)0);
        writer.Write(error);
        return stream.ToArray();
    }

    // ACK 239 023624B0：success 后 03C85290 u32、03C851B0 u8、03C851B0 u8；失败走公共 error:u16（10/19）。
    public static byte[] GiveUpSuccess() => GiveUpSuccess(0, 0);

    public static byte[] GiveUpSuccess(uint gold, byte state)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(gold);
        writer.Write(state);
        writer.Write((byte)0);
        return stream.ToArray();
    }

    // ACK235 必须带金币和耐久尾部；所有产物都来自已提交的实际回执。
    public static byte[] DisjointResult(ushort owner, DfoServer.Game.Inventory.Models.DisjointReceipt receipt, uint gold, uint endurance)
    {
        if (receipt.Rewards.Length > byte.MaxValue || receipt.Rewards.Any(row => row.Slot < -1 || row.ItemId <= 0 || row.Count <= 0))
            throw new InvalidDataException("机器分解回执超出已确认包体");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1); writer.Write((ushort)receipt.Request.Position.Index);
        writer.Write((byte)receipt.Request.Position.List); writer.Write(owner); writer.Write((byte)receipt.Rewards.Length);
        foreach (var row in receipt.Rewards)
        { writer.Write(row.Slot); writer.Write(row.ItemId); writer.Write(row.Count); }
        writer.Write(gold); writer.Write(endurance);
        return stream.ToArray();
    }

    // ACK 238 0236F770：count 次 (物品 u32, 数量 u32)，随后两个 u32 和一个 u8。
    public static byte[] CompoundResult(IReadOnlyList<(int ItemId, int Count)> items, uint successCount, uint failureCount)
    {
        if (items.Count > byte.MaxValue || successCount > int.MaxValue || failureCount > int.MaxValue)
            throw new InvalidDataException("副职业合成产物超出已确认应答");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write((byte)items.Count);
        foreach (var item in items)
        {
            if (item.ItemId <= 0 || item.Count <= 0) throw new InvalidDataException("副职业合成产物无效");
            writer.Write(item.ItemId);
            writer.Write(item.Count);
        }
        writer.Write(successCount);
        writer.Write(failureCount);
        writer.Write((byte)0);
        return stream.ToArray();
    }

    public static byte[] CompoundEmpty() => CompoundResult([], 0, 0);

    // ACK 601 0234FE71：u8 标志非 0 才 event17(11009)，随后 u32、u8、三个 u32。
    public static byte[] EnchantStoreSuccess(uint endurance, uint level, uint rarity, bool succeeded = true)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write((byte)(succeeded ? 1 : 0));
        writer.Write(0u);
        writer.Write((byte)0);
        writer.Write(endurance);
        writer.Write(level);
        writer.Write(rarity);
        return stream.ToArray();
    }

    // ACK 598 0234FD90 成功不读附加正文。
    public static byte[] CreateStoreSuccess() => [1];

    // NOTI 539 023CFC30→0209ECC0：03C85220 一个 u16，交给 02D90700 查演员。
    public static byte[] CloseStoreNotice(ushort actor)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(actor);
        return stream.ToArray();
    }

    // 538的可变字段是等级字节串，不是玩家店名；场景、坐标与费用不能照抄598字段序。
    public static byte[] CreateStoreNotice(DfoServer.Game.ExpertJob.ExpertJobStoreRecord store)
    {
        if (store.Grade is < 1 or > 11) throw new InvalidDataException("副职业商店等级不合法");
        return CreateStoreNotice(store.Mode, store.OwnerId, ((char)store.Grade).ToString(), null,
            store.Town, store.Area, store.Second, store.Third, store.First);
    }

    // NOTI 538 023DA6E0：mode:u8（0/3）+ actor:u16 + 03BE25F0 的 u32 长度+字节 + u8 + u32 + u16 + u16 + u32 + u8。
    // mode3追加资格数组；标量依次为场景、区域、X、Y、服务费、对象类别。等级串使用原始1..11字节，不是ASCII数字。
    public static byte[] CreateStoreNotice(byte mode, ushort actor, string name = "", byte[]? extra = null,
        byte flag = 0, uint first = 0, ushort second = 0, ushort third = 0, uint fourth = 0, byte last = 0)
    {
        if (mode is not (0 or 3)) throw new InvalidDataException("副职业开店通知模式不是 0 或 3");
        extra ??= [];
        if (mode == 3 && extra.Length > byte.MaxValue) throw new InvalidDataException("副职业开店通知附加列表过长");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(mode);
        writer.Write(actor);
        PacketEncoding.WriteString(writer, name);
        writer.Write(flag);
        writer.Write(first);
        writer.Write(second);
        writer.Write(third);
        writer.Write(fourth);
        writer.Write(last);
        if (mode == 3)
        {
            writer.Write((byte)extra.Length);
            writer.Write(extra);
        }
        return stream.ToArray();
    }

    // NOTI 544 023DA980：u8 + actor:u16 + 03BE25F0 字符串；u8==3 时同样跟 count:u8 列表。
    public static byte[] UpdateStoreNotice(byte mode, ushort actor, string name = "", byte[]? extra = null)
    {
        if (mode == 3 && extra is { Length: > byte.MaxValue }) throw new InvalidDataException("副职业商店更新附加列表过长");
        extra ??= [];
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(mode);
        writer.Write(actor);
        PacketEncoding.WriteString(writer, name);
        if (mode == 3)
        {
            writer.Write((byte)extra.Length);
            writer.Write(extra);
        }
        return stream.ToArray();
    }

    // NOTI 205 023D81A0：state:u8、mode:u8。mode1 为 n×u32 配方、m×u8 资格、再两个 u32；mode2/4 只有配方；mode3 是档位+耐久。
    public static byte[] Info(byte mode, IReadOnlyList<int> recipes, uint first = 0, uint second = 0,
        IReadOnlyList<byte>? rarities = null, byte giveUpState = 0)
    {
        if (mode is 0 or > 4) throw new InvalidDataException("NOTI 205 不使用 mode 0 发布副职业");
        if (recipes.Count > byte.MaxValue || rarities?.Count > byte.MaxValue)
            throw new InvalidDataException("副职业学习列表超出协议容量");
        var list = recipes;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(giveUpState);
        writer.Write(mode);
        if (mode is 1 or 2 or 4)
        {
            writer.Write((byte)list.Count);
            foreach (int id in list) writer.Write((uint)id);
            if (mode == 1)
            {
                writer.Write(checked((byte)(rarities?.Count ?? 0)));
                if (rarities is not null)
                    foreach (byte rarity in rarities) writer.Write(rarity);
                writer.Write(first);
                writer.Write(second);
            }
        }
        else
        {
            writer.Write(first);
            writer.Write(second);
        }
        return stream.ToArray();
    }

    public static byte[] Info(DfoServer.Game.Characters.Character character, DfoServer.GameWorld.Items.ExpertJobScriptCatalog scripts,
        bool includeQualifications = false, byte? learnedRarity = null)
    {
        byte mode = character.ExpertJobType;
        int level = scripts.Level(mode, character.ExpertJobExperience);
        var recipes = scripts.LearnedRecipes(mode, level, character.ExpertJobRecipes);
        byte[] rarities = includeQualifications ? scripts.LearnedRarities(recipes) : learnedRarity is { } rarity ? [rarity] : [];
        return mode switch
        {
            3 => Info(3, [], (uint)character.ExpertMachineGrade, (uint)character.ExpertMachineEndurance, giveUpState: (byte)character.ExpertGiveUpState),
            1 => Info(1, recipes, (uint)level, (uint)character.ExpertMachineEndurance, rarities, (byte)character.ExpertGiveUpState),
            2 or 4 => Info(mode, recipes, giveUpState: (byte)character.ExpertGiveUpState),
            _ => throw new InvalidDataException("NOTI 205 只发布类型 1–4")
        };
    }

    // ACK 416 02371A30：success、目标列表 u8、目标槽 u16、count u8、count×(槽 u16, 物品 u32, 数量 u32)。
    public static byte[] ExtractionResult(byte list, ushort slot, IReadOnlyList<(ushort Slot, uint ItemId, uint Count)> products)
    {
        if (products.Count > byte.MaxValue) throw new InvalidDataException("副职业萃取产物超出已确认应答");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(list);
        writer.Write(slot);
        writer.Write((byte)products.Count);
        foreach (var product in products)
        {
            writer.Write(product.Slot);
            writer.Write(product.ItemId);
            writer.Write(product.Count);
        }
        return stream.ToArray();
    }

    // ACK 240 023598D0：success + gold u32 + grade u32 + endurance u32。
    public static byte[] UpgradeMachineSuccess(uint gold, uint grade, uint endurance)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(gold);
        writer.Write(grade);
        writer.Write(endurance);
        return stream.ToArray();
    }

    // ACK 236 0234CDA0：success + gold u32 + endurance u32。
    public static byte[] RepairMachineSuccess(uint gold, uint endurance)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(gold);
        writer.Write(endurance);
        return stream.ToArray();
    }

    // ACK 611 02350080：成功不读附加字段。
    public static byte[] RepairStoreSuccess() => [1];

    // ACK599先由公共分派消费success:u8；02367836检查success，023678D4再读kind。
    // 成功尾部kind3为owner:u16+endurance:u32，kind0为grade:u8+cost:u32+endurance:u32。
    public static byte[] EnterStoreKind0(byte grade, uint cost, uint endurance)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write((byte)0);
        writer.Write(grade);
        writer.Write(cost);
        writer.Write(endurance);
        return stream.ToArray();
    }

    public static byte[] EnterStoreKind3(ushort owner, uint endurance)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write((byte)3);
        writer.Write(owner);
        writer.Write(endurance);
        return stream.ToArray();
    }
}
