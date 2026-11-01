using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Guilds;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GuildCargoTests
{
    public static void RunCore()
    {
        int before = Count;
        ParsersAndRules();
        Transactions();
        Console.WriteLine($"PASS {Count - before} 公会仓库UID、完整属性、跨账号事务及永久回执断言");
    }

    static ItemTemplate Template(ItemCore core, string attach = "[trade]") =>
        new(core.ItemId, core.Kind, 0, 0, 0, core.IsStackable ? 10000 : 1, 0, 0, 0, attach, ushort.MaxValue, 0, "Cargo item");

    static void ParsersAndRules()
    {
        That(GuildCargoRequests.TryDeposit([0, 121, 0, 1, 0, 0, 0, 5, 0, 0, 0, 7, 0], out var deposit) &&
            deposit is { Deposit: true, InventorySlot: 121, CargoSlot: 7, Count: 5, ItemId: 1 }, "247完整读取13字节布局");
        That(GuildCargoRequests.TryWithdraw([7, 0, 1, 0, 0, 0, 5, 0, 0, 0, 0], out var withdrawal) &&
            withdrawal is { Deposit: false, CargoSlot: 7, Count: 5, List: InventoryListType.Main }, "248没有目的格字段");
        That(!GuildCargoRequests.TryDeposit([0], out _) && !GuildCargoRequests.TryWithdraw([0], out _) &&
            !GuildCargoRequests.TryDeposit([1, 121, 0, 1, 0, 0, 0, 5, 0, 0, 0, 7, 0], out _) &&
            !GuildCargoRequests.TryWithdraw([0, 0, 1, 0, 0, 0, 0xff, 0xff, 0xff, 0xff, 0], out _),
            "仓库请求拒绝长度、特殊容器及溢出数量");
        That(GuildCargoCatalog.AllowsScript(new PvfDocument("[packagable]\n1\n[cash]\n0\n"), true) &&
            !GuildCargoCatalog.AllowsScript(new PvfDocument("[packagable]\n0\n"), false) &&
            !GuildCargoCatalog.AllowsScript(new PvfDocument("[packagable]\n1 2\n"), false) &&
            !GuildCargoCatalog.AllowsScript(new PvfDocument("[item category]\n`[packagable]`\n"), false) &&
            !GuildCargoCatalog.AllowsScript(new PvfDocument("[cash]\n1\n"), true),
            "资源packagable格式、类别及付费装备资格完整参与判断");
        That(GuildCargoCatalog.IsHall("map/GuildAgit/agit.map") &&
            GuildCargoCatalog.IsHall("map/Guildagit_2019/GuildAgit_Inside.map") && !GuildCargoCatalog.IsHall("map/village/guild.map"),
            "公会大厅仅接受运行资源目录guildagit真实场景");
        var core = new ItemCore(6100001, ItemKind.Material, 10);
        var item = new InventoryItem(1, new(InventoryListType.Main, 121), core);
        That(GuildCargoRules.Eligible(item, Template(core), true, 1700000000) &&
            !GuildCargoRules.Eligible(item with { Core = core with { EquipmentLockId = 1 } }, Template(core), true, 1700000000) &&
            !GuildCargoRules.Eligible(item with { Core = core with { TradeRestriction = 1 } }, Template(core), true, 1700000000) &&
            !GuildCargoRules.Eligible(item with { Core = core with { ExpireTime = 1600000000 } }, Template(core), true, 1700000000) &&
            !GuildCargoRules.Eligible(item, Template(core, "[account]"), true, 1700000000),
            "仓库拒绝锁定、绑定、过期及账号专用实例");
        var view = new GuildCargoView(1, 0, 8, [item with { Position = new(InventoryListType.Main, 0) }],
            [new(1, 1700000000, 1, new string('中', 20), core, 5)]);
        That(GuildCargoPackets.Snapshot(view).Length == 130 && GuildCargoPackets.Snapshot(view, false).Length == 129 &&
            GuildCargoPackets.History(view).Length == 62, "95仓库123字节物品行与57字节历史行保持独立协议布局");
        Bytes(GuildCargoPackets.Transfer(deposit!, new(0, view, Destination: 7)), [1, 0, 121, 0, 5, 0, 0, 0, 7, 0],
            "247提交确认仅使用已提交的实际数量和仓库格");
        Bytes(GuildCargoPackets.Transfer(withdrawal!, new(0, view, Replayed: true)), [0, 8, 0], "248永久重放解除等待且不再搬动物品");
        That(GuildCargoRequests.TryMove([1, 0, 2, 0, 0, 0, 7, 0, 0, 0, 0, 0], out var move) &&
            move is { Move: true, CargoSlot: 1, ItemId: 2, TargetSlot: 7, TargetItemId: 0, Count: 0 } &&
            !GuildCargoRequests.TryMove([1, 0, 7, 0], out _) &&
            !GuildCargoRequests.TryMove([1, 0, 2, 0, 0, 0, 0xff, 0xff, 0, 0, 0, 0], out _),
            "249严格读取12B源槽/源编号/目标槽/目标编号，不接收旧猜测4B布局");
        Bytes(GuildCargoPackets.Transfer(move!, new(0, view)), [1, 1, 0, 7, 0], "249成功确认仅携带两个真实槽号");
    }

    static void Transactions()
    {
        string path = Path.Combine(Path.GetTempPath(), "guild-cargo-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var masterOwner = owners.Acquire("cargo-master", () => { });
            using var peerOwner = owners.Acquire("cargo-peer", () => { });
            var room = new Location(0, 0, 100, 100);
            var core = new ItemCore(6100001, ItemKind.Material, 100)
            { Extension27 = 123, Extension35 = 456, Random0 = new(2, 3, 4), Extension114 = new(1, 789), Extension121 = 42 };
            var equipment = new ItemCore(6100002, ItemKind.Equipment, 0)
            { SealFlag = 1, Attr = 5, Durability = 99, EnchantCardId = 17, AmplifyType = 1, AmplifyValue = 72,
                Chronicle0 = new(12, 1, 2, 3, 4), EmblemSocketCount = 1, Emblem1 = 321, GuardianGemKeys = 123456,
                Extension97 = 91, Extension110 = 92, Extension119 = 93, Extension120 = 94, Extension121 = 95 };
            var master = repository.Create(masterOwner, new("CargoMaster", room, room)
            { Inventory = new([new(0, new(InventoryListType.Main, 121), core), new(0, new(InventoryListType.Main, 9), equipment)]) });
            var peer = repository.Create(peerOwner, new("CargoPeer", room, room));
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            void Sql(string sql)
            {
                using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
            }
            Sql($"INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix) VALUES(51,'Cargo guild','cargo',{master.Id},1600000000);" +
                $"INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(51,{master.Id},0,1600000000),(51,{peer.Id},4,1600000000);");
            GuildCargoActor Actor(AccountOwners.Lease owner, Character character) => new(owner, character, 1, () => owner.IsCurrent);
            GuildCargoView Read(AccountOwners.Lease owner, Character character) => repository.ReadGuildCargo(Actor(owner, character), true).View!;
            string Key(int value) => value.ToString("X64");
            GuildCargoCommit Transfer(AccountOwners.Lease owner, Character character, GuildCargoView view, GuildCargoRequest request, int key)
            {
                var source = request.Deposit ? character.Inventory.Find(new(request.List, request.InventorySlot)) : view.Items.FirstOrDefault(item => item.Position.Index == request.CargoSlot);
                var plan = source is null ? new GuildCargoPlan(198, request, view) : GuildCargoRules.Plan(character, view, request,
                    Template(source.Core, source.Core.Kind == ItemKind.Equipment ? "[sealing]" : "[trade]"), true, 1700000000, 56);
                return repository.CommitGuildCargo(Actor(owner, character), plan, Key(key), 1700000000);
            }
            string State()
            {
                using var command = db.CreateCommand();
                command.CommandText = """
                    SELECT COALESCE(group_concat(v,'|'),'') FROM(
                    SELECT 'i:'||item_uid||':'||COALESCE(character_id,-1)||':'||account||':'||item_core v FROM character_inventory_items
                    UNION ALL SELECT 's:'||guild_id||':'||slot_index||':'||item_uid FROM guild_cargo_slots
                    UNION ALL SELECT 'r:'||request_id||':'||revision FROM guild_cargo_receipts
                    UNION ALL SELECT 'q:'||request_id||':'||kind||':'||account||':'||character_id FROM guild_request_claims
                    UNION ALL SELECT 'h:'||id||':'||moved_count FROM guild_cargo_history
                    UNION ALL SELECT 'v:'||guild_id||':'||revision||':'||capacity FROM guild_cargo_state
                    UNION ALL SELECT 'c:'||id||':'||revision FROM characters ORDER BY v)
                    """;
                return (string)command.ExecuteScalar()!;
            }

            var initial = Read(masterOwner, master);
            That(initial is { Capacity: 8, Revision: 0, Items.Length: 0 }, "缺失公会仓库只在明确打开时原子初始化8格");
            That(repository.ReadGuildCargo(Actor(peerOwner, peer), true).Error == 36, "普通会员未获bit15不得读取仓库");
            Sql("INSERT INTO guild_roles(guild_id,grade,permissions,name) VALUES(51,4,32768,'Cargo role')");
            var sourceUid = master.Inventory.Find(new(InventoryListType.Main, 121))!.Uid;
            var deposit = new GuildCargoRequest(true, InventoryListType.Main, 121, 0, core.ItemId, 40);
            var split = Transfer(masterOwner, master, initial, deposit, 1);
            That(split.Success && split.View!.Revision == 1 && split.Character!.Inventory.Find(new(InventoryListType.Main, 121)) is { Core.Count: 60 } &&
                split.View.Items[0].Core == core with { Value = 40 } && split.View.Items[0].Uid != sourceUid,
                "拆分存入保留源UID并为独立仓库实例分配全局UID，所有核心字段完整保留");
            master = split.Character!;
            string before = State();
            var replay = Transfer(masterOwner, master, initial, deposit, 1);
            That(replay is { Success: true, Replayed: true } && State() == before, "同一连接请求永久重放不增版本、不扣物品、不追加历史");
            That(Transfer(masterOwner, master, split.View!, deposit with { Count = 1 }, 1).Error == 198 && State() == before,
                "同一永久请求身份不能换正文执行第二次");
            That(Transfer(masterOwner, master, initial, deposit, 2).Error == 198 && State() == before,
                "旧仓库快照拒绝且不改变任何资产或回执");

            var withdrawal = new GuildCargoRequest(false, InventoryListType.Main, 0, 0, core.ItemId, 40);
            var taken = Transfer(peerOwner, peer, Read(peerOwner, peer), withdrawal, 3);
            That(taken.Success && taken.View!.Items.Length == 0 && taken.Character!.Inventory.Find(new(InventoryListType.Main, 121)) is { } received &&
                received.Uid == split.View!.Items[0].Uid && received.Core == core with { Value = 40 },
                "另一账号完整取出沿用原仓库UID与全部实例属性，实际分配最低合法空格");
            peer = taken.Character!;
            before = State();
            That(Transfer(masterOwner, master, split.View!, withdrawal, 4).Error == 198 && State() == before,
                "并发第二个取出使用旧版本只能失败一次，不能复制资产");
            var fullDeposit = deposit with { Count = 60 };
            var stored = Transfer(masterOwner, master, Read(masterOwner, master), fullDeposit, 5);
            That(stored.Success && stored.View!.Items[0].Uid == sourceUid, "整堆存入保留原背包UID");
            master = stored.Character!;
            var merge = Transfer(peerOwner, peer, Read(peerOwner, peer), withdrawal with { Count = 60 }, 6);
            That(merge.Success && merge.Character!.Inventory.Find(new(InventoryListType.Main, 121)) is { Core.Count: 100 } merged &&
                merged.Uid == peer.Inventory.Find(new(InventoryListType.Main, 121))!.Uid && merge.View!.Items.Length == 0,
                "取出优先完整合并最低兼容堆栈，目标UID稳定，源UID原子消耗");
            peer = merge.Character!;

            var equipmentUid = master.Inventory.Find(new(InventoryListType.Main, 9))!.Uid;
            var equipmentDeposit = new GuildCargoRequest(true, InventoryListType.Main, 9, 1, equipment.ItemId, 1);
            var equipmentStored = Transfer(masterOwner, master, Read(masterOwner, master), equipmentDeposit, 7);
            That(equipmentStored.Success && equipmentStored.View!.Items[0].Core == equipment && equipmentStored.View.Items[0].Uid == equipmentUid,
                "封装装备存入保留增幅、附魔、异界、徽章、守护珠和全部尾字段");
            master = equipmentStored.Character!;
            var materialStored = Transfer(peerOwner, peer, Read(peerOwner, peer), deposit with { Count = 5 }, 20);
            That(materialStored.Success, "准备249交换的另一份真实材料实例");
            peer = materialStored.Character!;
            var moveBefore = materialStored.View!;
            var materialUid = moveBefore.Items.Single(item => item.Position.Index == 0).Uid;
            int historyBefore = moveBefore.History.Length;
            long memberRevision = master.Revision;
            var moveEquipment = new GuildCargoRequest(false, InventoryListType.Main, 0, 1, equipment.ItemId, 0)
                { Move = true, TargetSlot = 2, TargetItemId = 0 };
            var moved = Transfer(masterOwner, master, moveBefore, moveEquipment, 21);
            That(moved.Success && moved.Character!.Revision == memberRevision && moved.Character.Inventory.Items.SequenceEqual(master.Inventory.Items) &&
                moved.Character.Inventory.Gold == master.Inventory.Gold &&
                moved.View!.Items.Single(item => item.Position.Index == 2) is { } emptyMoved && emptyMoved.Uid == equipmentUid && emptyMoved.Core == equipment &&
                moved.View.History.Length == historyBefore, "249空位移动完整保留UID和装备核心，不动个人库存版本或伪造存取历史");
            before = State();
            That(Transfer(masterOwner, master, moveBefore, moveEquipment, 21).Replayed && State() == before &&
                Transfer(masterOwner, master, moved.View!, moveEquipment with { TargetSlot = 3 }, 21).Error == 198 && State() == before,
                "249永久重放不再次交换，同请求改目标也不能第二次执行");
            var swapRequest = moveEquipment with { CargoSlot = 2, TargetSlot = 0, TargetItemId = core.ItemId };
            var swapped = Transfer(masterOwner, master, moved.View!, swapRequest, 22);
            That(swapped.Success && swapped.View!.Items.Single(item => item.Position.Index == 0).Uid == equipmentUid &&
                swapped.View.Items.Single(item => item.Position.Index == 2).Uid == materialUid && swapped.View.History.Length == historyBefore,
                "249占用槽互换完整实例，数量与核心不合并、不拆分");
            before = State();
            That(Transfer(masterOwner, master, swapped.View!, swapRequest with { CargoSlot = 0, ItemId = equipment.ItemId, TargetSlot = 2, TargetItemId = 0 }, 26).Error == 198 && State() == before &&
                Transfer(masterOwner, master, moveBefore, moveEquipment, 27).Error == 198 && State() == before,
                "249目标编号或仓库版本过期均整笔拒绝");
            var swapBack = Transfer(masterOwner, master, swapped.View!, swapRequest with { CargoSlot = 0, TargetSlot = 2 }, 23);
            var restored = Transfer(masterOwner, master, swapBack.View!, moveEquipment with { CargoSlot = 2, TargetSlot = 1 }, 24);
            That(restored.Success && restored.View!.Items.Single(item => item.Position.Index == 1).Uid == equipmentUid,
                "249反向交换及空位恢复仍沿用原装备UID");
            before = State();
            Sql("CREATE TRIGGER fail_cargo_move BEFORE INSERT ON guild_cargo_receipts BEGIN SELECT RAISE(ABORT,'cargo move rollback'); END");
            bool moveRejected = false;
            try { Transfer(masterOwner, master, restored.View!, moveEquipment, 28); }
            catch (SqliteException) { moveRejected = true; }
            That(moveRejected && State() == before, "249回执失败回滚双槽、仓库版本与统一claim");
            Sql("DROP TRIGGER fail_cargo_move");
            var materialTaken = Transfer(peerOwner, peer, Read(peerOwner, peer), withdrawal with { Count = 5 }, 25);
            That(materialTaken.Success, "249往返后材料仍可正常跨账号取回");
            peer = materialTaken.Character!;
            before = State();
            Sql("CREATE TRIGGER fail_cargo_receipt BEFORE INSERT ON guild_cargo_receipts BEGIN SELECT RAISE(ABORT,'cargo rollback'); END");
            bool rejected = false;
            try { Transfer(peerOwner, peer, Read(peerOwner, peer), new(false, InventoryListType.Main, 0, 1, equipment.ItemId, 1), 8); }
            catch (SqliteException) { rejected = true; }
            That(rejected && State() == before, "回执写入故障回滚跨账号所有权、背包、仓库、版本与历史的全部中间写入");
            Sql("DROP TRIGGER fail_cargo_receipt");
            Sql("UPDATE guild_roles SET permissions=0 WHERE guild_id=51 AND grade=4");
            before = State();
            That(Transfer(peerOwner, peer, equipmentStored.View!, new(false, InventoryListType.Main, 0, 1, equipment.ItemId, 1), 9).Error == 36 && State() == before,
                "打开后权限被撤销也由writer事务复验，旧页面不能继续取物");
            Sql("UPDATE guild_roles SET permissions=32768 WHERE guild_id=51 AND grade=4");
            var equipmentTaken = Transfer(peerOwner, peer, Read(peerOwner, peer), new(false, InventoryListType.Main, 0, 1, equipment.ItemId, 1), 10);
            That(equipmentTaken.Success && equipmentTaken.Character!.Inventory.Find(new(InventoryListType.Main, 9)) is { } receivedEquipment &&
                receivedEquipment.Uid == equipmentUid && receivedEquipment.Core == equipment,
                "跨账号装备取出保留原UID与全部实例属性");
            peer = equipmentTaken.Character!;
            var raceDeposit = Transfer(peerOwner, peer, Read(peerOwner, peer), new(true, InventoryListType.Main, 121, 0, core.ItemId, 5), 500);
            That(raceDeposit.Success, "准备真实并发取出使用一份已提交材料");
            peer = raceDeposit.Character!;
            var raceView = raceDeposit.View!;
            var raceMaster = master;
            var racePeer = peer;
            var racing = Task.WhenAll(Task.Run(() => Transfer(masterOwner, raceMaster, raceView, withdrawal with { Count = 5 }, 501)),
                Task.Run(() => Transfer(peerOwner, racePeer, raceView, withdrawal with { Count = 5 }, 502))).GetAwaiter().GetResult();
            That(racing.Count(result => result.Success) == 1 && racing.Count(result => result.Error == 198) == 1 &&
                Read(masterOwner, master).Items.Length == 0, "两个账号真实并发竞争同一仓库实例，writer事务只允许一笔成功");
            if (racing[0].Success) master = racing[0].Character!;
            if (racing[1].Success) peer = racing[1].Character!;
            That(master.Inventory.Items.Concat(peer.Inventory.Items).Where(item => item.Core.ItemId == core.ItemId).Sum(item => item.Core.Count) == 100,
                "并发竞争后双方材料与仓库总量守恒");
            // 反复移动同一真实实例，历史有界而永久回执不能随历史裁剪。
            for (int i = 0; i < 26; i++)
            {
                var put = Transfer(peerOwner, peer, Read(peerOwner, peer), equipmentDeposit, 100 + i * 2);
                That(put.Success, "循环存入沿用统一事务");
                peer = put.Character!;
                var take = Transfer(peerOwner, peer, put.View!, new(false, InventoryListType.Main, 0, 1, equipment.ItemId, 1), 101 + i * 2);
                That(take.Success, "循环取出沿用统一事务");
                peer = take.Character!;
            }
            That(Read(peerOwner, peer).History.Length == 50, "既有仓库历史只保留最近50条");
            before = State();
            That(Transfer(masterOwner, master, initial, deposit, 1).Replayed && State() == before,
                "早期历史被裁剪后永久回执仍禁止重放扣物");
            var reopened = new CharacterRepository(path);
            That(reopened.ReadGuildCargo(Actor(peerOwner, peer), false).View is { History.Length: 50, Items.Length: 0 } &&
                reopened.List(peerOwner).Single().Inventory.Find(new(InventoryListType.Main, 9))!.Uid == equipmentUid,
                "重开数据库后仓库历史、完整装备所有权与UID仍一致");
        }
        finally { SqliteConnection.ClearAllPools(); TestFiles.DeleteFile(path); TestFiles.DeleteFile(path + "-wal"); TestFiles.DeleteFile(path + "-shm"); }
    }
}
