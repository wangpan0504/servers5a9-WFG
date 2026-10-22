using System.Buffers.Binary;
using DfoServer.Game.Adventure;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Adventure;

namespace DfoServer.Network.Builders;

internal static class AdventurePackets
{
    public const int InformationRawLength = 7440;
    public const int RefreshRawLength = 40;
    public const int LoginDaysOffset = 20;
    // 基本信息页「内容熟练度」由客户端按独立 u32 读取（2026-10-22 哨兵探针+实机截图逐项对上）：
    // 推荐地下城通关@16、卢克攻坚@28、超时空漩涡@32、普雷·伊希斯攻坚战@36、
    // 无双决斗场获胜@44、绝望之塔100层@48、悲叹之塔100层@52。
    // 当前只有推荐地下城通关有账号级累计来源；其余六项尚无统计，保持 0，不借用收藏表或其他字段。
    public const int RecommendedClearOffset = 16;
    public const int LukeRaidClearOffset = 28;
    public const int MetroRaidClearOffset = 32;
    public const int PreyRaidClearOffset = 36;
    public const int DuelWinOffset = 44;
    public const int DespairTowerClearOffset = 48;
    public const int LamentTowerClearOffset = 52;
    public const int RosterOffset = 72;
    public const int ShopOffset = 5272;
    public const int PurchasesOffset = 5312;
    public const int ContentCountsOffset = 7360;
    public const int CapsuleOffset = 7424;
    public const int ClaimsOffset = 7432;

    public static byte[] Ack(ushort error = 0) => Build(writer =>
    {
        writer.Write(error == 0 ? (byte)1 : (byte)0);
        if (error != 0) writer.Write(error);
    });
    public static byte[] AccountName(string name) => Build(writer =>
    {
        PacketEncoding.WriteString(writer, name);
        writer.Write((byte)0);
    });
    public static byte[] NameAccepted(string name) => Build(writer =>
    {
        writer.Write((byte)1);
        PacketEncoding.WriteString(writer, name);
    });
    public static byte[] Information(ushort actor, AdventureSnapshot snapshot, AdventureCatalog catalog,
        IReadOnlyList<AdventureMember> representatives, bool ack = false) => Build(writer =>
    {
        if (ack) writer.Write((byte)1);
        writer.Write(actor); writer.Write(checked((uint)snapshot.State.CreatedDate));
        writer.Write(0u); writer.Write(0u);
        var raw = new byte[InformationRawLength];
        U32(raw, 0, checked((uint)snapshot.State.Level)); U64(raw, 8, snapshot.State.Experience);
        U32(raw, LoginDaysOffset, checked((uint)snapshot.State.LoginStreakDays));
        // 内容熟练度按内容各自独立的 u32 下发；当前只有推荐地下城通关有账号级累计来源，
        // 其余六项没有统计数据，保持 0，不借用收藏分类表伪造。
        var counts = snapshot.ContentCounts;
        U32(raw, RecommendedClearOffset, counts.Length > AdventureContent.RecommendedDungeon
            ? counts[AdventureContent.RecommendedDungeon] : 0u);
        U16(raw, 68, checked((ushort)snapshot.Members.Length));
        AdventureMember? best = null;
        int at = 0;
        foreach (var profession in catalog.Professions.Values.OrderBy(row => row.Job).ThenBy(row => row.Grow))
        {
            var member = representatives.SingleOrDefault(row => row.Job == profession.Job && row.ProfessionGrow == profession.Grow);
            if (member is null && !snapshot.State.Representatives.Any(row => row.Job == profession.Job && row.Grow == profession.Grow)) continue;
            var row = raw.AsSpan(RosterOffset + 52 * at++, 52);
            row[0] = profession.Job; row[1] = profession.Grow;
            if (member is null) continue;
            WriteCollection(row, member);
            // 同熟练度取目录顺序靠前者，保持每次构包稳定。
            if (best is null || member.Proficiency > best.Proficiency) best = member;
        }
        // 基本信息页「最高熟练度角色」卡片：1343消费者01034220把RAW+16起14个dword搬到管理器+0xD4，
        // 页面从+0xFC/+0xFD读职业与成长、从+0x100读熟练度，值为0时只显示占位。
        // 布局与S5A3同源（本版整体+12B）：job@56、grow@57、熟练度u32@60；没有真实候选时保持0。
        if (best is not null)
        {
            raw[56] = best.Job; raw[57] = best.ProfessionGrow;
            U32(raw, 60, best.Proficiency);
        }
        for (int i = 0; i < 5; i++)
        {
            U32(raw, ShopOffset + i * 8, snapshot.State.Points[i]);
            if (i == 0) U32(raw, ShopOffset + 4, checked((uint)snapshot.State.BraveExperience));
        }
        at = 0;
        foreach (var pair in snapshot.State.Purchases.OrderBy(pair => pair.Key))
        {
            U32(raw, PurchasesOffset + at * 8, checked((uint)pair.Key)); U32(raw, PurchasesOffset + at++ * 8 + 4, pair.Value);
        }
        // 20组分类计数：count u16在前、type u8在后。90CN同源结构把该表用于收藏分类而非内容熟练度
        // （内容熟练度已改为上面的独立u32）；本版暂无收藏统计来源，计数保持0，不借内容累计充当。
        for (byte type = 0; type < AdventureContent.Count; type++)
            raw[ContentCountsOffset + type * 3 + 2] = type;
        U64(raw, CapsuleOffset, snapshot.State.CapsuleExperience);
        U32(raw, ClaimsOffset, Claims(snapshot.State, catalog));
        writer.Write(raw.Length); writer.Write(raw); PacketEncoding.WriteString(writer, snapshot.State.Name);
    });

    public static byte[] Refresh(ushort actor, AdventureState state, AdventureCatalog catalog) => Build(writer =>
    {
        writer.Write(actor);
        var raw = new byte[RefreshRawLength];
        U32(raw, 0, checked((uint)state.Level)); U64(raw, 8, state.Experience);
        U64(raw, 24, state.CapsuleExperience); U32(raw, 32, Claims(state, catalog));
        writer.Write(raw.Length); writer.Write(raw); writer.Write(new byte[4]);
    });
    // 客户端按胶囊格数展示可领取次数；经验上限可能覆盖多格，但显示不能超过资源声明的最大格数。
    public static uint Claims(AdventureState state, AdventureCatalog catalog) =>
        (uint)Math.Min(catalog.Capsule.MaximumGauges, state.CapsuleExperience / catalog.Capsule.MinimumExperience);

    public static byte[] Candidates(IReadOnlyList<AdventureMember> rows, byte region) => Build(writer =>
    {
        writer.Write((byte)1); writer.Write(rows.Count == 0 ? (byte)0 : (byte)1);
        if (rows.Count == 0) return;
        writer.Write(region); writer.Write(checked((byte)rows.Count));
        foreach (var member in rows)
        {
            var row = new byte[52]; row[0] = region;
            U32(row, 4, member.Proficiency); U32(row, 8, member.Level); U32(row, 12, member.Id);
            Name(row.AsSpan(20, 30), member.Name); writer.Write(row);
        }
    });
    public static byte[] Representative(AdventureProfession profession, ushort count, AdventureMember member) => Build(writer =>
    {
        writer.Write((byte)1); writer.Write(3u); writer.Write(profession.Group); writer.Write(count);
        var row = new byte[52]; WriteCollection(row, member);
        writer.Write(52u); writer.Write(row);
    });

    static void WriteCollection(Span<byte> row, AdventureMember member)
    {
        row[0] = member.Job; row[1] = member.ProfessionGrow;
        U32(row, 4, member.Proficiency); Name(row.Slice(12, 30), member.Name);
        // 010314C0 把 u8@42 读进显示结构+2C；选角卡片右上角的熟练角标按熟练等级分档取图标，
        // 原先写频道 ServerIndex 会让所有角色恒显示同一档。超过255沿用最高档，不按u8回绕。
        row[42] = checked((byte)Math.Min(member.Proficiency, byte.MaxValue));
        // 010314C0 把 word@44 读进显示结构+30；该字段的客户端用途尚未确认，
        // 「最高熟练度」卡片实际读取报文头的 RAW+56/57/60，不再依赖这里选人。
        U16(row, 44, (ushort)Math.Min(member.Proficiency, ushort.MaxValue));
        U32(row, 48, member.Level);
    }


    public static byte[] Expedition(AdventureSnapshot snapshot, DfoServer.GameWorld.Adventure.AdventureExpeditionCatalog catalog, IReadOnlyList<AdventureMember> representatives,
        IReadOnlyDictionary<byte, byte[]> attributes, DateTimeOffset now, bool ack = true) => Build(writer =>
    {
        // NOTI同步和ACK441均先读一个success/状态字节，再消费同一远征信息列表。
        if (ack) writer.Write((byte)1);
        writer.Write(checked((byte)(catalog.Areas.Count + 1)));
        WriteArea(0, null, [], representatives.Select((row, selector) => new ExpeditionMember(row.Id, checked((byte)selector),
            row.Name, row.Job, row.ProfessionGrow, row.Level)).Where(row => !snapshot.State.Expeditions.Any(run => run.Members.Contains(row.Id))).ToArray());
        foreach (var area in catalog.Areas.Keys.Order())
        {
            var run = snapshot.State.Expeditions.SingleOrDefault(row => row.Area == area);
            WriteArea(area, run, run?.Attributes.ToArray() ?? attributes[area], run is null ? [] :
                run.FrozenMembers.ToArray());
        }
        void WriteArea(byte area, AdventureExpedition? run, byte[] desired, IReadOnlyList<ExpeditionMember> members)
        {
            writer.Write(area); writer.Write(run is null ? (byte)0 : run.Ends <= now.ToUnixTimeSeconds() ? (byte)2 : (byte)1);
            writer.Write(checked((uint)(run?.Started ?? 0))); writer.Write(checked((uint)(run?.Ends ?? 0)));
            writer.Write(checked((byte)desired.Length)); writer.Write(desired);
            writer.Write(checked((ushort)members.Count));
            foreach (var member in members)
            {
                writer.Write((ushort)member.Selector); PacketEncoding.WriteString(writer, member.Name);
                writer.Write((uint)member.Level); writer.Write(member.Job); writer.Write(member.Grow); writer.Write((byte)0);
            }
        }
    });
    public static byte[] ExpeditionReward(uint reward) => Build(writer => { writer.Write((byte)1); writer.Write(reward); });

    static void Name(Span<byte> target, string name)
    {
        byte[] bytes = CharacterName.Encoding.GetBytes(name);
        if (bytes.Length >= target.Length) throw new InvalidDataException("冒险团角色名超过固定30B字段");
        bytes.CopyTo(target);
    }
    public const int ServerCharacterNameMaxBytes = 29;
    // 718服务器角色列表：server u8、count u8，每行u32角色、u32名称长度、CP936名称、尾u8 0。
    public static byte[] ServerCharacters(byte server, IReadOnlyList<(uint Id, string Name)> rows) => Build(writer =>
    {
        int count = Math.Min(rows.Count, byte.MaxValue);
        writer.Write(server); writer.Write(checked((byte)count));
        for (int i = 0; i < count; i++)
        {
            byte[] bytes = Prefix(rows[i].Name, ServerCharacterNameMaxBytes);
            writer.Write(rows[i].Id); writer.Write(checked((uint)bytes.Length)); writer.Write(bytes); writer.Write((byte)0);
        }
    });

    // 旧库角色名可能超过角色列表的窄字段；按完整字符截断，不拆开双字节字符。
    static byte[] Prefix(string name, int limit)
    {
        var encoding = CharacterName.Encoding;
        if (encoding.GetByteCount(name) <= limit) return encoding.GetBytes(name);
        int bytes = 0, chars = 0;
        foreach (char ch in name)
        {
            int width = encoding.GetByteCount(ch.ToString());
            if (bytes + width > limit) break;
            bytes += width; chars++;
        }
        return encoding.GetBytes(name[..chars]);
    }
    static void U16(Span<byte> data, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data[at..], value);
    static void U32(Span<byte> data, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data[at..], value);
    static void U64(Span<byte> data, int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data[at..], value);
    static byte[] Build(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        write(writer); return stream.ToArray();
    }
}
