using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.TagEvildom;
using DfoServer.GameWorld.Characters;
using DfoServer.Network.Builders;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class TagEvildomConfigurationTests
{
    static void Packets()
    {
        var configuration = new TagEvildomConfiguration(5, 0, [new(101, 509), default, default]);
        var roster = new CharacterRosterSnapshot(7, 11,
            [new(101, 0, "TagFirst", 0, 0, 95, []), new(202, 9, "TagLow", 0, 0, 94, []), new(509, 20, "TagBack", 0, 0, 95, [])]);
        var projection = new Game.TagEvildom.TagEvildomProjection(new(configuration, 1, ImmutableHashSet.Create<ushort>(509)), 2, roster, 7, 11, 101);
        byte[] identities = Build(writer =>
        {
            foreach (int index in new[] { 0, 2, -1, -1, -1, -1 })
            {
                writer.Write(index < 0 ? (byte)0 : (byte)2); writer.Write(index); writer.Write(0u);
            }
        });
        Bytes(TagEvildomPackets.Teams(projection), identities, "1677精确54B按真实原名册索引投影，既不是stableId也不重新过滤编号");
        Bytes(TagEvildomPackets.Accepted(projection), Build(writer =>
        {
            writer.Write((byte)1); writer.Write(new byte[13]); writer.Write(identities); writer.Write(0); writer.Write(0);
        }), "1662/1663成功为结果加完整75B，不回显未初始化前缀，不省成功读取尾");
        Bytes(TagEvildomPackets.ActiveTeam(projection), [2, 0, 2, 2], "1393精确4B，当前双角色使用原名册index，第二项不是队内ordinal1");
        Reject<InvalidDataException>(() => TagEvildomPackets.ActiveTeam(projection with { CharacterId = 509 }), "尚未重选队首不发布错误当前角色metadata");
        Bytes(TagEvildomPackets.Gathering(projection), Build(writer =>
        {
            writer.Write(1); writer.Write(0); writer.Write(-1); writer.Write(3);
            for (int index = 0; index < 6; index++) { writer.Write(index == 0 ? (byte)2 : (byte)0); writer.Write(index == 0 ? 2 : -1); }
        }), "1678精确46B来自真实已用计数及保护成员，无当前角色日期伪字段");

        var standby = new Character(509, 20, "备用甲", 9, new(62, 0, 543, 255), null)
        {
            Level = 95, Job = 0, GrowType = 18, EquipmentSlotFlags = 16, CloneTitleItemId = 0x11223344,
            Learning = new(500, [new(0, 321, 9, [new(7, 200, 3), new(1, 100, 2)]), new(1, 777, 4, [new(9, 300, 4)])])
        };
        var attributes = new CharacterStats(1001, 2002, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        byte[] expectedStats = new byte[89];
        BinaryPrimitives.WriteUInt32LittleEndian(expectedStats, 1001);
        BinaryPrimitives.WriteUInt32LittleEndian(expectedStats.AsSpan(4), 2002);
        expectedStats[84] = 100;
        foreach (int selected in new[] { -1, 0, 1 })
        {
            byte[] expected = Build(writer =>
            {
                writer.Write((byte)1); writer.Write((ushort)101); writer.Write((byte)1); writer.Write((byte)0);
                byte[] name = CharacterName.Encoding.GetBytes("备用甲"); writer.Write(name.Length); writer.Write(name);
                writer.Write(new byte[] { 95, 0, 18, 0, 0 }); writer.Write(0u); writer.Write((byte)0);
                writer.Write(89); writer.Write(expectedStats);
                writer.Write(new byte[] { 16, 1 }); writer.Write(0u); writer.Write((byte)0); writer.Write(0x11223344u);
                writer.Write(new byte[4]); writer.Write((byte)0); writer.Write(0x11223344u); writer.Write((byte)0);
                if (selected == 1)
                { writer.Write((ushort)777); writer.Write(new byte[] { 1, 1, 9 }); writer.Write((ushort)300); writer.Write((byte)4); }
                else
                {
                    writer.Write((ushort)321); writer.Write(new byte[] { 2, 0, 7 }); writer.Write((ushort)200); writer.Write((byte)3);
                    writer.Write((byte)1); writer.Write((ushort)100); writer.Write((byte)2);
                }
                writer.Write(0u); writer.Write(new byte[20]); writer.Write(new byte[8]); writer.Write(0UL);
            });
            Bytes(TagEvildomPackets.CharacterInfo(101, standby with { Learning = standby.Learning with { SelectedTree = selected } }, attributes, DateTimeOffset.FromUnixTimeSeconds(1700000000)), expected,
                "1394独立逐字段预期验证真实UID/备用姓名/属性/双称号/真实当前树SP和稳定skillSlot，不混另一树也不写未开通FF");
            var shieldDeck = new DfoServer.Game.Inventory.Models.KnightShieldDeck([101, 0, 102, 0, 0]);
            byte[] expectedShield = (byte[])expected.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(expectedShield.AsSpan(expectedShield.Length - 36), 101);
            BinaryPrimitives.WriteInt32LittleEndian(expectedShield.AsSpan(expectedShield.Length - 28), 102);
            var currentStandby = standby with { Learning = standby.Learning with { SelectedTree = selected } };
            Bytes(TagEvildomPackets.CharacterInfo(101, currentStandby, attributes, DateTimeOffset.FromUnixTimeSeconds(1700000000), shieldDeck),
                expectedShield, "TAG旧盾只读推导在原生既有20B位置写入，不追加字段或改变其它属性");
            Bytes(TagEvildomPackets.CharacterInfo(101, currentStandby with { Inventory = standby.Inventory with { ShieldDeck = shieldDeck } },
                attributes, DateTimeOffset.FromUnixTimeSeconds(1700000000)), expectedShield, "TAG持久化盾配置同样按原位编码");
        }
        Reject<InvalidDataException>(() => TagEvildomPackets.CharacterInfo(101, standby with { Learning = CharacterLearningState.Uninitialized }, attributes,
            DateTimeOffset.FromUnixTimeSeconds(1700000000)), "未初始化技能不以假SP/空树冒充TAG完整角色资料");
        That(standby.Revision == 9 && standby.Learning.Trees[0].Sp == 321 && standby.CloneTitleItemId == 0x11223344,
            "只读TAG构包不更改备用角色资产、技能余额或revision");
    }

    static byte[] Build(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true)) write(writer);
        return stream.ToArray();
    }
}
