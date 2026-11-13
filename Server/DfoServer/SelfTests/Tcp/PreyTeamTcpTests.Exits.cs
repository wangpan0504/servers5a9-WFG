using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class PreyTeamTcpTests
{
    static async Task ExitAsync(TcpTestFixture fixture, TcpPeer[] peers, TownCatalog towns, RaidResources definition,
        LukeRaidClockTests.Clock clock, uint key, int[] positions)
    {
        That(Cross(fixture, peers[0], towns, 4).All(row => row is not { Kind: 0, Opcode: 9 }),
            "首名玩家进入降落侧仍是无普通队伍的单人");
        ushort party = BoardingParty(Cross(fixture, peers[1], towns, 4), 0, (0, peers[0].Id), (1, peers[1].Id));
        That(BoardingParty(Read(peers[0], "exit party formed"), 0, (0, peers[0].Id), (1, peers[1].Id)) == party,
            "第二人进入同侧才与先到团长组成真实队伍");
        That(BoardingParty(Cross(fixture, peers[2], towns, 4), 0,
            (0, peers[0].Id), (1, peers[1].Id), (2, peers[2].Id)) == party,
            "第三种攻坚标记的成员按到达顺序加入同一实际队伍");
        foreach (var peer in peers.Take(2))
            That(BoardingParty(Read(peer, "third exit party member"), 0,
                (0, peers[0].Id), (1, peers[1].Id), (2, peers[2].Id)) == party,
                "原两成员同步完整三人实际队伍");

        const string ownedItems = "SELECT * FROM character_inventory_items WHERE item_uid NOT IN (SELECT item_uid FROM mailbox_attachments) ORDER BY character_id,item_uid";
        string inventory = fixture.Snapshot(ownedItems);
        peers[0].Send(665, Build(writer => writer.Write(peers[0].Id)));
        var departed = Read(peers[0], "active raid leave");
        Ack(departed, 665, [1, 0], "主动退出原665使用非强退成功标志");
        That(Returned(departed, peers[0], 1) && Removed(departed, party),
            "主动退团从鸟背侧区退出真实Party并以原24回赛丽亚出口街区59/1");
        var remaining = peers.Skip(1).ToArray();
        foreach (var peer in remaining)
            That(BoardingParty(Read(peer, "captain left raid"), 0, (0, peers[1].Id), (1, peers[2].Id)) == party,
                "团长退出后余两名仍保持原实际Party，由先到的剩余成员继任队长");
        var roster = ReadRoster(peers[1], key);
        That(roster.Count == 2 && !roster.ContainsKey(peers[0].Id) && remaining.All(peer =>
            roster[peer.Id].Position == positions[Array.IndexOf(peers, peer)] &&
            roster[peer.Id].Squad == positions[Array.IndexOf(peers, peer)] / 10),
            "主动退团只移除本人，余两名攻坚成员及整局标记保留");

        int total = definition.Script.Headers.Single(row => row.Tag == "[phase total time over]").Values.Single().Number;
        clock.Advance(total);
        var failed = remaining.Select(_ => new List<WirePacket>()).ToArray();
        int movieSeconds = definition.Script.Headers.Single(row => row.Tag == "[movie end time]").Values.Max(row => row.Number);
        long movieDue = checked(clock.GetTimestamp() + movieSeconds * clock.TimestampFrequency);
        // 观察两个等待都已登记，再推进可控时钟；收到了587本身不能证明异步执行器已开始等待。
        for (int attempt = 0; attempt < 100 && clock.PendingOneShots.Count(due => due == movieDue) < remaining.Length; attempt++)
        {
            for (int index = 0; index < remaining.Length; index++)
                failed[index].AddRange(Read(remaining[index], "failure movie before return"));
            if (clock.PendingOneShots.Count(due => due == movieDue) < remaining.Length) await Task.Delay(10);
        }
        That(clock.PendingOneShots.Count(due => due == movieDue) >= remaining.Length,
            "每位留团成员均已写完失败演出两帧并按资源期限登记可取消等待");
        for (int index = 0; index < remaining.Length; index++)
            failed[index].AddRange(Read(remaining[index], "failure movie written barrier"));
        foreach (var rows in failed)
            That(rows.Any(row => row is { Kind: 0, Opcode: 601, Body: [3, 0, 0, 0, 0, 0, 0, 0, 0, 0] }) &&
                rows.Any(row => row is { Kind: 0, Opcode: 587, Body: [3, 0] }) &&
                rows.All(row => row is not { Kind: 0, Opcode: 24, Body: [59, 2, ..] }),
                "失败先在原场景发送601和587/state3，不提前退回悬崖");
        clock.Advance(movieSeconds - 1);
        foreach (var peer in remaining)
        {
            peer.Send(1361, Build(writer => { writer.Write((byte)2); writer.Write(key); }));
            var during = Read(peer, "failure movie command barrier");
            That(Position(fixture, peer).Area == 4 && during.Any(row => row is { Kind: 1, Opcode: 1361 } &&
                RaidInformationState(row.Body, key) == 3),
                "演出期间保留原地点及state3，等待不占命令门禁，末秒之前不退场或用名册提前清电影");
            failed[Array.IndexOf(remaining, peer)].AddRange(during);
        }
        clock.Advance(1);
        for (int attempt = 0; attempt < 100 && remaining.Where((peer, index) => !FailureDisplayed(failed[index], peer)).Any(); attempt++)
        {
            for (int index = 0; index < remaining.Length; index++)
                failed[index].AddRange(Read(remaining[index], "failure return continuation"));
            if (remaining.Where((peer, index) => !FailureDisplayed(failed[index], peer)).Any())
                await Task.Delay(10);
        }
        for (int index = 0; index < remaining.Length; index++)
        {
            remaining[index].Send(1361, Build(writer => { writer.Write((byte)2); writer.Write(key); }));
            failed[index].AddRange(Read(remaining[index], "failed raid locations"));
            var information = failed[index].Last(row => row is { Kind: 1, Opcode: 1361, Body: [1, 2, ..] }).Body;
            var locations = RosterLocations(information);
            That(locations.Count == 2 && remaining.All(peer => locations.GetValueOrDefault(peer.Id) == 2),
                "失败后的完整1361地点将余两名更新为备战区2，不残留原侧区4");
            int arrival = failed[index].FindLastIndex(row => row is { Kind: 0, Opcode: 24, Body: [59, 2, ..] });
            int result = failed[index].FindIndex(row => row is { Kind: 0, Opcode: 601 });
            int movie = failed[index].FindIndex(row => row is { Kind: 0, Opcode: 587, Body: [3, 0] });
            int ended = failed[index].FindIndex(row => row is { Kind: 0, Opcode: 587, Body: [6, 0] });
            Bytes(failed[index].Single(row => row is { Kind: 0, Opcode: 601 }).Body,
                [3, 0, 0, 0, 0, 0, 0, 0, 0, 0], "失败601保留完整10B和当前phase0，不伪造成功结算统计");
            That(Returned(failed[index], remaining[index], 2) && Removed(failed[index], party) &&
                result >= 0 && movie > result && arrival > movie && ended > arrival &&
                failed[index].Count(row => row is { Kind: 0, Opcode: 601 }) == 1 &&
                failed[index].Count(row => row is { Kind: 0, Opcode: 587, Body: [6, 0] }) == 1 &&
                failed[index].All(row => row.Opcode is not (600 or 665)),
                "总时限失败先播放原场景失败演出，再以原24回59/2解除Party，最后发587终态，保留攻坚队且不展示成功奖励");
            That(RaidInformationState(information, key) == 6 &&
                failed[index].Skip(ended + 1).All(row => row is not { Kind: 0, Opcode: 587, Body: [2, 0] }),
                "失败后的完整1361及通知保持原生state6，不能重新安装战斗态");
        }
        clock.Advance(1);
        foreach (var peer in remaining)
        {
            peer.Send(1361, Build(writer => { writer.Write((byte)2); writer.Write(key); }));
            var replayed = Read(peer, "failed raid snapshot replay");
            That(replayed.All(row => row is not { Kind: 0, Opcode: 601 or 587 or 594 } && row.Opcode != 600) &&
                RaidInformationState(replayed.Single(row => row is { Kind: 1, Opcode: 1361, Body: [1, 2, ..] }).Body, key) == 6,
                "失败后重复tick和完整查询保持state6，不重播601、587、飞鸟或奖励");
        }
        roster = ReadRoster(peers[1], key);
        That(roster.Count == 2 && remaining.All(peer =>
            roster[peer.Id].Position == positions[Array.IndexOf(peers, peer)] &&
            roster[peer.Id].Squad == positions[Array.IndexOf(peers, peer)] / 10),
            "失败回备战区仍保留原攻坚成员和整局标记");
        That(fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE raid_type=3") is [[0L]] &&
            fixture.Query("SELECT count(*) FROM character_raid_runs WHERE raid_type=3 AND (clear_week IS NOT NULL OR json_extract(phase0_receipt,'$.Eligible')=1 OR json_extract(phase1_receipt,'$.Eligible')=1)") is [[0L]],
            "未清图失败没有成功资格、周通关或奖励收据");
        foreach (var peer in remaining)
        {
            peer.Send(665, Build(writer => writer.Write(peer.Id)));
            var left = Read(peer, "failed raid voluntary leave");
            Ack(left, 665, [1, 0], "失败后仍通过本人665主动退出原攻坚队");
            That(Returned(left, peer, 1), "失败后主动退团才由59/2回赛丽亚出口街区59/1");
        }
        foreach (var peer in peers)
        {
            RejectTravel(fixture, peer, Area(59, 2, 27, 378, 59, 1), 2,
                "主动离团后仍不能借真实街区门进入59/2备战区");
            RejectTravel(fixture, peer, BoardingRequest, 3,
                "失效的原登船请求不能在退团后重新进入59/3鸟背", reply: false);
        }
        That(inventory == fixture.Snapshot(ownedItems) &&
            fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE raid_type=3") is [[0L]],
            "主动退团、超时和拒绝旧入场不重扣门票或发奖，退票附件不改原背包");
        foreach (var peer in peers)
            That(fixture.Query("SELECT count(*) FROM mailbox_messages WHERE recipient_id=$p0 AND delivery_key LIKE 'prey-refund:%'", peer.Id)
                is [[var mails]] && Convert.ToInt64(mails) == (peer == peers[0] ? 0 : 1),
                "主动离团者不退票，留团成员失败后各退原票一次");

        bool Returned(List<WirePacket> rows, TcpPeer peer, byte area)
        {
            var position = Position(fixture, peer);
            return position is { Town: 59 } && position.Area == area && rows.Any(row =>
                row is { Kind: 0, Opcode: 24, Body.Length: >= 10 } && row.Body[0] == 59 && row.Body[1] == area &&
                U16(row.Body, 4) == peer.Id && U16(row.Body, 6) == position.X && U16(row.Body, 8) == position.Y);
        }
        static bool Removed(List<WirePacket> rows, ushort id) =>
            rows.Any(row => row is { Kind: 0, Opcode: 9, Body.Length: 11 } && U16(row.Body, 4) == id && row.Body[9] == 3);
        bool FailureDisplayed(List<WirePacket> rows, TcpPeer peer) => Returned(rows, peer, 2) &&
            rows.Any(row => row is { Kind: 0, Opcode: 601, Body: [3, 0, 0, 0, 0, 0, 0, 0, 0, 0] }) &&
            rows.Any(row => row is { Kind: 0, Opcode: 587, Body: [6, 0] });
    }
}
