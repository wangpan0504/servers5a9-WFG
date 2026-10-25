using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Characters;
using DfoServer.Network.Builders.Inventory;

namespace DfoServer.Network.Builders;

internal static partial class TownPackets
{
    public static byte[] PeerMenu(ushort target, ushort party = ushort.MaxValue)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(target);
        writer.Write((byte)15);
        writer.Write(0u); // NOTI7公共字段；mode15不使用。
        // 023D1D47：FFFF表示无队伍；其余必须使用当前队伍ID。
        writer.Write(party);
        return stream.ToArray();
    }

    public static byte[] OtherPlayer(Character character, CharacterStats attributes, int adventureLevel,
        ChannelIdentity channel, DateTimeOffset utcNow)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)3);
        writer.Write((ushort)1);
        writer.Write(channel.ServerIndex);
        writer.Write(channel.Number);
        // 0267CC75读取9B公开头；本人subtype1的17B熟练头与此布局不同。
        writer.Write(checked((uint)adventureLevel));
        writer.Write((ushort)0);
        // 02D83080/02D83150/02D83100分别将这三项装回装备容器的bit3/4/5。
        writer.Write((byte)((character.EquipmentSlotFlags & 8) == 0 ? 0 : 1));
        writer.Write((byte)((character.EquipmentSlotFlags & 16) == 0 ? 0 : 1));
        writer.Write((byte)((character.EquipmentSlotFlags & 32) == 0 ? 0 : 1));
        writer.Write(character.Id);
        writer.Write(character.Learning.Experience);
        byte[] stats = CharacterStatsPackets.Build(attributes, StaminaRecoveryService.Percent(character.Inventory, utcNow));
        writer.Write(stats.Length);
        writer.Write(stats);
        writer.Write(character.EquipmentSlotFlags);
        EquipmentProtocolWriter.Player(writer, character.Inventory, utcNow.ToUnixTimeSeconds());
        writer.Write(character.CloneTitleItemId);
        writer.Write((byte)0); // 023C3A90装备尾部，与本人相同。
        // 公开资料不调用0267BFA0，不能插入本人的BUFF换装登记块。
        EquipmentProtocolWriter.NameTag(writer, character.Inventory, utcNow.ToUnixTimeSeconds());
        writer.Write(character.Skills.Length == 0 ? (byte)0xff : (byte)0);
        for (int tree = 0; tree < 2; tree++)
        {
            CharacterSkill[] skills = character.Learning.Initialized
                ? character.Learning.Trees.Single(value => value.Context == tree).Skills.OrderBy(skill => skill.Id)
                    .Select(skill => new CharacterSkill(skill.Id, skill.Level)).ToArray()
                : character.Skills;
            writer.Write(checked((byte)skills.Length));
            foreach (var skill in skills)
            {
                writer.Write(skill.Id);
                writer.Write(skill.Level);
            }
        }
        writer.Write((byte)0);
        // 0267A540：三个u32、四个u8、u8数量及其u32列表；尚无决斗场成绩，保留空状态。
        writer.Write(new byte[17]);
        writer.Write(0u); // 0267D182。
        // 02678F60：u16、u32、公会名DSTR、两个u8；其余字段语义待确认，保留零。
        writer.Write((ushort)0);
        writer.Write(0u);
        PacketEncoding.WriteString(writer, character.Guild?.Name ?? "");
        writer.Write(new byte[2]);
        writer.Write(0u); // 02676510空列表。
        writer.Write((byte)0); // 0267A7F0空列表。
        writer.Write((byte)0); // 0267B480空状态；公开资料没有本人的皮肤仓库尾部。
        return stream.ToArray();
    }
}
