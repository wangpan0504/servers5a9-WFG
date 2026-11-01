using System.Buffers.Binary;
using System.Diagnostics;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FiendwarRaidTcpTests
{
    static void VerifyParties(TcpTestFixture fixture, TcpTestResources resources, RaidResources raid,
        CharacterRepository repository, LukeRaidClockTests.Clock clock, int count)
    {
        var peers = new List<TcpPeer>();
        var locations = new Dictionary<ushort, int>();
        var symbols = new Dictionary<(ushort Character, uint Symbol), uint>();
        var returned = new HashSet<ushort>();
        var results = new HashSet<ushort>();
        var victories = new HashSet<ushort>();
        bool titleReplayChecked = false;
        bool readingSupport = false;
        try
        {
            for (int index = 0; index < count; index++)
            {
                string account = "fiend" + count + "x" + index;
                using var owner = new AccountOwners().Acquire(account, () => { });
                var point = new Location(38, 0, 629, 196);
                var candidate = CharacterCreation.Prepare("Fw" + count + "x" + index, resources.Characters.Get(0), point, point, resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                    { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning);
                var role = repository.Create(owner, candidate with { Level = 95, AcceptedStoryLevel = 95,
                    CompletedQuests = [8672], InitialLearning = learning });
                var items = resources.ItemResources;
                var grant = InventoryRewardGrantService.TryGrantBatch(role,
                    [new(10306760, 2), new(10312385, 2), new(1114, 3), new(7180, 2)],
                    items.Items, items.GoldLimits, items.EpicPieces, clock.GetUtcNow());
                var funded = InventoryService.Commit(repository, owner, role, grant);
                repository.CommitInventory(owner, funded, new(funded.Inventory, funded.Inventory with { ReviveCoins = 20 }, "fixture_fiendwar_coins"));
                if (index == 0)
                    fixture.Sql("""
                        INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix)
                        VALUES($p0,$p1,$p1,$p2,1000),($p3,$p4,$p4,$p2,1000)
                        """, 1000 + count, "FwGuild" + count, role.Id, 2000 + count, "FwOther" + count);
                if (count != 8 || index != 6)
                    fixture.Sql("INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES($p0,$p1,4,1000)",
                        count == 8 && index == 5 ? 2000 + count : 1000 + count, role.Id);
                var peer = Open(fixture, account, gamePort: ChannelTcpTests.Endpoints(fixture)[39], channelNumber: 39);
                peers.Add(peer); peer.Select(); Drain(peer); locations.Add(peer.Id, 2);
                VerifyConsumable(fixture, peer, false, 10);
                VerifyConsumable(fixture, peer, true, 20, 1114);
            }
            That(resources.ItemResources.RecoveryConsumables.TryGet(1114, out var ordinary), "普通药使用真实目录声明");
            clock.Advance(checked((ordinary.CoolTime + 999) / 1000));
            var leader = peers[0];
            leader.Send(664, Build(writer => { writer.Write((byte)2); writer.Write(Name("Fiend")); writer.Write(new byte[4]); }));
            var created = Drain(leader);
            That(created.Any(row => row is { Kind: 1, Opcode: 664, Body: [1, ..] }), "多人创建同一完整漩涡框架");
            uint raidKey = U32(created.Single(row => row is { Kind: 1, Opcode: 664 }).Body, 1);
            byte server = checked((byte)(raidKey >> 24));
            if (count == 1)
            {
                VerifyNameHistory(fixture, resources, repository, leader, server);
                VerifyCrossChannel(fixture, resources, repository, leader, server, raidKey);
            }
            That(!RaidPanelTests.ReadGuideFlag(created.Single(row => row is { Kind: 0, Opcode: 590 }).Body, 4), "可单人开战的完整团队面板不显示引导标记");
            foreach (var (peer, index) in peers.Skip(1).Select((peer, index) => (peer, index)))
            {
                var requester = index % 2 == 0 ? leader : peer;
                var responder = index % 2 == 0 ? peer : leader;
                CheckUser(requester, responder, server, index % 2 == 0 ? 0u : raidKey,
                    index % 2 == 0 ? (ushort)0 : leader.Id, index % 2 == 0 ? (byte)0 : checked((byte)(index + 1)));
                requester.Send(10, AntonRaidTcpTests.Peer(responder.Id, 0, true)); Drain(requester); Drain(responder);
                responder.Send(11, AntonRaidTcpTests.Peer(requester.Id, 0, false));
                That(Drain(responder).Any(row => row is { Kind: 1, Opcode: 11, Body: [1, ..] }), "真实邀请或申请进入八人攻坚名册");
                if (responder != peer) Drain(peer);
                foreach (var other in peers.Where(other => other != peer)) Drain(other);
            }
            leader.Send(1361, Build(writer => { writer.Write((byte)2); writer.Write(raidKey); }));
            That(!RaidPanelTests.ReadGuideFlag(Drain(leader).Single(row => row is { Kind: 1, Opcode: 1361 }).Body, 6),
                "完整名册信息在一人、四人、八人时均保持团队模式");
            CheckUser(leader, peers[^1], server, raidKey, leader.Id, checked((byte)count));
            leader.Send(891, [server, (byte)peers[^1].Id, (byte)(peers[^1].Id >> 8), 38]);
            That(Drain(leader).Any(row => row is { Kind: 1, Opcode: 891, Body: [0, ..] }), "891错误频道明确失败，不让客户端无限等待或泄漏错误目标");
            foreach (byte inactive in new byte[] { 1, 0 })
            {
                peers[^1].Send(825, [inactive]);
                foreach (var peer in peers.OrderBy(peer => peer == peers[^1] ? 0 : 1))
                {
                    var states = Drain(peer);
                    That(states.Any(row => row is { Kind: 0, Opcode: 764, Body.Length: 3 } &&
                        U16(row.Body) == peers[^1].Id && row.Body[2] == inactive), "825只改变本人状态并向全团发送真实764");
                    var snapshot = RaidPanelTests.ReadMemberStates(states.Single(row => row is { Kind: 0, Opcode: 765 }).Body);
                    That(snapshot.Count == count && snapshot[peers[^1].Id] == (inactive == 1) &&
                        snapshot.All(row => row.Key == peers[^1].Id || !row.Value), "765完整快照保留其他成员状态，不挪作准备或通关资格");
                }
                peers[^1].Send(825, [inactive]);
                That(!Drain(peers[^1]).Any(row => row is { Kind: 0, Opcode: 764 or 765 }), "重复状态幂等，不重复广播");
            }
            if (count == 4)
            {
                leader.Send(670, Build(writer => { writer.Write((byte)2); writer.Write(Name("FwPanel")); writer.Write((byte)0); }));
                That(Drain(leader).Any(row => row is { Kind: 1, Opcode: 670, Body: [1] }), "漩涡团长通过原面板改名");
                foreach (var peer in peers.Skip(1)) Drain(peer);
                leader.Send(669, Build(writer => { writer.Write(1u); writer.Write((uint)peers[1].Id); writer.Write(0u); }));
                That(Drain(leader).Any(row => row is { Kind: 1, Opcode: 669, Body: [1] }), "漩涡面板真实委任团长");
                foreach (var peer in peers.Skip(1)) Drain(peer);
                leader.Send(666);
                That(Drain(leader).Any(row => row is { Kind: 1, Opcode: 666, Body: [0, ..] }), "旧团长开战被拒绝，不扣费");
                peers[1].Send(669, Build(writer => { writer.Write(1u); writer.Write((uint)leader.Id); writer.Write(0u); }));
                That(Drain(peers[1]).Any(row => row is { Kind: 1, Opcode: 669, Body: [1] }), "新团长可通过原面板重新委任");
                foreach (var peer in peers.Where(peer => peer != peers[1])) Drain(peer);
                leader.Send(665, BitConverter.GetBytes(peers[3].Id));
                That(!Drain(leader).Any(row => row is { Kind: 1, Opcode: 665 }), "踢人不向团长发送关闭自身面板的退团回包");
                That(Drain(peers[3]).Any(row => row is { Kind: 1, Opcode: 665, Body: [1, 1] }), "被踢者收到强制退出面板通知");
                foreach (var peer in peers.Skip(1).Take(2)) Drain(peer);
                peers[3].Send(10, AntonRaidTcpTests.Peer(leader.Id, 0, true)); Drain(peers[3]); Drain(leader);
                leader.Send(11, AntonRaidTcpTests.Peer(peers[3].Id, 0, false));
                That(Drain(leader).Any(row => row is { Kind: 1, Opcode: 11, Body: [1, ..] }), "被踢者可重新申请，团长接受后恢复真实名册");
                foreach (var peer in peers.Skip(1)) Drain(peer);
            }
            CompletePriorCommands();
            SupportRequest(leader, [0], null);
            leader.Send(666);
            That(Drain(leader).Any(row => row is { Kind: 1, Opcode: 666, Body: [1] }), "多人不预设路线分队也能开团");
            foreach (var peer in peers.Skip(1))
                That(Drain(peer).Any(row => row is { Kind: 0, Opcode: 1522 }), "所有成员收到真实漩涡开战");
            if (count > 1) SupportRequest(peers[1], [0], null);
            SupportRequest(leader, [3], null);
            SupportRequest(leader, [0, 0], null);
            SupportRequest(leader, [0], 0);
            SupportRequest(leader, [1], null);
            clock.AdvanceMillisecondsWithoutCallbacks(2999);
            SupportRequest(leader, [1], null);
            clock.AdvanceMillisecondsWithoutCallbacks(1);
            SupportRequest(leader, [1], 1);
            clock.AdvanceWithoutCallbacks(3);
            SupportRequest(leader, [2], 2);
            foreach (var (peer, index) in peers.Select((peer, index) => (peer, index)))
                That(fixture.Query("SELECT guild_id,guild_joined_at FROM character_raid_runs WHERE character_id=$p0 AND state=0", peer.Id)
                    is [[long frozenGuild, long frozenJoined]] && frozenGuild ==
                        (count == 8 && index == 6 ? 0 : count == 8 && index == 5 ? 2000 + count : 1000 + count) &&
                    frozenJoined == (count == 8 && index == 6 ? 0 : 1000), "开战扣票事务冻结原会籍，无会籍明确记零");
            if (count == 8)
            {
                fixture.Sql("INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES($p0,$p1,4,1000)", 1000 + count, peers[6].Id);
                fixture.Sql("UPDATE guild_members SET joined_at_unix=1001 WHERE character_id=$p0", peers[7].Id);
            }
            if (count == 4) Reconnect(3);
            foreach (var (peer, index) in peers.Select((peer, index) => (peer, index))) Move(peer, index < 4 ? 15 : 17);
            var first = peers.Take(4).ToArray(); var second = peers.Skip(4).ToArray();
            var entry = Enter(first, 15, direct: true);
            if (second.Length != 0) Enter(second, 17);
            clock.AdvanceWithoutCallbacks(3);
            SupportRequest(leader, [0], 0);
            // 实际移动/快照不会重播已写支援；另一分队也必须只收到一次。
            foreach (var peer in peers)
                That(!Drain(peer).Any(row => row is { Kind: 0, Opcode: 1554 }), "同一支援事件在持续战况同步中不重播");
            foreach (var team in new[] { first, second }.Where(team => team.Length != 0))
                foreach (var peer in team)
                {
                    VerifyConsumable(fixture, peer, false, 21, 1114);
                    VerifyConsumable(fixture, peer, false, 22, 1114);
                    VerifyBlockedLegacyConsumable(fixture, peer);
                    VerifyConsumable(fixture, peer, true, 11);
                    VerifyConsumable(fixture, peer, false, 12);
                }
            // 本场四币由不同角色依次消耗；第五次不能借个人计数重新获得额度。
            for (int index = 0; index <= 4; index++)
            {
                var target = first[index % first.Length];
                long coins = Convert.ToInt64(fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0", target.Id)[0][0]);
                target.Send(40, [0x74, 0, 4, 1, 0, 0]);
                foreach (var peer in first.OrderBy(peer => peer == target ? 0 : 1)) Drain(peer);
                target.Send(41, [(byte)target.Id, (byte)(target.Id >> 8)]);
                var own = Drain(target);
                foreach (var peer in first.Where(peer => peer != target)) Drain(peer);
                That(own.Any(row => row is { Kind: 1, Opcode: 41 } && row.Body[0] == (index < 4 ? 1 : 0)) &&
                    Convert.ToInt64(fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0", target.Id)[0][0]) == coins - (index < 4 ? 1 : 0),
                    "共享四币实际扣钱包与第五次拒绝：" + index);
            }
            foreach (var peer in first) { peer.Send(42); Drain(peer); }
            // 取消罚站后立即原地重进；先前死亡与用币不能污染新Run。
            entry = Enter(first, 15);
            // 四人场保留雷达，走真实终局正例；其余人数继续覆盖拆过设施的负例。
            if (count != 4) Clear(first, 15, entry);
            foreach (var peer in first) { peer.Send(42); Drain(peer); }
            That(first.All(peer => fixture.Query("SELECT state FROM character_raid_runs WHERE character_id=$p0", peer.Id) is [[0L]]),
                "四人真实终图死亡清雷达只记局部成果");
            That(peers.All(peer => fixture.Query("SELECT count(*) FROM guild_contribution_events WHERE source=5 AND character_id=$p0", peer.Id) is [[0L]]),
                "真实局部清图不提前发放整团通关贡献");
            var timed = second;
            if (timed.Length == 0)
            {
                foreach (var peer in first) Move(peer, 17, 15);
                timed = first; Enter(timed, 17);
            }
            // 一人/八人保留局部超时；单人另有299秒断线，四人保留较短安全通关正例。
            if (count != 4)
            {
                clock.Advance(raid.Fiendwar!.Areas[17].LimitSeconds * 2);
                foreach (var peer in timed)
                {
                    // 时钟回调异步取得原命令门禁，等待真实退场包；不由测试代替发送42。
                    bool exitSeen = false;
                    for (int budget = 0; budget < 256 && !exitSeen; budget++)
                    {
                        var frame = peer.Read();
                        exitSeen = frame is { Kind: 0, Opcode: 24, Body.Length: >= 4 } && frame.Body[0] == 57 && frame.Body[1] == 17;
                    }
                    That(exitSeen, "局部超时自动返回原战场区域，不要求客户端主动撤退"); Drain(peer);
                }
                Enter(timed, 17);
            }
            foreach (var peer in timed) { peer.Send(42); Drain(peer); }
            foreach (int camp in new[] { 27, 28, 29 })
            {
                foreach (var peer in first) Move(peer, camp);
                var campEntry = Enter(first, camp); Clear(first, camp, campEntry);
                foreach (var peer in first) { peer.Send(42); Drain(peer); }
            }
            That(symbols[(first[0].Id, 125)] == 1, "三座兵营真实清图锁存Boss入口");
            foreach (var peer in first) Move(peer, 30);
            var blackRoom = Clear(first, 30, Enter(first, 30), terminal: false);
            uint firstElement = symbols[(first[0].Id, 92)];
            That(firstElement is >= 5 and <= 8, "单侧抵达真实本体即可获得元素，不等对侧上线");
            first[0].Send(1554, Build(writer => { writer.Write(1u); writer.Write(99u); }));
            That(!Drain(first[0]).Any(row => row is { Kind: 1, Opcode: 1554 }), "未知元素不授予成果或伪造成功");
            for (int element = 0; element < 3; element++) BossReport(first, 1, symbols[(first[0].Id, 92)]);
            That(symbols[(first[0].Id, 92)] == 9, "本侧三个不同真实元素完成后独立开原窗口");
            BossReport(first, 0, 0);
            clock.Advance(15); BossReport(first, 2, 0);
            WaitReturn(first);
            if (count == 1)
            {
                uint saved = symbols[(first[0].Id, 95)];
                first[0] = Reconnect(0);
                That(symbols[(first[0].Id, 95)] == saved && symbols[(first[0].Id, 92)] == 9,
                    "全团离线重返保留原黑世界完成状态与实际剩余HP，不重抽元素或回血");
            }
            var white = second.Length == 0 ? first : second;
            foreach (var peer in white) Move(peer, 31);
            var whiteRoom = Clear(white, 31, Enter(white, 31), terminal: false);
            That(symbols[(white[0].Id, 93)] is >= 5 and <= 8, "另一侧保留第四个必要元素，接力不重抽已做成果");
            BossReport(white, 1, symbols[(white[0].Id, 93)]);
            That(symbols[(white[0].Id, 93)] == 9, "完成第四元素才开启另一侧原窗口");
            BossReport(white, 0, 0); clock.Advance(15); BossReport(white, 2, 0);
            That(symbols[(white[0].Id, 95)] == 0 && symbols[(white[0].Id, 96)] == 0,
                "两侧真实输出结果汇合归零，尚不能用报告替代终图死亡");
            That(fixture.Query("SELECT phase0_event IS NULL FROM character_raid_runs WHERE character_id=$p0", white[0].Id) is [[1L]],
                "纯Boss报告尚未产生胜利奖励资格");
            var bossMap = whiteRoom[0].Single(row => row is { Kind: 0, Opcode: 29 }).Body;
            if (count == 4)
            {
                white[0].Send(40, [0x74, 0, 4, 1, 0, 0]);
                foreach (var peer in white) Drain(peer);
                white[0].Send(41, [(byte)white[0].Id, (byte)(white[0].Id >> 8)]);
                That(Drain(white[0]).Any(row => row is { Kind: 1, Opcode: 41, Body: [1, ..] }),
                    "白世界实际使用一枚共享币，后续成就按本场用币事实判断");
                foreach (var peer in white.Skip(1)) Drain(peer);
            }
            ClearMap(white, bossMap, true);
            foreach (var peer in white) Drain(peer);
            WaitReturn(white);
            foreach (var peer in peers) Drain(peer);
            That(peers.All(peer => fixture.Query("SELECT state FROM character_raid_runs WHERE character_id=$p0", peer.Id) is [[2L]]),
                "两世界机制、汇合及正式死亡后只产生一次整团通关");
            SupportRequest(peers[0], [0], null);
            foreach (var peer in white)
                That(fixture.Query("SELECT quest_id,total,applied FROM character_fiendwar_title_facts WHERE character_id=$p0 AND quest_id IN(7003,7008) ORDER BY quest_id", peer.Id)
                        is var worldFacts && (count == 4 ? worldFacts is [[7003L, 1L, 1L]] : worldFacts is [[7003L, 1L, 1L], [7008L, 1L, 1L]]) &&
                    fixture.Query("SELECT remaining1 FROM character_title_progress WHERE character_id=$p0 AND quest_id=7003", peer.Id) is [[29L]] &&
                    (count == 4 ? fixture.Query("SELECT count(*) FROM character_title_progress WHERE character_id=$p0 AND quest_id=7008", peer.Id) is [[0L]] :
                        fixture.Query("SELECT remaining1+remaining2+remaining3 FROM character_title_progress WHERE character_id=$p0 AND quest_id=7008", peer.Id) is [[0L]]),
                    "真实白世界清图只奖励实际参与者，无币称号复验整个本场的共享用币事实");
            That(peers.All(peer => fixture.Query("SELECT count(*) FROM character_fiendwar_title_facts WHERE character_id=$p0 AND quest_id=7005", peer.Id)
                    is [[long facilityFacts]] && facilityFacts == (count == 4 ? 1 : 0)),
                "真实未拆设施通关记一次，拆过雷达的场次不记成就");
            foreach (var peer in peers)
            {
                // 已返场的另一分队沿原生417消费持久事实；客户端任意Delta不能多记或补造。
                peer.Send(417, Build(writer => { writer.Write(7004); writer.Write(ushort.MaxValue);
                    writer.Write(ushort.MaxValue); writer.Write(ushort.MaxValue); }));
                Drain(peer);
            }
            That(peers.All(peer => fixture.Query("SELECT total,applied FROM character_fiendwar_title_facts WHERE character_id=$p0 AND quest_id=7004", peer.Id)
                    is var safetyFacts && (count == 1 ? safetyFacts.Length == 0 : safetyFacts is [[1L, 1L]])),
                "真实安全通关只记一次，单人长断线曾到基地前线不记；417最大自报不补造或放大事实");
            foreach (var (peer, index) in peers.Select((peer, index) => (peer, index)))
            {
                long expected = count == 1 ? 250 : count == 4 || index < 4 ? 1000 : index is 6 or 7 ? 0 : 250;
                That(fixture.Query("SELECT points FROM guild_contribution_events WHERE character_id=$p0 AND source=5", peer.Id)
                    is [[long points]] && points == expected,
                    "真实终局按最后小队及原会籍计贡献；混会、入场后入会、退会重入不借八人整团人数放大");
            }
            string guildSnapshot = fixture.Snapshot("SELECT * FROM guild_contribution_events WHERE source=5 ORDER BY character_id");
            foreach (var peer in peers)
            {
                peer.Send(1403, [0, 0]);
                var adventure = Drain(peer).Single(row => row is { Kind: 1, Opcode: 1403 }).Body;
                That(adventure[0] == 1 && U32(adventure, 19 + 32) == 1 &&
                    U32(adventure, 19 + 28) == 0 && U32(adventure, 19 + 36) == 0,
                    "1/4/8人正式终局后原生冒险团面板读取本账号真实漩涡累计，不串其他团本");
            }
            That(peers.All(peer => results.Contains(peer.Id) && victories.Contains(peer.Id)),
                "原生601结果与1523胜利到达每个角色，不套用587");
            foreach (var peer in peers)
                foreach (byte kind in new byte[] { 1, 0 })
                {
                    peer.Send(660, [kind, 0]);
                    var claimed = Drain(peer);
                    int ack = claimed.FindIndex(row => row is { Kind: 1, Opcode: 660, Body: [1] });
                    var display = claimed.SingleOrDefault(row => row is { Kind: 0, Opcode: 600 });
                    That(ack >= 0 && display is not null && claimed.IndexOf(display) < ack &&
                        display.Body[0] == kind && display.Body[1] == 1 && U16(display.Body, 2) == peer.Id &&
                        display.Body[5] > 0 && display.Body.Length == 6 + 16 * display.Body[5],
                        "真实产物先提交与展示，再回复660成功，通用16B行属于本人");
                    var receipts = fixture.Snapshot("SELECT * FROM character_raid_reward_receipts WHERE character_id=$p0 ORDER BY kind", peer.Id);
                    peer.Send(660, [kind, 0]);
                    That(Drain(peer).Any(row => row is { Kind: 1, Opcode: 660, Body: [1] }) &&
                        receipts == fixture.Snapshot("SELECT * FROM character_raid_reward_receipts WHERE character_id=$p0 ORDER BY kind", peer.Id),
                        "660重放不增加收据、不重抽已冻结奖励");
                }
            That(peers.All(peer => fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE character_id=$p0 AND raid_type=2", peer.Id) is [[2L]]),
                "同框架1/4/8人实际各领取两类末期奖励");
            foreach (var peer in peers)
            {
                That(fixture.Query("SELECT m.balance FROM account_material_balances m JOIN characters c ON c.account=m.account WHERE c.id=$p0 AND m.item_id=10308807 AND m.balance<>0", peer.Id).Length == 0,
                    "660领取的是原始不渝礼盒，未兑换不提前记材料或周获得");
                var box = fixture.Query("SELECT s.slot_index FROM character_inventory_slots s JOIN character_inventory_items i ON i.item_uid=s.item_uid WHERE s.character_id=$p0 AND s.list_type=0 AND json_extract(i.item_core,'$.ItemId')=10306817", peer.Id);
                That(box.Length == 1, "真实翻牌领取150不渝礼盒，重复660不复制礼盒");
                short slot = Convert.ToInt16(box[0][0]);
                peer.Send(160, Build(writer => { writer.Write(slot); writer.Write(1); }));
                var opened = Drain(peer);
                var expectedAck = Build(writer =>
                {
                    writer.Write((byte)1); writer.Write((byte)0); writer.Write(10306817); writer.Write(slot);
                    writer.Write(0); writer.Write(0); writer.Write((ushort)1); writer.Write(10308807); writer.Write(150);
                });
                That(opened.Count(row => row is { Kind: 1, Opcode: 160 } && row.Body.SequenceEqual(expectedAck)) == 1 &&
                    opened.Any(row => row is { Kind: 0, Opcode: 563, Body.Length: 10 } && row.Body[0] == 1 &&
                    U32(row.Body, 2) == 10308807 && U32(row.Body, 6) == 150) &&
                    opened.Any(row => row is { Kind: 0, Opcode: 1460 }) && opened.Any(row => row is { Kind: 0, Opcode: 1461 }),
                    "真实160开盒后同步账号余额与两级周额度");
                string materialState = fixture.Snapshot("SELECT * FROM account_material_balances ORDER BY account,item_id");
                peer.Send(160, Build(writer => { writer.Write(slot); writer.Write(1); }));
                That(Drain(peer).Any(row => row is { Kind: 1, Opcode: 160, Body: [0, 19, 0] }) &&
                    materialState == fixture.Snapshot("SELECT * FROM account_material_balances ORDER BY account,item_id"),
                    "重复打开已消费礼盒不再计余额或周额度");
            }
            That(peers.All(peer => fixture.Query("SELECT m.balance,m.raid_earned FROM account_material_balances m JOIN characters c ON c.account=m.account WHERE c.id=$p0 AND m.item_id=10308807", peer.Id)
                is [[150L, 150L]]), "团队原始礼盒实际兑换150不渝，重复660和160均不重复计入");
            That(guildSnapshot == fixture.Snapshot("SELECT * FROM guild_contribution_events WHERE source=5 ORDER BY character_id"),
                "通关贡献不依赖660翻牌，领取及重复请求均不再发分");
            Console.WriteLine("PASS fiendwar native encounter roster=" + count);
        }
        finally { foreach (var peer in peers) peer.Dispose(); }

        TcpPeer Reconnect(int index)
        {
            var old = peers[index]; ushort id = old.Id;
            string before = fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", id);
            old.Dispose();
            var wait = Stopwatch.StartNew();
            while (fixture.LukeRejoinCandidate(id) is not { OldOwnerReleased: true } && wait.Elapsed < TimeSpan.FromSeconds(10))
                Task.Delay(10).GetAwaiter().GetResult();
            var candidate = fixture.LukeRejoinCandidate(id);
            That(candidate is { OldOwnerReleased: true }, "漩涡真实断线释放原owner后保留原团五分钟候选");
            if (count == 1) clock.Advance(299);
            var fresh = Open(fixture, "fiend" + count + "x" + index,
                gamePort: ChannelTcpTests.Endpoints(fixture)[39], channelNumber: 39);
            peers[index] = fresh;
            var selected = fresh.Select().Concat(Drain(fresh)).ToArray();
            That(selected.Any(frame => frame is { Kind: 0, Opcode: 835 } &&
                BinaryPrimitives.ReadUInt32LittleEndian(frame.Body) == candidate!.Key), "新连接收到指向原团的835");
            fresh.Send(668, BitConverter.GetBytes(candidate!.Key + 1));
            That(Drain(fresh).Any(frame => frame is { Kind: 1, Opcode: 668, Body: [0, 19, 0] }), "错团重返拒绝且不消费候选");
            fresh.Send(668, BitConverter.GetBytes(candidate.Key));
            That(Drain(fresh).Any(frame => frame is { Kind: 1, Opcode: 668, Body: [1] }), "668实际恢复原Battle与战场位置");
            fresh.Send(668, BitConverter.GetBytes(candidate.Key));
            That(Drain(fresh).Any(frame => frame is { Kind: 1, Opcode: 668, Body: [1] }) &&
                fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", id) == before &&
                fixture.Query("SELECT count(*) FROM character_raid_runs WHERE character_id=$p0", id) is [[1L]],
                "重返及重复请求不重扣门票、不新建入场或重抽奖励");
            foreach (var peer in peers.Where(peer => peer != fresh)) Drain(peer);
            return fresh;
        }

        void CompletePriorCommands()
        {
            var wait = Stopwatch.StartNew();
            foreach (var peer in peers)
            {
                // 先固定当前连接/代际/选角epoch，不能借旧连接或旧1278日志满足本次屏障。
                System.Text.Json.JsonElement previous = default;
                while (wait.Elapsed < TimeSpan.FromSeconds(10))
                {
                    var events = fixture.Events();
                    var selected = events.LastOrDefault(row => row.GetProperty("Kind").GetString() == "selected" &&
                        row.GetProperty("Detail").GetProperty("Character").GetUInt16() == peer.Id);
                    if (selected.ValueKind != System.Text.Json.JsonValueKind.Undefined)
                    {
                        int currentConnection = selected.GetProperty("Detail").GetProperty("Connection").GetInt32();
                        previous = events.LastOrDefault(row => row.GetProperty("Kind").GetString() == "command_completed" &&
                            row.GetProperty("Detail").GetProperty("Connection").GetInt32() == currentConnection &&
                            row.GetProperty("Detail").GetProperty("Character").ValueKind == System.Text.Json.JsonValueKind.Number &&
                            row.GetProperty("Detail").GetProperty("Character").GetUInt16() == peer.Id);
                    }
                    if (previous.ValueKind != System.Text.Json.JsonValueKind.Undefined) break;
                    Task.Delay(10).GetAwaiter().GetResult();
                }
                That(previous.ValueKind != System.Text.Json.JsonValueKind.Undefined,
                    $"开战前取得成员{peer.Id}本连接完成身份，不能只凭ACK");
                var before = previous.GetProperty("Detail");
                int connection = before.GetProperty("Connection").GetInt32();
                long generation = before.GetProperty("Generation").GetInt64();
                long epoch = before.GetProperty("SelectionEpoch").GetInt64();
                ulong serial = before.GetProperty("RequestSerial").GetUInt64();
                ushort sequence = peer.NextClientSequence;
                int eventBoundary = fixture.Events().Length;
                That(wait.Elapsed < TimeSpan.FromSeconds(10), "发送1278前仍在10秒轮询/阶段边界预算内");
                Drain(peer); // 原1278 ACK[1]与所有既有Observe包断言仍逐包执行；单次读沿原socket超时。
                That(wait.Elapsed < TimeSpan.FromSeconds(10), "1278读取后核10秒阶段边界，不声称中断socket读取");
                bool completed = false;
                while (wait.Elapsed < TimeSpan.FromSeconds(10))
                {
                    completed = fixture.Events().Skip(eventBoundary).Any(row =>
                    {
                        if (row.GetProperty("Kind").GetString() != "command_completed") return false;
                        var detail = row.GetProperty("Detail");
                        return detail.GetProperty("Connection").GetInt32() == connection &&
                            detail.GetProperty("Generation").GetInt64() == generation &&
                            detail.GetProperty("SelectionEpoch").GetInt64() == epoch &&
                            detail.GetProperty("Character").GetUInt16() == peer.Id &&
                            detail.GetProperty("Opcode").GetUInt16() == 1278 &&
                            detail.GetProperty("Sequence").GetUInt16() == sequence &&
                            detail.GetProperty("RequestSerial").GetUInt64() > serial;
                    });
                    if (completed) break;
                    Task.Delay(10).GetAwaiter().GetResult();
                }
                That(completed, $"成员{peer.Id}连接{connection}/代际{generation}/epoch{epoch}/1278序号{sequence}完成全部请求刷新，10秒轮询/阶段边界预算内不重发开战");
            }
        }

        List<WirePacket> Drain(TcpPeer peer)
        {
            var frames = AntonTcpProbe.Barrier(peer, "fiendwar party " + count);
            foreach (var frame in frames) Observe(peer, frame);
            return frames;
        }
        void Observe(TcpPeer peer, WirePacket frame)
        {
                if (frame is { Kind: 0, Opcode: 1554 })
                    That(readingSupport, "移动、进退图、重返及持续战况不重播已写或过期的支援提示");
                That(frame is not { Kind: 0, Opcode: 587 }, "漩涡只走自己的1523状态合同");
                if (frame is { Kind: 0, Opcode: 601, Body.Length: 10 }) results.Add(peer.Id);
                if (frame is { Kind: 0, Opcode: 1523, Body: [3, 0, 0] }) victories.Add(peer.Id);
                if (frame is { Kind: 0, Opcode: 24, Body.Length: >= 2 } && frame.Body[0] == 57) returned.Add(peer.Id);
                if (frame is not { Kind: 0, Opcode: 1530, Body.Length: > 0 }) return;
                for (int i = 0; i < frame.Body[0]; i++)
                    symbols[(peer.Id, BinaryPrimitives.ReadUInt32LittleEndian(frame.Body.AsSpan(1 + i * 8)))] =
                        BinaryPrimitives.ReadUInt32LittleEndian(frame.Body.AsSpan(5 + i * 8));
        }
        void WaitReturn(TcpPeer[] team)
        {
            foreach (var peer in team)
            {
                for (int budget = 0; budget < 256 && !returned.Contains(peer.Id); budget++)
                    if (peer.Read() is { Kind: 0, Opcode: 24, Body: [57, ..] }) returned.Add(peer.Id);
                That(returned.Contains(peer.Id), "保存本侧成果后自动正常返回战场"); Drain(peer);
            }
        }
        void BossReport(TcpPeer[] team, uint operation, uint value)
        {
            team[0].Send(1554, Build(writer => { writer.Write(operation); writer.Write(value); }));
            var frames = Drain(team[0]);
            That(frames.Any(row => row is { Kind: 0, Opcode: 1530 }), "Boss报告通过正式实例校验后发布真实机制符号");
            That(!frames.Any(row => row.Opcode == 1554), "Boss报告不伪造同号支援通知或未知成功ACK");
            foreach (var peer in team.Skip(1)) Drain(peer);
        }
        void SupportRequest(TcpPeer source, byte[] body, byte? accepted)
        {
            string before = System.Text.Json.JsonSerializer.Serialize(fixture.Query(
                "SELECT id,revision FROM characters ORDER BY id"));
            source.Send(1570, body);
            readingSupport = accepted.HasValue;
            foreach (var peer in peers.OrderBy(peer => peer == source ? 0 : 1))
            {
                var frames = Drain(peer);
                var notices = frames.Where(row => row is { Kind: 0, Opcode: 1554 }).ToArray();
                That(notices.Length == (accepted.HasValue ? 1 : 0), "真实1570仅向原团全体广播一次合格团长支援，拒绝不通知");
                if (accepted is { } lane) Bytes(notices.Single().Body, [lane], "真实1554完整字节为上中下路线号");
                That(!frames.Any(row => row is { Kind: 1, Opcode: 1570 }), "1570不伪造原生空回调的成功ACK");
            }
            readingSupport = false;
            That(before == System.Text.Json.JsonSerializer.Serialize(fixture.Query(
                "SELECT id,revision FROM characters ORDER BY id")), "支援显示不修改角色持久revision或资产");
        }
        void Move(TcpPeer peer, int target, int? from = null)
        {
            int origin = from ?? locations[peer.Id];
            var queue = new Queue<int>(); queue.Enqueue(origin);
            var previous = new Dictionary<int, int> { [origin] = -1 };
            while (queue.TryDequeue(out int current) && !previous.ContainsKey(target))
                foreach (int next in raid.Fiendwar!.Areas[current].Neighbours.Where(next => next == target || next is not (30 or 31)))
                    if (previous.TryAdd(next, current)) queue.Enqueue(next);
            var path = new List<int>();
            for (int step = target; step != origin; step = previous[step]) path.Add(step);
            path.Reverse();
            foreach (int next in path)
            {
                var destination = new Location(57, checked((byte)next), 0, 0);
                // 黑白入口为单向门，目标图只声明一处回门；其它兵营入口使用真实可站落点。
                var arrival = resources.Towns.Entrance(new(57, checked((byte)origin), 0, 0), destination, new HashSet<int> { 8672 })
                    ?? resources.Towns.Spawn(destination, new HashSet<int> { 8672 })
                    ?? throw new InvalidDataException("Fiendwar destination has no valid landing");
                peer.Send(36, Area(57, checked((byte)next), checked((ushort)arrival.X), checked((ushort)arrival.Y), 57, checked((ushort)origin)));
                That(Drain(peer).Any(row => row is { Kind: 0, Opcode: 23 } && row.Body[3] == next), "多人各自移动且保存同一战场位置");
                origin = next;
                locations[peer.Id] = next;
            }
            // 八人时另一线路仍持续接收全团战况，不能把数百个合法广播积到最后才读。
            foreach (var other in peers.Where(other => other != peer)) Drain(other);
        }
        List<WirePacket>[] Enter(TcpPeer[] team, int area, bool direct = false)
        {
            foreach (var peer in team) returned.Remove(peer.Id);
            int dungeon = raid.Fiendwar!.Areas[area].DungeonId;
            if (!direct)
            {
                team[0].Send(15, BitConverter.GetBytes(dungeon));
                var selected = team.Select(Drain).ToArray();
                That(selected[0].Any(row => row is { Kind: 1, Opcode: 15, Body: [1] }), "同区域四人自动临时成队并接受15");
                That(selected.All(frames => frames.Count(row => row is { Kind: 0, Opcode: 27 }) == 1), "团队15先为每名成员建立一次原生选图场景");
            }
            byte[] request = (byte[])AntonTcpProbe.LiveSelect210.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(request, dungeon); team[0].Send(16, request);
            var rows = team.Select(Drain).ToArray();
            var map = rows[0].Single(row => row is { Kind: 0, Opcode: 29 }).Body;
            foreach (var frames in rows)
            {
                That(frames.Count(row => row is { Kind: 0, Opcode: 27 }) == (direct ? 1 : 0) &&
                    (!direct || frames.FindIndex(row => row is { Kind: 0, Opcode: 27 }) < frames.FindIndex(row => row is { Kind: 0, Opcode: 28 })),
                    "团队直接16先发27再加载，已有15场景不重开");
                Bytes(frames.Single(row => row is { Kind: 0, Opcode: 29 }).Body, map, "同遭遇共用真实地图、种子和怪物键");
                int scaledCount = Math.Min(4, area is 30 or 31 ? count : team.Length);
                if (scaledCount < 4)
                {
                    var combat = frames.Single(row => row is { Kind: 0, Opcode: 32752 }).Body;
                    That(frames.FindIndex(row => row is { Kind: 0, Opcode: 32752 }) <
                        frames.FindIndex(row => row is { Kind: 0, Opcode: 29 }) && combat.Length == 80 &&
                        U32(combat) == 0x3349414D && U32(combat, 8) == (uint)dungeon &&
                        U32(combat, 12) == U32(map, 14) && combat[20] == map[0] && combat[21] == map[1],
                        "真实遭遇在原生建图前发送匹配房间身份的MAI3，不以领域公式代替上线参数");
                    That(U32(combat, 56) == 2 && BinaryPrimitives.ReadSingleLittleEndian(combat.AsSpan(60)) == scaledCount * .25f &&
                        U32(combat, 72) == 2 && BinaryPrimitives.ReadSingleLittleEndian(combat.AsSpan(76)) == (40 + scaledCount * 15) / 100f,
                        "真实网络包同时携带冻结HP及物魔攻击倍率，双世界按整团冻结人数");
                }
                else
                    That(!frames.Any(row => row is { Kind: 0, Opcode: 32752 }), "满倍率团队保持原生属性，不发送额外覆盖");
            }
            Load(team); return rows;
        }
        void CheckUser(TcpPeer requester, TcpPeer target, byte server, uint key, ushort captain, byte members)
        {
            requester.Send(891, [server, (byte)target.Id, (byte)(target.Id >> 8), 39]);
            var body = Drain(requester).Single(row => row is { Kind: 1, Opcode: 891 }).Body;
            Bytes(body, Build(writer => { writer.Write((byte)1); writer.Write(key); writer.Write(captain);
                writer.Write(members); writer.Write(new byte[3]); }), "自然申请前891回报真实团key/团长/人数，随后仍由10/11实际加入");
        }
        void Load(TcpPeer[] team)
        {
            foreach (var peer in team) peer.Send(37, new byte[8]);
            foreach (var peer in team)
            {
                bool ready = false;
                for (int budget = 0; budget < 256 && !ready; budget++)
                {
                    var frame = peer.Read(); Observe(peer, frame);
                    ready = frame is { Kind: 1, Opcode: 37, Body: [1] };
                }
                That(ready, "正式四人加载屏障全部释放");
                Drain(peer);
            }
        }
        List<WirePacket>[] Clear(TcpPeer[] team, int area, List<WirePacket>[] rows, bool terminal = true)
        {
            int dungeon = raid.Fiendwar!.Areas[area].DungeonId;
            var info = rows[0].Single(row => row is { Kind: 0, Opcode: 28 }).Body;
            var map = rows[0].Single(row => row is { Kind: 0, Opcode: 29 }).Body;
            var path = AntonTcpProbe.Route(raid.EntryCatalog!, dungeon, info[7], map[0], map[1], info[8], info[9]);
            for (int index = 0; index < path.Length; index++)
            {
                if (index != 0)
                {
                    var move = new byte[100]; move[0] = checked((byte)path[index].X); move[1] = checked((byte)path[index].Y);
                    team[0].Send(45, move); rows = team.Select(Drain).ToArray();
                    map = rows[0].Single(row => row is { Kind: 0, Opcode: 29 }).Body; Load(team);
                }
                if (!terminal && index + 1 == path.Length) break;
                ClearMap(team, map, index + 1 == path.Length);
            }
            return rows;
        }
        void ClearMap(TcpPeer[] team, byte[] map, bool bossRoom)
        {
            int count = map[18];
            That(map.Length >= 19 + count * 22, "真实演员表完整");
            bool killed = false;
            foreach (int index in Enumerable.Range(0, count).OrderBy(index => map[19 + index * 22 + 13] == 3))
            {
                int offset = 19 + index * 22;
                if (map[offset + 21] == 1 || map[offset + 13] == 9) continue;
                team[0].Send(39, FormalDungeonTcpTests.Death(U16(map, offset + 6), team[0].Id, map[offset + 12]));
                // 统一经本测试的收包入口观察战场符号，避免公共清图探针先消费1530。
                That(Drain(team[0]).Any(row => row is { Kind: 0, Opcode: 38 }), "正式实例接受实际怪物死亡");
                foreach (var peer in team.Skip(1)) Drain(peer);
                if (!titleReplayChecked)
                {
                    titleReplayChecked = true;
                    foreach (var peer in team)
                        That(fixture.Query("SELECT total,applied FROM character_fiendwar_title_facts WHERE character_id=$p0 AND quest_id=7007", peer.Id) is [[1L, 1L]] &&
                            fixture.Query("SELECT remaining1 FROM character_title_progress WHERE character_id=$p0 AND quest_id=7007", peer.Id) is [[99L]],
                            "一人和多人团队的首次真实击杀均提交事实并发布进度");
                    team[0].Send(39, FormalDungeonTcpTests.Death(U16(map, offset + 6), team[0].Id, map[offset + 12]));
                    foreach (var peer in team) Drain(peer);
                    That(team.All(peer => fixture.Query("SELECT total,applied FROM character_fiendwar_title_facts WHERE character_id=$p0 AND quest_id=7007", peer.Id) is [[1L, 1L]]),
                        "同一原死亡重放不重复累计称号事实或进度");
                }
                killed = true;
            }
            That(!bossRoom || killed, "终房存在实际可击杀对象");
            foreach (var other in peers.Where(other => !team.Contains(other))) Drain(other);
        }
    }
}
