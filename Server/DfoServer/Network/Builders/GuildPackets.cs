using DfoServer.Game.Guild;

namespace DfoServer.Network.Builders;

internal static class GuildPackets
{
    public static byte[] Invite(GuildPermit permit)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        PacketEncoding.WriteString(writer, permit.Name);
        writer.Write(permit.Region);
        PacketEncoding.WriteString(writer, permit.InitiatorName);
        PacketEncoding.WriteString(writer, permit.Promote);
        return stream.ToArray();
    }

    public static byte[] Created(uint guildId, string guildName)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(guildId);
        PacketEncoding.WriteString(writer, guildName);
        return stream.ToArray();
    }

    public static byte[] Entered(uint guildId, ushort characterId, string guildName, string characterName)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(guildId);
        writer.Write((uint)characterId);
        PacketEncoding.WriteString(writer, guildName);
        PacketEncoding.WriteString(writer, characterName);
        return stream.ToArray();
    }

    public static byte[] Information(GuildMembership guild, byte region, GuildPanel? panel = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        PacketEncoding.WriteString(writer, guild.Name);
        writer.Write(panel?.Management is { } permissions && (permissions.Permissions & 2) != 0 ? (byte)1 : (byte)0);
        writer.Write((byte)0);
        writer.Write(guild.MemberCount);
        writer.Write((byte)0);
        PacketEncoding.WriteString(writer, guild.Notice);
        writer.Write(guild.Level);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write(guild.Appearance.Mark);
        writer.Write(guild.Experience);
        PacketEncoding.WriteString(writer, guild.MasterName);
        PacketEncoding.WriteString(writer, guild.MasterName);
        writer.Write(panel?.OnlineCount ?? (ushort)0);
        PacketEncoding.WriteString(writer, panel?.Management?.Promotion ?? "");
        if (panel?.Management is { } management)
            writer.Write(GuildGovernancePackets.Roles(management.Roles));
        else if (panel is null)
            writer.Write(new byte[168]);
        else
        {
            // 只提供成员页的职位名称；管理权限尚未接入，不能宣告任何权限已启用。
            foreach (string name in new[] { "", "会长", "副会长", "优秀会员", "普通会员", "" })
            {
                writer.Write(0u);
                byte[] bytes = DfoServer.Game.Characters.CharacterName.Encoding.GetBytes(name);
                writer.Write(bytes);
                writer.Write(new byte[24 - bytes.Length]);
            }
        }
        if (panel?.Economy is { } economy) writer.Write(GuildEconomyPackets.Benefits(economy));
        else writer.Write((byte)0);
        // 实机探针（@257=111111、@312=444444、@316=555555、@320=333333）确认：本版客户端不消费权益块之后的
        // @257/@262（90CN 在同一结构位写的是公会公共积分），公会资金实际来自成长尾的 @316。
        writer.Write(0u);
        writer.Write((byte)0);
        writer.Write(0u);
        writer.Write((byte)0);
        writer.Write((uint)(panel?.Management?.RecommendedChannel?.ServerIndex ?? 0));
        writer.Write((uint)(panel?.Management?.RecommendedChannel?.Number ?? 0));
        writer.Write(guild.Appearance.AuraMark);
        writer.Write(guild.Appearance.AuraFrame);
        writer.Write((ushort)0);
        writer.Write(panel?.Management?.Webpage ?? (byte)0);
        writer.Write(region);
        writer.Write(panel?.Management?.Tags ?? 0u);
        writer.Write(panel?.Management?.JoinMode ?? (byte)0);
        writer.Write(guild.CreatedAtUnix);
        writer.Write((uint)guild.Tier);
        writer.Write((uint)guild.Level);
        // 023DB934起读取现代成长尾：累计经验为8B；末个u32同时供NPC升级材料检查。
        writer.Write((ulong)guild.Experience);
        writer.Write(0u); // @312 本版不消费（哨兵 444444 无处显示）。
        // @316 才是公会资金显示位：哨兵 555555 实测渲染为「公会资金 555,555」。
        writer.Write(checked((uint)(panel?.Economy?.PublicFunds ?? 0)));
        writer.Write(guild.Crystals); // @320 公会水晶：哨兵 333333 实测显示。
        writer.Write((byte)0);
        return stream.ToArray();
    }

    // 02365BE0：67/140首包含名称及总数，141续包仅追加行；140/141末尾还有4B保留引用。
    public static byte[] Members(GuildPanel panel, int offset, int count, bool first, bool all, long? nowUnix = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(panel.Guild.Id);
        if (first)
        {
            PacketEncoding.WriteString(writer, panel.Guild.Name);
            writer.Write(checked((ushort)panel.Members.Length));
        }
        writer.Write(checked((ushort)count));
        long now = nowUnix ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // 02DA62C7按代表ID分组，02DAAC11要求每组恰有代表；使用完整名册，不能按分块或在线状态选代表。
        var representatives = panel.Members.GroupBy(member => member.Account, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Min(member => member.Id), StringComparer.Ordinal);
        foreach (var member in panel.Members.AsSpan(offset, count))
        {
            writer.Write((uint)member.Id);
            PacketEncoding.WriteString(writer, member.Name);
            WriteMemberMemo(writer, member.Memo);
            writer.Write(member.Level);
            writer.Write(member.Job);
            writer.Write(member.GrowType);
            writer.Write(member.Region);
            writer.Write((byte)0); // 原生+180标记，不把大区号填成资格标记。
            writer.Write(member.Channel);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write(member.Grade);
            // 只使用自然生命周期维护的观测值；旧0值仍未知，原生界面会落在最小时间档。
            long elapsed = member.Channel == byte.MaxValue && member.LastLoginUnix > 0
                ? Math.Max(0, now - member.LastLoginUnix) : 0;
            writer.Write((uint)Math.Min(elapsed, uint.MaxValue));
            ushort representative = representatives[member.Account];
            writer.Write((uint)representative);
            writer.Write((byte)(member.Id == representative ? 1 : 0));
            byte offline = member.Channel == byte.MaxValue ? (byte)1 : (byte)0;
            PacketEncoding.WriteString(writer, "");
            writer.Write(offline);
            writer.Write(member.Region);
            writer.Write((byte)0);
            writer.Write((byte)0);
            // 原生行模型+188为本周贡献，+190为累计；中间+18C另有语义，不能挪作贡献。
            writer.Write(member.WeeklyContribution);
            writer.Write(0u);
            writer.Write(member.Contribution);
            writer.Write((byte)0);
            writer.Write((byte)0);
        }
        if (all) writer.Write(0u);
        return stream.ToArray();
    }

    // 0236A530跳转02369660：1455首包额外读总数，1456只追加职位行。
    public static byte[] MemberGrades(GuildPanel panel, int offset, int count, bool first)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(panel.Guild.Id);
        if (first) writer.Write(checked((ushort)panel.Members.Length));
        writer.Write(checked((ushort)count));
        foreach (var member in panel.Members.AsSpan(offset, count))
        {
            writer.Write(member.Region);
            PacketEncoding.WriteString(writer, member.Name);
            writer.Write(member.Grade);
            // 原生状态与转让限制标记；不是完整成员行的账号代表标记。
            writer.Write((byte)0);
            writer.Write((byte)0);
        }
        return stream.ToArray();
    }

    static void WriteMemberMemo(BinaryWriter writer, string memo)
    {
        // 本版成员备注读取缓冲为0x15；按完整CP936字符截取显示，不修改持久文本。
        var encoding = DfoServer.Game.Characters.CharacterName.Encoding;
        int length = memo.Length;
        while (length > 0 && encoding.GetByteCount(memo.AsSpan(0, length)) > 20) length--;
        PacketEncoding.WriteString(writer, memo[..length]);
    }
}
