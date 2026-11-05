using DfoServer.GameWorld.Towns;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class PreyTeamTcpTests
{
    static void BoardingParties(TcpTestFixture fixture, TcpPeer[] peers, TownCatalog towns, uint key, int[] positions)
    {
        That(Cross(fixture, peers[1], towns, 4).All(row => row is not { Kind: 0, Opcode: 9 }),
            "左侧只有一名玩家时保持无普通队伍，不发送单人队伍名册");
        ushort left = BoardingParty(Cross(fixture, peers[0], towns, 4), 0, (0, peers[1].Id), (1, peers[0].Id));
        That(
            BoardingParty(Read(peers[1], "left boarding member joined"), 0, (0, peers[1].Id), (1, peers[0].Id)) == left,
            "不同普雷标记按到达顺序加入同一左侧队伍，先到的普通队员成为队长");
        That(Cross(fixture, peers[2], towns, 5).All(row => row is not { Kind: 0, Opcode: 9 }),
            "右侧的单独玩家不会加入左侧队伍，也不创建单人队伍");

        var leftExit = Cross(fixture, peers[1], towns, 3);
        That(leftExit.Any(row => row is { Kind: 0, Opcode: 9 } && row.Body[9] == 3 && U16(row.Body, 4) == left),
            "回鸟背中间实际退出本人左侧队伍");
        That(Read(peers[0], "left boarding leader departed").Any(row =>
            row is { Kind: 0, Opcode: 9 } && row.Body[9] == 3 && U16(row.Body, 4) == left),
            "左侧只剩一名玩家时对剩余玩家也取消普通组队");
        ushort recreated = BoardingParty(Cross(fixture, peers[1], towns, 4), 0, (0, peers[0].Id), (1, peers[1].Id));
        That(BoardingParty(Read(peers[0], "left boarding former leader rejoined"), 0, (0, peers[0].Id), (1, peers[1].Id)) == recreated,
            "原队长重新进入时按新到达顺序追加，仍在左侧的先到者担任新队长");
        Cross(fixture, peers[1], towns, 3);
        That(Read(peers[0], "left boarding dissolved again").Any(row =>
            row is { Kind: 0, Opcode: 9 } && row.Body[9] == 3 && U16(row.Body, 4) == recreated),
            "再次剩余一人时新建队伍同样解散，不保留单人Party");
        ushort right = BoardingParty(Cross(fixture, peers[1], towns, 5), 0, (0, peers[2].Id), (1, peers[1].Id));
        That(right != recreated &&
            BoardingParty(Read(peers[2], "right boarding member joined"), 0, (0, peers[2].Id), (1, peers[1].Id)) == right,
            "不同攻坚标记的成员到同一右侧才成队，左右分开且队长取到达顺序");

        That(Cross(fixture, peers[0], towns, 3).All(row => row is not { Kind: 0, Opcode: 9 }),
            "左侧单人回中间不发送不存在的队伍退出通知");
        That(Cross(fixture, peers[1], towns, 3).Any(row =>
            row is { Kind: 0, Opcode: 9 } && row.Body[9] == 3 && U16(row.Body, 4) == right) &&
            Read(peers[2], "right boarding dissolved").Any(row =>
                row is { Kind: 0, Opcode: 9 } && row.Body[9] == 3 && U16(row.Body, 4) == right),
            "回中间退出右侧队伍，剩余一人同时解除组队");
        That(Cross(fixture, peers[2], towns, 3).All(row => row is not { Kind: 0, Opcode: 9 }),
            "右侧恢复单人后返回中间保持无普通队伍");
        var roster = ReadRoster(peers[0], key);
        That(peers.Select((peer, index) => roster[peer.Id].Position == positions[index] &&
            roster[peer.Id].Squad == positions[index] / 10).All(value => value),
            "左右混编、队长继任及中间退队均不改变整局普雷标记");
    }

    static ushort BoardingParty(List<WirePacket> rows, byte leader, params (byte Slot, ushort Character)[] expected)
    {
        byte[] body = rows.Last(row => row is { Kind: 0, Opcode: 9 } && row.Body[9] is 0 or 2).Body;
        That(U16(body) == 1 && U16(body, 2) == 9999 && body[6] == 2 && body[7] == 42,
            "鸟背自动组队使用原9的完整本频道普通队伍合同");
        int at = 11;
        if (body[9] == 0)
        {
            at += 2;
            if (body[12] == 0)
            {
                int width = checked((int)U32(body, at));
                That(width <= body.Length - at - 4, "普通队伍标题字段未截断");
                at += 4 + width;
            }
            at += 24;
        }
        int count = body[at++];
        That(body.Length == at + count * 8 + 10, "普通队伍完整消费成员及原生尾部");
        var members = Enumerable.Range(0, count).Select(index =>
            (Slot: body[at + index * 8], Character: U16(body, at + index * 8 + 1))).ToArray();
        at += count * 8;
        That(members.SequenceEqual(expected) && body[at] == 0 && body[at + 2] == leader &&
            rows.Any(row => row is { Kind: 0, Opcode: 26 } && row.Body.SequenceEqual(new[] { leader })),
            "普通队伍实际成员顺序、队长字段与UDP主机一致");
        return U16(body, 4);
    }
}
