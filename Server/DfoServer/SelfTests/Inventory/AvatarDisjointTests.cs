using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class AvatarDisjointTests
{
    public static void RunCore()
    {
        int checks = Count;
        const int avatarId = 2000, firstEmblem = 1001, secondEmblem = 1002, fillerEmblem = 1999;
        var request = AvatarDisjointRequests.Read([2, 0, 0xD0, 0x07, 0, 0]);
        That(request == new AvatarDisjointRequest(2, avatarId), "202/449共用的六字节请求保留时装槽与期望物品编号");
        foreach (byte[] invalid in new byte[][]
        {
            [], [2, 0], [2, 0, 0xD0, 0x07, 0], [2, 0, 0xD0, 0x07, 0, 0, 0],
            [0xff, 0xff, 0xD0, 0x07, 0, 0], [2, 0, 0, 0, 0, 0], [2, 0, 0, 0, 0, 0x80]
        })
            Reject<InvalidDataException>(() => AvatarDisjointRequests.Read(invalid), "时装分解拒绝截断、附加、负槽及越界编号");

        const string tableText = "[min max value]\n50 69\n[result info]\n2 1001 1 1 0 1002 3 2 1\n" +
            "[result info]\n1 1001 1 3 0";
        var catalog = new AvatarDisjointCatalog(path =>
            path.EndsWith("emblemlistinfo_0.etc", StringComparison.Ordinal) ? tableText : null);
        var table = catalog.Get(0)!;
        That(table is { Grade: 0, MinimumValue: 50, MaximumValue: 69 } && table.Pools.Length == 2 &&
            table.Pools[0].Rewards[1] == new AvatarDisjointRewardDefinition(secondEmblem, 3, 2, true) &&
            catalog.Get(1) is null, "等级表保留范围、重复奖励池、权重、数量与special字段");
        foreach (string malformed in new[]
        {
            "[result info]\n1 1001 1 1",
            "[result info]\n1 1001 0 1 0",
            "[result info]\n1 1001 1 1 2",
            "[min max value]\n70 50\n[result info]\n1 1001 1 1 0",
            "[min max value]\n1 2 3\n[result info]\n1 1001 1 1 0"
        })
            Reject<InvalidDataException>(() => AvatarDisjointCatalog.Parse(malformed, 0), "畸形奖励四元组、范围、权重和special明确拒绝");
        Reject<InvalidDataException>(() => AvatarDisjointCatalog.Parse("[min max value]\n1 2", 0), "空奖励表不伪造产物");

        var calculated = AvatarDisjointResultCalculator.Calculate(table, new SequenceRandom(0, 1, 0));
        That(calculated.SequenceEqual(new InventoryReward[] { new(firstEmblem, 4), new(secondEmblem, 2) }),
            "多奖励池按权重抽取并按首次出现顺序合并重复物品");
        var overflowTable = new AvatarDisjointGradeTable(0, 0, 0,
            [new(2, [new(firstEmblem, 1, int.MaxValue, false)])]);
        Reject<OverflowException>(() => AvatarDisjointResultCalculator.Calculate(overflowTable, new SequenceRandom(0, 0)),
            "重复奖励数量溢出不截断");
        Reject<InvalidDataException>(() => AvatarDisjointResultCalculator.Calculate(table, new SequenceRandom(4)),
            "越界随机源不能落入最后一项");

        var receipt = new AvatarDisjointReceipt(request,
            [new(289, firstEmblem, 4), new(290, secondEmblem, 2)]);
        var accepted = new InventoryResult(true, 0);
        That(Convert.ToHexString(AvatarDisjointPackets.StandardAck(accepted, receipt)) ==
            "01020002002101E9030000040000002201EA03000002000000",
            "CMD202成功ACK包含u16槽、u16行数及带目标槽的10字节增量行");
        That(Convert.ToHexString(AvatarDisjointPackets.RandomRewardAck(accepted, receipt)) ==
            "01020002E903000004000000EA03000002000000",
            "CMD449成功ACK包含u16源槽、u8行数及不带目标槽的8字节增量行");
        That(AvatarDisjointPackets.StandardAck(InventoryResult.Reject(19), null).SequenceEqual(new byte[] { 0, 19, 0 }) &&
            AvatarDisjointPackets.RandomRewardAck(InventoryResult.Reject(4), null).SequenceEqual(new byte[] { 0, 4, 0 }),
            "两种失败ACK均沿用状态0加u16错误");
        Reject<InvalidDataException>(() => AvatarDisjointPackets.StandardAck(accepted, null), "成功ACK不能缺少已规划回执");

        var items = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        var avatarTemplate = new ItemTemplate(avatarId, ItemKind.Avatar, 0, 1, 0, 1, 0, 0, 0,
            "[trade]", ushort.MaxValue, 0, "avatar");
        var emblemTemplate = new ItemTemplate(firstEmblem, ItemKind.Emblem, 0, 1, 0, 1000, 0, 0, 0,
            "[trade]", ushort.MaxValue, 0, "emblem");
        items.PrefillTemplates(new Dictionary<int, ItemTemplate>
        {
            [avatarId] = avatarTemplate,
            [firstEmblem] = emblemTemplate,
            [secondEmblem] = emblemTemplate with { Id = secondEmblem },
            [fillerEmblem] = emblemTemplate with { Id = fillerEmblem }
        });
        var limits = new GoldLimitCatalog("[gold limit from level]\n0 1000000\n[/gold limit from level]");
        var pieces = new EpicPieceCatalog("[piece list]\n50000 50001\n[/piece list]");
        var now = DateTimeOffset.FromUnixTimeSeconds(2_000_000_000);
        var source = new InventoryItem(10, new(InventoryListType.Avatar, 2),
            new(avatarId, ItemKind.Avatar, 10) { SortLockFlag = 1 })
        {
            Avatar = new() { ExpireTime = (int)now.AddMinutes(1).ToUnixTimeSeconds() }
        };
        var existing = new InventoryItem(11, new(InventoryListType.Main, 289),
            new(firstEmblem, ItemKind.Emblem, 7) { SortLockFlag = 1 });
        var character = CharacterWith(source, existing);
        var service = new InventoryAvatarDisjointService(items, catalog, limits, pieces, new SequenceRandom(0, 1, 0));
        var result = service.Disjoint(character, request, out receipt, out var rejection, now);
        var plan = result.Plan;
        That(result.Accepted && rejection is null && plan is { Reason: "avatar_disjoint" } &&
            character.Inventory.Find(source.Position) == source && plan.After.Find(source.Position) is null &&
            plan.After.Find(existing.Position) is { Uid: 11, Core.Value: 11, Core.SortLockFlag: 1 } &&
            plan.After.Find(new(InventoryListType.Main, 290)) is { Core.ItemId: secondEmblem, Core.Value: 2 },
            "单个纯计划删除完整时装详情、合并既有锁定堆并发放全部徽章，不原地修改快照");
        var committedPlan = plan!;
        That(receipt!.Rewards.SequenceEqual(new AvatarDisjointReward[]
            {
                new(289, firstEmblem, 4), new(290, secondEmblem, 2)
            }) && committedPlan.CharacterPrecondition!.Matches(character, source.ExpireTime - 1) &&
            !committedPlan.CharacterPrecondition.Matches(character, source.ExpireTime),
            "回执只携带实际槽位增量，提交前绑定源时装期限和角色revision");
        That(!service.Disjoint(character with { Inventory = committedPlan.After }, request, out var repeated, out _, now).Accepted &&
            repeated is null, "已删除源槽重放不重复发奖");

        foreach (var denied in new[]
        {
            (Role(source with { Core = source.Core with { EquipmentLockId = 1 } }), request, (ushort)19),
            (Role(source with { Avatar = source.Avatar! with { ExpireTime = (int)now.ToUnixTimeSeconds() } }), request, (ushort)19),
            (character, request with { ExpectedItemId = avatarId + 1 }, (ushort)19)
        })
        {
            var rejected = new InventoryAvatarDisjointService(items, catalog, limits, pieces, new SequenceRandom(0, 1, 0))
                .Disjoint(denied.Item1, denied.Item2, out var deniedReceipt, out _, now);
            That(!rejected.Accepted && rejected.Error == denied.Item3 && rejected.Plan is null && deniedReceipt is null,
                "实例锁、到期和期望编号不匹配均保留源时装");
        }
        items.PrefillTemplates(new Dictionary<int, ItemTemplate>
        {
            [avatarId] = avatarTemplate with { DisjointForbidden = true }
        });
        That(!new InventoryAvatarDisjointService(items, catalog, limits, pieces).Disjoint(character, request, out _, out _, now).Accepted,
            "PVF disjoint禁用规则拒绝时装");
        items.PrefillTemplates(new Dictionary<int, ItemTemplate> { [avatarId] = avatarTemplate with { Grade = 1 } });
        That(!new InventoryAvatarDisjointService(items, catalog, limits, pieces).Disjoint(character, request, out _, out _, now).Accepted,
            "缺少对应grade表不回退到其他奖池");
        items.PrefillTemplates(new Dictionary<int, ItemTemplate> { [avatarId] = avatarTemplate });

        var occupied = Enumerable.Range(289, 59).Select(slot => new InventoryItem(1000 + slot,
            new(InventoryListType.Main, (short)slot), new(fillerEmblem, ItemKind.Emblem, 1000))).ToArray();
        var fourFree = CharacterWith(source, occupied);
        result = new InventoryAvatarDisjointService(items, catalog, limits, pieces, new SequenceRandom(0, 1, 0))
            .Disjoint(fourFree, request, out receipt, out rejection, now);
        That(!result.Accepted && result.Error == 4 && receipt is null && rejection == "emblem-space",
            "徽章页不足五个物理空格时即使现有堆可用也不删除源时装");
        var fiveFree = CharacterWith(source, occupied[..58]);
        result = new InventoryAvatarDisjointService(items, catalog, limits, pieces, new SequenceRandom(0, 1, 0))
            .Disjoint(fiveFree, request, out receipt, out _, now);
        That(result.Accepted && receipt!.Rewards.Length == 2 && result.Plan!.After.Find(source.Position) is null,
            "徽章页恰有五个空格时完整奖励可原子规划");

        var invalidItems = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        invalidItems.PrefillTemplates(new Dictionary<int, ItemTemplate>
        {
            [avatarId] = avatarTemplate,
            [firstEmblem] = emblemTemplate,
            [secondEmblem] = emblemTemplate with { Id = secondEmblem, Kind = ItemKind.Material }
        });
        result = new InventoryAvatarDisjointService(invalidItems, catalog, limits, pieces)
            .Disjoint(character, request, out receipt, out rejection, now);
        That(!result.Accepted && result.Error == 19 && receipt is null && rejection == "reward-definition",
            "奖池中非徽章物品按资源错误拒绝，不先删除源时装");
        Console.WriteLine($"PASS {Count - checks} 时装分解协议、奖池、随机与原子规划断言");

        Character CharacterWith(InventoryItem avatar, params InventoryItem[] extra)
        {
            return new(1, 0, "AvatarDisjoint", 0, new(0, 0, 0, 0), null)
            {
                Level = 1,
                Inventory = new([avatar, .. extra])
            };
        }
        Character Role(InventoryItem avatar) => CharacterWith(avatar, existing);
    }

    public static void RunResources(TownCatalog towns, ItemResources resources)
    {
        int checks = Count;
        var template = FindEligibleAvatar(resources);
        var table = resources.AvatarDisjoint.Get(template.Grade)!;
        That(table.Pools.Length > 0 && table.Pools.All(pool => pool.PickCount > 0 && !pool.Rewards.IsEmpty) &&
            table.Pools.SelectMany(pool => pool.Rewards).All(reward => resources.Items.Get(reward.ItemId).Kind == ItemKind.Emblem),
            "现场时装grade及全部奖励池指向真实徽章模板");

        string root = Path.Combine(Path.GetTempPath(), "s5a9-avatar-disjoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("avatar-disjoint", () => { });
            var source = InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Avatar, 0),
                InventoryRewardGrantService.Create(template));
            repository.Create(owner, new("AvatarDisjoint", towns.Room, towns.InitialReturn)
            {
                Inventory = new([source])
            });
            var session = new GameSession(repository, towns, owner);
            session.Select(0);
            session.MarkReady();
            var service = new InventoryAvatarDisjointService(resources.Items, resources.AvatarDisjoint,
                resources.GoldLimits, resources.EpicPieces);
            var request = new AvatarDisjointRequest(0, template.Id);
            var original = session.Selected!;
            var stalePlan = service.Disjoint(original, request, out _, out _);
            That(stalePlan.Accepted, "现场PVF时装可生成待提交的完整徽章计划");

            string before = Snapshot();
            Query("CREATE TRIGGER fail_avatar_disjoint BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='avatar_disjoint' BEGIN SELECT RAISE(ABORT,'avatar disjoint rollback'); END");
            RejectSql(() => session.DisjointAvatar(request, service, out _, out _),
                "库存审计晚期失败向调用方返回异常");
            That(Snapshot() == before && session.Selected == original,
                "SQL失败回滚时装详情、徽章、revision、槽位与审计，并保留在线快照");
            Query("DROP TRIGGER fail_avatar_disjoint");

            var result = session.DisjointAvatar(request, service, out var receipt, out var rejection);
            That(result.Accepted && rejection is null && receipt is { Rewards.Length: > 0 } &&
                session.Selected!.Inventory.Find(request.Position) is null &&
                session.Selected.Inventory.Items.Where(item => item.Core.Kind == ItemKind.Emblem)
                    .Sum(item => item.Core.Count) == receipt.Rewards.Sum(row => row.Count),
                "Session只发布一次已提交结果，源详情删除且数据库徽章总量等于回执增量");
            RejectSql(() => repository.CommitInventory(owner, original, stalePlan.Plan!),
                "旧revision计划不能在成功分解后再次提交");
            That(!session.DisjointAvatar(request, service, out var repeated, out _).Accepted && repeated is null,
                "同一已提交源槽不能重复分解");
            var restored = new CharacterRepository(path).List(owner).Single();
            That(JsonSerializer.Serialize(restored) == JsonSerializer.Serialize(session.Selected) &&
                Query("SELECT count(*) FROM inventory_avatar_details") is 0L,
                "重开仓储恢复相同revision、徽章UID与数量，已分解时装详情没有孤行");
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject<InvalidOperationException>(() => session.DisjointAvatar(request, service, out _, out _),
                "旧generation不能继续提交时装分解");
            That(Query("PRAGMA integrity_check") is "ok", "时装分解数据库完整");

            object? Query(string sql)
            {
                using var database = new SqliteConnection("Data Source=" + path + ";Pooling=False");
                database.Open();
                using var command = database.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
            string Snapshot() => JsonSerializer.Serialize(repository.List(owner)) + ":" +
                Query("SELECT count(*) FROM inventory_audit_log") + ":" +
                Query("SELECT count(*) FROM inventory_avatar_details") + ":" +
                Query("SELECT group_concat(name || '=' || seq, ',') FROM sqlite_sequence");
        }
        finally
        {
            Directory.Delete(root, true);
        }
        Console.WriteLine($"PASS {Count - checks} 现场时装分解资源、事务与持久化断言");
    }

    internal static ItemTemplate FindEligibleAvatar(ItemResources resources)
    {
        int[] candidates =
        [
            101550507, 406560053, 401550176, 401560113, 401570077,
            401520097, 401500147, 401530091, 401510121, 401540118, 401580026, 42611
        ];
        foreach (int itemId in candidates)
        {
            try
            {
                var template = resources.Items.Get(itemId);
                var table = resources.AvatarDisjoint.Get(template.Grade);
                if (template.Kind == ItemKind.Avatar && !template.DisjointForbidden && table is not null &&
                    table.Pools.SelectMany(pool => pool.Rewards)
                        .All(reward => resources.Items.Get(reward.ItemId).Kind == ItemKind.Emblem))
                    return template;
            }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or
                FileNotFoundException or NotSupportedException)
            {
            }
        }
        throw new InvalidDataException("现场PVF候选时装均不满足分解资格");
    }

    static void RejectSql(Action action, string name)
    {
        bool failed = false;
        try
        {
            action();
        }
        catch (Exception error) when (error is SqliteException or InvalidOperationException)
        {
            failed = true;
        }
        That(failed, name);
    }

    sealed class SequenceRandom(params int[] values) : Random
    {
        int index;

        public override int Next(int maxValue)
        {
            if (index >= values.Length)
                throw new InvalidOperationException("测试随机序列已耗尽");
            return values[index++];
        }
    }
}
