using System.Buffers.Binary;
using DfoServer.Game.TagEvildom;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Builders.Inventory;

namespace DfoServer.Network.Builders;

internal static class TagEvildomPackets
{
    public static byte[] CharacterInfo(ushort ownerUid, Character standby, CharacterStats attributes, DateTimeOffset now,
        DfoServer.Game.Inventory.Models.KnightShieldDeck? knightShieldDeck = null)
    {
        standby.Learning.Validate();
        if (!standby.Learning.Initialized) throw new InvalidDataException("TAG备用角色须有真实已初始化的技能树");
        var tree = standby.Learning.Trees.Single(row => row.Context == standby.Learning.ActiveContext);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(ownerUid);
        writer.Write((byte)1);
        writer.Write((byte)0); // 0177A3EA已消费但不再使用；不是角色索引。
        PacketEncoding.WriteString(writer, standby.Name);
        writer.Write(standby.Level);
        writer.Write(standby.Job);
        writer.Write(standby.GrowType);
        // 与普通Actor state[23]/[24]/[25..28]同源，当前本服该附属状态未启用。
        writer.Write((byte)0); writer.Write((byte)0); writer.Write(0u);
        writer.Write(standby.IsPvpPrivate ? (byte)1 : (byte)0);
        byte[] stats = CharacterStatsPackets.Build(attributes, StaminaRecoveryService.Percent(standby.Inventory, now));
        writer.Write(stats.Length); writer.Write(stats);
        writer.Write(standby.EquipmentSlotFlags);
        writer.Write((byte)((standby.EquipmentSlotFlags & 16) != 0 ? 1 : 0));
        writer.Write(standby.Inventory.Skins.Selected);
        EquipmentProtocolWriter.Appearance(writer, standby.Inventory);
        writer.Write(standby.CloneTitleItemId);
        // 与普通Actor称号后四字节相同：DTO+264/+268及两个未使用的消费字节。
        writer.Write(new byte[4]);
        EquipmentProtocolWriter.Player(writer, standby.Inventory, now.ToUnixTimeSeconds());
        writer.Write(standby.CloneTitleItemId);
        EquipmentProtocolWriter.Runes(writer, standby.Inventory);
        writer.Write(tree.Sp);
        writer.Write(checked((byte)tree.Skills.Count));
        writer.Write(tree.Context);
        foreach (var skill in tree.Skills)
        {
            writer.Write(skill.Slot); writer.Write(skill.Id); writer.Write(skill.Level);
        }
        writer.Write(0u); // 与普通Player的02676510空列表相同。
        // 028C7A17只在TAG分支读取五个i32；普通本人Player使用独立NOTI580初始化。
        writer.Write(KnightShieldPackets.Deck(knightShieldDeck ?? standby.Inventory.ShieldDeck));
        EquipmentProtocolWriter.NameTag(writer, standby.Inventory, now.ToUnixTimeSeconds());
        // 未启用Privilege Service，与普通Actor state[88]=0一致；不能拿黑钻/达人期限代替。
        writer.Write(0L);
        return stream.ToArray();
    }

    static Dictionary<ushort, int> Indices(TagEvildomProjection projection) => projection.Roster.Entries
        .Select((row, index) => (row.Id, index)).ToDictionary(row => row.Id, row => row.index);

    public static byte[] ActiveTeam(TagEvildomProjection projection)
    {
        var configuration = projection.State.Configuration;
        var team = configuration.Teams[configuration.SelectedTeam];
        if (team.Empty || team.First != projection.CharacterId) throw new InvalidDataException("TAG运行资料须对应当前已登录的队首角色");
        var indices = Indices(projection);
        return [projection.Server, checked((byte)indices[team.First]), projection.Server, checked((byte)indices[team.Second])];
    }

    public static byte[] Teams(TagEvildomProjection projection)
    {
        projection.State.Configuration.Validate();
        var indices = Indices(projection);
        var body = new byte[54];
        int offset = 0;
        foreach (var team in projection.State.Configuration.Teams)
        {
            foreach (ushort member in new[] { team.First, team.Second })
            {
                body[offset] = member == 0 ? (byte)0 : projection.Server;
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(offset + 1), member == 0 ? -1 : indices[member]);
                // 015476B5..EC 原生6项均写aux=0，不把稳定角色ID伪装成该字段。
                offset += 9;
            }
        }
        return body;
    }

    public static byte[] Accepted(TagEvildomProjection projection)
    {
        var body = new byte[76];
        body[0] = 1;
        Teams(projection).CopyTo(body, 14);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(68), projection.State.Configuration.SelectedTeam);
        // 客户端成功分支读足75B；不回显输入未初始化的13B及未消费尾4B。
        return body;
    }

    public static byte[] Gathering(TagEvildomProjection projection)
    {
        var indices = Indices(projection);
        var body = new byte[46];
        BinaryPrimitives.WriteInt32LittleEndian(body, projection.State.UsedThisWeek);
        // offset4/8沿未开始run的构造初态0/-1；mode沿实际已提交准入恢复，首次保持本版初值3。
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), -1);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(12), projection.State.Mode);
        var protectedIndices = projection.State.ProtectedMembers.Where(indices.ContainsKey).Select(id => indices[id]).Order().ToArray();
        if (protectedIndices.Length > 6) throw new InvalidDataException("再战魔界受保护名册超出6项");
        for (int index = 0; index < 6; index++)
        {
            body[16 + index * 5] = index < protectedIndices.Length ? projection.Server : (byte)0;
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(17 + index * 5), index < protectedIndices.Length ? protectedIndices[index] : -1);
        }
        return body;
    }
}
