using System.Diagnostics;
using System.Text.Json;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class PreyTeamTcpTests
{
    static async Task RejoinLandingAsync(TcpTestFixture fixture, List<TcpPeer> peers, TownCatalog towns,
        RaidResources resources, LukeRaidClockTests.Clock clock, Dictionary<int, int> symbols, uint key, int[] positions)
    {
        That(Cross(fixture, peers[1], towns, 4).All(row => row is not { Kind: 0, Opcode: 9 }),
            "断线前普通成员先到左侧仍保持单人");
        ushort previousParty = BoardingParty(Cross(fixture, peers[0], towns, 4), 0, (0, peers[1].Id), (1, peers[0].Id));
        That(BoardingParty(Read(peers[1], "party before disconnect"), 0, (0, peers[1].Id), (1, peers[0].Id)) == previousParty,
            "断线前非团长实际带领一支跨标记双人队伍");
        ushort character = peers[1].Id;
        string Assets() => fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY character_id,item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM character_raid_runs ORDER BY character_id,instance_id") +
            fixture.Snapshot("SELECT * FROM character_raid_reward_receipts ORDER BY character_id,instance_id,phase,kind");
        string assets = Assets();
        var instance = Guid.Parse((string)fixture.Query("SELECT DISTINCT instance_id FROM character_raid_runs WHERE raid_type=3").Single()[0]!);
        var elapsed = Stopwatch.StartNew();
        int connection = -1;
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10) && connection < 0)
        {
            var selected = Events().Where(row => row.GetProperty("Kind").GetString() == "selected" &&
                row.GetProperty("Detail").GetProperty("Character").GetUInt16() == character).ToArray();
            if (selected.Length > 0) connection = selected[^1].GetProperty("Detail").GetProperty("Connection").GetInt32();
            else await Task.Delay(10);
        }
        That(connection >= 0, "原selected日志确定即将关闭的真实连接");
        peers[1].Dispose();
        elapsed.Restart();
        bool disconnected = false;
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            disconnected = Events().Any(row => row.GetProperty("Kind").GetString() == "disconnected" &&
                row.GetProperty("Detail").GetProperty("Connection").GetInt32() == connection);
            if (disconnected && fixture.LukeRejoinCandidate(character) is { OldOwnerReleased: true }) break;
            await Task.Delay(10);
        }
        var candidate = fixture.LukeRejoinCandidate(character);
        That(disconnected && candidate is { RaidSlot: 1, Squad: 2, PartySlot: 0, Phase: 0, OldOwnerReleased: true } &&
            candidate.Instance == instance && candidate.Key == key && candidate.PartyId == previousParty,
            "真实socket清理释放旧owner后保留原实例、攻坚标记及历史临时Party候选");
        var abandoned = Read(peers[0], "last party member after disconnect");
        That(abandoned.Any(row => row is { Kind: 0, Opcode: 9, Body.Length: 11 } &&
            U16(row.Body, 4) == previousParty && row.Body[9] == 3),
            "原双人Party因断线只剩一人时自动解散");
        Cross(fixture, peers[0], towns, 3);
        foreach (var peer in peers.Skip(2)) Read(peer, "other raid members after disconnect");

        var fresh = Open(fixture, "preyteam1", gamePort: ChannelTcpTests.Endpoints(fixture)[42], channelNumber: 42);
        peers[1] = fresh;
        var observed = fresh.Select().Concat(Read(fresh, "fresh rejoin notice")).ToList();
        That(fresh.Id == character && observed.Count(row => row is { Kind: 0, Opcode: 835, Body.Length: 7 } && U32(row.Body) == key) == 1 &&
            observed.All(row => row.Opcode is not (9 or 594 or 587)),
            "新会话收到恰一次完整7B835候选，668前不绑定原Raid或恢复旧Party");
        fresh.Send(668, Build(writer => writer.Write(key + 1)));
        var rejected = Read(fresh, "wrong rejoin key");
        Ack(rejected, 668, [0, 19, 0], "错key668沿原协议拒绝");
        That(rejected.All(row => row.Opcode is not (9 or 594 or 587)) && fixture.LukeRejoinCandidate(character) == candidate && Assets() == assets,
            "错key不消费候选，不改原实例、门票、审计、资格或奖励");
        var landingBefore = Position(fixture, fresh);
        Console.WriteLine($"普雷重返忙锁前位置：{landingBefore.Town}/{landingBefore.Area} ({landingBefore.X},{landingBefore.Y})");
        string returnBefore = fixture.Snapshot("SELECT return_town,return_area,return_x,return_y,return_direction FROM characters WHERE id=$p0", character);
        // 原Read的1278还须完成自动刷新，避免外部writer把登录/周期准备误当位置写的忙锁。
        ushort barrierSequence = unchecked((ushort)(fresh.NextClientSequence - 1));
        int freshConnection = -1;
        elapsed.Restart();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10) && freshConnection < 0)
        {
            var completed = Events().Where(row => row.GetProperty("Kind").GetString() == "command_completed" &&
                row.GetProperty("Detail").GetProperty("Character").GetUInt16() == character &&
                row.GetProperty("Detail").GetProperty("Sequence").GetUInt16() == barrierSequence &&
                row.GetProperty("Detail").GetProperty("Connection").GetInt32() != connection).ToArray();
            if (completed.Length > 0) freshConnection = completed[^1].GetProperty("Detail").GetProperty("Connection").GetInt32();
            else await Task.Delay(10);
        }
        That(freshConnection >= 0, "新连接原1278完成分派与自动刷新后再持真实SQLite写锁");
        int eventBoundary = Events().Length;
        using (var databaseWriter = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = fixture.Database, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            databaseWriter.Open();
            using var held = databaseWriter.BeginTransaction(deferred: false);
            try
            {
                fresh.Send(668, Build(writer => writer.Write(key)));
                JsonElement[] attempts = [];
                elapsed.Restart();
                while (elapsed.Elapsed < TimeSpan.FromSeconds(45))
                {
                    attempts = Events().Skip(eventBoundary).Where(row => row.GetProperty("Kind").GetString() == "luke_raid_rejoin" &&
                        row.GetProperty("Detail").GetProperty("Connection").GetInt32() == freshConnection &&
                        row.GetProperty("Detail").GetProperty("Self").GetUInt16() == character &&
                        row.GetProperty("Detail").GetProperty("Key").GetString() == key.ToString("X8") &&
                        row.GetProperty("Detail").GetProperty("Accepted").GetBoolean()).ToArray();
                    if (attempts.Length >= 2) break;
                    await Task.Delay(100);
                }
                That(attempts.Length == 2 && attempts[0].GetProperty("Detail").GetProperty("Reason").GetString() == "bound-without-wire" &&
                    attempts[1].GetProperty("Detail").GetProperty("Reason").GetString() == "already-bound",
                    "一次真实668先绑定原团，位置SQL忙锁后幂等Restore重试，不提前快回成功");
                var sqlWait = attempts[1].GetProperty("Time").GetDateTimeOffset() - attempts[0].GetProperty("Time").GetDateTimeOffset();
                Console.WriteLine($"普雷重返真实SQLite忙锁：首次绑定至幂等重试 {sqlWait.TotalMilliseconds:F1}ms");
                That(sqlWait >= TimeSpan.FromSeconds(25), "第二次Restore发生在真实SQLite约30秒忙等之后");
                var blocked = Events().Skip(eventBoundary).Where(row => row.GetProperty("Detail").TryGetProperty("Connection", out var owner) &&
                    owner.GetInt32() == freshConnection).ToArray();
                That(blocked.Count(row => row.GetProperty("Kind").GetString() == "recv" &&
                        row.GetProperty("Detail").GetProperty("Opcode").GetUInt16() == 668) == 1 &&
                    blocked.All(row => row.GetProperty("Kind").GetString() != "send" ||
                        !(row.GetProperty("Detail").GetProperty("Kind").GetByte() == 1 &&
                            row.GetProperty("Detail").GetProperty("Opcode").GetUInt16() == 668 ||
                          row.GetProperty("Detail").GetProperty("Kind").GetByte() == 0 &&
                            row.GetProperty("Detail").GetProperty("Opcode").GetUInt16() is 24 or 594 or 587 or 1595)) &&
                    Position(fixture, fresh) == landingBefore && Assets() == assets,
                    "writer未释放时只有一次668收包，完整原位置保持且没有成功ACK、迁城或重返初始化，资产不变");
            }
            finally { held.Rollback(); }
        }
        var restored = Read(fresh, "restore prey raid");
        Ack(restored, 668, [1], "正确668重新绑定原普通普雷攻坚队");
        string returnAfter = fixture.Snapshot("SELECT return_town,return_area,return_x,return_y,return_direction FROM characters WHERE id=$p0", character);
        That(returnAfter == returnBefore, $"正确668保留选角后的完整普通返回点：before={returnBefore}, after={returnAfter}");
        That(restored.Any(row => row is { Kind: 0, Opcode: 24, Body: [59, 2, ..] }) &&
            Position(fixture, fresh) is { Town: 59, Area: 2 } && restored.All(row => row is not { Kind: 0, Opcode: 9 }) && Assets() == assets,
            "重返只回原59/2备战区，不重建旧临时Party、不重扣门票或创建新生命周期");
        That(restored.Count(row => row is { Kind: 0, Opcode: 594 }) == 1 &&
            restored.FindIndex(row => row is { Kind: 0, Opcode: 594, Body.Length: 0 }) >
                restored.FindLastIndex(row => row is { Kind: 0, Opcode: 24, Body: [59, 2, ..] }) &&
            restored.Any(row => row is { Kind: 0, Opcode: 587, Body: [1, 0] }) &&
            restored.All(row => row is not { Kind: 0, Opcode: 587, Body: [2, 0] }) &&
            restored.Where(row => row is { Kind: 1, Opcode: 1361, Body: [1, 0 or 2, ..] }).All(row => RaidInformationState(row.Body, key) == 1) &&
            ReadDisplayedState(fresh, key) == 1,
            "已启动原规则的重返者先以24回候战区再收到一次594，原36登船前通知及1361仍保持state1");
        string waiting = fixture.Snapshot("SELECT * FROM characters WHERE id=$p0", character);
        fresh.Send(668, Build(writer => writer.Write(key)));
        var replayed = Read(fresh, "waiting rejoin replay");
        Ack(replayed, 668, [1], "未登船时重复668回放原绑定成功");
        clock.Advance(1);
        var tick = Read(fresh, "rejoin waiting natural tick");
        That(replayed.Concat(tick).All(row => row.Opcode is not (24 or 594) && row is not { Kind: 0, Opcode: 587, Body: [2, 0] }) &&
            ReadDisplayedState(fresh, key) == 1 && waiting == fixture.Snapshot("SELECT * FROM characters WHERE id=$p0", character) && Assets() == assets,
            "重复668及自然tick不重播飞鸟、不重建准备期或更改原等待位置与资产");
        var roster = ReadRoster(fresh, key);
        That(roster.Count == 4 && peers.Select((peer, index) => roster[peer.Id].Position == positions[index] &&
            roster[peer.Id].Squad == positions[index] / 10 && roster[peer.Id].Clears == 0).All(value => value),
            "重返后原四人攻坚名册、标记和清图账本完整保留");
        foreach (var peer in peers.Where(peer => !ReferenceEquals(peer, fresh))) Read(peer, "rejoined raid member roster");
        Board(fixture, fresh, towns, 3);
        That(ReadDisplayedState(fresh, key) == 2, "重返者原36及本人24完成后1361才恢复state2");
        string boarded = fixture.Snapshot("SELECT * FROM characters WHERE id=$p0", character);
        fresh.Send(668, Build(writer => writer.Write(key)));
        var boardedReplay = Read(fresh, "boarded rejoin replay");
        Ack(boardedReplay, 668, [1], "已登船时重复668回放原绑定成功");
        That(boardedReplay.All(row => row.Opcode is not (24 or 594) && row is not { Kind: 0, Opcode: 587, Body: [1, 0] }) &&
            ReadDisplayedState(fresh, key) == 2 && boarded == fixture.Snapshot("SELECT * FROM characters WHERE id=$p0", character),
            "已登船重复668只同步当前战况，不回等待区或重播飞鸟");
        That(Cross(fixture, fresh, towns, 4).All(row => row is not { Kind: 0, Opcode: 9 }),
            "重返者以新到达序首先进入左侧，只等待而不恢复历史Party");
        TcpPeer[] team = [fresh, peers[0], peers[2], peers[3]];
        ushort party = ushort.MaxValue;
        for (int size = 2; size <= team.Length; size++)
        {
            var expected = team.Take(size).Select((peer, slot) => (checked((byte)slot), peer.Id)).ToArray();
            ushort created = BoardingParty(Cross(fixture, team[size - 1], towns, 4), 0, expected);
            if (size == 2) party = created;
            That(created == party && party != previousParty && team.Take(size - 1).All(peer =>
                BoardingParty(Read(peer, "rejoin party member added"), 0, expected) == party),
                "重返者按新到达序带领新Party，不同攻坚标记的成员顺次加入至四人");
        }
        await FinishLandedPartyAsync(fixture, team, resources, towns, clock, symbols, key, party,
            [positions[1], positions[0], positions[2], positions[3]]);

        JsonElement[] Events()
        {
            try { return fixture.Events().ToArray(); }
            catch (JsonException) { return []; } // 日志最后一行尚未刷完时重读，清理完成仍须真实日志与候选共同确认。
        }
    }
}
