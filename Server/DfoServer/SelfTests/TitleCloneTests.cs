using System.Buffers.Binary;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer;

internal static class TitleCloneTests
{
    public static void RunCore()
    {
        That(TitleRequests.CloneTitle([0x1c, 0xf1, 0xfa, 0x05]) == 100331804 &&
            TitleRequests.CloneTitle([0, 0, 0, 0]) == 0 &&
            TitleRequests.CloneTitle([255, 255, 255, 255]) == uint.MaxValue,
            "动画请求保留完整4字节ID，0清除；模板是否合法由领域资格处理");
        foreach (int length in new[] { 0, 3, 5 })
            Reject<InvalidDataException>(() => TitleRequests.CloneTitle(new byte[length]), "动画请求拒绝截断与多余尾部");
        Bytes(TitlePackets.CloneTitle(100331804), [1, 0x1c, 0xf1, 0xfa, 0x05], "动画成功回执是成功头与实际选择ID，共5字节");
        Bytes(TitlePackets.CloneTitle(0), [1, 0, 0, 0, 0], "清除动画明确回零，不沿用旧选择");
        Bytes(TitlePackets.CloneTitleFailure(0x1234), [0, 0x34, 0x12], "动画失败沿公共u16错误头，不追加来源槽位或ID");

        var stats = new CharacterStats(8550, 5150, 123, 45, 67, 89, -10, 20, -30, 40,
            480000, 525, 800, 100, 100, 6000, 100, 600);
        var now = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        foreach (string name in new[] { "CloneTitle", "称号动画" })
        {
            var original = new Character(1, 0, name, 0, new(1, 0, 10, 10), null);
            foreach (uint id in new uint[] { 100331804, uint.MaxValue })
            {
                var changed = original with { CloneTitleItemId = id };
                Check(TownPackets.Actor(original, utcSeconds: now.ToUnixTimeSeconds()),
                    TownPackets.Actor(changed, utcSeconds: now.ToUnixTimeSeconds()), 134 + NameWidth(name), id, "Actor");
                Check(TownPackets.Player(original, stats, 1, utcNow: now),
                    TownPackets.Player(changed, stats, 1, utcNow: now), 127, id, "本人Player");
                Check(TownPackets.OtherPlayer(original, stats, 1, default, now),
                    TownPackets.OtherPlayer(changed, stats, 1, default, now), 119, id, "他人公开资料");
                var roster = CharacterRosterEntry.FromCharacter(changed);
                That(roster.CloneTitleItemId == id, "轻量名册保留动画选择，不要求重新加载完整背包");
                Check(CharacterPackets.Roster([CharacterRosterEntry.FromCharacter(original)]),
                    CharacterPackets.Roster([roster]), 37 + NameWidth(name), id, "名册尾部");
            }
        }
    }

    static void Check(byte[] zero, byte[] actual, int offset, uint id, string consumer)
    {
        That(U32(zero, offset) == 0, consumer + "缺省动画为0");
        // 在独立计算的字段偏移写期望值，完整比较确保字段之外的装备、属性和尾部均不变。
        BinaryPrimitives.WriteUInt32LittleEndian(zero.AsSpan(offset), id);
        Bytes(actual, zero, consumer + "只替换独立动画u32，不移位或覆盖其它字段");
    }
}
