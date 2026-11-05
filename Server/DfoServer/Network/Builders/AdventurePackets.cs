using System.Buffers.Binary;
using DfoServer.Game.Adventure;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
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
    // 推荐地下城、三类团本与双塔100层使用各自账号级真实累计；无双决斗场暂无统计，保持0。
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
        // 内容熟练度使用独立u32，不借用收藏分类表；团本与双塔不沿用推荐地下城的u16上限。
        var counts = snapshot.ContentCounts;
        U32(raw, RecommendedClearOffset, counts.Length > AdventureContent.RecommendedDungeon
            ? counts[AdventureContent.RecommendedDungeon] : 0u);
        U32(raw, LukeRaidClearOffset, snapshot.RaidClears.Luke);
        U32(raw, MetroRaidClearOffset, snapshot.RaidClears.Fiendwar);
        U32(raw, PreyRaidClearOffset, snapshot.RaidClears.Prey);
        U32(raw, DuelWinOffset, snapshot.DuelWins);
        U32(raw, DespairTowerClearOffset, snapshot.DespairTowerClears);
        U32(raw, LamentTowerClearOffset, snapshot.LamentTowerClears);
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
        // 20组收藏分类状态：每组type:u8@0、count:u8@1、complete:u8@2。客户端01034220按4块×15B把该表整体复制到收藏对象+0x98，
        // 显示侧FUN_00D8F4B0逐槽取u16@0与byte@2到角色簿显示槽+0x12C/+0x12D/+0x12E，
        // FUN_00D8F1C0再按“有符号byte0>=0、有符号byte1>0、byte2!=0”决定左侧分类按钮金色且可点，否则置灰禁用。
        // 因此count只能占1字节（超过127会被当作负数），byte2是整类集齐标志而不是计数高字节。
        // 1477回包由01034DC0用同一3字节结构按byte0匹配分类并写回该槽，两处布局必须一致。
        // 该表不是内容熟练度，后者是上面的独立u32；未收集分类的count与complete都写0。
        var collection = snapshot.Collections(catalog);
        for (int type = 0; type < AdventureCatalog.CollectionGroupCount; type++)
        {
            raw[ContentCountsOffset + type * 3] = (byte)type;
            raw[ContentCountsOffset + type * 3 + 1] = (byte)Math.Min((int)collection[type].Count, 127);
            raw[ContentCountsOffset + type * 3 + 2] = collection[type].Complete ? (byte)1 : (byte)0;
        }
        U64(raw, CapsuleOffset, snapshot.State.CapsuleExperience);
        U32(raw, ClaimsOffset, Claims(snapshot.State, catalog));
        writer.Write(raw.Length); writer.Write(raw); PacketEncoding.WriteString(writer, snapshot.State.Name);
    });

    public static byte[] Refresh(ushort actor, AdventureState state, AdventureCatalog catalog) => Build(writer =>
    {
        writer.Write(actor);
        var raw = new byte[RefreshRawLength];
        U32(raw, 0, checked((uint)state.Level)); U64(raw, 8, state.Experience);
        // 客户端0102F910把RAW+16起的u64直接写入管理器+0x190；1343消费者01034220对同一字段改用
        // 经验表按level+1自行计算后写入。两条路径写同一份「下一级所需经验」，本服必须下发同一结果：
        // 留0会让每个1349（入城、商店、远征、胶囊提取）到达后把该字段覆盖成0，面板经验显示失真。
        U64(raw, 16, catalog.Experience.GetValueOrDefault(state.Level + 1));
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
    // 1477成功回包：ACK、分类结构长度u32=3、type:u8、count:u8、complete:u8、收藏行长度u32=52、52B行。
    // 客户端01034DC0把这3字节整体写回收藏表对应槽并复用显示刷新，布局必须与1343表完全一致；
    // count超过127会被显示侧当作负数而置灰，宽度写错还会让52B行整体错位。
    public static byte[] Representative(AdventureProfession profession, AdventureCollection collection, AdventureMember member) => Build(writer =>
    {
        writer.Write((byte)1); writer.Write(3u); writer.Write(profession.Group);
        writer.Write((byte)Math.Min((int)collection.Count, 127)); writer.Write(collection.Complete ? (byte)1 : (byte)0);
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
        // 成员行u32是角色熟练度而不是等级：客户端01111FA0把它装入远征视图，成员行写95（等级）时卡片恒显示
        // 「熟练者46」（95属熟练者50–99档，95-49=46）。运行地区沿用冻结身份，熟练度按UID从当前名册投影；
        // 冻结快照没有该字段，成员已不在名册时退回冻结等级，保持原显示与行宽。
        var proficiency = snapshot.Members.ToDictionary(row => row.Id, row => row.Proficiency);
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
                writer.Write(proficiency.GetValueOrDefault(member.Id, (uint)member.Level)); writer.Write(member.Job); writer.Write(member.Grow); writer.Write((byte)0);
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
    // 718服务器角色列表：server u8、count u8，每行 u32角色、u32名称长度、CP936名称、u8标志、
    // u16等级、u8职业、u8分支、u8二觉。客户端只把等级95的行装进面板，职业名按「职业、分支(<6)、二觉(<=2)」查表。
    // 外传职业（黑暗武士9/缔造者10）的CHR资源没有分支与二觉名表，发送分支0、二觉0走基础名。
    public static byte[] ServerCharacters(byte server, IReadOnlyList<AdventureWeeklyCharacter> rows) => Build(writer =>
    {
        int count = Math.Min(rows.Count, byte.MaxValue);
        writer.Write(server); writer.Write(checked((byte)count));
        for (int i = 0; i < count; i++)
        {
            var row = rows[i];
            byte[] bytes = Prefix(row.Name, ServerCharacterNameMaxBytes);
            bool foreign = row.Job is 9 or 10;
            writer.Write(row.Id); writer.Write(checked((uint)bytes.Length)); writer.Write(bytes);
            writer.Write((byte)0); writer.Write(row.Level); writer.Write(row.Job);
            writer.Write(foreign ? (byte)0 : AdventureCatalog.ProfessionGrow(row.Job, row.Grow));
            writer.Write(foreign ? (byte)0 : (byte)(row.Grow >> 4));
        }
    });

    // 721：5个槽位原样回放，0xFF表示未选择；不按服务端规则重排客户端提交的排列。
    public static byte[] WeeklyConfig(IReadOnlyList<byte> slots) => Build(writer =>
    {
        foreach (byte slot in slots) writer.Write(slot);
    });

    // 720：空正文，清空客户端每周地下城信息列表。
    public static byte[] WeeklyReset() => Build(_ => { });

    // 719每周地下城信息：u8 key、u8 count，每行 u32名称长度 + CP936名称 + 0x51字节记录。
    // 记录布局：A[9]u32已用次数@0、B[9]u32高亮阈值@0x24、C[9]u8是否参与显示@0x48。
    // 客户端显示 max(列上限 - A, 0)；A==-1 或 C==0 显示"-"。次数来自角色本周实际入场，B没有服务端来源保持0。
    // names 为空表示整份名册（客户端打开面板后仍会按可见行逐名请求，回复用请求里的 key）。
    public static byte[] WeeklyDungeonInfo(byte key, IReadOnlyList<AdventureWeeklyCharacter> rows,
        IReadOnlyList<string>? names = null) => Build(writer =>
    {
        var selected = names is null ? rows.ToArray() : Requested(rows, names);
        int count = Math.Min(selected.Length, byte.MaxValue);
        writer.Write(key); writer.Write(checked((byte)count));
        var block = new byte[0x51];
        for (int i = 0; i < WeeklyDungeonPolicy.SlotCount; i++) block[0x48 + i] = 1;
        for (int i = 0; i < count; i++)
        {
            for (int slot = 0; slot < WeeklyDungeonPolicy.SlotCount; slot++)
                U32(block, slot * 4, unchecked((uint)selected[i].UsedAt(slot)));
            byte[] bytes = Prefix(selected[i].Name, ServerCharacterNameMaxBytes);
            writer.Write(checked((uint)bytes.Length)); writer.Write(bytes); writer.Write(block);
        }
    });

    // 按请求名的顺序回包；同名只回一条，未知名跳过（客户端可能缓存了刚删除的角色）。
    static AdventureWeeklyCharacter[] Requested(IReadOnlyList<AdventureWeeklyCharacter> rows, IReadOnlyList<string> names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var selected = new List<AdventureWeeklyCharacter>(names.Count);
        foreach (string name in names)
        {
            if (!seen.Add(name)) continue;
            foreach (var row in rows)
                if (row.Name == name) { selected.Add(row); break; }
        }
        return selected.ToArray();
    }

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
