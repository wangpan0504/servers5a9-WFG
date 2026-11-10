using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.CollectBox;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class CollectBoxTests
{
    const string CatalogText = """
        [collect box info]
        [index] 2
        [base expiration date] `2029-01-01`
        [max expiration date] `2030-12-31 23:59:59`
        [extend item] 5001 30 5002 60
        [item position info]
        2 2
        101 -5
        0 0
        -1 -1
        104 1
        [/collect box info]
        [collect box info]
        [index] 3
        [item position info]
        1 1
        201 7
        [/collect box info]
        """;


    const string Spring2020CatalogText = """
        [collect box info]
        [index] 8
        [base expiration date] `2020-12-31 06:00:00`
        [max expiration date] `2020-12-31 06:00:00`
        [item position info]
        3 3
        490705309 6 490705314 6 490705310 6
        490705307 6 490705306 6 490705308 6
        490705312 6 490705313 6 490705311 6
        [/collect box info]
        """;

    public static void RunCore()
    {
        int before = Count;
        var catalog = new CollectBoxCatalog(CatalogText);
        var first = catalog.Get(2);
        That(catalog.Entries.Length == 2 && first is { Rows: 2, SlotsPerRow: 2 } &&
            first.Slots.Select(slot => slot.ItemId).SequenceEqual([101, 0, -1, 104]) &&
            first.Slots[0].Metadata == -5 && first.Extensions.SequenceEqual(
                [new CollectBoxExtensionDefinition(5001, 30), new CollectBoxExtensionDefinition(5002, 60)]),
            "收集箱目录保留物理占位、附属值与续期数量");
        long baseline = new DateTimeOffset(2029, 1, 1, 0, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeSeconds();
        That(first.BaseExpiration == baseline && first.TrySlot(104, out var configured) && configured.PhysicalSlot == 3 &&
            !first.TrySlot(0, out _), "收集箱日期按UTC+8解析且只索引正模板");
        Reject<InvalidDataException>(() => new CollectBoxCatalog(CatalogText.Replace("104 1", "101 1")),
            "同箱重复模板拒绝");
        Reject<InvalidDataException>(() => new CollectBoxCatalog(CatalogText.Replace("2 2", "200 2")),
            "客户端u8数量以上槽位拒绝");

        var state = CollectBoxState.Empty.Select(2).Add(2, 3, 104).Extend(2, baseline + 100);
        That(state.SelectedBox == 2 && state.Contains(2, 104) && state.Expiration(2) == baseline + 100,
            "选择、模板槽与期限使用同一不可变角色状态");
        state = state.Remove(2, 3, 104);
        That(!state.Contains(2, 104), "取出只删除精确物理槽");

        Bytes(CollectBoxPackets.Select(2, Accept()), [2, 2], "SELECT成功ACK使用客户端双box字段");
        Bytes(CollectBoxPackets.Mutation(CollectBoxResult.Reject(0x17)), [0, 0x17, 0], "变更失败ACK为零和u16错误");
        Bytes(CollectBoxPackets.Extension(new(2, 7, 5001), Accept()), [1, 2, 0x89, 0x13, 0, 0],
            "续期成功ACK回显box与item");
        var active = CollectBoxPackets.State(first, CollectBoxState.Empty.Select(2).Add(2, 3, 104), 2, baseline - 10);
        That(active.Length == 12 && active[0] == 2 && active[1] == 1 &&
            BinaryPrimitives.ReadUInt32LittleEndian(active.AsSpan(2)) == 10 && active[6] == 0 && active[7] == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(active.AsSpan(8)) == 104,
            "COLLECT_BOX按box、selected、剩余秒、永久位、u8数量和物品序列编码");
        var expired = CollectBoxPackets.State(first, CollectBoxState.Empty, 3, first.MaximumExpiration!.Value + 1);
        That(expired[1] == 0 && BinaryPrimitives.ReadUInt32LittleEndian(expired.AsSpan(2)) == uint.MaxValue && expired[6] == 0,
            "过期箱使用FFFFFFFF且只有实际选中箱置位");
        var permanent = CollectBoxPackets.State(catalog.Get(3), CollectBoxState.Empty, 3, baseline);
        That(permanent[1] == 1 && BinaryPrimitives.ReadUInt32LittleEndian(permanent.AsSpan(2)) == 0 &&
            permanent[6] == 1, "无期限箱使用永久标志");

        var noDateCatalog = new CollectBoxCatalog(CatalogText.Replace("[base expiration date] `2029-01-01`", "")
            .Replace("[max expiration date] `2030-12-31 23:59:59`", ""));
        var noDates = noDateCatalog.Get(2);
        var oldPermanent = CollectBoxPackets.State(noDates, state, 2, baseline + 1000);
        That(noDates.Extensions.Length == 2 && noDates.ResolveExpiration(baseline) is null &&
            oldPermanent[6] == 1 && BinaryPrimitives.ReadUInt32LittleEndian(oldPermanent.AsSpan(2)) == 0,
            "删除两日期后即使保留续期物品及历史期限也按永久发送");
        var permanentCharacter = new Character(1, 0, "Permanent", 1, new(1, 1, 0, 0, 0), null) with { CollectBox = state };
        var rejectedExtension = new CollectBoxService(noDateCatalog, null!, null!).Extend(permanentCharacter,
            new(2, 0, 5001), DateTimeOffset.FromUnixTimeSeconds(baseline));
        That(!rejectedExtension.Accepted && rejectedExtension.Error == CollectBoxService.ErrorMissing && rejectedExtension.Plan is null,
            "永久箱拒绝续期且不产生扣券计划");

        RunSpring2020Permanent();

        That(CollectBoxRequests.Select([2]) == 2 &&
            CollectBoxRequests.Add([2, 0, 7, 0, 104, 0, 0, 0]) == new CollectBoxAddRequest(2, 7, 104) &&
            CollectBoxRequests.Remove([2, 104, 0, 0, 0]) == new CollectBoxRemoveRequest(2, 104) &&
            CollectBoxRequests.Extend([2, 0, 7, 0, 0x89, 0x13, 0, 0]) == new CollectBoxExtendRequest(2, 7, 5001),
            "四条请求严格按S5A9字段解析");
        Reject<InvalidDataException>(() => CollectBoxRequests.Select([]), "SELECT畸形长度拒绝");
        Reject<InvalidDataException>(() => CollectBoxRequests.Add([2, 1, 7, 0, 104, 0, 0, 0]), "ADD保留位非零拒绝");
        Reject<InvalidDataException>(() => CollectBoxRequests.Remove([2, 0, 0, 0, 0]), "REMOVE非正模板拒绝");
        Console.WriteLine($"PASS {Count - before} 宝珠收集箱目录、状态与精确wire断言");

        static CollectBoxResult Accept()
        {
            var character = new Character(1, 0, "CollectWire", 1, new(1, 1, 0, 0, 0), null);
            var inventory = new InventoryPlan(character.Inventory, character.Inventory, "collectbox_test");
            return CollectBoxResult.Accept(new(character, character.CollectBox, inventory));
        }
    }

    static void RunSpring2020Permanent()
    {
        var box = new CollectBoxCatalog(Spring2020CatalogText).Get(8);
        long cutoff = new DateTimeOffset(2020, 12, 31, 6, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeSeconds();
        var old = CollectBoxState.Empty.Select(8).Add(8, 0, 490705309).Extend(8, cutoff - 1);
        var packet = CollectBoxPackets.State(box, old, 8, cutoff + 1);
        That(packet[6] == 1 && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2)) == 0,
            "2020九珠箱常驻：历史过期存档仍发送永久位1和剩余0，避免原生消费者跳过属性");
        That(box.BaseExpiration == cutoff && box.MaximumExpiration == cutoff && old.Expiration(8) == cutoff - 1 &&
            packet[0] == 8 && packet[1] == 1 && packet[7] == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(8)) == 490705309,
            "常驻解释保留资源声明、历史存档、选中状态和真实珠列表");
        var beforeCutoff = CollectBoxPackets.State(box, CollectBoxState.Empty, 0, cutoff - 1);
        That(beforeCutoff[1] == 0 && beforeCutoff[6] == 1 &&
            BinaryPrimitives.ReadUInt32LittleEndian(beforeCutoff.AsSpan(2)) == 0,
            "常驻与活动时钟跨截止、是否选中和是否已有珠分离");

        var nine = old;
        foreach (var slot in box.Slots.Skip(1))
            nine = nine.Add(8, slot.PhysicalSlot, slot.ItemId);
        var full = CollectBoxPackets.State(box, nine, 8, cutoff + 86400);
        var removed = nine.Remove(8, 0, 490705309);
        var emptySlot = CollectBoxPackets.State(box, removed, 8, cutoff + 86400);
        var readded = removed.Add(8, 0, 490705309);
        Bytes(CollectBoxPackets.State(box, readded, 8, cutoff + 86400), full,
            "九珠取下后重新放入按真实状态完整重放，不累积影子属性");
        That(full[7] == 9 && emptySlot[7] == 8 && emptySlot[6] == 1 &&
            Enumerable.Range(0, 9).Select(at => BinaryPrimitives.ReadInt32LittleEndian(full.AsSpan(8 + at * 4)))
                .SequenceEqual(box.Slots.Select(slot => slot.ItemId)) &&
            !Enumerable.Range(0, 8).Select(at => BinaryPrimitives.ReadInt32LittleEndian(emptySlot.AsSpan(8 + at * 4)))
                .Contains(490705309), "常驻不改变九珠物理顺序，取下通知移除准确珠");
        Bytes(CollectBoxPackets.State(box, old, 8, cutoff + 1), packet,
            "另一个状态快照的九珠变更不污染原角色单珠快照");

        CollectBoxEntry[] otherDefinitions =
        [
            new(9, 3, 3, cutoff, cutoff, box.Slots, []),
            new(8, 1, 9, cutoff, cutoff, box.Slots, []),
            new(8, 3, 3, cutoff, cutoff, box.Slots.SetItem(0, box.Slots[0] with { ItemId = 10007322 }), []),
            new(8, 3, 3, cutoff, cutoff, box.Slots.SetItem(0, box.Slots[0] with { Metadata = 7 }), []),
            new(8, 3, 3, cutoff, cutoff, box.Slots.SetItem(0, box.Slots[0] with { PhysicalSlot = 9 }), []),
            new(8, 3, 3, cutoff, cutoff, box.Slots, [new(10007322, 1)])
        ];
        foreach (var other in otherDefinitions)
        {
            var unchanged = CollectBoxPackets.State(other, old, 0, cutoff + 1);
            That(other.ResolveExpiration(cutoff - 1) == cutoff - 1 && unchanged[6] == 0 &&
                BinaryPrimitives.ReadUInt32LittleEndian(unchanged.AsSpan(2)) == uint.MaxValue,
                "其它编号、几何或珠定义保留历史期限，不按index8泛化永久");
        }
    }

    public static void RunResources(TownCatalog towns, ItemResources resources)
    {
        int before = Count;
        var box = resources.CollectBoxes.Entries.FirstOrDefault(entry =>
            entry.Slots.Any(slot => slot.ItemId > 0) && entry.Extensions.Length > 0 &&
            entry.BaseExpiration is { } baseline &&
            (entry.MaximumExpiration is null || entry.MaximumExpiration > baseline) &&
            entry.Slots.Any(slot => slot.ItemId > 0 &&
                entry.Extensions.Any(extension => extension.ItemId != slot.ItemId)))
            ?? throw new InvalidDataException("当前PVF没有可执行一次续期的宝珠收集箱");
        var slot = box.Slots.First(row => row.ItemId > 0 &&
            box.Extensions.Any(extension => extension.ItemId != row.ItemId));
        var extension = box.Extensions.First(row => row.ItemId != slot.ItemId);
        var itemTemplate = resources.Items.Get(slot.ItemId);
        var extensionTemplate = resources.Items.Get(extension.ItemId);
        long nowSeconds = Math.Max(1, box.BaseExpiration!.Value - 3600);
        var now = DateTimeOffset.FromUnixTimeSeconds(nowSeconds);

        var draft = new Character(1, 0, "CollectDraft", 0, towns.Room, towns.InitialReturn);
        draft = Grant(draft, itemTemplate, Math.Min(2, itemTemplate.StackLimit), now, resources.EpicPieces);
        draft = Grant(draft, extensionTemplate, extension.RequiredCount, now, resources.EpicPieces);
        string root = Path.Combine(Path.GetTempPath(), "S5A9-collectbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("collectbox", () => { });
            var current = repository.Create(owner, new("CollectRole", towns.Room, towns.InitialReturn)
            {
                Inventory = draft.Inventory
            });
            var service = new CollectBoxService(resources.CollectBoxes, resources.Items, resources.EpicPieces);

            current = repository.CommitCollectBox(owner, service.Select(current, box.Index).Plan!);
            var source = current.Inventory.Items.Single(item => item.Core.ItemId == slot.ItemId);
            int sourceBefore = source.Core.Count;
            var addRequest = new CollectBoxAddRequest(box.Index, checked((ushort)source.Position.Index), slot.ItemId);
            long historicalDeadline = box.MaximumExpiration ?? box.BaseExpiration.Value;
            var historicalAdd = service.Add(current, addRequest,
                DateTimeOffset.FromUnixTimeSeconds(checked(historicalDeadline + 1)));
            That(historicalAdd.Accepted, "历史活动期限只影响客户端状态，不阻断参考端允许的宝珠放入");

            var add = service.Add(current, addRequest, now);
            var addPlan = add.Plan ?? throw new InvalidDataException("放入成功却缺少原子提交规划");
            That(add.Accepted && addPlan.Inventory.Reason == "collectbox_add", "放入规划复用库存扣减并使用独立审计原因");
            current = repository.CommitCollectBox(owner, addPlan);
            That(current.CollectBox.Contains(box.Index, slot.ItemId) &&
                (current.Inventory.Find(source.Position)?.Core.Count ?? 0) == sourceBefore - 1,
                "放入原子提交模板槽与真实来源数量");
            That(service.Add(current, new(box.Index, checked((ushort)source.Position.Index), slot.ItemId), now).Error ==
                CollectBoxService.ErrorAlreadyStored, "重复放入使用客户端白名单错误");

            var remove = service.Remove(current, new(box.Index, slot.ItemId), now);
            That(remove.Accepted, "取出可合并或分配标准背包槽");
            using (var db = Open(path))
                Execute(db, "CREATE TRIGGER fail_collectbox_state BEFORE INSERT ON character_collectbox_state BEGIN SELECT RAISE(ABORT,'collectbox rollback'); END");
            var rollback = current;
            Reject<SqliteException>(() => repository.CommitCollectBox(owner, remove.Plan!), "收集箱晚期状态写失败");
            current = repository.LoadCharacter(owner, rollback.Slot);
            That(current.Revision == rollback.Revision && current.CollectBox.Same(rollback.CollectBox) &&
                current.Inventory.Items.SequenceEqual(rollback.Inventory.Items), "取出发放、UID、审计、revision与箱状态整笔回滚");
            using (var db = Open(path))
                Execute(db, "DROP TRIGGER fail_collectbox_state");
            current = repository.CommitCollectBox(owner, remove.Plan!);
            That(!current.CollectBox.Contains(box.Index, slot.ItemId) &&
                current.Inventory.Items.Single(item => item.Core.ItemId == slot.ItemId).Core.Count == sourceBefore,
                "取出成功归还一件模板物品");

            var ticket = current.Inventory.Items.Single(item => item.Core.ItemId == extension.ItemId);
            var extend = service.Extend(current,
                new(box.Index, checked((ushort)ticket.Position.Index), extension.ItemId), now);
            var extendPlan = extend.Plan ?? throw new InvalidDataException("续期成功却缺少原子提交规划");
            That(extend.Accepted && extendPlan.After.Expiration(box.Index) ==
                Math.Min(box.BaseExpiration.Value + 86400, box.MaximumExpiration ?? long.MaxValue),
                "续期从当前基础期限增加24小时并按PVF上限截断");
            using (var db = Open(path))
                Execute(db, "CREATE TRIGGER fail_collectbox_expiry BEFORE INSERT ON character_collectbox_expirations BEGIN SELECT RAISE(ABORT,'expiry rollback'); END");
            rollback = current;
            Reject<SqliteException>(() => repository.CommitCollectBox(owner, extendPlan), "期限晚期写入失败");
            current = repository.LoadCharacter(owner, rollback.Slot);
            That(current.Revision == rollback.Revision && current.CollectBox.Same(rollback.CollectBox) &&
                current.Inventory.Items.SequenceEqual(rollback.Inventory.Items), "续期券扣减、期限、审计和revision原子回滚");
            using (var db = Open(path))
                Execute(db, "DROP TRIGGER fail_collectbox_expiry");
            current = repository.CommitCollectBox(owner, extendPlan);
            var restored = new CharacterRepository(path).LoadCharacter(owner, current.Slot);
            That(restored.CollectBox.SelectedBox == box.Index &&
                restored.CollectBox.Expiration(box.Index) == extendPlan.After.Expiration(box.Index) &&
                (restored.Inventory.Items.SingleOrDefault(item => item.Core.ItemId == extension.ItemId)?.Core.Count ?? 0) == 0,
                "选择、期限与续期券消耗在仓储重开后保持");

            var finalSource = current.Inventory.Items.Single(item => item.Core.ItemId == slot.ItemId);
            var finalAdd = service.Add(current,
                new(box.Index, checked((ushort)finalSource.Position.Index), slot.ItemId), now);
            current = repository.CommitCollectBox(owner,
                finalAdd.Plan ?? throw new InvalidDataException("删除回归准备收集箱槽位失败"));
            repository.Delete(owner, repository.LoadDeletionTarget(owner, current.Slot));
            using (var db = Open(path))
                That(Scalar(db, """
                    SELECT
                        (SELECT count(*) FROM characters) +
                        (SELECT count(*) FROM character_collectbox_state) +
                        (SELECT count(*) FROM character_collectbox_slots) +
                        (SELECT count(*) FROM character_collectbox_expirations)
                    """) == 0, "角色仓储删除显式清理选择、模板槽和期限三张收集箱子表");
        }
        finally
        {
            TestFiles.DeleteDirectory(root, true);
        }
        Console.WriteLine($"PASS {Count - before} 宝珠收集箱真实PVF、事务与持久化断言");
    }

    static Character Grant(Character character, ItemTemplate template, int count, DateTimeOffset now, EpicPieceCatalog pieces)
    {
        var result = InventoryRewardGrantService.TryGrantCore(character,
            InventoryRewardGrantService.Create(template, count, now, shopExpiration: true), template, pieces, now);
        if (!result.Accepted)
            throw new InvalidDataException("测试物品不能进入普通背包: " + template.Id);
        return character with { Inventory = result.Plan!.After };
    }

    static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
            Pooling = false
        }.ToString());
        db.Open();
        return db;
    }

    static void Execute(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    static long Scalar(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
