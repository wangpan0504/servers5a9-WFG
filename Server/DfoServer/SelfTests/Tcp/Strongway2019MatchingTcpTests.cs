using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Development.Dungeons;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class Strongway2019MatchingTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int before = Count;
        var clock = new MatchClock();
        await using var fixture = new TcpTestFixture(source with { DevelopmentDungeonEntryProbe = DungeonEntryProbeMode.Disabled },
            Path.Combine(root, "strongway2019-matching"), resources: resources, clock: clock);
        var repository = new CharacterRepository(fixture.Database);
        var equipmentIndex = PvfScript.List(resources.Archive.ReadText("equipment/equipment.lst"));
        int weapon = ExactItem("equipment/character/swordman/weapon/ssword/101000783.equ");
        int stone = ExactItem("equipment/character/common/magicstone/100353080.equ");
        int earring = ExactItem("equipment/character/common/earring/100390020.equ");
        int[] equipmentIds = [weapon, 400050102, 400150086, 400100094, 400250092, 400200086,
            400300070, 400310070, 400320074, 400340233, stone, earring];
        var provider = new CharacterAntiEvil(resources.ItemResources.Items, new(resources.Archive),
            resources.ItemResources.RecoveryConsumables, clock, resources.ItemResources.Contracts, new(resources.Archive));
        Character[] roles = Enumerable.Range(0, 8).Select(Prepare).ToArray();
        var events = new ServerEventService(new ServerEventRepository(fixture.Database), clock);
        events.Override(312, false);
        await fixture.StartAsync();
        var entry = ContentEntry(fixture);
        var peers = new List<TcpPeer>();
        var transcripts = new Dictionary<TcpPeer, List<WirePacket>>();
        byte[] restartHeroHistory = [];
        byte[] winnerHistory = new byte[16], winnerAccount = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(winnerAccount.AsSpan(20), 1);
        try
        {
            for (int index = 0; index < 8; index++)
            {
                var peer = Open(fixture, Account(index), gamePort: entry.Port, channelNumber: entry.Number);
                peers.Add(peer); transcripts.Add(peer, []);
                That(peer.LoginEndpoint[3] == 55, "八个真实LOGIN均绑定原生type55频道");
                peer.Select(roles[index].Slot, prepared: true, resumeTown: true, room: false);
                FlushAll(peer);
                peer.Send(2, Build(writer =>
                {
                    writer.Write((byte)5); writer.Write(0x0100007fu); writer.Write(0x0100007fu);
                    writer.Write(checked((ushort)(31000 + index))); writer.Write(512u);
                }));
                FlushAll(peer);
            }
            for (int side = 0; side < 2; side++)
            {
                var leader = peers[side * 4];
                for (int member = 1; member < 4; member++)
                {
                    var invited = peers[side * 4 + member];
                    leader.Send(10, PartyPeer(invited.Id, invitation: true));
                    var invitation = FlushAll(leader);
                    That(invitation[leader].Any(packet => packet is { Kind: 1, Opcode: 10, Body: [1] }) &&
                        invitation[invited].Any(packet => packet is { Kind: 0, Opcode: 7 }), "两队沿真实邀请10建立各自原四人Party");
                    invited.Send(11, PartyPeer(leader.Id, invitation: false));
                    var joined = FlushAll(invited);
                    That(peers.Skip(side * 4).Take(member + 1).All(peer => joined[peer].Any(packet => packet is { Kind: 0, Opcode: 9 })),
                        "真实接受11向该原队全部已加入连接发布名册");
                }
            }
            string unentered = EntryState();
            peers[0].Send(520, QueueRequest());
            var closed = FlushAll(peers[0]);
            That(closed[peers[0]].Any(packet => packet is { Kind: 1, Opcode: 520, Body: [0, 23, 0] }) &&
                closed.Values.All(rows => rows.All(packet => packet.Opcode is not (28 or 29 or 1492))) && EntryState() == unentered,
                "关闭312的真实520结束等待，八人均不预留attempt、不安装Run");
            events.Override(312, true);
            peers[1].Send(520, QueueRequest());
            var memberRejected = FlushAll(peers[1]);
            That(memberRejected[peers[1]].Any(packet => packet is { Kind: 1, Opcode: 520, Body: [0, 23, 0] }) && EntryState() == unentered,
                "非队长520不借其它三名角色资格创建票据或污染另一队");
            peers[0].Send(520, QueueRequest());
            var queued = FlushAll(peers[0]);
            That(queued[peers[0]].Any(packet => packet is { Kind: 1, Opcode: 520, Body: [1, 1, 14, 0, 0] }) && EntryState() == unentered,
                "原队长520排队成功只返回原生gameType14合同，未配对不提前入场");
            peers[0].Send(521, [1, 14, 0]);
            var cancelled = FlushAll(peers[0]);
            That(cancelled[peers[0]].Any(packet => packet is { Kind: 1, Opcode: 521, Body: [1] }) &&
                cancelled.Values.All(rows => rows.All(packet => packet.Opcode is not (28 or 29 or 1492))) && EntryState() == unentered,
                "真实521只取消原四人等待队，不消耗任何入场或奖励事实");
            peers[0].Send(520, QueueRequest()); FlushAll(peers[0]);
            peers[4].Send(520, QueueRequest()); FlushAll(peers[4]);
            for (int attempt = 0; attempt < 40 && peers.Any(peer => !transcripts[peer].Any(packet => packet is { Kind: 0, Opcode: 29 })); attempt++)
            { await Task.Delay(25); FlushAll(); }
            var maps = new byte[2][];
            DungeonMap[]? route = null;
            for (int index = 0; index < 8; index++)
            {
                int side = index / 4;
                var rows = transcripts[peers[index]];
                var waiting = rows.Single(packet => packet is { Kind: 0, Opcode: 1492 }).Body;
                That(waiting.Length == 18 && U32(waiting) == clock.GetUtcNow().ToUnixTimeSeconds() + 5 && U32(waiting, 4) == 0 &&
                    waiting[8] == side && waiting[9] == 4 &&
                    Enumerable.Range(0, 4).Select(slot => U16(waiting, 10 + slot * 2)).SequenceEqual(peers.Skip((1 - side) * 4).Take(4).Select(peer => peer.Id)),
                    "1492向八连接安装真实side、同一截止秒及原对手四人ID");
                var info = rows.Single(packet => packet is { Kind: 0, Opcode: 28 }).Body;
                That(info.Length == 36 && U32(info) == 5360 && info[4] == 2, "真实配对只进入5360指定难度2");
                var map = rows.Single(packet => packet is { Kind: 0, Opcode: 29 }).Body;
                if (index % 4 == 0) maps[side] = map;
                else Bytes(map, maps[side], "同一原四人队共享NOTI29地图与对象key");
                route ??= SelfTestCatalogs.Dungeons(resources.Archive).Get(5360).Mazes[info[7]].MapSpecification!.ToArray();
            }
            That(route is { Length: 9 } && fixture.Query("SELECT count(*) FROM character_dungeon_state WHERE last_attempt=1") is [[8L]] &&
                fixture.Query("SELECT count(*) FROM account_strongway2019_rewards") is [[0L]], "八人入场一次提交八个原尝试，开场加八战斗段未提前发奖");
            LoadTeam(0, partial: true);
            That(peers.All(peer => Symbols(peer, 0, 5).Length == 0), "缺第四名37时不发布来源0路线进度");
            peers[3].Send(37, new byte[8]); FlushAll(peers[3]);
            That(peers.All(peer => Symbols(peer, 0, 5).SequenceEqual(new uint[] { 0 })), "原四人加载齐备才向八连接发布current0");
            LoadTeam(1);
            That(peers.All(peer => Symbols(peer, 1, 5).SequenceEqual(new uint[] { 0 })), "另一原队独立加载屏障发布来源1 current0");
            peers[0].Send(1525, Symbol(0, 7, 4)); FlushAll(peers[0]);
            That(peers.All(peer => Symbols(peer, 0, 7).Length == 0), "未完成双队阶段3时客户端阶段4不直接开放战斗");
            foreach (uint phase in new uint[] { 3, 4 })
            foreach (int side in new[] { 0, 1 })
            { peers[side * 4].Send(1525, Symbol((uint)side, 7, phase)); FlushAll(peers[side * 4]); }
            That(peers.All(peer => Symbols(peer, 0, 7).SequenceEqual(new uint[] { 3, 4 }) &&
                Symbols(peer, 1, 7).SequenceEqual(new uint[] { 3, 4 })), "真实1525双队3/4握手共同开放原Run战斗");
            VerifyFiveSkillsAndNextRoom(maps, route!);
            for (int used = 1; used <= 3; used++) MatchingRevive(peers[1], used);
            clock.Advance(TimeSpan.FromSeconds(1));
            byte[]? finalDeath = null;
            int deaths = 0;
            uint actorBoundary = 0;
            for (int ordinal = 0; ordinal < route!.Length; ordinal++)
            {
                if (ordinal > 0)
                {
                    var move = new byte[100]; move[0] = checked((byte)route[ordinal].Position.X); move[1] = checked((byte)route[ordinal].Position.Y);
                    peers[0].Send(45, move);
                    var moved = FlushAll(peers[0]);
                    maps[0] = moved[peers[0]].Single(packet => packet is { Kind: 0, Opcode: 29 }).Body;
                    That(peers.Take(4).All(peer => moved[peer].Any(packet => packet is { Kind: 1, Opcode: 45, Body: [1] })) &&
                        peers.Skip(4).All(peer => moved[peer].All(packet => packet is not { Kind: 0, Opcode: 29 })),
                        "真实45仅推进本原四人实例，对手留在自己的原房间");
                    LoadTeam(0);
                    if (ordinal == 1)
                    {
                        MatchingRevive(peers[1], 4);
                        MatchingRevive(peers[1], 5, accepted: false);
                        MatchingRevive(peers[2], 1);
                    }
                    That(peers.All(peer => Symbols(peer, 0, 5).SequenceEqual(Enumerable.Range(0, ordinal + 1).Select(value => (uint)value))),
                        "八连接current按真实map-spec顺序只增加一次：" + ordinal);
                }
                byte[] map = maps[0];
                That(map.Length > 19 + map[18] * 22 && map[19 + map[18] * 22] == 0,
                    "NOTI29保留完整真实演员行，禁掉落不安装任何被动物品行");
                foreach (int at in Enumerable.Range(0, map[18]).Select(index => 19 + index * 22))
                    actorBoundary = Math.Max(actorBoundary, (uint)U16(map, at + 6) + 1);
                foreach (int at in Enumerable.Range(0, map[18]).Select(index => 19 + index * 22)
                    .Where(at => map[at + 13] <= 3 && map[at + 21] == 0).OrderBy(at => map[at + 13] == 3))
                {
                    ushort key = U16(map, at + 6);
                    byte[] report = FormalDungeonTcpTests.Death(key, peers[0].Id, map[at + 12]);
                    if (map[at + 13] == 3)
                    {
                        report[55] = 1; BinaryPrimitives.WriteUInt64LittleEndian(report.AsSpan(18), 9000);
                        BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(26), 30); FormalDungeonTcpTests.SealDeath(report);
                    }
                    var invalid = report.ToArray(); invalid[^1] ^= 1;
                    peers[0].Send(39, invalid);
                    var refused = FlushAll(peers[0]);
                    That(refused.Values.All(rows => rows.All(packet => packet is not { Kind: 0, Opcode: 38 })),
                        "真实39内层校验失败不退场任何原实例演员");
                    peers[0].Send(39, report);
                    var killed = FlushAll(peers[0]); deaths++;
                    That(peers.Take(4).All(peer => killed[peer].Count(packet => packet is { Kind: 0, Opcode: 38 } && U16(packet.Body) == key) == 1) &&
                        peers.Skip(4).All(peer => killed[peer].All(packet => packet is not { Kind: 0, Opcode: 38 })),
                        "真实39只在原四人共享死亡事务退场一次，同key不污染对方实例");
                    That(killed.Values.SelectMany(rows => rows).Where(packet => packet is { Kind: 0, Opcode: 38 }).All(packet =>
                        packet.Body.Length == 9 && U16(packet.Body, 2) == 0),
                        "正式TCP每个真实怪物死亡只含退场，不携带普通/显式/春节物品RAW");
                    if (ordinal == route.Length - 1) finalDeath = report;
                }
            }
            That(deaths >= 8 && finalDeath is not null && peers.All(peer => transcripts[peer].Count(packet =>
                packet is { Kind: 0, Opcode: 1494, Body: [0, 0] }) == 1), "九房真实终点向八连接仅投影一次同winnerSide0的1494");
            That(peers.Take(4).All(peer => fixture.Query("SELECT next_object_key FROM character_dungeon_state WHERE character_id=$p0", peer.Id)
                    is [[long nextKey]] && nextKey == actorBoundary),
                "匹配原四人数据库key尾界与九房NOTI29实际演员尾界相同，没有额外地面对象预留");
            foreach (var peer in peers.Take(4))
            {
                Bytes(transcripts[peer].Last(packet => packet is { Kind: 0, Opcode: 1753 }).Body, winnerAccount,
                    "获胜原四人1753只更新匹配账号周次数1");
                winnerHistory = transcripts[peer].Last(packet => packet is { Kind: 0, Opcode: 1491 }).Body;
                That(U32(winnerHistory) == 1 && U32(winnerHistory, 4) == 251000 && U32(winnerHistory, 8) == 0 && U32(winnerHistory, 12) == 0,
                    "1491实际胜1次与246000ms真实战斗加5000ms原生显示延迟，不借普通成绩");
                That(fixture.Query("SELECT json_extract(i.item_core,'$.ItemId'),json_extract(i.item_core,'$.Value') " +
                    "FROM mailbox_messages m JOIN mailbox_attachments a USING(mail_id) JOIN character_inventory_items i USING(item_uid) " +
                    "WHERE m.recipient_id=$p0 AND a.claimed=0", peer.Id) is [[490012737L, 1L]], "每个真实胜者持久化完整737邮件附件");
            }
            That(fixture.Query("SELECT count(*),count(DISTINCT account),count(DISTINCT character_id) " +
                    "FROM account_strongway2019_rewards WHERE dungeon_id=5360 AND bucket=1") is [[4L, 4L, 4L]] &&
                peers.Skip(4).All(peer => fixture.Query("SELECT count(*) FROM account_strongway2019_rewards WHERE character_id=$p0", peer.Id) is [[0L]] &&
                    fixture.Query("SELECT count(*) FROM mailbox_messages WHERE recipient_id=$p0 AND delivery_key LIKE 'strongway2019:%'", peer.Id) is [[0L]]),
                "匹配胜方四账号各使用一次独立额度，败方无奖励邮件");
            string completed = RewardState();
            peers[0].Send(39, finalDeath!); peers[0].Send(1525, Symbol(0, 7, 4)); FlushAll(peers[0]);
            That(RewardState() == completed && peers.All(peer => transcripts[peer].Count(packet => packet is { Kind: 0, Opcode: 1494 }) == 1),
                "终点死亡与开场消息重放不重复1494、成绩、737邮件或账号额度");
        }
        finally { foreach (var peer in peers) peer.Dispose(); }
        using (var winner = Open(fixture, Account(0), gamePort: entry.Port, channelNumber: entry.Number))
        {
            winner.Select(roles[0].Slot, prepared: true, resumeTown: true, room: false,
                strongwayHistory: winnerHistory, strongwayAccountRewards: winnerAccount);
            Drain(winner);
            Bytes(winner.StrongwayAccountRewards, winnerAccount, "胜者新连接从真实账号账本恢复1753匹配周次数");
            Strongway2019TcpTests.ClaimAndOpenReward(fixture, winner, resources.ItemResources, 490012737, [(490012699, 4)]);
        }
        using (var loser = Open(fixture, Account(4), gamePort: entry.Port, channelNumber: entry.Number))
        {
            loser.Select(roles[4].Slot, prepared: true, resumeTown: true, room: false);
            Drain(loser);
            Bytes(loser.StrongwayAccountRewards, new byte[24], "败方重连保持零额度，不借对方终局缓存");
        }
        VerifyHero();
        await VerifyProcessRestart();
        Console.WriteLine($"PASS {Count - before} 首期八人真实TCP匹配、737及四人英雄结算、736领取开盒与历史恢复");

        void VerifyFiveSkillsAndNextRoom(byte[][] maps, DungeonMap[] route)
        {
            var activity = new Strongway2019Catalog(resources.Archive);
            string beforeRewards = RewardState();
            clock.Advance(TimeSpan.FromSeconds(100));
            Use(0, accepted: true);
            Use(1, accepted: false); // 能量100足够；仍受技能0设置的共享10秒冷却限制。
            clock.Advance(TimeSpan.FromSeconds(10));
            Use(1, accepted: true); // 100 - 30 = 70。
            Use(2, accepted: false); // 70足够支付45；同一时刻被共享冷却拒绝。
            clock.Advance(TimeSpan.FromSeconds(10));
            Use(0, accepted: false); // 冷却已到，零消耗仍不能越过整局一次上限。
            Use(2, accepted: true); // 70 + 10 - 45 = 35。
            clock.Advance(TimeSpan.FromSeconds(10));
            Use(3, accepted: false); // 冷却已到，但45点不足支付60。
            clock.Advance(TimeSpan.FromSeconds(15));
            Use(3, accepted: true); // 35 + 10 + 15 - 60 = 0。
            clock.Advance(TimeSpan.FromSeconds(100));
            uint replacementId = Use(4, accepted: true); // 充满100，尚未到600秒比赛截止。
            That(peers.All(peer => Enumerable.Range(0, 5).All(kind => Symbols(peer, 0, (uint)kind).Length == 1)),
                "五种真实1525各向八连接仅广播一次，冷却、能量和次数拒绝不占发送位");

            // 地图技能只登记对方下一格；先沿原39清当前房间，再由目标队真实45/37应用。
            byte[] original = maps[1];
            foreach (int at in Enumerable.Range(0, original[18]).Select(index => 19 + index * 22)
                .Where(at => original[at + 13] <= 3 && original[at + 21] == 0).OrderBy(at => original[at + 13] == 3))
            {
                ushort key = U16(original, at + 6);
                byte[] report = FormalDungeonTcpTests.Death(key, peers[4].Id, original[at + 12]);
                if (original[at + 13] == 3)
                {
                    report[55] = 1; BinaryPrimitives.WriteUInt64LittleEndian(report.AsSpan(18), 9000);
                    BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(26), 30); FormalDungeonTcpTests.SealDeath(report);
                }
                peers[4].Send(39, report);
                var cleared = FlushAll(peers[4]);
                That(peers.Skip(4).All(peer => cleared[peer].Count(packet => packet is { Kind: 0, Opcode: 38 } &&
                        U16(packet.Body) == key) == 1) &&
                    peers.Take(4).All(peer => cleared[peer].All(packet => packet is not { Kind: 0, Opcode: 38 })),
                    "目标队当前房间按原39共享清理，不借技能通知伪造清房或污染来源队");
            }
            var target = route![1];
            That(target.Kind == "map" && target.Position != route![^1].Position,
                "换图目标取原路线相邻未进入普通格，不替换原终点");
            uint oldKeyFloor = peers.Skip(4).Select(peer => checked((uint)(long)fixture.Query(
                "SELECT next_object_key FROM character_dungeon_state WHERE character_id=$p0", peer.Id).Single()[0])).Max();
            var move = new byte[100]; move[0] = checked((byte)target.Position.X); move[1] = checked((byte)target.Position.Y);
            peers[4].Send(45, move);
            var moved = FlushAll(peers[4]);
            That(peers.Skip(4).All(peer => moved[peer].Count(packet => packet is { Kind: 1, Opcode: 45, Body: [1] }) == 1 &&
                    moved[peer].Count(packet => packet is { Kind: 0, Opcode: 29 }) == 1) &&
                peers.Take(4).All(peer => moved[peer].All(packet => packet is not { Kind: 0, Opcode: 29 })),
                "目标队真实45只给原四人安装下一房，来源四人不被技能移图");
            byte[] replaced = moved[peers[4]].Single(packet => packet is { Kind: 0, Opcode: 29 }).Body;
            foreach (var peer in peers.Skip(4))
                Bytes(moved[peer].Single(packet => packet is { Kind: 0, Opcode: 29 }).Body, replaced,
                    "换图后原四人29保持同一真实地图、完整演员与对象key");
            That(replaced.Length >= 19 + replaced[18] * 22 && replaced[0] == target.Position.X &&
                replaced[1] == target.Position.Y && U32(replaced, 14) == replacementId,
                "实际29 mapId等于技能4服务端候选，保留原下一格坐标");
            var dungeonCatalog = SelfTestCatalogs.Dungeons(resources.Archive);
            var replacementRoom = SelfTestCatalogs.Rooms(resources.Archive).Get(new DungeonMap("map", target.Position,
                checked((int)replacementId), dungeonCatalog.MapPath(checked((int)replacementId))!));
            var actorRows = Enumerable.Range(0, replaced[18]).Select(index => 19 + index * 22).ToArray();
            That(actorRows.Length > 0 && actorRows.All(at => replaced[at + 13] <= 3) &&
                actorRows.Select(at => checked((int)U32(replaced, at + 8))).Order().SequenceEqual(
                    replacementRoom.Monsters.Select(monster => monster.MonsterId).Order()) &&
                actorRows.Any(at => U32(replaced, at + 8) == activity.MapMonsters[checked((int)replacementId)]) &&
                actorRows.Select(at => U16(replaced, at + 6)).Distinct().Count() == actorRows.Length &&
                actorRows.All(at => U16(replaced, at + 6) >= oldKeyFloor),
                "替换29的完整怪物模板来自实际候选MAP，四人共用新key且不复用原预留演员");
            LoadTeam(1, partial: true);
            That(peers.All(peer => Symbols(peer, 1, 5).SequenceEqual(new uint[] { 0 })),
                "换图后三人37仍保留原目标队加载屏障，不提前确认下一格进度");
            peers[7].Send(37, new byte[8]); FlushAll(peers[7]);
            That(peers.All(peer => Symbols(peer, 1, 5).SequenceEqual(new uint[] { 0, 1 })) &&
                peers.Skip(4).All(peer => fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0",
                    peer.Id) is [[1L]]) && RewardState() == beforeRewards &&
                peers.All(peer => transcripts[peer].All(packet => packet is not { Kind: 0, Opcode: 1494 })),
                "目标四人真实37齐备后确认current1，仍为原attempt且换图不提前生成终局、邮件或成绩");
            maps[1] = replaced;

            uint Use(uint kind, bool accepted)
            {
                int[] beforeCounts = peers.Select(peer => Symbols(peer, 0, kind).Length).ToArray();
                peers[0].Send(1525, Symbol(0, kind, uint.MaxValue));
                var published = FlushAll(peers[0]);
                That(peers.Select((peer, index) => Symbols(peer, 0, kind).Length == beforeCounts[index] + (accepted ? 1 : 0)).All(value => value),
                    "真实1525按能量、共享冷却及整局次数裁决八端广播：kind=" + kind + ", accepted=" + accepted);
                That(published.Values.All(rows => rows.All(packet => packet is not { Kind: 0, Opcode: 29 })),
                    "施技不立即重建任一当前房间：kind=" + kind);
                if (!accepted) return 0;
                uint serverValue = Symbols(peers[0], 0, kind).Last();
                That(peers.All(peer => Symbols(peer, 0, kind).Last() == serverValue) &&
                    (kind == 4 ? activity.RandomMapIds.Contains(checked((int)serverValue)) : serverValue == 0),
                    "八端收到同一服务端效果值，忽略客户端未初始化第三栈值：kind=" + kind);
                return serverValue;
            }
        }

        void VerifyHero()
        {
            // 复用已创建且抗魔合格的败方四角色和同一fixture；不预种英雄邮件或另起服务。
            peers.Clear(); transcripts.Clear();
            byte[]? heroHistory = null;
            byte[] heroAccount = new byte[24];
            BinaryPrimitives.WriteUInt32LittleEndian(heroAccount.AsSpan(12), 1);
            try
            {
                for (int index = 4; index < 8; index++)
                {
                    var peer = Open(fixture, Account(index), gamePort: entry.Port, channelNumber: entry.Number);
                    peers.Add(peer); transcripts.Add(peer, []);
                    peer.Select(roles[index].Slot, prepared: true, resumeTown: true, room: false);
                    FlushAll(peer);
                    peer.Send(2, Build(writer =>
                    {
                        writer.Write((byte)5); writer.Write(0x0100007fu); writer.Write(0x0100007fu);
                        writer.Write(checked((ushort)(32000 + index))); writer.Write(512u);
                    }));
                    FlushAll(peer);
                }
                for (int member = 1; member < 4; member++)
                {
                    peers[0].Send(10, PartyPeer(peers[member].Id, invitation: true)); FlushAll(peers[0]);
                    peers[member].Send(11, PartyPeer(peers[0].Id, invitation: false));
                    var joined = FlushAll(peers[member]);
                    That(peers.Take(member + 1).All(peer => joined[peer].Any(packet => packet is { Kind: 0, Opcode: 9 })),
                        "既有四角色重连后通过真实10/11重新建立英雄队伍");
                }
                string experience = Experience();
                peers[0].Send(15, new byte[4]);
                That(FlushAll(peers[0]).Values.All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: 27 })),
                    "真实15使英雄原四人共同选图");
                byte[] request = new byte[25]; BinaryPrimitives.WriteUInt32LittleEndian(request, 5363);
                request[4] = 2; request[9] = request[10] = 255;
                peers[0].Send(16, request);
                var entered = FlushAll(peers[0]);
                var info = entered[peers[0]].Single(packet => packet is { Kind: 0, Opcode: 28 }).Body;
                byte[] map = entered[peers[0]].Single(packet => packet is { Kind: 0, Opcode: 29 }).Body;
                That(entered.Values.All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: 28 } &&
                        U32(packet.Body) == 5363 && packet.Body[4] == 2)) &&
                    fixture.Query("SELECT count(*) FROM character_dungeon_state WHERE character_id IN ($p0,$p1,$p2,$p3) AND last_attempt=2",
                        peers[0].Id, peers[1].Id, peers[2].Id, peers[3].Id) is [[4L]],
                    "5363真实16只给原四人各预留一次新attempt，保留指定难度2");
                var heroRoute = SelfTestCatalogs.Dungeons(resources.Archive).Get(5363).Mazes[info[7]].MapSpecification!.ToArray();
                That(heroRoute.Length == 13, "英雄沿真实13房资源路线运行，不借匹配九房路线");
                for (int ordinal = 0; ordinal < heroRoute.Length; ordinal++)
                {
                    if (ordinal > 0)
                    {
                        byte[] move = new byte[100]; move[0] = checked((byte)heroRoute[ordinal].Position.X);
                        move[1] = checked((byte)heroRoute[ordinal].Position.Y); peers[0].Send(45, move);
                        var moved = FlushAll(peers[0]);
                        That(moved.Values.All(rows => rows.Any(packet => packet is { Kind: 1, Opcode: 45, Body: [1] })),
                            "英雄四个原Run共同沿真实45推进");
                        map = moved[peers[0]].Single(packet => packet is { Kind: 0, Opcode: 29 }).Body;
                    }
                    LoadTeam(0);
                    if (ordinal == 0) clock.Advance(TimeSpan.FromSeconds(1));
                    That(map.Length >= 19 + map[18] * 22, "英雄NOTI29含完整实际演员行");
                    foreach (int at in Enumerable.Range(0, map[18]).Select(index => 19 + index * 22)
                        .Where(at => map[at + 13] <= 3 && map[at + 21] == 0).OrderBy(at => map[at + 13] == 3))
                    {
                        ushort key = U16(map, at + 6);
                        byte[] death = FormalDungeonTcpTests.Death(key, peers[0].Id, map[at + 12]);
                        if (map[at + 13] == 3)
                        {
                            death[55] = 1; BinaryPrimitives.WriteUInt64LittleEndian(death.AsSpan(18), 9000);
                            BinaryPrimitives.WriteUInt32LittleEndian(death.AsSpan(26), 30); FormalDungeonTcpTests.SealDeath(death);
                        }
                        peers[0].Send(39, death);
                        That(FlushAll(peers[0]).Values.All(rows => rows.Count(packet =>
                            packet is { Kind: 0, Opcode: 38 } && U16(packet.Body) == key) == 1),
                            "英雄真实39在四人共享事务中各退场一次同一演员");
                    }
                }
                foreach (var peer in peers)
                {
                    That(fixture.Query("SELECT dungeon_id,mode,elapsed_ms FROM character_strongway2019_statistics WHERE character_id=$p0 AND dungeon_id=5363", peer.Id)
                            is [[5363L, 2L, 1000L]] &&
                        fixture.Query("SELECT dungeon_id,bucket FROM account_strongway2019_rewards WHERE character_id=$p0", peer.Id)
                            is [[5363L, 0L]] &&
                        fixture.Query("SELECT json_extract(i.item_core,'$.ItemId'),json_extract(i.item_core,'$.Value') " +
                            "FROM mailbox_messages m JOIN mailbox_attachments a USING(mail_id) JOIN character_inventory_items i USING(item_uid) " +
                            "WHERE m.recipient_id=$p0 AND a.claimed=0", peer.Id) is [[490012736L, 1L]],
                        "英雄四个真实终点各记录模式2及原Run一秒耗时、Challenge额度和唯一736邮件");
                    Bytes(transcripts[peer].Last(packet => packet is { Kind: 0, Opcode: 1753 }).Body, heroAccount,
                        "败方账号英雄通关只增加普通周次数，匹配仍为0");
                }
                heroHistory = transcripts[peers[1]].Last(packet => packet is { Kind: 0, Opcode: 1491 }).Body;
                That(U32(heroHistory) == 0 && U32(heroHistory, 8) == 2 && U32(heroHistory, 12) == 1000,
                    "1491英雄模式2保留原Run一秒真实耗时，不伪造匹配胜场或加匹配展示延迟");
                string assets = Assets();
                byte[] report = DungeonSettlementProtocolTests.Report(peers.Select(peer => (peer.Id, (byte)99)).ToArray());
                foreach (var peer in peers)
                {
                    peer.Send(46, report); var result = FlushAll(peer)[peer];
                    That(result.Any(packet => packet is { Kind: 0, Opcode: 34, Body.Length: 37 } && packet.Body[8] == 4) &&
                        result.Any(packet => packet is { Kind: 0, Opcode: 35 }) &&
                        result.Count(packet => packet is { Kind: 1, Opcode: 905 } && packet.Body.SequenceEqual(peer.DevilService0)) == 1 &&
                        result.All(packet => packet is not { Kind: 0, Opcode: 14 }),
                        "四个真实46各接纳完整成员结果、同步一次原空契约，首期不发普通奖励库存差量");
                }
                That(Experience() == experience && Assets() == assets &&
                    fixture.Query("SELECT (SELECT count(*) FROM character_dungeon_card_rewards)+(SELECT count(*) FROM character_dungeon_card_options)") is [[0L]],
                    "英雄完整结果不增加经验熟练度、不发普通奖励或保存普通翻牌资格");
                peers[0].Send(72, [1, 2]); var returned = FlushAll(peers[0]);
                That(returned.Values.All(rows => rows.Any(packet => packet is { Kind: 1, Opcode: 72 })) &&
                    peers.All(peer => fixture.Query("SELECT town,area FROM characters WHERE id=$p0", peer.Id) is [[65L, 1L]]),
                    "真实结算选项2使原四人共同返城65/1");
            }
            finally { foreach (var peer in peers) peer.Dispose(); }
            using var restored = Open(fixture, Account(5), gamePort: entry.Port, channelNumber: entry.Number);
            restored.Select(roles[5].Slot, prepared: true, resumeTown: true, room: false,
                strongwayHistory: heroHistory!, strongwayAccountRewards: heroAccount); Drain(restored);
            Strongway2019TcpTests.ClaimAndOpenReward(fixture, restored, resources.ItemResources, 490012736,
                [(490012698, 50), (490012699, 8)]);
            restartHeroHistory = heroHistory!;

            string Experience() => string.Concat(peers.Select(peer =>
                fixture.Snapshot("SELECT experience FROM characters WHERE id=$p0", peer.Id) +
                fixture.Snapshot("SELECT proficiency_level,proficiency_experience FROM character_dungeon_state WHERE character_id=$p0", peer.Id)));
            string Assets() => fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
                fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");
        }

        async Task VerifyProcessRestart()
        {
            // 复用正式英雄736的50/8，不预种邮件、额度或皮肤所有权。
            using (var peer = Open(fixture, Account(5), gamePort: entry.Port, channelNumber: entry.Number))
            {
                peer.Select(roles[5].Slot, prepared: true, resumeTown: true, room: false,
                    strongwayHistory: restartHeroHistory, strongwayAccountRewards: RewardProfile(5, clock.GetUtcNow()));
                peer.Barrier();
                BuyAndOpen(peer, 490013018, 490012698, 45, 10305786, 200, selection: true);
                BuyAndOpen(peer, 490013316, 490012699, 3, 490013318, 1, selection: false);
                AddSkin(peer, 490013318, 0, 420000004, [new(0, 420000004)]);
                BuyAndOpen(peer, 490013022, 490012699, 1, 490013039, 1, selection: true);
                AddSkin(peer, 490013039, 1, 430000134, [new(1, 430000134), new(0, 420000004)]);
                That(ItemCount(peer.Id, 490012698) == 45 && ItemCount(peer.Id, 490012699) == 1 &&
                    ItemCount(peer.Id, 10305786) == 200,
                    "重启准备仅用正式736材料完成三次兑换，最终余额45/1及200材料");
            }

            var characterSkin = SkinState(roles[5].Id);
            var accountSkin = SharedSkin();
            That(characterSkin.PartyFrames is [{ SkinId: 420000004, ExpiresAt: 0 }] && characterSkin.Cutscenes.IsEmpty &&
                accountSkin.Cutscenes is [{ SkinId: 430000134, ExpiresAt: 0 }] && accountSkin.PartyFrames.IsEmpty,
                "购买来源318保存角色永久边框，039保存账号永久插画，归属不混淆");
            byte[] beforeMain;
            using (var peer = Open(fixture, Account(5), gamePort: entry.Port, channelNumber: entry.Number))
            {
                var selected = peer.Select(roles[5].Slot, prepared: true, resumeTown: true, room: false,
                    strongwayHistory: restartHeroHistory, strongwayAccountRewards: RewardProfile(5, clock.GetUtcNow()),
                    partyFrames: ([new(420000004, 0)], 20000), accountSkins: accountSkin,
                    recentSkins: [new(1, 430000134), new(0, 420000004)]);
                beforeMain = MainPacket(selected);
                Counts(peer, clock.GetUtcNow());
                That(peer.Mailbox.SelectMany(body => MailboxTcpTests.ReadList(body).Summaries).All(row => row.ItemId != 490012736),
                    "重启前已领取736不再投影为可领取邮件");
                peer.Barrier();
            }
            var matchingMail = CaptureMail(1, 490012737, winnerHistory);
            var heroMail = CaptureMail(4, 490012736, restartHeroHistory);
            long matchingUid = AttachmentUid(matchingMail.MailId), heroUid = AttachmentUid(heroMail.MailId);
            using var oldReady = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Options.LogDirectory, "ready.json")));
            int oldPid = oldReady.RootElement.GetProperty("Pid").GetInt32();
            DateTimeOffset preparedAt = clock.GetUtcNow();
            await fixture.StopAsync();
            // 停服后的最终提交状态为基准；下一阶段必须是正式独立子进程加载同一Data。
            string committed = RestartState();
            await fixture.StartAsync("strongway-reopened", isolatedProcess: true);
            entry = ContentEntry(fixture);
            using var newReady = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Options.LogDirectory, "ready.json")));
            int childPid = newReady.RootElement.GetProperty("Pid").GetInt32();
            That(childPid != oldPid && childPid != Environment.ProcessId && RestartState() == committed,
                "独立子进程ready使用新PID，原邮件、额度、完整物品、皮肤、限购和活动行在登录前全部恢复");
            That(new ServerEventRepository(fixture.Database).Read(312).OverrideActive == true,
                "进程重开直接恢复312活动开关，不重新Override补状态");

            DateTimeOffset reopenedAt = DateTimeOffset.UtcNow;
            using (var peer = Open(fixture, Account(5), gamePort: entry.Port, channelNumber: entry.Number))
            {
                // 模拟战斗时钟不会跨进程；1753与周限购按新服务的真实墙钟周期计算。
                var selected = peer.Select(roles[5].Slot, prepared: true, resumeTown: true, room: false,
                    strongwayHistory: restartHeroHistory, strongwayAccountRewards: RewardProfile(5, reopenedAt),
                    partyFrames: ([new(420000004, 0)], 20000), accountSkins: accountSkin,
                    recentSkins: [new(1, 430000134), new(0, 420000004)]);
                Bytes(MainPacket(selected), beforeMain, "真实进程重开保留主背包每条123B完整核心及UID，不重发starter");
                Counts(peer, reopenedAt);
                peer.Send(96); peer.Expect(1, 96, [1, 0, 0]);
                That(MailboxTcpTests.ReadList(peer.Expect(0, 97).Body).Summaries.All(row => row.ItemId != 490012736),
                    "真实重启后已领736附件资格不复活");
                peer.Barrier();
                string beforeRejected = RestartState();
                peer.Send(21, BuyRequest(490013316)); peer.Expect(1, 21, [0, 0x85, 0]); peer.Barrier();
                That(RestartState() == beforeRejected, "重启后316累积1限购仍拒绝，材料、来源、审计与额度不变");
                int oldWeek = ShopPurchaseCount.PeriodDay(ShopPurchaseCount.Today(preparedAt), ItemPurchasePeriod.Weekly);
                int currentWeek = ShopPurchaseCount.PeriodDay(ShopPurchaseCount.Today(reopenedAt), ItemPurchasePeriod.Weekly);
                if (oldWeek == currentWeek)
                {
                    peer.Send(21, BuyRequest(490013018)); peer.Expect(1, 21, [0, 0x85, 0]); peer.Barrier();
                    That(RestartState() == beforeRejected, "同一真实商店周重启仍拒018第二次购买，不重置周限购");
                }
            }
            RestoreMail(1, matchingMail, matchingUid, winnerHistory);
            RestoreMail(4, heroMail, heroUid, restartHeroHistory);
            That(RestartState() == committed && SkinState(roles[5].Id).Same(characterSkin) && SharedSkin().Same(accountSkin),
                "重启后的选角、邮箱和限购查询不重发奖励、不改变已提交皮肤或业务账本");
            await fixture.StopAsync();
            That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
                "强者真进程恢复后数据库完整且无外键残缺");
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "strongway2019-process-restart", isolatedProcess = true,
                oldPid, childPid, preparedAt, reopenedAt, matchingUid, heroUid, fullCoreRestored = true,
                mailRestored = true, quotaRestored = true, characterFrameRestored = true,
                accountIllustrationRestored = true, purchaseCountsRestored = true }));

            MailboxTcpTests.Summary CaptureMail(int index, int box, byte[] history)
            {
                using var peer = Open(fixture, Account(index), gamePort: entry.Port, channelNumber: entry.Number);
                peer.Select(roles[index].Slot, prepared: true, resumeTown: true, room: false,
                    strongwayHistory: history, strongwayAccountRewards: RewardProfile(index, clock.GetUtcNow()));
                peer.Send(96); peer.Expect(1, 96, [1, 0, 0]);
                var mail = MailboxTcpTests.ReadList(peer.Expect(0, 97).Body).Summaries.Single(row => row.ItemId == box);
                peer.Barrier();
                That(ItemCount(peer.Id, box) == 0 && AttachmentUid(mail.MailId) > 0,
                    "重启正控保留自然通关未领邮件，Main未预放同一奖励盒：" + box);
                return mail;
            }
            void RestoreMail(int index, MailboxTcpTests.Summary expected, long uid, byte[] history)
            {
                using var peer = Open(fixture, Account(index), gamePort: entry.Port, channelNumber: entry.Number);
                peer.Select(roles[index].Slot, prepared: true, resumeTown: true, room: false,
                    strongwayHistory: history, strongwayAccountRewards: RewardProfile(index, DateTimeOffset.UtcNow));
                peer.Send(96); peer.Expect(1, 96, [1, 0, 0]);
                var actual = MailboxTcpTests.ReadList(peer.Expect(0, 97).Body).Summaries.Single(row => row.MailId == expected.MailId);
                That(actual.ClaimId == expected.ClaimId && actual.ItemId == expected.ItemId && actual.Gold == expected.Gold &&
                    AttachmentUid(actual.MailId) == uid && ItemCount(peer.Id, actual.ItemId) == 0,
                    "真实重启后同一未领邮件ID、ClaimId、原UID与归属保持，不复制到Main");
                Bytes(actual.Core, expected.Core, "真实重启保留未领邮件完整123B核心");
                peer.Barrier();
            }
        }

        byte[] RewardProfile(int index, DateTimeOffset now)
        {
            using var owner = new AccountOwners().Acquire(Account(index), () => { });
            var period = DfoServer.Game.Strongway2019.Strongway2019RewardPeriod.At(now);
            var repo = new CharacterRepository(fixture.Database);
            return DfoServer.Network.Builders.Strongway2019ProfilePackets.UserData(
                repo.ReadStrongway2019RewardState(owner, period, DfoServer.Game.Strongway2019.Strongway2019RewardBucket.Challenge),
                repo.ReadStrongway2019RewardState(owner, period, DfoServer.Game.Strongway2019.Strongway2019RewardBucket.Matching));
        }
        void BuyAndOpen(TcpPeer peer, int box, int material, int remaining, int output, int quantity, bool selection)
        {
            peer.Send(21, BuyRequest(box));
            var bought = peer.Expect(1, 21).Body;
            That(bought.Length == 149 && bought[0] == 1 && U32(bought, 19) == box && U32(bought, 23) == 1 &&
                bought[140] == 1 && U32(bought, 141) == material && U32(bought, 145) == remaining,
                "重启准备真实21购买完整商品并只扣一次活动材料：" + box);
            short slot = ItemSlot(peer.Id, box);
            var delta = peer.Expect(0, 14).Body;
            BoosterTcpTests.Entry(delta, slot, box, 1);
            BoosterTcpTests.Entry(delta, ItemSlot(peer.Id, material), material, remaining);
            peer.Barrier();
            if (selection) BoosterSelectionTcpTests.SendSelect(peer, slot, [output]);
            else peer.Send(160, Build(w => { w.Write(slot); w.Write(1); }));
            BoosterTcpTests.ExpectAck(peer, box, slot, [(output, quantity)]);
            delta = peer.Expect(0, 14).Body;
            BoosterTcpTests.Entry(delta, slot, -1, 0);
            short outputSlot = ItemSlot(peer.Id, output);
            // 同槽换模板先收到清空14，再收到安装新Core的14，不能把清空包当作奖励包。
            if (outputSlot == slot) delta = peer.Expect(0, 14).Body;
            BoosterTcpTests.Entry(delta, outputSlot, output, quantity);
            peer.Barrier();
            That(ItemCount(peer.Id, box) == 0 && ItemCount(peer.Id, output) == quantity && ItemCount(peer.Id, material) == remaining,
                "重启准备真实160开盒给全部叶子，不二次扣材料：" + box);
        }
        void AddSkin(TcpPeer peer, int token, byte type, int skin, SkinStorageKey[] recent)
        {
            short slot = ItemSlot(peer.Id, token);
            peer.Send(515, Build(w => { w.Write(slot); w.Write((byte)0); w.Write(0); w.Write(199); w.Write(0); w.Write(0); }));
            peer.Expect(1, 515, Build(w => { w.Write((byte)1); w.Write(slot); w.Write((byte)0); w.Write(199); }));
            peer.Expect(0, 1557, Build(w =>
            {
                w.Write(type); w.Write((ushort)(type == 0 ? 2 : 1)); w.Write(type == 0 ? 20000 : 30000); w.Write(0);
                if (type == 0) { w.Write(skin); w.Write(0); }
                w.Write((ushort)(type == 1 ? 1 : 0));
                if (type == 1) { w.Write(skin); w.Write(0); }
            }));
            peer.Expect(0, 1559, Build(w =>
            { w.Write((byte)recent.Length); foreach (var key in recent) { w.Write(key.Type); w.Write(key.SkinId); } }));
            peer.Expect(0, 1558, Build(w =>
            { w.Write(type); if (type == 1) w.Write((ushort)1); w.Write(type == 0 ? 20000 : 30000); }));
            peer.Barrier();
            That(ItemCount(peer.Id, token) == 0, "真实515恰消费一张已购买来源的外观券：" + token);
        }
        void Counts(TcpPeer peer, DateTimeOffset now)
        {
            using var owner = new AccountOwners().Acquire(Account(5), () => { });
            var current = new CharacterRepository(fixture.Database).LoadCharacter(owner, roles[5].Slot);
            var purchases = current.Inventory.ShopPurchases;
            That(purchases.Single(row => row.ItemId == 490013018).Count == 1 &&
                purchases.Single(row => row.ItemId == 490013316).Count == 1 && purchases.Single(row => row.ItemId == 490013022).Count == 1,
                "三次真实购买的完整账号限购记录均保存一次");
            var expected = purchases.Where(row => row.Effective(ShopPurchaseCount.Today(now)) > 0)
                .OrderBy(row => row.Period).ThenBy(row => row.ItemId).ToArray();
            peer.Send(715, Build(w => w.Write(400001052)));
            peer.Expect(1, 715, Build(w =>
            {
                w.Write((byte)1); w.Write(0); w.Write(0); w.Write(expected.Length);
                foreach (var row in expected) { w.Write(row.ItemId); w.Write(row.Count); }
                w.Write(0);
            }));
            peer.Barrier();
        }
        static byte[] BuyRequest(int box) => Build(w => { w.Write(box); w.Write(1); w.Write(400000086); w.Write(400001052); });
        static byte[] MainPacket(WirePacket[] packets)
        {
            var body = packets.Single(row => row is { Kind: 0, Opcode: 13 } && row.Body[0] == 0).Body;
            That(body.Length == 5 + 123 * U16(body, 3), "重启库存完整主背包每项123B，不使用简化槽位摘要");
            return body;
        }
        long ItemCount(ushort id, int item) => Convert.ToInt64(fixture.Query(
            "SELECT coalesce(sum(json_extract(i.item_core,'$.Value')),0) FROM character_inventory_items i " +
            "JOIN character_inventory_slots s USING(item_uid) WHERE i.character_id=$p0 AND s.list_type=0 AND json_extract(i.item_core,'$.ItemId')=$p1", id, item)[0][0]);
        short ItemSlot(ushort id, int item) => Convert.ToInt16(fixture.Query(
            "SELECT s.slot_index FROM character_inventory_items i JOIN character_inventory_slots s USING(item_uid) " +
            "WHERE i.character_id=$p0 AND s.list_type=0 AND json_extract(i.item_core,'$.ItemId')=$p1", id, item).Single()[0]);
        long AttachmentUid(uint mail) => Convert.ToInt64(fixture.Query(
            "SELECT item_uid FROM mailbox_attachments WHERE mail_id=$p0 AND claimed=0", mail).Single()[0]);
        SkinStorageState SkinState(ushort id) => JsonSerializer.Deserialize<SkinStorageState>((string)fixture.Query(
            "SELECT weapon_skin_state FROM characters WHERE id=$p0", id)[0][0])!;
        SkinStorageState SharedSkin() => JsonSerializer.Deserialize<SkinStorageState>((string)fixture.Query(
            "SELECT state_json FROM account_skin_storage WHERE account=$p0", Account(5))[0][0])!;
        string RestartState() => RewardState() +
            fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_avatar_details ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_creature_details ORDER BY item_uid") +
            fixture.Snapshot("SELECT id,weapon_skin_state FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM account_skin_storage ORDER BY account") +
            fixture.Snapshot("SELECT * FROM item_purchase_counts ORDER BY account,character_id,item_id,period") +
            fixture.Snapshot("SELECT * FROM server_events ORDER BY event_id") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");


        void MatchingRevive(TcpPeer actor, int used, bool accepted = true)
        {
            byte[] key = [(byte)actor.Id, (byte)(actor.Id >> 8)];
            int side = peers.IndexOf(actor) / 4;
            string attempt = fixture.Snapshot("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", actor.Id);
            actor.Send(40, [0x74, 0, 4, 1, 0, 0]);
            var dead = FlushAll(actor);
            That(peers.Skip(side * 4).Take(4).All(peer => dead[peer].Count(packet =>
                    packet is { Kind: 0, Opcode: 32 } && packet.Body.SequenceEqual(new byte[] { key[0], key[1], 0, 0 })) == 1) &&
                peers.Skip((1 - side) * 4).Take(4).All(peer => dead[peer].All(packet => packet is not { Kind: 0, Opcode: 32 })),
                "匹配真实40死亡只通知本原四人分队，不影响对方原实例");
            string before = Saved();
            actor.Send(41, key);
            var revived = FlushAll(actor);
            if (accepted)
            {
                That(revived[actor].Count(packet => packet is { Kind: 1, Opcode: 41 } &&
                        packet.Body.SequenceEqual(new byte[] { 1, key[0], key[1], 0 })) == 1 &&
                    revived[actor].Count(packet => packet is { Kind: 0, Opcode: 32 } &&
                        packet.Body.SequenceEqual(new byte[] { key[0], key[1], 1, 0 })) == 1 &&
                    fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0", actor.Id) is [[long coins]] && coins == 30 - used,
                    "匹配5360真实41上限内恰扣一币并发布原ACK41/32，过房保留个人计数：" + used);
                string after = Saved();
                actor.Send(41, key);
                var replay = FlushAll(actor);
                That(Saved() == after && replay.Values.All(rows => rows.All(packet =>
                    packet is not { Kind: 1, Opcode: 41 } && packet is not { Kind: 0, Opcode: 32 })),
                    "匹配已存活重放41不二次扣币或重播复活");
            }
            else
                That(revived[actor].Count(packet => packet is { Kind: 1, Opcode: 41, Body: [0, 1, 0] }) == 1 &&
                    revived.Values.All(rows => rows.All(packet => packet is not { Kind: 0, Opcode: 32 })) &&
                    fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0", actor.Id) is [[26L]] && before == Saved(),
                    "匹配个人四次耗尽后第5次明确拒绝，即使仍有26币也不改revision或资产");
            That(fixture.Snapshot("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", actor.Id) == attempt,
                "匹配死亡复活始终沿原5360尝试，不重进副本或重置额度");
            string Saved() => fixture.Snapshot() + fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid");
        }
        int ExactItem(string path) => equipmentIndex.Single(row => PvfArchive.NormalizePath("equipment/" + row.Value) == path).Key;
        Character Prepare(int index)
        {
            using var owner = new AccountOwners().Acquire(Account(index), () => { });
            var candidate = CharacterCreation.Prepare("SWMatch" + index, resources.Characters.Get(0), new(65, 1, 450, 234),
                resources.Towns.InitialReturn, resources.Learning.Get);
            var equipped = equipmentIds.Select(id =>
            {
                var template = resources.ItemResources.Items.Get(id);
                return new InventoryItem(0, new(InventoryListType.Equipment, template.EquipmentSlot),
                    new(id, template.Kind, 0) { Durability = template.Durability, Attr = id == weapon ? (byte)10 : (byte)0 });
            }).ToImmutableArray();
            var created = repository.Create(owner, candidate with { Level = 95, AcceptedStoryLevel = 95,
                EquipmentSlotFlags = InventoryMoveService.ExpansionSlotFlags,
                Inventory = candidate.Inventory with { Items = equipped, ReviveCoins = 30 },
                InitialLearning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                    { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning) });
            That(created.Inventory.Items.Length == 12 && provider.Compute(created) >= 5400 && created.Inventory.Items.All(item =>
                InventoryMoveService.Allows(created, item.Position, item.Core, resources.ItemResources.Items,
                    clock.GetUtcNow().ToUnixTimeSeconds(), resources.ItemResources.Contracts)),
                "真实十二件固定模板及武器普通+10满足部位/等级/职业/槽解锁，正式计算抗魔达到5400：" + provider.Compute(created));
            return created;
        }
        Dictionary<TcpPeer, WirePacket[]> FlushAll(TcpPeer? requester = null)
        {
            // 先等命令发起者屏障，保证其跨连接通知已入队，再排空其它连接。
            // 先读其它连接可能在本次命令提交之前收到屏障，漏掉随后抵达的真实名册或进度。
            var ordered = requester is null ? peers : peers.OrderBy(peer => ReferenceEquals(peer, requester) ? 0 : 1).ToList();
            return ordered.ToDictionary(peer => peer, peer =>
            { var rows = Drain(peer); transcripts[peer].AddRange(rows); return rows; });
        }
        uint[] Symbols(TcpPeer peer, uint side, uint kind) => transcripts[peer].Where(packet =>
            packet is { Kind: 1, Opcode: 1525, Body.Length: 13 } && packet.Body[0] == 1 &&
            U32(packet.Body, 1) == side && U32(packet.Body, 5) == kind).Select(packet => U32(packet.Body, 9)).ToArray();
        void LoadTeam(int side, bool partial = false)
        { foreach (var peer in peers.Skip(side * 4).Take(partial ? 3 : 4)) { peer.Send(37, new byte[8]); FlushAll(peer); } }
        string EntryState() => fixture.Snapshot("SELECT id,revision FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id");
        string RewardState() => fixture.Snapshot("SELECT * FROM character_strongway2019_statistics ORDER BY character_id,instance_id") +
            fixture.Snapshot("SELECT * FROM account_strongway2019_rewards ORDER BY account,instance_id") +
            fixture.Snapshot("SELECT * FROM mailbox_messages ORDER BY mail_id") + fixture.Snapshot("SELECT * FROM mailbox_attachments ORDER BY attachment_id");
    }

    static string Account(int index) => "strongway-match-tcp-" + index;
    static byte[] QueueRequest() => Convert.FromHexString("010E000000FFFFFFFFFFFFFFFF00F0140000");
    static byte[] PartyPeer(ushort id, bool invitation) => Build(writer =>
    { writer.Write(id); writer.Write((byte)0); writer.Write(0u); if (invitation) writer.Write(new byte[6]); });
    static byte[] Symbol(uint side, uint kind, uint value) => Build(writer =>
    { writer.Write(side); writer.Write(kind); writer.Write(value); });

    static WirePacket[] Drain(TcpPeer peer)
    {
        peer.SendBarrier();
        var rows = new List<WirePacket>();
        for (int count = 0; count < 256; count++)
        {
            var packet = peer.Read();
            if (packet is { Kind: 1, Opcode: 1347 }) { Bytes(packet.Body, [1], "匹配TCP有界收包屏障"); return rows.ToArray(); }
            That(packet.Kind == 0 || packet.Opcode is 10 or 11 or 15 or 16 or 37 or 39 or 41 or 45 or 46 or 72 or 520 or 521 or 1525 ||
                packet is { Kind: 1, Opcode: 905, Body.Length: 77 } && packet.Body[0] == 1 && U16(packet.Body, 1) == 1 &&
                    packet.Body.AsSpan(3).IndexOfAnyExcept((byte)0) == -1,
                "匹配收包只允许通知与本链已登记应答，实际" + packet.Kind + "/" + packet.Opcode);
            rows.Add(packet);
        }
        throw new InvalidOperationException("FAIL 首期匹配TCP通知超过有界预算");
    }

    static (byte Number, int Port) ContentEntry(TcpTestFixture fixture)
    {
        using var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Options.LogDirectory, "ready.json")));
        var entries = ready.RootElement.GetProperty("ChannelEntries").EnumerateArray()
            .Where(row => row.GetProperty("Entry").GetProperty("Type").GetInt32() == 55).ToArray();
        That(entries.Length == 1, "八人生产TCP需要配置中唯一真实type55入口");
        return (entries[0].GetProperty("Entry").GetProperty("Identity").GetProperty("Number").GetByte(), entries[0].GetProperty("Port").GetInt32());
    }

    sealed class MatchClock : TimeProvider
    {
        readonly DateTimeOffset started = DateTimeOffset.UtcNow;
        long milliseconds;
        public override long TimestampFrequency => 1000;
        public override DateTimeOffset GetUtcNow() => started.AddMilliseconds(Interlocked.Read(ref milliseconds));
        public override long GetTimestamp() => Interlocked.Read(ref milliseconds);
        internal void Advance(TimeSpan amount) => Interlocked.Add(ref milliseconds, checked((long)amount.TotalMilliseconds));
    }
}
