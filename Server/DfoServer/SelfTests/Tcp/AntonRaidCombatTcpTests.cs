using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

// 夹具已选角并安装正式副本，隔离START/MAP；战斗、117/660/42分派、发包与SQL仍使用生产实现。
internal static class AntonRaidCombatTcpTests
{
    public static async Task RunAsync(string root, TcpTestResources resources)
    {
        string directory = Path.Combine(root, "anton-combat"); Directory.CreateDirectory(directory);
        string database = Path.Combine(directory, "game.db");
        var repository = new CharacterRepository(database);
        using var owner = new AccountOwners().Acquire("raid-combat", () => { });
        var created = repository.Create(owner, CharacterCreation.Prepare("RaidCombat", resources.Characters.Get(0),
            resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get) with { ActiveMainQuests = [new(0, 3145, 1)] });
        var session = new GameSession(repository, resources.Towns, owner);
        session.Select(created.Slot); session.MarkReady();
        var actor = new RaidActor(session, owner, session.SelectionEpoch, created.Id, created.Name, new(2, 19));
        // 两阶段都使用已有正式副本3。奖励与未知展示列为明确合成配置，不代表安徒恩自然掉落/评分。
        var battle = new AntonRaidBattle(AntonRaidBattleTests.Script(3, 3));
        var policies = Enumerable.Range(0, 2).Select(phase => new RaidPhaseRewardPolicy("fixture/explicit-raid-rewards",
            [new(0, 2, [new(10400, 1)], [new(created.Id, 2, [new(0x71, 10400, 1, 1)])]),
             new(1, 2, [new(10400, 1)], [new(created.Id, 2, [new(0x72, 10400, 1, 1)])])],
            new(0, (byte)phase, 17, 0x1234, 3, 1))).ToImmutableArray();
        That(session.AttachRaidCombat(actor, battle, policies), "正式开战夹具绑定唯一Session和显式奖励配置");
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        var rooms = SelfTestCatalogs.Rooms(resources.Archive);
        var kills = SelfTestCatalogs.KillExperience(resources.Archive);
        var clear = SelfTestCatalogs.ClearExperience(resources.Archive);
        var drops = SelfTestCatalogs.MonsterDrops(resources.Archive);
        var stats = new CharacterStatComputer(resources.Characters);
        var town = new TownProjection(resources.Towns, stats, null, resources.ItemResources.EpicPieces);
        var inventory = new InventoryProjection(stats, town, resources.ItemResources.EpicPieces);
        var levels = new CharacterLevelProjection(stats);
        using var log = new ServerLog(Path.Combine(directory, "logs"));
        var commands = new GameCommandRegistry();
        new DungeonSettlementHandler(log, dungeons, clear, resources.Growth, town, rooms, drops,
            resources.ItemResources.Items, levels).Register(commands);
        new DungeonHandler(log, dungeons, rooms, resources.Growth, kills, town, levels).Register(commands);
        new RaidCombatHandler(repository, resources.ItemResources.Items, resources.ItemResources.GoldLimits,
            resources.ItemResources.EpicPieces, inventory, log).Register(commands);
        new KeepaliveHandler(log).Register(commands); commands.Seal();

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var accepting = listener.AcceptTcpClientAsync(stop.Token).AsTask();
        using var peer = new TcpPeer(((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await accepting; server.NoDelay = true;
        await using var sender = new GameConnectionSender(owner, server.GetStream(), stop, 0, (_, _, _, _, _) => { });
        var connection = new ConnectionContext(1, IPAddress.Loopback, server.GetStream(), stop);
        var context = new GameConnectionContext(connection, owner, session, sender);
        session.RaidBattleNoticeArrived = (view, recipient) => RaidCombatProjection.Notice(context, view, recipient);
        var dispatch = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                    await commands.DispatchAsync(context, GameFrame.ParseClient(await connection.ReadAsync()));
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (IOException) when (stop.IsCancellationRequested) { }
        });
        try
        {
            for (int phase = 0; phase < 2; phase++)
            {
                if (phase == 0) That(session.LeaveRoom() && session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)), "夹具沿现有正式入口准备下一阶段");
                session.BeginDungeonSelection();
                var entered = session.EnterFormalDungeon(3, 3145, 0, dungeons, rooms)!;
                That(entered is not null && session.BindRaidCombatDungeon(battle.Instance, phase), "MAP交界使用真实批准实例绑定当前攻坚阶段");
                var loaded = session.CompleteFormalDungeonLoading()!;
                var maps = entered!.Entry.Maze.MapSpecification!;
                var fighting = battle.View;
                peer.Send(695, new byte[5]); peer.Send(1278); peer.Expect(1, 1278);
                That(ReferenceEquals(fighting, battle.View), "695已注册但不能将本地零值报告升级为Boss死亡");
                DungeonPlannedMonster? boss = null;
                for (int room = 0; room < maps.Count; room++)
                {
                    foreach (var monster in loaded.Monsters.Monsters.Where(m => m.IsBlocking))
                    {
                        if (monster.IsBoss) boss = monster;
                        var death = session.ObserveFormalDungeonDeath(new(monster.ObjectKey, created.Id, 0x0111, created.Id,
                            600, 3, 0, monster.SpawnType == 3 ? 6u : (uint)monster.CandidateLevel,
                            monster.SpawnType == 3 ? 0UL : 504UL, 620, 3, true, new string('A', 64)), kills, resources.Growth);
                        That(death.Outcome == DungeonRunOutcome.Accepted, "阶段胜利来自权威战斗及真实死亡提交");
                    }
                    if (room + 1 < maps.Count)
                    {
                        var target = maps[room + 1].Position;
                        That(session.MoveFormalDungeon((byte)target.X, (byte)target.Y) is not null, "正式清房后沿批准路线前进");
                        loaded = session.CompleteFormalDungeonLoading()!;
                    }
                }
                That(boss is not null, "成功链使用真实Boss键");
                byte[] body = new byte[39]; BinaryPrimitives.WriteUInt16LittleEndian(body, created.Id);
                BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), boss!.ObjectKey);
                BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(13), 6);
                peer.Send(117, body);
                Bytes(peer.Expect(0, 601).Body, [0, (byte)phase, 17, 0, 0, 0, 0x34, 0x12, 3, 1], "601先安装配置明确的本阶段结果");
                Bytes(peer.Expect(0, 587).Body, [3, (byte)phase], "587随后打开本阶段奖励");
                Bytes(peer.Expect(0, 115).Body, [1, 1, (byte)boss.ObjectKey, (byte)(boss.ObjectKey >> 8)], "117确认仅回实际Boss键");
                var rewardView = battle.View;
                peer.Send(117, body); peer.Expect(0, 115);
                That(ReferenceEquals(rewardView, battle.View), "重复117不再次完成阶段");

                if (phase == 0)
                {
                    long revision = session.Selected!.Revision;
                    Sql("CREATE TRIGGER raid_reward_failure BEFORE INSERT ON character_raid_reward_receipts BEGIN SELECT RAISE(ABORT,'raid reward rollback'); END");
                    Reject<SqliteException>(() => session.ClaimRaidReward(0, 2, repository, resources.ItemResources.Items,
                        resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces, DateTimeOffset.UtcNow),
                        "回执末段失败回滚同事务物品和UID");
                    That(session.Selected.Revision == revision && Convert.ToInt64(Sql("SELECT COUNT(*) FROM character_raid_reward_receipts")) == 0,
                        "SQL失败不安装资产或成功回执");
                    Sql("DROP TRIGGER raid_reward_failure");
                }

                if (phase == 1)
                {
                    // 当前真实库存填满剩余装备格；两份末阶段奖励复用库存的永久溢出邮件。
                    var template = resources.ItemResources.Items.Get(10400);
                    var range = ItemSlotBoundService.Range(template.Kind, session.Selected!.Inventory.Expansion);
                    var fill = session.MutateInventory(character => new(true, 0, new(character.Inventory,
                        character.Inventory with { Items = [.. character.Inventory.Items,
                            .. Enumerable.Range(range.Start, range.End - range.Start + 1)
                                .Where(slot => character.Inventory.Find(new(InventoryListType.Main, (short)slot)) is null)
                                .Select(slot => new InventoryItem(0, new(InventoryListType.Main, (short)slot), InventoryRewardGrantService.Create(template)))] },
                        "raid_full_fixture")), requireTown: false);
                    That(fill.Accepted, "满包夹具使用正式库存提交");
                }
                for (byte kind = 0; kind < 2; kind++)
                {
                    peer.Send(660, [kind, 2]);
                    var reward = Until(peer, 0, 600);
                    Bytes(reward.Body, [kind, 1, (byte)created.Id, (byte)(created.Id >> 8), 2, 1,
                        (byte)(0x71 + kind), 0xa0, 0x28, 0, 0, 1, 0, 1], "600使用安徒恩专用展示读宽");
                    Bytes(peer.Expect(1, 660).Body, [1], "660提交之后才确认成功");
                    if (kind == 0)
                    {
                        long revision = session.Selected!.Revision;
                        peer.Send(660, [kind, 2]); Until(peer, 0, 600); Bytes(peer.Expect(1, 660).Body, [1], "重复选同牌回放成功");
                        That(session.Selected.Revision == revision, "重复660不再写库存或邮件");
                    }
                }
                Until(peer, 0, 587);
                That(battle.View.State == (phase == 0 ? AntonRaidBattleState.Fighting : AntonRaidBattleState.Completed),
                    "两份奖励提交后推进下一阶段或完成");
                peer.Send(42); Bytes(peer.Expect(1, 42).Body, [1], "复用正式回城请求42与ACK");
                Bytes(peer.Expect(0, 3).Body, [1, (byte)created.Id, (byte)(created.Id >> 8), 0], "回城先发本人NOTI3状态");
                peer.Expect(0, 24);
                peer.Send(1278); peer.Expect(1, 1278);
                That(session.Phase == SessionPhase.Town, "位置提交和场景初始化完成后回到城镇");
            }
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()); db.Open();
            using var query = db.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM character_raid_reward_receipts";
            That(Convert.ToInt64(query.ExecuteScalar()) == 4, "两阶段两种奖励各保存一次稳定回执");
            query.CommandText = "SELECT COUNT(*) FROM mailbox_attachments";
            That(Convert.ToInt64(query.ExecuteScalar()) == 2, "末阶段两份满包奖励各保存一个真实邮件附件");

            // 存储层验证下一次实际奖励及周上限；复用本轮数据库、账号和已加载资源，不另起服务。
            var current = session.Selected!;
            var now = DateTimeOffset.UtcNow;
            var second = new PreparedRaidReward(Guid.NewGuid(), 1, 0, 2, current.Id, "fixture/weekly",
                [new(10400, 1)], true, DungeonIncountPolicy.Period(now).Week);
            var secondPlan = RewardPlan(current);
            var committed = repository.CommitRaidReward(owner, current, second, secondPlan, now)!;
            That(committed is { Duplicate: false } && committed.MailIds.Length == 1, "每周第二次最终奖励完整提交满包邮件");
            var replay = new CharacterRepository(database).CommitRaidReward(owner, current, second, secondPlan, now)!;
            That(replay.Duplicate && replay.Character.Revision == committed.Character.Revision && replay.MailIds.SequenceEqual(committed.MailIds),
                "重建仓储回放原奖励回执和真实邮件，不再增发");
            query.CommandText = $"UPDATE character_raid_reward_receipts SET plan_json=json_remove(plan_json,'$.RaidType') WHERE instance_id='{second.Instance:N}'";
            query.ExecuteNonQuery();
            var legacy = new CharacterRepository(database).CommitRaidReward(owner, current, second, secondPlan, now)!;
            That(legacy.Duplicate&&legacy.MailIds.SequenceEqual(committed.MailIds),"迁移前缺type的Anton旧JSON完整兼容重放，无需Luke生命周期行");
            query.CommandText = $"UPDATE character_raid_reward_receipts SET plan_json=json_set(plan_json,'$.UnknownField',1) WHERE instance_id='{second.Instance:N}'";
            query.ExecuteNonQuery();
            Reject<InvalidDataException>(()=>repository.CommitRaidReward(owner,current,second,secondPlan,now),"旧JSON未知字段不能被兼容规范化吞掉");
            query.CommandText = $"UPDATE character_raid_reward_receipts SET plan_json=json_remove(plan_json,'$.UnknownField') WHERE instance_id='{second.Instance:N}'";
            query.ExecuteNonQuery();
            Reject<InvalidDataException>(()=>repository.CommitRaidReward(owner,current,second with {RaidType=RaidKind.Luke},secondPlan,now),
                "同实例不能改type绕过原奖励PK再发一次");
            That(repository.CommitRaidReward(owner,committed.Character,second with {Instance=Guid.NewGuid(),RaidType=RaidKind.Luke},RewardPlan(committed.Character),now) is null,
                "新Luke缺666/阶段资格时不得借旧Anton兼容分支发奖");
            var third = second with { Instance = Guid.NewGuid() };
            That(repository.CommitRaidReward(owner, committed.Character, third, RewardPlan(committed.Character), now) is null,
                "每周第三次最终奖励不提交资产");
            repository.Delete(owner, repository.LoadDeletionTarget(owner, created.Slot));
            query.CommandText = "SELECT (SELECT COUNT(*) FROM character_raid_reward_receipts)+(SELECT COUNT(*) FROM mailbox_attachments)";
            That(Convert.ToInt64(query.ExecuteScalar()) == 0, "删角同事务清理实际攻坚回执和满包邮件资产");

            InventoryPlan RewardPlan(Character character)
            {
                var granted = InventoryRewardGrantService.TryGrantBatch(character, [new(10400, 1)], resources.ItemResources.Items,
                    resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces, utcNow: now, allowOverflowMail: true).Plan!;
                return new(granted.Before, granted.After, "raid_reward", new(character), granted.OverflowItems);
            }
        }
        finally
        {
            stop.Cancel(); server.Dispose(); await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
            session.End();
        }

        object? Sql(string statement)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()); db.Open();
            using var command = db.CreateCommand(); command.CommandText = statement; return command.ExecuteScalar();
        }
    }

    static WirePacket Until(TcpPeer peer, byte kind, ushort opcode)
    {
        for (int count = 0; count < 64; count++)
        { var packet = peer.Read(); if (packet.Kind == kind && packet.Opcode == opcode) return packet; }
        throw new InvalidDataException("攻坚成功链超过通知预算");
    }
}
