using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Guild;
using DfoServer.Network.Parsers;
using DfoServer.Network.Builders;
using DfoServer.Game.Characters;

namespace DfoServer;

/// <summary>
/// 建会认证申请的解析与登记表：请求体来自实机抓包，登记表只保存内存态、取出即删、过期视为不存在。
/// </summary>
internal static class GuildPermitTests
{
    public static void RunCore()
    {
        int before = Count;

        // 实机样本：大区 2 +「admin」。
        byte[] sample = [0x02, 0x05, 0x00, 0x00, 0x00, 0x61, 0x64, 0x6D, 0x69, 0x6E];
        var (region, name) = GuildRequests.Permit(sample);
        That(region == 2 && name == "admin", "★ 实机样本解出大区序号 2 与角色名「admin」");
        That(GuildRequests.PermitReply([0x01]) && !GuildRequests.PermitReply([0x00]),
            "认证答复 1=接受、0=拒绝");
        Reject<InvalidDataException>(() => GuildRequests.Permit([0x02, 0x05, 0x00, 0x00]), "认证邀请正文过短必须拒绝");
        Reject<InvalidDataException>(() => GuildRequests.Permit([0x02, 0x05, 0x00, 0x00, 0x00, 0x61]), "正文宽度与长度前缀不符必须拒绝");
        Reject<InvalidDataException>(() => GuildRequests.PermitReply([0x01, 0x00]), "认证答复正文宽度必须恰为 1 字节");

        var registry = new GuildPermitRegistry();
        var permit = new GuildPermit(2, "测试公会", "test-guild", "介绍", 4, "10000", 5, "test2", 1_700_000_060);
        registry.Register(permit);
        That(registry.Count == 1, "登记后存在一条待认证申请");
        That(registry.Take(5, 1_700_000_000) == permit, "被邀请角色答复时能取回原申请");
        That(registry.Count == 0 && registry.Take(5, 1_700_000_000) is null, "取出即删除，不重复使用");

        registry.Register(permit);
        That(registry.Take(5, 1_700_000_061) is null && registry.Count == 0,
            "★ 超过截止时间的申请视为不存在（客户端 60 秒超时后自行发拒绝，服务端不返回半成品）");

        registry.Register(permit);
        var second = permit with { InviteeCharacterId = 9, InviteeAccount = "test3" };
        registry.Register(second);
        That(registry.Count == 1 && registry.Take(9, 1_700_000_000) == second,
            "同一创建者重复邀请同一目标会覆盖旧条目，只保留最新一条");
        That(registry.Take(5, 1_700_000_000) is null, "被覆盖的旧条目不再可取");

        var cancelled = registry.CancelByInitiator(4);
        That(cancelled is null && registry.Count == 0, "已被覆盖的旧被邀请者不会因取消而复活");
        registry.Register(permit);
        That(registry.CancelByInitiator(4) == permit && registry.Count == 0,
            "创建者取消时能取回申请（供服务端通知被邀请方）");

        var competing = new GuildPermitRegistry();
        competing.Register(permit);
        var replacement = permit with { InitiatorCharacterId = 8, InitiatorAccount = "other-creator" };
        competing.Register(replacement);
        That(competing.CancelByInitiator(4) is null && competing.Find(5, 1_700_000_000) == replacement,
            "其他创建者覆盖邀请后，旧创建者取消不能删除新申请");
        competing.Register(permit);
        competing.Register(replacement with { InviteeCharacterId = 9, InviteeAccount = "test3" });
        That(competing.Find(5, 1_700_000_000) == permit && competing.Find(9, 1_700_000_000)?.InitiatorCharacterId == 8,
            "旧创建者换目标重新邀请不能删除其他创建者当前有效的申请");

        // 入城补投用的查询：不移除（补投之后被邀请方仍要能拒绝），过期条目顺手清掉。
        registry.Register(permit);
        That(registry.Find(5, 1_700_000_000) == permit && registry.Count == 1,
            "★ 入城补投的查询不移除申请：被邀请方仍能用 0x2E6[00] 拒绝");
        That(registry.Find(6, 1_700_000_000) is null && registry.Count == 1, "没有指向该角色的申请时补投什么都不发");
        That(registry.Find(5, 1_700_000_061) is null && registry.Count == 0,
            "★ 补投查询顺手清掉过期条目（既不补投也不能再被答复）");
        registry.Register(permit);
        That(registry.Take(5, 1_700_000_000) == permit, "补投查询之后答复仍能取出原申请");

        // 创建窗口草稿：`0x2E5` 只带大区序号与目标角色名，公会名/地址/介绍来自查重（0x9C/0x9D）与介绍校验（0x2E3）。
        var drafts = new GuildPermitRegistry();
        That(drafts.Draft(7, 1_700_000_000) is null, "没有查重成功的角色没有建会草稿");
        drafts.RememberName(7, "测试公会", 1_700_000_000);
        drafts.RememberPromote(7, "介绍", 1_700_000_001);
        var draft = drafts.Draft(7, 1_700_000_002);
        That(draft is { Name: "测试公会", Promote: "介绍" } && draft.Address.Length == 0 && draft.AddressOrName == "测试公会",
            "★ 名称与介绍各自记账，域名缺失时回退为公会名（本版客户端没有域名输入框，address 又是唯一非空）");
        drafts.RememberAddress(7, "test-guild", 1_700_000_003);
        That(drafts.Draft(7, 1_700_000_003)?.AddressOrName == "test-guild", "客户端报过域名时用域名建会");
        That(drafts.Draft(7, 1_700_000_003 + GuildPermitRegistry.DraftLifetimeSeconds + 1) is null && drafts.DraftCount == 0,
            "★ 超过草稿存活时间的文本视为不存在：不沿用上一次创建的公会名");
        drafts.RememberName(8, "另一个公会", 1_700_000_000);
        drafts.ClearDraft(8);
        That(drafts.Draft(8, 1_700_000_000) is null && drafts.DraftCount == 0, "关闭创建窗口后丢弃草稿");
        var notice = permit with { Name = "TeaParty", Promote = "测试", InitiatorName = "gunner" };
        byte[] invitation = [8,0,0,0,84,101,97,80,97,114,116,121,2,6,0,0,0,103,117,110,110,101,114,4,0,0,0,0xB2,0xE2,0xCA,0xD4];
        Bytes(GuildPackets.Invite(notice), invitation, "685包含完整DSTR及位于字符串之间的大区2，不触发缺省文本或错读大区");
        That(Created(GuildPackets.Created(0x01020304, "TeaParty"), "TeaParty") == 0x01020304,
            "71在公会ID之后读取真实名称");
        That(Entered(GuildPackets.Entered(0x01020304, 4, "TeaParty", "gunner"), 4, "TeaParty", "gunner") == 0x01020304,
            "58的公会ID与角色ID不能反序，后续两条文本也不能省略");
        var membership = new GuildMembership(0x01020304, "TeaParty", 1, 2, "gunner", "", 0x1234, 1_700_000_000);
        Information(GuildPackets.Information(membership, 2), "TeaParty", "gunner", 2, 2, 0x1234);
        Console.WriteLine($"PASS {Count - before} 建会认证申请的解析、登记表与创建窗口草稿断言");
    }

    internal static uint Created(byte[] body, string name)
    {
        using var reader = new BinaryReader(new MemoryStream(body));
        uint id = reader.ReadUInt32();
        That(id > 0 && Text(reader) == name && reader.BaseStream.Position == body.Length,
            "独立读取71的ID、CP936名称及完整边界");
        return id;
    }

    internal static uint Entered(byte[] body, ushort characterId, string guildName, string characterName)
    {
        using var reader = new BinaryReader(new MemoryStream(body));
        uint id = reader.ReadUInt32();
        That(id > 0 && reader.ReadUInt32() == characterId && Text(reader) == guildName && Text(reader) == characterName &&
            reader.BaseStream.Position == body.Length, "独立读取58的公会、角色及两条CP936文本");
        return id;
    }

    internal static void Information(byte[] body, string name, string master, ushort members, byte region, uint experience = 0,
        ushort? online = null, byte canInvite = 0)
    {
        using var reader = new BinaryReader(new MemoryStream(body));
        That(Text(reader) == name && reader.ReadByte() == canInvite && reader.ReadByte() == 0 &&
            reader.ReadUInt16() == members && reader.ReadByte() == 0 && Text(reader) == "" && reader.ReadByte() == 1,
            "70的名称、成员数量、公告与等级按匹配客户端顺序读取");
        That(reader.ReadByte() == 0 && reader.ReadByte() == 0 && reader.ReadUInt32() == 0 && reader.ReadUInt32() == experience &&
            Text(reader) == master && Text(reader) == master && reader.ReadUInt16() == (online ?? 0) && Text(reader) == "",
            "徽章为u32，不套用其它版本u8；后续名称与列表仍对齐");
        byte[] roles = reader.ReadBytes(168);
        That(roles.Length == 168, "固定职位表完整");
        if (online is not null) That(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(roles.AsSpan(28)) ==
            GuildRoleRules.PermissionMask, "自然入会70保留真实会长权限表，不再被零表覆盖");
        byte benefits = reader.ReadByte();
        for (int index = 0; index < benefits; index++)
        {
            reader.ReadByte(); reader.ReadUInt32(); reader.ReadByte(); reader.ReadUInt32();
        }
        That(reader.ReadUInt32() == 0 && reader.ReadByte() == 0 && reader.ReadUInt32() == 0 && reader.ReadByte() == 0,
            "公会扩展状态保留空编码");
        That(reader.ReadUInt32() == 0 && reader.ReadUInt32() == 0 &&
            reader.ReadUInt32() == uint.MaxValue && reader.ReadUInt32() == uint.MaxValue,
            "公会扩展无额外状态，未选光环使用本版成对清除值");
        That(reader.ReadUInt16() == 0 && reader.ReadByte() == 0 && reader.ReadByte() == region &&
            reader.ReadUInt32() == 0 && reader.ReadByte() == 0 && reader.ReadUInt32() > 0, "大区与真实创建日期保持原生位置");
        That(reader.ReadUInt32() == 0 && reader.ReadUInt32() == 1, "新公会真实档位0和档内等级1");
        That(reader.ReadUInt64() == experience, "现代成长尾累计经验与既有经验投影一致");
        for (int index = 0; index < 3; index++) That(reader.ReadUInt32() == 0, "新公会保留字段及结晶余额为零");
        That(reader.ReadByte() == 0 && reader.BaseStream.Position == body.Length, "70消费完所有字段，无截断、错位或多余尾部");
    }

    static string Text(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("公会DSTR长度越界");
        return CharacterName.Encoding.GetString(reader.ReadBytes(length));
    }
}
