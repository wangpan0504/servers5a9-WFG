using DfoServer.Game.Characters;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PlayerInteractionTests
{
    public static void RunCore()
    {
        int before = Count;
        That(ChannelPackets.Endpoint("127.0.0.1", 12345, 22)[3] == 22 &&
            ChannelPackets.Endpoint("127.0.0.1", 12345, 59)[3] == 59,
            "LOGIN模式跟随实际频道类型，普通与特殊入口不能固定为0或22");
        byte[] menu = Convert.FromHexString("34120F00000000000000000000");
        That(CharacterRequests.TryPeerMenu(menu, out ushort target) && target == 0x1234, "实测13B右键请求识别目标");
        That(!CharacterRequests.TryPeerMenu([0x34, 0x12, 2, 0, 0, 0, 0, 0, 0, 0, 0], out _),
            "其他peer模式保持未实现，不套用mode15布局");
        byte[] auxiliary = menu.ToArray();
        auxiliary[9] = 1;
        That(!CharacterRequests.TryPeerMenu(auxiliary, out _), "未知辅助字段不返回通用成功");
        Reject<InvalidDataException>(() => CharacterRequests.TryPeerMenu(menu[..^1], out _), "截断的mode15拒绝");
        Bytes(TownPackets.PeerMenu(0x1234), Convert.FromHexString("34120F00000000FFFF"),
            "NOTI7 mode15独立客户端消费布局：目标、模式、公共u32、无队伍FFFF");
        That(CharacterRequests.TryOtherPlayer([0x34, 0x12, 3], out target) && target == 0x1234 &&
            !CharacterRequests.TryOtherPlayer([255, 255, 2], out _) && CharacterRequests.IsRoster([255, 255, 2]),
            "公开资料不吞掉既有选角名册握手");
        Reject<InvalidDataException>(() => CharacterRequests.TryOtherPlayer([1, 0, 3, 0], out _), "资料请求尾部拒绝");
        var character = new Character(0x1234, 0, "PublicTarget", 1, new(38, 0, 622, 196), null)
        {
            EquipmentSlotFlags = 56,
            Skills = [new(5, 2), new(46, 3)],
            Learning = CharacterLearningState.Uninitialized with { Experience = 0x0102030405060708UL },
            Guild = new(9, "目标公会", 1, 1, "会长", "", 0, 0)
        };
        var stats = new DfoServer.GameWorld.Characters.CharacterStats(8550, 5150, 123, 45, 67, 89, -10, 20, -30, 40,
            480000, 525, 800, 100, 100, 6000, 100, 600);
        byte[] body = TownPackets.OtherPlayer(character, stats, 37, new(64, 12), DateTimeOffset.UnixEpoch);
        using var stream = new MemoryStream(body);
        using var reader = new BinaryReader(stream);
        That(reader.ReadByte() == 3 && reader.ReadUInt16() == 1 && reader.ReadByte() == 64 && reader.ReadByte() == 12,
            "USERINFO公开资料分派及目标频道");
        That(reader.ReadUInt32() == 37 && reader.ReadUInt16() == 0 && reader.ReadByte() == 1 &&
            reader.ReadByte() == 1 && reader.ReadByte() == 1 && reader.ReadUInt16() == 0x1234 &&
            reader.ReadUInt64() == 0x0102030405060708UL, "9B公开头后紧跟目标ID和完整u64经验，不使用17B本人头");
        That(reader.ReadUInt32() == 89 && reader.ReadUInt32() == 8550 && reader.ReadUInt32() == 5150,
            "目标属性块长度和生命魔法值保持");
        reader.ReadBytes(81);
        That(reader.ReadByte() == 56 && reader.ReadByte() == 0 && reader.ReadUInt32() == 0 && reader.ReadByte() == 0,
            "装备标志、空装备列表及装备尾部");
        That(reader.ReadUInt64() == 0 && reader.ReadByte() == 0, "装备后直接读8B名称装饰和技能树，不插入本人BUFF登记块");
        for (int tree = 0; tree < 2; tree++)
            That(reader.ReadByte() == 2 && reader.ReadUInt16() == 5 && reader.ReadByte() == 2 &&
                reader.ReadUInt16() == 46 && reader.ReadByte() == 3, "公开技能树完整消费：" + tree);
        That(reader.ReadByte() == 0 && reader.ReadBytes(21).All(value => value == 0), "公开状态和决斗场空列表不偏移");
        That(reader.ReadUInt16() == 0 && reader.ReadUInt32() == 0, "公会辅助字段保留");
        int width = reader.ReadInt32();
        That(CharacterName.Encoding.GetString(reader.ReadBytes(width)) == "目标公会" && reader.ReadUInt16() == 0,
            "公开公会名使用CP936长度前缀");
        That(reader.ReadUInt32() == 0 && reader.ReadByte() == 0 && reader.ReadByte() == 0 && stream.Position == stream.Length,
            "公开尾部列表与状态完全消费，没有本人的皮肤仓库数据");
        Console.WriteLine($"PASS {Count - before} 玩家菜单与公开资料协议断言");
    }
}
