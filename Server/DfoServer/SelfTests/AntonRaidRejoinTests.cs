using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AntonRaidRejoinTests
{
    public static void RunCore()
    {
        That(!new RaidStartSettings().AntonRejoinEnabled, "安徒恩断线恢复配置缺省关闭");
        var options = new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        var enabled = JsonSerializer.Deserialize<RaidStartSettings>("""{"AntonRejoinEnabled":true}""", options);
        That(enabled!.AntonRejoinEnabled && !enabled.AntonFormalEntryEnabled, "断线恢复开关独立于正式进图开关");
        That((ushort)CmdPacketType.REJOIN_RAID == 0x029C && (ushort)NotiPacketType.REAL_ESTATE_STATE == 0x029C &&
            (ushort)NotiPacketType.RAID_DISCONN_INFO == 835, "CMD668与同号NOTI668数值相同但枚举分开，835是断线通知");
        That(RaidPackets.DisconnInfo(0x02130001).SequenceEqual(new byte[] { 1, 0, 19, 2, 0, 0, 0 }),
            "835固定7B，尾部u8/u16保持未知零值");

        string root = Path.Combine(Path.GetTempPath(), "s5a9-anton-rejoin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        List<AccountOwners.Lease> leases = [];
        List<GameSession> sessions = [];
        try
        {
            var origin = new Location(1, 0, 10, 10);
            var map = new TownMap("rejoin.map", [new(new(0, 0, 30, 30), [], [])], [], false, []);
            var waiting = map with { Links = [new(new(10, 6, 4, 10), 1, 0, [], [])] };
            var towns = new TownCatalog(new()
            {
                [(1, 0)] = new(1, 0, 1, [], [], [], origin, map, [], []),
                [(19, 2)] = new(19, 2, 1, [], [], [], null, waiting, [], [])
            }, origin, origin, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var registry = new RaidRegistry();
            var rejoin = new AntonRaidRejoin(true);
            registry.BindRejoin(rejoin);
            var owners = new AccountOwners();
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var parties = new PartyService(repository);
            var pieces = new EpicPieceCatalog("[piece list]\n9001 9002\n[/piece list]");
            var script = new RaidScript("synthetic/anton-rejoin.etc", new(19, 2), new(100, 1), null,
                ImmutableDictionary<string, int>.Empty.Add("[hp]", 1), [new("first",
                    [new(210, RaidDungeonState.Open, "[hp]", 4, 5)], [], [])], [], []);
            var definition = new RaidDefinition(0, script.Path, 20, 10, 1) { Resources = new(script, [], [], [], [], []) };
            var solo = new RaidStartSettings { StartMinimumOverride = 1 }
                .Apply(new RaidCatalog(ImmutableDictionary<byte, RaidDefinition>.Empty.Add(0, definition))).Find(0)!;
            var service = new AntonRaidStart(registry, parties, repository, towns, pieces);
            RaidActor Make(int index)
            {
                var lease = owners.Acquire("anton-rejoin-" + index, () => { });
                leases.Add(lease);
                repository.Create(lease, new("Rej" + index, origin, origin)
                {
                    Level = 85, CompletedQuests = [12829, 3779],
                    Inventory = new([new InventoryItem(0, new(InventoryListType.Main, 121), new(100, ItemKind.Material, 2))])
                });
                var session = new GameSession(repository, towns, lease, entry: new(new(2, 19), 23, "[raid]"));
                session.CurrentParty = () => parties.Current(session);
                sessions.Add(session);
                session.Select(0);
                session.MarkReady();
                return session.CaptureRaidActor(registry)!;
            }
            var actors = Enumerable.Range(0, 3).Select(Make).ToArray();
            var luke = new AntonRaidBattle(script, kind: RaidKind.Luke);
            That(registry.CaptureAntonDisconnect(actors[0], luke, null, false) is null, "卢克Battle不写入安徒恩断线记录");

            RaidParty Recruit(RaidActor leader, params RaidActor[] members)
            {
                registry.Create(leader, 0, "rejoin", "rejoin"u8.ToArray(), 20, solo.StartMinimum);
                foreach (var actor in members)
                {
                    registry.Invite(leader, actor, 0);
                    That(registry.Respond(actor, leader.Character, true).Accepted, "断线夹具按原聚合加入");
                }
                registry.Assign(leader, leader.Character, 1);
                foreach (var actor in members) registry.Assign(leader, actor.Character, 1);
                return registry.Find(leader)!;
            }
            var party = Recruit(actors[0], actors[1], actors[2]);
            var started = service.Start(actors[0], party, solo);
            That(started.Error == 0 && actors[0].Session.CurrentRaidBattle(actors[0]) is { Kind: RaidKind.Anton }, "三人覆盖开战成功");
            uint key = registry.Find(actors[0])!.Key;
            sessions[1].ReturnToRoster();
            parties.Detach(sessions[1]);
            That(rejoin.Peek(actors[1].Character) is null && registry.Find(actors[0])!.Members.Length == 2,
                "返回选角移除成员，但不记断线恢复资格");

            sessions[2].PrepareAntonRejoinDisconnect();
            parties.Detach(sessions[2]);
            sessions[2].End();
            var fact = rejoin.Peek(actors[2].Character);
            That(fact is not null && fact.Key == key && fact.Epoch == actors[2].Epoch && fact.Account == actors[2].Owner.Account &&
                registry.Find(key, actors[0].Channel)!.Members.All(m => m.Actor.Character != actors[2].Character) &&
                registry.AntonBattleAlive(fact), "原Battle存活时断线留下记录，并从当前名册移除");
            leases[2].Dispose();
            var nextLease = owners.Acquire("anton-rejoin-2", () => { });
            leases.Add(nextLease);
            var next = new GameSession(repository, towns, nextLease, entry: new(new(2, 19), 23, "[raid]"));
            next.CurrentParty = () => parties.Current(next);
            sessions.Add(next);
            next.Select(0);
            next.MarkReady();
            var rejoined = next.CaptureRaidActor(registry)!;
            var notice = rejoin.DescribeNotice(rejoined, registry.AntonBattleAlive(fact!), false);
            That(notice.Matched && notice.Reason == "ready" && notice.Key == key && notice.Unknown == "u8+u16",
                "同频道重登可下发835，尾部仍标unknown");
            var otherChannel = rejoined with { Channel = new ChannelIdentity(2, 18) };
            That(rejoin.DescribeNotice(otherChannel, true, false).Reason == "channel-mismatch", "异频道不下发835");
            var first = rejoin.Evaluate(rejoined, key, registry.AntonBattleAlive(fact!), registry.AntonMemberCurrent(fact!));
            That(first.Matched && first.Reason == "recovery-sequence-unknown" && first.OwnerReplace == "withheld" &&
                first.LoadingBarrier == "not-applicable" && first.OldSessionReleased && first.OldEpoch == fact!.Epoch &&
                first.NewEpoch == rejoined.Epoch && registry.Find(key, actors[0].Channel)!.Members.Length == 1,
                "668校验通过仍不替换owner，等待室没有加载屏障可复用");
            That(registry.RestoreAnton(rejoined, key + 1, rejoin, parties).Restored == false, "错误key不能消费状态恢复候选");
            var commands = parties.Current(actors[0].Session)!.Commands;
            commands.Wait();
            try { That(registry.RestoreAnton(rejoined, key, rejoin, parties).Reason == "command-busy" && !fact!.Attempted,
                "原分队忙时有界拒绝，不消费恢复资格"); }
            finally { commands.Release(); }
            using (var db = new SqliteConnection("Data Source=" + Path.Combine(root, "game.db") + ";Pooling=False"))
            {
                db.Open();
                using var fail = db.CreateCommand();
                fail.CommandText = "CREATE TRIGGER reject_rejoin_position BEFORE UPDATE OF town ON characters BEGIN SELECT RAISE(ABORT,'rejoin rollback'); END";
                fail.ExecuteNonQuery();
                var beforePosition = next.Selected;
                Reject<SqliteException>(() => registry.RestoreAnton(rejoined, key, rejoin, parties), "原位置SQL失败不能先恢复成员");
                That(ReferenceEquals(next.Selected, beforePosition) && next.RaidCombatView() is null &&
                    registry.Find(key, actors[0].Channel)!.Members.Length == 1 && !fact!.Attempted,
                    "位置失败保留新owner角色和原名册，候选未消费可重试");
                fail.CommandText = "DROP TRIGGER reject_rejoin_position";
                fail.ExecuteNonQuery();
            }
            That(registry.RestoreAnton(rejoined, key, rejoin, parties) is { Restored: true } &&
                ReferenceEquals(next.CurrentRaidBattle(rejoined), actors[0].Session.CurrentRaidBattle(actors[0])) &&
                registry.Find(key, actors[0].Channel)!.Members.Length == 2 && parties.Current(next)?.Id == parties.Current(actors[0].Session)?.Id,
                "状态恢复绑定原Battle和普通分队，不分配新实例或再扣票");
            That(registry.RestoreAnton(rejoined, key, rejoin, parties).Reason == "duplicate-rejoin", "恢复候选只能消费一次");
            actors[2].Session.End();
            That(nextLease.IsCurrent && next.RaidCombatView() is not null && registry.Find(key, actors[0].Channel)!.Members.Length == 2,
                "旧Session迟到清理不能移除新owner名册、Battle或原Run");
            var ordered = new AntonRaidRejoin(true);
            var newer = fact! with { OldOwner = nextLease, Epoch = rejoined.Epoch };
            That(ordered.Record(newer) && !ordered.Record(fact!) && ReferenceEquals(ordered.Peek(rejoined.Character), newer),
                "旧generation迟到断线记录不能覆盖较新owner的恢复候选");
            That(!ordered.Record(newer with { Anton = null }), "同代缺少上下文的记录不能降级完整Run恢复能力");
            That(rejoin.Evaluate(rejoined, key + 1, true, false).Reason == "key-mismatch", "已决定后错误key仍按key拒绝，不写成恢复成功");
            var barrier = new AntonRaidRejoin(true);
            barrier.Record(new AntonRaidDisconnectFact(fact!.Account, fact.Character, fact.Epoch, fact.OldOwner, fact.Key,
                fact.Instance, fact.Channel, fact.Slot, fact.Squad, Guid.NewGuid(), true));
            var loading = barrier.Evaluate(rejoined, fact.Key, true, false);
            That(loading.Matched && loading.OwnerReplace == "withheld" && loading.LoadingBarrier == "withheld" &&
                loading.Reason == "recovery-sequence-unknown", "加载中的断线只生成复用计划，不写回屏障");
            That(registry.Find(actors[0])!.Members.Any(m => m.Actor == rejoined), "缺少加载能力的拒绝不能移除已恢复成员");

            var disabled = new RaidRegistry();
            disabled.BindRejoin(new AntonRaidRejoin(false));
            That(disabled.CaptureAntonDisconnect(actors[0], actors[0].Session.CurrentRaidBattle(actors[0])!, null, false) is null,
                "开关关闭不捕获断线记录");
        }
        finally
        {
            foreach (var session in sessions) try { session.End(); } catch (InvalidOperationException) { }
            foreach (var lease in leases) lease.Dispose();
            Directory.Delete(root, true);
        }
    }
}
