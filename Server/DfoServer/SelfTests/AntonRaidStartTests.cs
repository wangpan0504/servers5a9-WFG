using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AntonRaidStartTests
{
    public static void RunCore()
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-anton-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        List<AccountOwners.Lease> owners = []; List<GameSession> sessions = [];
        try
        {
            var origin = new Location(1, 0, 10, 10);
            var landing = new Location(19, 2, 12, 11);
            var map = new TownMap("start.map", [new(new(0, 0, 30, 30), [], [])], [], false, []);
            var waiting = map with { Links = [new(new(10, 6, 4, 10), 1, 0, [], [])] };
            var towns = new TownCatalog(new() { [(1, 0)] = new(1, 0, 1, [], [], [], origin, map, [], []),
                [(19, 2)] = new(19, 2, 1, [], [], [], null, waiting, [], []) }, origin, origin, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var registry = new RaidRegistry(); var ownership = new AccountOwners();
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var parties = new PartyService(repository);
            var pieces = new EpicPieceCatalog("[piece list]\n9001 9002\n[/piece list]");
            var script = new RaidScript("synthetic/anton.etc", new(19, 2), new(100, 1), null,
                ImmutableDictionary<string, int>.Empty, [new("first", [], [], [])], [], []);
            var definition = new RaidDefinition(0, script.Path, 20, 10, 1) { Resources = new(script, [], [], [], [], []) };
            var service = new AntonRaidStart(registry, parties, repository, towns, pieces);
            var actors = Enumerable.Range(0, 30).Select(i =>
            {
                var lease = ownership.Acquire("anton-start-" + i, () => { }); owners.Add(lease);
                repository.Create(lease, new("AntS" + i, origin, origin) { Level = 85, CompletedQuests = [12829, 3779],
                    Inventory = new([new InventoryItem(0, new(InventoryListType.Main, 121), new(100, ItemKind.Material, 2))]) });
                var session = new GameSession(repository, towns, lease, entry: new(new(2, 19), 23, "[raid]"));
                sessions.Add(session); session.Select(0); session.MarkReady();
                return session.CaptureRaidActor(registry)!;
            }).ToArray();

            RaidParty Recruit(RaidActor[] members)
            {
                var leader = members[0]; registry.Create(leader, 0, "start", "start"u8.ToArray(), 20, 10);
                foreach (var actor in members.Skip(1))
                {
                    registry.Invite(leader, actor, 0);
                    That(registry.Respond(actor, leader.Character, true).Accepted, "开战夹具通过真实聚合加入");
                }
                for (int i = 0; i < members.Length; i++) registry.Assign(leader, members[i].Character, (byte)(i / 4 + 1));
                return registry.Find(leader)!;
            }
            long Scalar(string sql)
            {
                using var db = new SqliteConnection($"Data Source={Path.Combine(root, "game.db")};Pooling=False"); db.Open();
                using var command = db.CreateCommand(); command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar());
            }
            void Sql(string sql)
            {
                using var db = new SqliteConnection($"Data Source={Path.Combine(root, "game.db")};Pooling=False"); db.Open();
                using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
            }
            const string balance = "SELECT sum(json_extract(item_core,'$.Value')) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=100";
            var expected = Recruit(actors[..20]);
            var original = sessions.Take(20).Select(s => s.Selected!).ToArray();
            Reject<InvalidOperationException>(() => AccountOwners.Lease.ExecuteGroup(actors.Take(20).Select(a => (a.Owner, a.Character)).ToArray(), () => true),
                "普通ExecuteGroup保持四人上限，不能被团本实现放大");
            AccountOwners.Lease.ExecuteRaid(actors.Take(20).Select(a => (a.Owner, a.Character)).ToArray(), () =>
            {
                Reject<InvalidOperationException>(() => actors[20].Owner.Execute(() => true), "固定全团锁不能引入团外owner");
                return true;
            });
            Sql($"CREATE TRIGGER fail_anton_last BEFORE UPDATE ON characters WHEN OLD.id={actors[19].Character} BEGIN SELECT RAISE(ABORT,'raid-fault'); END;");
            Reject<SqliteException>(() => service.Start(actors[0], expected, definition), "第20人SQL失败必须整体回滚");
            That(Scalar(balance) == 60 && Scalar("SELECT count(*) FROM characters WHERE town=19 AND area=2") == 0 &&
                Scalar("SELECT count(*) FROM inventory_audit_log WHERE action_name LIKE 'anton_raid_start:%'") == 0 &&
                registry.IsCurrent(expected) && parties.Snapshot().Length == 0 &&
                sessions.Take(20).Select((s, i) => ReferenceEquals(s.Selected, original[i]) && s.StartedRaid is null).All(x => x),
                "SQL失败不留下部分扣票、迁移、审计、分队或在线开战状态");
            Sql("DROP TRIGGER fail_anton_last;");
            var result = service.Start(actors[0], expected, definition);
            That(result is { Error: 0, Receipt.Party.Started.Participants.Length: 20 } && Scalar(balance) == 40 &&
                Scalar("SELECT count(*) FROM characters WHERE town=19 AND area=2") == 20, "二十人一次提交实际成功并持久扣票/迁移");
            var receipt = result.Receipt!;
            for (int i = 0; i < 20; i++)
            {
                var committed = sessions[i].Selected!;
                That(committed.Position == landing && committed.Return == origin && committed.Revision > original[i].Revision &&
                    committed.Inventory.Items.Single().Uid == original[i].Inventory.Items.Single().Uid && committed.Inventory.Items.Single().Core.Count == 1 &&
                    sessions[i].StartedRaid?.InstanceId == receipt.Party.Started!.InstanceId,
                    "全员只安装本次提交位置/数量与实例，保留原UID和普通返回点");
            }
            That(parties.Snapshot() is { Length: 5 } squads && squads.All(p => p.Members.Length == 4) &&
                squads.SelectMany(p => p.Members.Select(m => m.Slot)).All(s => s < 4), "20人落为五个独立四人普通Party及有效slot");
            That(service.Start(actors[0], registry.Find(actors[0])!, definition) is { Error: 0, Replay: true } && Scalar(balance) == 40,
                "重复开战只回放已提交成功，不再扣票或分配新实例");
            That(registry.Assign(actors[0], actors[1].Character, 2) is null && registry.TransferLeadership(actors[0], actors[1].Character) is null,
                "开战冻结分队；尚未闭合的战中委任不借招募合同放行");
            That(parties.Leave(sessions[3]).Error == 19 && parties.Current(sessions[3])?.Members.Length == 4,
                "普通13不能绕过开战冻结名单拆队，应通过攻坚队665退出");

            var ten = Recruit(actors[20..]);
            var racing = Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => service.Start(actors[20], ten, definition))))
                .GetAwaiter().GetResult();
            That(racing.All(r => r.Error == 0) && racing.Count(r => r.Receipt is not null) == 1 && racing.Count(r => r.Replay) == 1 &&
                Scalar(balance) == 30 && sessions.All(s => s.StartedRaid is not null) && parties.Snapshot().Length == 8,
                "十人并发重复开战只有一笔实际提交，4/4/2分队不截断成员");
            var saved = repository.LoadCharacter(actors[0].Owner, 0);
            That(saved.Position == landing && saved.Inventory.Items.Single().Core.Count == 1,
                "新仓储读取实际已落库门票和候战室，不用在线快照证明持久化");
            var departing = sessions[3].Selected;
            var left = service.Leave(actors[3], actors[3].Character);
            That(left is not null && left.Before.Started?.InstanceId == receipt.Party.Started!.InstanceId && left.After?.Members.Length == 19 &&
                registry.Find(actors[3]) is null && sessions[3].StartedRaid is null && parties.Current(sessions[3]) is null &&
                parties.Current(sessions[0])?.Members.Length == 3 && ReferenceEquals(sessions[3].Selected, departing) && Scalar(balance) == 30,
                "665在原owner内同时清除攻坚与普通分队身份，剩余名册取实际结果且不凭未知退款合同改资产");

            var soloDefinition = new RaidStartSettings { StartMinimumOverride = 1 }
                .Apply(new RaidCatalog(ImmutableDictionary<byte, RaidDefinition>.Empty.Add(0, definition))).Find(0)!;
            var solo = Recruit([actors[3]]);
            That(service.Start(actors[3], solo, definition) is { Error: 22 } && Scalar(balance) == 30,
                "未覆盖时已编队持票单人仍按PVF十人拒绝且不扣票");
            registry.Leave(actors[3], actors[3].Character);
            registry.Create(actors[3], 0, "solo", "solo"u8.ToArray(), 20, soloDefinition.StartMinimum);
            registry.Assign(actors[3], actors[3].Character, 1);
            solo = registry.Find(actors[3])!;
            var soloResult = service.Start(actors[3], solo, soloDefinition);
            That(soloResult is { Error: 0, Receipt.Party.Started.Participants.Length: 1 } && Scalar(balance) == 29 &&
                parties.Current(sessions[3]) is { Members.Length: 1 } && sessions[3].Selected!.Inventory.Items.Length == 0,
                "覆盖一人经真实固定owner锁、SQL扣票及普通单人分队完成开战");
            That(service.Start(actors[3], registry.Find(actors[3])!, soloDefinition) is { Error: 0, Replay: true } && Scalar(balance) == 29,
                "单人重复开战不重复扣票或建立新实例");
        }
        finally
        {
            foreach (var session in sessions) session.End();
            foreach (var owner in owners) owner.Dispose();
            Directory.Delete(root, true);
        }
    }
}
