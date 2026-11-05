using static DfoServer.Testing.TestCheck;
using DfoServer.Infrastructure;
using System.Text.Json;
using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer.Testing.Inventory;

internal static class CargoTests
{
    static void VerifyAcquisition(TownCatalog towns, ItemResources resources)
    {
        RunMileagePersonalCargo(towns, resources);
        var now = DateTimeOffset.UtcNow;
        var role = new Character(1, 0, "CargoAcquire", 0, towns.InitialReturn, null)
            { Level = 60, Inventory = new([]) { AccountCargo = new(Capacity: 8) } };
        var small = resources.Items.Get(50);
        var silver = resources.Items.Get(110);
        That(small.PersonalCargoUpgradeTarget == 24 && silver.PersonalCargoUpgradeTarget == 40 && small.HasAcquisitionEffect,
            "个人工具沿现有资源目标识别即时获取效果");
        That(Enumerable.Range(0, 17).All(i => GameWorld.Items.CargoCatalog.SupportsPersonalCapacity(8 + 16 * i)) &&
            !GameWorld.Items.CargoCatalog.SupportsPersonalCapacity(32) && !GameWorld.Items.CargoCatalog.SupportsPersonalCapacity(280) &&
            resources.Items.Get(6).PersonalCargoUpgradeTarget == 40,
            "新扩容只接受本版客户端17档，旧32格工具兼容到40格");
        foreach (int id in new[] { 50, 6, 110, 2681921 })
        {
            var template = resources.Items.Get(id);
            var core = InventoryRewardGrantService.Create(template, utcNow: now);
            var batch = InventoryRewardGrantService.TryGrantBatch(role, [new(id, 1)], resources.Items, resources.GoldLimits, resources.EpicPieces, now);
            var frozen = InventoryRewardGrantService.TryGrantCore(role, core, template, resources.EpicPieces, now);
            var instance = InventoryRewardGrantService.TryGrantItems(role, [new(1234, new(InventoryListType.Main, 65), core)],
                resources.Items, resources.GoldLimits, resources.EpicPieces, now);
            That(batch.Accepted && frozen.Accepted && instance.Accepted && batch.Plan!.After.Items.IsEmpty &&
                frozen.Plan!.After.Items.IsEmpty && instance.Plan!.After.Items.IsEmpty &&
                batch.Plan.After.CargoCapacity == frozen.Plan.After.CargoCapacity && batch.Plan.After.AccountCargo == frozen.Plan.After.AccountCargo &&
                batch.Plan.After.CargoCapacity == instance.Plan.After.CargoCapacity && batch.Plan.After.AccountCargo == instance.Plan.After.AccountCargo,
                "普通奖励、冻结核心及邮件实例获取均直接扩容，不留下STK " + id);
            That(batch.Plan!.After.Gold == 0 && batch.Plan.After.AccountWallet.Same(role.Inventory.AccountWallet),
                "取得工具后只激活效果，不二次收取金币或点券 " + id);
            That(!InventoryRewardGrantService.TryGrantCore(role, core, template, resources.EpicPieces, now, usePickupSlotPolicy: true).Accepted,
                "未实现无实体回执的地面路径在提交前拒绝，不伪造目标槽 " + id);
        }
        foreach (var row in resources.Cargo.AcquisitionItems)
            That(resources.Items.Get(row.Key).AcquisitionAccountCargo == row.Value, "账号工具绑定同一合法档位目录 " + row.Key);
        foreach (ushort capacity in new ushort[] { 24, 40, 264 })
            That(!InventoryRewardGrantService.TryGrantBatch(role with { Inventory = role.Inventory with { CargoCapacity = capacity } },
                [new(50, 1)], resources.Items, resources.GoldLimits, resources.EpicPieces, now).Accepted,
                "已达或超过目标时拒绝，不退成STK " + capacity);
        foreach (ushort capacity in new ushort[] { 0, 1, 16, 240 })
            That(!InventoryRewardGrantService.TryGrantBatch(role with { Inventory = role.Inventory with { AccountCargo = new(Capacity: capacity) } },
                [new(2681921, 1)], resources.Items, resources.GoldLimits, resources.EpicPieces, now).Accepted,
                "账号未开通、越档、旧档或满级不能获取该工具实体 " + capacity);
        That(!InventoryRewardGrantService.TryGrantBatch(role, [new(50, 2)], resources.Items, resources.GoldLimits, resources.EpicPieces, now).Accepted &&
            !InventoryRewardGrantService.TryGrantBatch(role, [new(110, 1), new(50, 1)], resources.Items, resources.GoldLimits, resources.EpicPieces, now).Accepted,
            "重复或倒序扩容整笔拒绝，不静默吞掉额外工具");
        var chain = InventoryRewardGrantService.TryGrantBatch(role, [new(50, 1), new(110, 1)], resources.Items, resources.GoldLimits, resources.EpicPieces, now);
        That(chain.Accepted && chain.Plan!.After.CargoCapacity == 40 && chain.Plan.After.Items.IsEmpty,
            "同批按计划中容量连续扩容，不退回初始容量");
        var old = new InventoryItem(99, new(InventoryListType.Main, 65), InventoryRewardGrantService.Create(small, utcNow: now));
        var stocked = role with { Inventory = role.Inventory with { Items = [old] } };
        var higher = InventoryRewardGrantService.TryGrantBatch(stocked, [new(110, 1)], resources.Items, resources.GoldLimits, resources.EpicPieces, now);
        That(higher.Accepted && higher.Plan!.After.CargoCapacity == 40 && higher.Plan.After.Items is [var retained] && retained == old,
            "新获得工具生效，不扫描或消费旧背包中的其他工具");
        var expired = old with { Core = old.Core with { ExpireTime = checked((int)now.ToUnixTimeSeconds()) } };
        That(!InventoryRewardGrantService.TryGrantItems(role, [expired], resources.Items, resources.GoldLimits, resources.EpicPieces, now).Accepted,
            "过期附件不能激活扩容或变成无效果STK");
        long until = now.ToUnixTimeSeconds() + 60;
        var expiring = old with { Core = old.Core with { ExpireTime = checked((int)until) } };
        var pending = InventoryRewardGrantService.TryGrantItems(role, [expiring], resources.Items, resources.GoldLimits, resources.EpicPieces, now);
        That(pending.Accepted && pending.Plan!.CharacterPrecondition!.Matches(role, until - 1) &&
            !pending.Plan.CharacterPrecondition.Matches(role, until) &&
            !pending.Plan.CharacterPrecondition.Matches(role with { Level = 0 }, until - 1),
            "扩容获取在提交前复验到期边界和原角色资格，不能重绑旧计划");
        var funded = role with { Inventory = role.Inventory with { AccountWallet = role.Inventory.AccountWallet.Set(CurrencyKind.Cera, 1000) } };
        var bonusShop = GameWorld.Items.CashShopCatalog.Parse("[item etc]\n100063 50 1 0 0 10 0 0 0",
            "[bonus item]\n100063 110 1\n[/bonus item]");
        var bought = new InventoryCashShopService(resources.Items, bonusShop, resources.GoldLimits, resources.EpicPieces, resources.Cargo)
            .Buy(funded, new(0, [new(100063, 0)]), out _, now);
        That(bought.Accepted && bought.Plan!.After.CargoCapacity == 40 && bought.Plan.After.Items.IsEmpty &&
            bought.Plan.After.AccountWallet.Get(CurrencyKind.Cera) == 990,
            "主商品24格与附赠40格连续生效，最终合并不覆盖附赠扩容");
    }

    public static void RunMileagePersonalCargo(TownCatalog towns, ItemResources resources)
    {
        var now = DateTimeOffset.UtcNow;
        var leaf = resources.Items.Get(490705297);
        That(leaf.Kind == GameWorld.Items.ItemKind.Consumable && leaf.StackableType == "[etc] 0" &&
            leaf.PersonalCargoUpgradeTarget == 120 && leaf.HasAcquisitionEffect && leaf.IsPremiumService,
            "积分金库券终叶保持原类型并识别具名120格服务");
        That(new[] { 490705277, 490705285 }.All(id => resources.Items.Get(id).PersonalCargoUpgradeTarget == 0 &&
            !resources.Items.Get(id).HasAcquisitionEffect), "两层原包装不提前识别为扩容服务");
        var source = new Character(1, 0, "MileCargo", 0, towns.Room, towns.InitialReturn)
        {
            Level = 95,
            Inventory = new([]) { CargoCapacity = 56, Gold = 9876,
                AccountWallet = InventorySnapshot.Empty.AccountWallet.Set(CurrencyKind.Cera, 1234) }
        };
        var items = new InventoryService(resources.Items, resources.GoldLimits, resources.EpicPieces);
        var boosters = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits, resources.EpicPieces);
        var cargo = new InventoryCargoService(resources.Items, resources.Cargo, resources.GoldLimits);
        Character Apply(Character character, InventoryResult result)
        {
            That(result.Accepted && ReferenceEquals(result.Plan!.Before, character.Inventory), "积分金库券沿正常快照规划");
            return character with { Inventory = result.Plan!.After };
        }
        short Slot(Character character, int id) => character.Inventory.Items.Single(item => item.Core.ItemId == id).Position.Index;
        var first = Apply(source, items.Grant(source, [new(490705277, 1)]));
        var second = Apply(first, boosters.Open(first, new(Slot(first, 490705277), 1), out _, now));
        That(second.Inventory.CargoCapacity == 56 && second.Inventory.Items.Single().Core is { ItemId: 490705285, Count: 1 },
            "第一层只消费277并发出原285包装");
        short used = Slot(second, 490705285);
        var upgraded = Apply(second, boosters.Open(second, new(used, 1), out _, now));
        That(upgraded.Inventory.CargoCapacity == 120 && upgraded.Inventory.Items.IsEmpty &&
            upgraded.Inventory.Gold == source.Inventory.Gold && upgraded.Inventory.AccountWallet.Same(source.Inventory.AccountWallet) &&
            upgraded.Inventory.AccountCargo == source.Inventory.AccountCargo && source.Inventory.CargoCapacity == 56 && source.Inventory.Items.IsEmpty,
            "第二层单奖终叶即时扩容120格，不留STK、不二次收费或改账号金库");
        That(!boosters.Open(upgraded, new(used, 1), out _, now).Accepted, "重复原槽开包不能再扩容");
        foreach (ushort capacity in new ushort[] { 120, 136, 264 })
        {
            var holder = source with { Inventory = source.Inventory with { CargoCapacity = capacity } };
            holder = Apply(holder, items.Grant(holder, [new(490705285, 1)]));
            string before = JsonSerializer.Serialize(holder);
            var rejected = boosters.Open(holder, new(Slot(holder, 490705285), 1), out _, now);
            That(!rejected.Accepted && rejected.Plan is null && JsonSerializer.Serialize(holder) == before,
                "已达或超过120格时整包拒绝并保留285 " + capacity);
        }
        var core = InventoryRewardGrantService.Create(leaf, utcNow: now);
        var frozen = InventoryRewardGrantService.TryGrantCore(source, core, leaf, resources.EpicPieces, now);
        var attached = new InventoryItem(9123, new(InventoryListType.Main, 65), core);
        var mail = InventoryRewardGrantService.TryGrantItems(source, [attached], resources.Items, resources.GoldLimits, resources.EpicPieces, now);
        That(frozen.Accepted && mail.Accepted && frozen.Plan!.After.CargoCapacity == 120 && mail.Plan!.After.CargoCapacity == 120 &&
            frozen.Plan.After.Items.IsEmpty && mail.Plan.After.Items.IsEmpty, "冻结核心与邮件实例复用同一120格获取效果");
        That(!items.Grant(source, [new(490705297, 2)]).Accepted &&
            !items.Grant(source with { Level = 0 }, [new(490705297, 1)]).Accepted &&
            !InventoryRewardGrantService.TryGrantItems(source, [attached with { Core = core with { ExpireTime = 1 } }],
                resources.Items, resources.GoldLimits, resources.EpicPieces, now).Accepted,
            "多张、等级不足和过期附件不吞券、不越过资格");
        // 历史存量领域入口夹具；这不证明原生右键会发送408。
        var old = attached with { Core = core with { Value = 2 } };
        var legacy = source with { Inventory = source.Inventory with { Items = [old] } };
        var redeemed = Apply(legacy, cargo.PersonalUpgrade(legacy, old.Position.Index));
        That(redeemed.Inventory.CargoCapacity == 120 && redeemed.Inventory.Find(old.Position)?.Core.Count == 1 &&
            !cargo.PersonalUpgrade(redeemed, old.Position.Index).Accepted,
            "历史297沿已有408领域入口只消费一张，重复拒绝保留第二张");
    }

    public static void RunCore()
    {
        VerifyAccountCommon45Persistence();
        VerifySortRules();
        var template = new DfoServer.GameWorld.Items.ItemTemplate(50, DfoServer.GameWorld.Items.ItemKind.Consumable,
            0, 10, 0, 100, 0, 0, 0, "[free]", 1, 0, "仓库资格测试") { MaximumLevel = 20, PersonalCargoUpgradeTarget = 24 };
        var catalog = new DfoServer.GameWorld.Items.ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        catalog.PrefillTemplates(new Dictionary<int, DfoServer.GameWorld.Items.ItemTemplate> { [50] = template });
        var service = new InventoryCargoService(catalog, null!, null!);
        var source = new InventoryItem(1, new(InventoryListType.Main, 65), new ItemCore(50, template.Kind, 2));
        var role = new Character(1, 0, "CargoGuard", 0, new(0, 0, 0, 0), null) { Level = 10, Inventory = new([source]) };
        var captured = InventoryRequests.Move(Convert.FromHexString("007200ED913F1D010000000201000100000000000000FFFFFFFF0000"));
        That(captured.Source == new InventorySlot(InventoryListType.Main, 114) && captured.SourceItemId == 490705389 &&
            captured.Count == 1 && captured.Destination == new InventorySlot(InventoryListType.PersonalCargo, 1) && captured.DestinationItemId == 1,
            "现场存入请求按现行解析器还原字段");
        var move = new InventoryMoveRequest(source.Position, 50, 1, new(InventoryListType.PersonalCargo, 1), 1, 0, -1, 0, 0);
        That(InventoryMoveService.Plan(role, move, catalog, 0).Accepted, "空个人金库格接纳当前客户端格号别名");
        That(InventoryMoveService.Plan(role, move with { DestinationItemId = 0 }, catalog, 0).Accepted, "保留标准空格零值");
        var accountRole = role with { Inventory = role.Inventory with { AccountCargo = new(Capacity: 8) } };
        var accountMove = move with { Count = 2, Destination = new(InventoryListType.AccountCargo, 1) };
        var accountBead = template with { AttachType = "", AccountBoundAfterTrade = true };
        foreach (byte attr in new byte[] { 0, 32 })
        {
            catalog.PrefillTemplates(new Dictionary<int, DfoServer.GameWorld.Items.ItemTemplate> { [50] = accountBead });
            var bead = source with { Core = source.Core with { Attr = attr, Extension97 = 1234 } };
            var beadOwner = accountRole with { Inventory = accountRole.Inventory with { Items = [bead] } };
            var storedBead = InventoryMoveService.Plan(beadOwner, accountMove, catalog, 0);
            That(storedBead.Accepted && storedBead.Plan!.After.Find(accountMove.Destination) == bead with { Position = accountMove.Destination } &&
                storedBead.Plan.After.Find(bead.Position) is null, "限次交易后账号绑定宝珠存入保留UID、完整核心和数量，不依赖实例剩余交易次数");
        }
        foreach (var denied in new[] { accountBead with { AccountBoundAfterTrade = false }, accountBead with { AccountCargoForbidden = true },
            accountBead with { PvpFreeEquipment = true }, accountBead with { AttachType = "[trade]" }, accountBead with { AttachType = "[trade delete]" } })
            That(!InventoryCargoService.AllowsInsert(source.Core, InventoryListType.AccountCargo, denied),
                "缺少有效账号声明、禁入标记、PK专用装备及显式角色绑定仍拒绝入库");
        That(!InventoryCargoService.AllowsInsert(source.Core with { EquipmentLockId = 1 }, InventoryListType.AccountCargo, accountBead) &&
            !InventoryCargoService.AllowsInsert(source.Core with { TradeRestriction = 1 }, InventoryListType.AccountCargo, accountBead),
            "新交易限制声明不绕过实例安全锁和交易限制");
        var limited = template with { AttachType = "[trade limit]" };
        catalog.PrefillTemplates(new Dictionary<int, DfoServer.GameWorld.Items.ItemTemplate> { [50] = limited });
        foreach (byte attr in new byte[] { 0, 31, 32, 63, 255 })
        {
            var limitedItem = source with { Core = source.Core with { Attr = attr } };
            var limitedOwner = accountRole with { Inventory = accountRole.Inventory with { Items = [limitedItem] } };
            var limitedMove = InventoryMoveService.Plan(limitedOwner, accountMove, catalog, 0);
            That(limitedMove.Accepted == (attr >> 5 != 0) &&
                (!limitedMove.Accepted || limitedMove.Plan!.After.Find(accountMove.Destination) == limitedItem with { Position = accountMove.Destination }),
                "限次交易物品只在剩余次数大于0时可存账号仓库，低五位强化不冒充次数，存入不消费次数或改核心");
        }
        That(!InventoryCargoService.AllowsInsert(source.Core with { Attr = 32, TradeRestriction = 1 }, InventoryListType.AccountCargo, limited) &&
            !InventoryCargoService.AllowsInsert(source.Core with { Attr = 32, EquipmentLockId = 1 }, InventoryListType.AccountCargo, limited) &&
            !InventoryCargoService.AllowsInsert(source.Core with { Attr = 32 }, InventoryListType.AccountCargo, limited with { AccountCargoForbidden = true }),
            "限次交易资格不绕过实例限制、安全锁或账号仓库禁入");
        catalog.PrefillTemplates(new Dictionary<int, DfoServer.GameWorld.Items.ItemTemplate> { [50] = template });
        foreach (short target in new short[] { 1, 7 })
        {
            var result = InventoryMoveService.Plan(accountRole, accountMove with
                { Destination = new(InventoryListType.AccountCargo, target), DestinationItemId = target }, catalog, 0);
            That(result.Accepted && result.Plan!.After.Find(new(InventoryListType.AccountCargo, target)) is { Uid: 1 } accountItem &&
                accountItem.Core == source.Core, "账号仓库升级后的非零空格接受客户端格号且保留UID和完整核心");
        }
        That(!InventoryMoveService.Plan(accountRole, accountMove with { DestinationItemId = 2 }, catalog, 0).Accepted &&
            !InventoryMoveService.Plan(accountRole, accountMove with
                { Destination = new(InventoryListType.AccountCargo, 8), DestinationItemId = 8 }, catalog, 0).Accepted,
            "账号格号兼容不接受错误编号或超出8格容量的目标");
        var accountStored = source with { Uid = 3, Position = new(InventoryListType.AccountCargo, 1) };
        var accountWithdraw = new InventoryMoveRequest(accountStored.Position, 1, 1, source.Position, 0, 0, -1, 0, 0);
        var accountTaken = InventoryMoveService.Plan(accountRole with { Inventory = accountRole.Inventory with { Items = [accountStored] } },
            accountWithdraw, catalog, 0);
        That(accountTaken.Accepted && accountTaken.Plan!.After.Find(source.Position) is { Core.ItemId: 50, Core.Value: 1 } &&
            accountTaken.Plan.After.Find(accountStored.Position) is { Core.Value: 1 }, "账号非零格取出接受格号并正确同步拆堆数量");
        That(!InventoryMoveService.Plan(role, move with { SourceItemId = 51 }, catalog, 0).Accepted &&
            !InventoryMoveService.Plan(role, move with { DestinationItemId = 2 }, catalog, 0).Accepted &&
            !InventoryMoveService.Plan(role, move with { Destination = new(InventoryListType.PersonalCargo, 8), DestinationItemId = 8 }, catalog, 0).Accepted,
            "目标别名不放宽源模板、错误格号或容量");
        var occupied = role with { Inventory = role.Inventory with { Items = role.Inventory.Items.Add(source with { Uid = 2, Position = move.Destination }) } };
        var merged = InventoryMoveService.Plan(occupied, move, catalog, 0);
        That(merged.Accepted && merged.Plan!.After.Find(move.Destination)!.Core.Count == 3 &&
            merged.Plan.After.Find(source.Position)!.Core.Count == 1,
            "丢到同模板的已占用金库格时按格号别名并入，不覆盖原堆");
        var other = new DfoServer.GameWorld.Items.ItemTemplate(51, DfoServer.GameWorld.Items.ItemKind.Material,
            0, 1, 0, 100, 0, 0, 0, "[free]", 1, 0, "另一格材料");
        catalog.PrefillTemplates(new Dictionary<int, DfoServer.GameWorld.Items.ItemTemplate> { [50] = template, [51] = other });
        var mirror = source with { Uid = 8, Position = new(InventoryListType.Main, 3) };
        var echoRole = accountRole with { Inventory = accountRole.Inventory with { Items = [source, mirror] } };
        var echoRequest = accountMove with { Destination = new(InventoryListType.AccountCargo, 3), DestinationItemId = 50 };
        var echoResult = InventoryMoveService.Plan(echoRole, echoRequest, catalog, 0);
        That(echoResult.Accepted && echoResult.Plan!.After.Find(echoRequest.Destination) == source with { Position = echoRequest.Destination } &&
            echoResult.Plan.After.Find(mirror.Position) == mirror,
            "空账号格接受源容器同编号格模板回显，只移动真实源，保留被回显实例的UID和完整核心");
        That(InventoryMoveService.Plan(echoRole, echoRequest with { Destination = new(InventoryListType.PersonalCargo, 3) }, catalog, 0).Accepted,
            "同一原生跨容器发送器的个人仓库回显也保持兼容");
        That(!InventoryMoveService.Plan(echoRole, echoRequest with { DestinationItemId = 51 }, catalog, 0).Accepted &&
            !InventoryMoveService.Plan(echoRole, echoRequest with { SourceItemId = 51 }, catalog, 0).Accepted &&
            !InventoryMoveService.Plan(echoRole with { Inventory = echoRole.Inventory with { AccountCargo = new(Capacity: 1) } },
                echoRequest, catalog, 0).Accepted,
            "回显兼容不接受未知目标编号、错误源模板或未开放仓库格");
        var echoBlocker = new InventoryItem(10, echoRequest.Destination, new ItemCore(51, other.Kind, 4));
        var echoOccupied = InventoryMoveService.Plan(echoRole with { Inventory = echoRole.Inventory with { Items = [source, mirror, echoBlocker] } },
            echoRequest, catalog, 0);
        That(echoOccupied.Accepted && echoOccupied.Plan!.After.Find(echoBlocker.Position) == echoBlocker &&
            echoOccupied.Plan.After.Find(new(InventoryListType.AccountCargo, 0))?.Uid == source.Uid &&
            echoOccupied.Plan.After.Find(mirror.Position) == mirror,
            "目标仓库实际已占用时改投合法空格，不把源容器回显当作授权覆盖目标物品");
        var reverseEcho = InventoryMoveService.Plan(echoRole with { Inventory = echoRole.Inventory with { Items = [mirror, echoBlocker] } },
            new(new(InventoryListType.Main, 121), 0, 0, echoBlocker.Position, 50, 0, -1, 0, 0), catalog, 0);
        That(reverseEcho.Accepted && reverseEcho.Plan!.After.Find(new(InventoryListType.Main, 121))?.Uid == echoBlocker.Uid &&
            reverseEcho.Plan.After.Find(echoBlocker.Position) is null && reverseEcho.Plan.After.Find(mirror.Position) == mirror,
            "空源反向取出不会误走存入改投空格，保留数量与第三格实例且不触发空引用");
        var returning = source with { Position = new(InventoryListType.AccountCargo, 1) };
        var cargoMirror = mirror with { Position = new(InventoryListType.AccountCargo, 3), Core = new(51, other.Kind, 4) };
        var returnEcho = InventoryMoveService.Plan(accountRole with { Inventory = accountRole.Inventory with { Items = [returning, cargoMirror] } },
            new(returning.Position, 50, 2, new(InventoryListType.Main, 3), 51, 0, -1, 0, 0), catalog, 0);
        That(returnEcho.Accepted && returnEcho.Plan!.After.Find(new(InventoryListType.Main, 3))?.Uid == source.Uid &&
            returnEcho.Plan.After.Find(cargoMirror.Position) == cargoMirror,
            "取出到空Main格也按真实源容器解释回显，不误消费另一件仓库物品");
        var blocker = new InventoryItem(9, move.Destination, new ItemCore(51, other.Kind, 4));
        var blocked = role with { Inventory = role.Inventory with { Items = role.Inventory.Items.Add(blocker) } };
        var relocated = InventoryMoveService.Plan(blocked, move, catalog, 0);
        That(relocated.Accepted && relocated.Plan!.After.Find(move.Destination) == blocker &&
            relocated.Plan.After.Find(new(InventoryListType.PersonalCargo, 0))!.Core.ItemId == 50,
            "直接丢到已占用金库格时改放空格，不替换原物品");
        That(!InventoryMoveService.Plan(blocked, move with { DestinationItemId = 99 }, catalog, 0).Accepted,
            "既不是格号也不是目标模板的编号仍然拒绝");
        var stored = new InventoryItem(3, new(InventoryListType.PersonalCargo, 4), new ItemCore(50, template.Kind, 6));
        var withdraw = new InventoryMoveRequest(stored.Position, 4, 2, new(InventoryListType.Main, 65), 0, 0, -1, 0, 0);
        var taken = InventoryMoveService.Plan(role with { Inventory = new([stored]) }, withdraw, catalog, 0);
        That(taken.Accepted && taken.Plan!.After.Find(withdraw.Destination)!.Core is { ItemId: 50, Value: 2 } &&
            taken.Plan.After.Find(stored.Position)!.Core.Count == 4,
            "从个人金库取出时接受格号作为源物品编号");
        That(!InventoryMoveService.Plan(role with { Inventory = new([stored]) }, withdraw with { SourceItemId = 51 }, catalog, 0).Accepted,
            "错误的源模板不能借格号别名取出");
        var sortRequest = InventoryRequests.Sort(Convert.FromHexString("06000000100218142000"));
        That(sortRequest.List == InventoryListType.PersonalCargo && (byte)sortRequest.Kind == 20 && sortRequest.Condition == 0,
            "本版实收整理请求为个人仓库category20，非物品模板类别");
        var gear = new InventoryItem(2, new(InventoryListType.PersonalCargo, 7),
            new ItemCore(51, GameWorld.Items.ItemKind.Equipment, 1) { Rune = 3, EquipmentLockId = 7 });
        var pinned = source with { Uid = 3, Position = new(InventoryListType.PersonalCargo, 4), Core = source.Core with { SortLockFlag = 1 } };
        var expired = source with { Uid = 4, Position = new(InventoryListType.PersonalCargo, 6), Core = source.Core with { ExpireTime = 1, SortLockFlag = 1 } };
        var beforeSort = role.Inventory with { Items = [source, gear, pinned, expired] };
        var sorted = InventorySortService.Plan(beforeSort, sortRequest.Kind, 0, catalog, 2, sortRequest.List);
        That(sorted.Accepted && sorted.Plan!.After.Find(new(InventoryListType.PersonalCargo, 0)) == gear with { Position = new(InventoryListType.PersonalCargo, 0) } &&
            sorted.Plan.After.Find(pinned.Position) == pinned && sorted.Plan.After.Find(source.Position) == source && sorted.Plan.After.Items.Length == 3,
            "个人仓库整理保持安全锁装备UID及完整核心、排序锁位置和主背包，先清到期物品");
        var repeated = InventorySortService.Plan(sorted.Plan!.After, sortRequest.Kind, 0, catalog, 2, sortRequest.List);
        That(repeated.Accepted && repeated.Plan!.After.Items.SequenceEqual(sorted.Plan.After.Items), "重复整理不复制或移动已排好物品");
        var legacySort = InventorySortService.Plan(beforeSort, (GameWorld.Items.ItemKind)11, 0, catalog, 2, sortRequest.List);
        That(legacySort.Accepted && legacySort.Plan!.After.Items.SequenceEqual(sorted.Plan.After.Items), "保留既有个人仓库11入口相同行为");
        That(!InventorySortService.Plan(beforeSort, (GameWorld.Items.ItemKind)19, 0, catalog, 2, sortRequest.List).Accepted &&
            !InventorySortService.Plan(beforeSort, sortRequest.Kind, 0, catalog, 2, InventoryListType.Main).Accepted,
            "个人仓库别名不放开未知类别或主背包范围");
        That(InventorySortService.Plan(InventorySnapshot.Empty, sortRequest.Kind, 0, catalog, 2, sortRequest.List) is
            { Accepted: true, Plan.After.Items.Length: 0 }, "空个人仓库整理成功且不生成物品");
        foreach (var (level, job, allowed) in new (byte, byte, bool)[] { (9, 0, false), (10, 0, true), (20, 0, true), (21, 0, false), (10, 1, false) })
        {
            var result = service.PersonalUpgrade(role with { Level = level, Job = job }, 65);
            That(result.Accepted == allowed && role.Inventory.Find(source.Position) == source &&
                (!allowed || result.Plan!.After.CargoCapacity == 24 && result.Plan.After.Find(source.Position)!.Core.Count == 1 &&
                    result.Plan.CharacterPrecondition!.Matches(role with { Level = level, Job = job }) &&
                    !result.Plan.CharacterPrecondition.Matches(role with { Level = 21 })),
                "仓库升级校验源工具资格，拒绝不消费，合法边界仍扩容");
        }
        catalog.PrefillTemplates(new Dictionary<int, DfoServer.GameWorld.Items.ItemTemplate> { [50] = template with { UseForbidden = true } });
        That(!service.PersonalUpgrade(role, 65).Accepted, "仓库工具禁止使用标记不可绕过");
    }

    static void VerifySortRules()
    {
        var equipment = new GameWorld.Items.ItemTemplate(101, GameWorld.Items.ItemKind.Equipment, 19, 10, 10, 1, 0, 4, 1, "[free]", 1, 0, "测试装备");
        var other = equipment with { Id = 102, EquipmentSlot = 12, MinimumLevel = 20, Rarity = 6 };
        var potion = equipment with { Id = 201, Kind = GameWorld.Items.ItemKind.Consumable, StackLimit = 100, EquipmentSlot = 0, StackableType = "zeta" };
        var timed = potion with { Id = 202, StackableType = "alpha" };
        var catalog = new GameWorld.Items.ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        catalog.PrefillTemplates(new Dictionary<int, GameWorld.Items.ItemTemplate> { [101] = equipment, [102] = other, [201] = potion, [202] = timed });
        foreach (var list in new[] { InventoryListType.PersonalCargo, InventoryListType.AccountCargo })
        {
            var original = new InventoryItem[]
            {
                new(1, new(list, 7), new(101, equipment.Kind, 10) { Attr = 3 }),
                new(2, new(list, 4), new(102, other.Kind, 20)),
                new(3, new(list, 3), new(201, potion.Kind, 3)),
                new(4, new(list, 2), new(202, timed.Kind, 4) { ExpireTime = 100 })
            };
            var before = new InventorySnapshot([.. original]) { AccountCargo = new(Capacity: 8) };
            foreach (var row in new (byte Condition, long[] Order)[] { (0, [1, 2, 3, 4]),
                (1, [1, 2, 3, 4]), (2, [2, 1, 3, 4]), (3, [2, 1, 4, 3]), (4, [1, 2, 4, 3]), (255, [1, 2, 3, 4]) })
            {
                var result = InventorySortService.Plan(before, (GameWorld.Items.ItemKind)20, row.Condition, catalog, 0, list);
                var sorted = result.Plan!.After.Items.OrderBy(item => item.Position.Index).ToArray();
                That(result.Accepted && sorted.Select(item => item.Uid).SequenceEqual(row.Order) &&
                    sorted.Select(item => item.Position.Index).SequenceEqual(new short[] { 0, 1, 2, 3 }) &&
                    sorted.All(item => item.Core == original.Single(old => old.Uid == item.Uid).Core),
                    "仓库按类别分组并复用背包品质、等级、类型及期限顺序，保留完整实例 " + row.Condition);
            }
            var pinned = before with { Items = before.Items.Select(item => item.Uid == 1 ? item with { Core = item.Core with { SortLockFlag = 1 } } : item).ToImmutableArray() };
            var locked = InventorySortService.Plan(pinned, (GameWorld.Items.ItemKind)20, 2, catalog, 0, list);
            That(locked.Plan!.After.Find(new(list, 7)) == pinned.Find(new(list, 7)), "混合仓库排序保留固定排序锁格");
            var stable = InventorySortService.Plan(locked.Plan.After, (GameWorld.Items.ItemKind)20, 2, catalog, 0, list);
            That(stable.Accepted && !stable.Plan!.Changed, "相同条件重复整理为零变化");
        }
    }

    static void VerifyStepUpgrade(TownCatalog towns, ItemResources resources)
    {
        var service = new InventoryCargoService(resources.Items, resources.Cargo, resources.GoldLimits);
        foreach (int id in new[] { 10007903, 10008055, 10008120, 2660237 })
        {
            var source = new InventoryItem(1, new(InventoryListType.Main, 65),
                new ItemCore(id, GameWorld.Items.ItemKind.Consumable, 1));
            foreach (ushort capacity in new ushort[] { 8, 24, 248 })
            {
                var role = new Character(1, 0, "CargoStep", 0, towns.InitialReturn, null)
                    { Level = 60, Inventory = new([source]) { CargoCapacity = capacity } };
                var result = service.PersonalUpgrade(role, 65);
                That(result.Accepted && result.Plan!.After.CargoCapacity == capacity + 16 &&
                    result.Plan.After.Find(source.Position) is null &&
                    role.Inventory.CargoCapacity == capacity && role.Inventory.Find(source.Position) == source,
                    "逐档金库券每次提升一档并扣一件，规划不修改原快照 " + id);
            }
            foreach (ushort capacity in new ushort[] { 264, 16, 48 })
            {
                var role = new Character(1, 0, "CargoStep", 0, towns.InitialReturn, null)
                    { Level = 60, Inventory = new([source]) { CargoCapacity = capacity } };
                That(!service.PersonalUpgrade(role, 65).Accepted && role.Inventory.Find(source.Position) == source,
                    "逐档金库券满档或非法旧容量拒绝且不扣券 " + id);
            }
        }
    }

    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources itemResources)
    {
        VerifyAcquisition(towns, itemResources);
        VerifyStepUpgrade(towns, itemResources);
        int checks = Count;
        That(itemResources.Items.Get(6).PersonalCargoUpgradeTarget == 40 &&
            itemResources.Items.Get(110).PersonalCargoUpgradeTarget == 40 &&
            itemResources.Items.Get(50).PersonalCargoUpgradeTarget == 24,
            "旧银质32格声明兼容为40格，新银质40格和原24格工具保持各自目标");
        void Reject(Action action, string name)
        {
            bool rejected = false;
            try { action(); }
            catch (Exception e) when (e is SqliteException or InvalidDataException or InvalidOperationException) { rejected = true; }
            That(rejected, name);
        }
        string root = Path.Combine(Path.GetTempPath(), "s5a9-cargo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("cargo", () => { });
            var items = new InventoryService(itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
            var cargo = new InventoryCargoService(itemResources.Items, itemResources.Cargo, itemResources.GoldLimits);
            var currency = new CurrencyService(itemResources.GoldLimits);
            var current = repo.Create(owner, new("CargoOne", towns.Room, towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = characters.Get(0).Skills,
                Inventory = InventoryRewardGrantService.CreateStarter(characters.Get(0))
            });
            That(current.Inventory.CargoCapacity == 8 && current.Inventory.AccountCargo == new AccountCargoState(), "new account closed, personal eight slots");
            That(itemResources.Cargo.AccountTiers.Count == 31 && itemResources.Cargo.AccountTiers[^1].Capacity == 240,
                "matched resource covers all account tiers through 240");
            That(!cargo.AccountUpgrade(current, true).Accepted && !cargo.AccountUpgrade(current, false).Accepted, "insufficient creation fee and unopened upgrade rejected");
            current = Commit(currency.Grant(current, CurrencyKind.Gold, 20_000_000));
            That(!cargo.AccountUpgrade(current with { Level = 59 }, true).Accepted, "creation level comes from PVF");
            current = Commit(cargo.AccountUpgrade(current, true));
            That(current.Inventory.AccountCargo.Capacity == 1 && current.Inventory.Gold == 19_900_000, "create charges once and opens one slot");
            That(!cargo.AccountUpgrade(current, true).Accepted, "duplicate creation cannot charge again");
            current = Commit(cargo.AccountUpgrade(current, false));
            That(current.Inventory.AccountCargo.Capacity == 8 && current.Inventory.Gold == 17_900_000, "first expansion charges two million");
            That(!cargo.Gold(current, 0, true).Accepted && !cargo.Gold(current, -1, false).Accepted, "nonpositive transfer rejected");
            current = Commit(cargo.Gold(current, 1234, true));
            That(current.Inventory.AccountCargo.Gold == 1234 && current.Inventory.Gold == 17_898_766, "deposit conserves gold");
            current = Commit(cargo.Gold(current, 234, false));
            That(current.Inventory.AccountCargo.Gold == 1000 && current.Inventory.Gold == 17_899_000, "withdraw conserves gold");
            That(!cargo.Gold(current, 1001, false).Accepted && !cargo.Gold(current, int.MaxValue, true).Accepted, "insufficient transfer changes neither owner");
            var limitFixture = current with { Inventory = current.Inventory with { Gold = 1, AccountCargo = current.Inventory.AccountCargo with { Gold = 100_000_000 } } };
            That(!cargo.Gold(limitFixture, 1, true).Accepted, "account gold cap uses current tier");
            limitFixture = current with { Inventory = current.Inventory with { Gold = 400_000_000 } };
            That(!cargo.Gold(limitFixture, 1, false).Accepted, "withdraw cannot overflow character carry limit");
            var weapon = current.Inventory.Find(new(InventoryListType.Equipment, 12))!;
            var detailed = weapon with { Core = weapon.Core with { Attr = 9, EnchantCardId = 123, AmplifyValue = 78,
                Rune = 42, Random0 = new(1, 2, 3), GuardianGemKeys = 1234567, Extension114 = new(2, 345), SortLockFlag = 1,
                DisjointForbidden = true, DestructionSoulForbidden = true } };
            current = Commit(new(true, 0, new(current.Inventory, current.Inventory with { Items = current.Inventory.Items.Replace(weapon, detailed) }, "cargo_fixture")));
            current = Commit(Move(detailed.Position, new(InventoryListType.PersonalCargo, 7)));
            That(current.Inventory.Find(new(InventoryListType.PersonalCargo, 7)) == detailed with { Position = new(InventoryListType.PersonalCargo, 7) },
                "personal deposit preserves UID and all equipment attributes including worn sorting lock");
            current = Commit(items.Lock(current, new(InventoryListType.PersonalCargo, 7), InventoryLockOperation.EquipmentLock));
            That(!Move(new(InventoryListType.PersonalCargo, 7), new(InventoryListType.AccountCargo, 0)).Accepted, "security lock blocks cross-character storage");
            current = Commit(Move(new(InventoryListType.PersonalCargo, 7), new(InventoryListType.Main, 3)));
            That(current.Inventory.Find(new(InventoryListType.Main, 3)) is { Uid: var uid, Core.EquipmentLockId: not 0 } && uid == detailed.Uid,
                "locked equipment may leave personal cargo through quick bar");
            current = Commit(items.Lock(current, new(InventoryListType.Main, 3), InventoryLockOperation.EquipmentUnlock));
            current = Commit(Move(new(InventoryListType.Main, 3), new(InventoryListType.AccountCargo, 7)));
            var sharedItem = current.Inventory.Find(new(InventoryListType.AccountCargo, 7))!;
            That(sharedItem.Core == detailed.Core with { SortLockFlag = 0 } && Query("SELECT character_id IS NULL FROM character_inventory_items WHERE item_uid=" + detailed.Uid) is 1L,
                "account deposit changes actual database owner without cloning core or UID");
            That(!items.Lock(current, sharedItem.Position, InventoryLockOperation.EquipmentLock).Accepted, "account cargo supports sorting lock, not per-character security locks");
            current = Commit(items.Lock(current, sharedItem.Position, InventoryLockOperation.SortLock));
            current = Commit(items.Sort(current, new(InventoryListType.AccountCargo, 0, 0)));
            That(current.Inventory.Find(sharedItem.Position) is not null, "account sorting lock fixes position");
            current = Commit(items.Lock(current, sharedItem.Position, InventoryLockOperation.SortUnlock));
            current = Commit(items.Sort(current, new(InventoryListType.AccountCargo, 0, 0)));
            That(current.Inventory.Find(new(InventoryListType.AccountCargo, 0))?.Uid == sharedItem.Uid, "account sort moves complete instance");
            var oldRole = current;
            var sibling = repo.Create(owner, new("CargoTwin", towns.Room, towns.InitialReturn));
            That(sibling.Inventory.CargoCapacity == 8 && sibling.Inventory.AccountCargo == current.Inventory.AccountCargo &&
                sibling.Inventory.Find(new(InventoryListType.AccountCargo, 0))?.Uid == sharedItem.Uid, "new role loads shared storage without copying it");
            Reject(() => repo.Create(owner, new("CargoBad", towns.Room, towns.InitialReturn) { Inventory = current.Inventory }), "creation rejects caller-owned account cargo seed");
            using var other = owners.Acquire("other-cargo", () => { });
            var isolated = repo.Create(other, new("CargoOther", towns.Room, towns.InitialReturn));
            That(isolated.Inventory.AccountCargo.Capacity == 0 && isolated.Inventory.Items.IsEmpty, "another account sees no shared assets");
            current = sibling;
            current = Commit(Move(new(InventoryListType.AccountCargo, 0), new(InventoryListType.Main, 3)));
            That(current.Inventory.Find(new(InventoryListType.Main, 3))?.Uid == sharedItem.Uid &&
                Query("SELECT character_id FROM character_inventory_items WHERE item_uid=" + sharedItem.Uid) is long owned && owned == sibling.Id,
                "another role withdraws original instance and assumes ownership");
            Reject(() => InventoryService.Commit(repo, owner, oldRole, currency.Grant(oldRole, CurrencyKind.Gold, 1)), "stale account storage cannot be overwritten by another role");
            current = repo.List(owner)[0];
            current = Commit(items.Grant(current, [new(3299, 600)]));
            current = Commit(new(true, 0, new(current.Inventory, current.Inventory with
                { Items = current.Inventory.Items.Add(new(0, new(InventoryListType.Main, 65), new(50, GameWorld.Items.ItemKind.Consumable, 2))) }, "legacy_cargo_tool_fixture")));
            var material = current.Inventory.Items.Single(item => item.Core.ItemId == 3299);
            current = Commit(Move(material.Position, new(InventoryListType.AccountCargo, 1), 17));
            That(current.Inventory.Find(new(InventoryListType.AccountCargo, 1)) is { Core.Count: 17 } split && split.Uid != material.Uid,
                "cross-owner partial stack receives exactly one new UID");
            current = Commit(Move(new(InventoryListType.AccountCargo, 1), material.Position, 17));
            That(current.Inventory.Find(material.Position) is { Core.Count: 600 } merged && merged.Uid == material.Uid, "merge preserves target identity and total quantity");
            current = Commit(Move(material.Position, new(InventoryListType.Main, 122), 400));
            short toolSlot = current.Inventory.Items.Single(item => item.Core.ItemId == 50).Position.Index;
            current = Commit(cargo.PersonalUpgrade(current, toolSlot));
            That(current.Inventory.CargoCapacity == 24 && current.Inventory.Find(new(InventoryListType.Main, toolSlot))?.Core.Count == 1,
                "personal tool grants defined capacity and consumes one");
            That(!cargo.PersonalUpgrade(current, toolSlot).Accepted, "duplicate target does not consume second tool");
            current = Commit(currency.Grant(current, CurrencyKind.Cera, 1_000_000));
            VerifyAccountPurchases(current, itemResources);
            var cashShop = new InventoryCashShopService(itemResources.Items, itemResources.CashShop, itemResources.GoldLimits, itemResources.EpicPieces, itemResources.Cargo);
            var stalePurchaseRole = current;
            var stalePurchase = cashShop.Buy(current, new(0, [new(100534, 0)]), out _);
            current = Commit(currency.Grant(current, CurrencyKind.Gold, 1));
            string freshState = Snapshot();
            Reject(() => InventoryService.Commit(repo, owner, stalePurchaseRole, stalePurchase), "旧角色revision的购买计划拒绝");
            Reject(() => InventoryService.Commit(repo, owner, current, stalePurchase), "刷新expected也不能重绑旧购买资格");
            That(Snapshot() == freshState, "旧购买计划不改变费用、容量或审计");
            string beforeCashFailure = Snapshot();
            Sql("CREATE TRIGGER reject_cargo_cash BEFORE INSERT ON cargo_audit_log BEGIN SELECT RAISE(ABORT,'fixture'); END");
            Reject(() => Commit(cashShop.Buy(current, new(0, [new(100534, 0)]), out _)), "capacity failure rolls back account Cera and wallet version");
            Sql("DROP TRIGGER reject_cargo_cash");
            That(Snapshot() == beforeCashFailure, "failed paid upgrade retains balance, capacity and audits");
            foreach (var tier in itemResources.Cargo.AccountTiers.Where(tier => tier.Capacity > 8))
            {
                var before = current.Inventory;
                if (tier.ProductId == 0 && tier.MaterialCount > 0)
                {
                    var firstInput = current.Inventory.Items.Where(item => item.Position.List == InventoryListType.Main &&
                        item.Core.ItemId == tier.MaterialId).OrderBy(item => item.Position.Index).First();
                    InventoryQualificationTests.CheckInputs(current, role => cargo.AccountUpgrade(role, false),
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds(), firstInput.Position);
                }
                current = Commit(tier.ProductId > 0 ? cashShop.Buy(current, new(0, [new(tier.ProductId, 0)]), out _) : cargo.AccountUpgrade(current, false));
                That(current.Inventory.AccountCargo.Capacity == tier.Capacity && current.Inventory.Gold == before.Gold - tier.GoldCost &&
                    current.Inventory.AccountWallet.Get(CurrencyKind.Cera) == before.AccountWallet.Get(CurrencyKind.Cera) - tier.CeraCost,
                    "resource-defined upgrade and cost " + tier.Capacity);
            }
            That(!cargo.AccountUpgrade(current, false).Accepted && current.Inventory.Items.Single(item => item.Core.ItemId == 3299).Core.Count == 100,
                "maximum capacity rejects and both material tiers consumed exactly 250");
            string beforeFailure = Snapshot();
            Sql("CREATE TRIGGER reject_cargo BEFORE INSERT ON cargo_audit_log BEGIN SELECT RAISE(ABORT,'fixture'); END");
            var failed = cargo.Gold(current, 1, true);
            Reject(() => Commit(failed), "cargo failure rolls back both currencies and character/account revisions");
            material = current.Inventory.Items.Single(item => item.Core.ItemId == 3299);
            Reject(() => Commit(Move(material.Position, new(InventoryListType.AccountCargo, 0))), "cargo failure after moving core rolls back owner and bindings");
            Sql("DROP TRIGGER reject_cargo");
            That(Snapshot() == beforeFailure, "failed operations leave durable state and all audits identical");
            current = Commit(Move(material.Position, new(InventoryListType.PersonalCargo, 23)));
            That(!Move(new(InventoryListType.PersonalCargo, 23), new(InventoryListType.PersonalCargo, 24)).Accepted, "closed personal slot rejected");
            current = Commit(items.Sort(current, new(InventoryListType.PersonalCargo, (GameWorld.Items.ItemKind)20, 0)));
            That(current.Inventory.Find(new(InventoryListType.PersonalCargo, 0))?.Uid == material.Uid, "个人仓库按本版category20整理全部类别");
            var template = itemResources.Items.Get(sharedItem.Core.ItemId);
            That(!InventoryCargoService.AllowsInsert(sharedItem.Core, InventoryListType.PersonalCargo, template with { PersonalCargoForbidden = true }) &&
                !InventoryCargoService.AllowsInsert(sharedItem.Core, InventoryListType.AccountCargo, template with { AttachType = "[trade]" }), "PVF forbidden and character binding enforced");
            That(InventoryCargoService.AllowsInsert(sharedItem.Core with { SealFlag = 0 }, InventoryListType.AccountCargo, template with { AttachType = "[sealing]" }) &&
                !InventoryCargoService.AllowsInsert(sharedItem.Core with { TradeRestriction = 1 }, InventoryListType.AccountCargo, template),
                "unsealing alone does not invent a trade restriction");
            That(Convert.ToHexString(CargoPackets.Full(InventorySnapshot.Empty, InventoryListType.PersonalCargo)) == "020800000000" &&
                Convert.ToHexString(CargoPackets.Full(InventorySnapshot.Empty, InventoryListType.AccountCargo)) == "0C0000000000000000", "empty warehouse header widths");
            foreach (var inventory in new[] { InventorySnapshot.Empty, current.Inventory })
            {
                using var cargoBody = new MemoryStream(CargoPackets.Full(inventory, InventoryListType.PersonalCargo));
                using var cargoReader = new BinaryReader(cargoBody);
                That(cargoReader.ReadByte() == 2 && cargoReader.ReadUInt16() == inventory.CargoCapacity, "personal cargo header");
                int count = cargoReader.ReadUInt16();
                That(count == inventory.Items.Count(item => item.Position.List == InventoryListType.PersonalCargo), "personal cargo physical count");
                for (int index = 0; index < count; index++)
                    That(cargoReader.ReadBytes(123).Length == 123, "personal cargo complete physical entry");
                // 物品循环结束后仍须进入0267B480，空列表也不能省略该读取。
                That(cargoReader.ReadByte() == 0 && cargoBody.Position == cargoBody.Length, "personal cargo auxiliary count consumes final byte");
            }
            That(Convert.ToHexString(CargoPackets.Ack(InventoryResult.Reject(20))) == "001400" && CargoRequests.PersonalUpgrade([65, 0]) == 65,
                "Client u16 failure and upgrade tool slot");
            Reject(() => CargoRequests.Gold([1, 0, 0]), "truncated money request");
            Reject(() => CargoRequests.AccountUpgrade([0]), "nonempty account upgrade request");
            var mileageBooster = new InventoryBoosterService(itemResources.Items, itemResources.Boosters, itemResources.GoldLimits, itemResources.EpicPieces);
            current = Commit(items.Grant(current, [new(490705277, 1)]));
            short mileageSlot = current.Inventory.Items.Single(item => item.Core.ItemId == 490705277).Position.Index;
            current = Commit(mileageBooster.Open(current, new(mileageSlot, 1), out _));
            mileageSlot = current.Inventory.Items.Single(item => item.Core.ItemId == 490705285).Position.Index;
            string beforeMileageFailure = Snapshot();
            Sql("CREATE TRIGGER reject_mileage_cargo BEFORE INSERT ON cargo_audit_log BEGIN SELECT RAISE(ABORT,'fixture'); END");
            Reject(() => Commit(mileageBooster.Open(current, new(mileageSlot, 1), out _)), "积分金库券扩容末端SQL失败");
            Sql("DROP TRIGGER reject_mileage_cargo");
            That(Snapshot() == beforeMileageFailure, "积分金库券失败保留原包装、全角色资产和审计");
            var beforeMileage = current.Inventory;
            current = Commit(mileageBooster.Open(current, new(mileageSlot, 1), out _));
            That(current.Inventory.CargoCapacity == 120 && current.Inventory.Items.All(item => item.Core.ItemId is not (490705277 or 490705285 or 490705297)) &&
                current.Inventory.AccountWallet.Same(beforeMileage.AccountWallet) && current.Inventory.Gold == beforeMileage.Gold &&
                current.Inventory.AccountCargo == beforeMileage.AccountCargo, "积分金库券重新规划提交只升级个人容量并消费原包装");
            That(!mileageBooster.Open(current, new(mileageSlot, 1), out _).Accepted, "积分金库券重复槽请求无二次提交");
            var restored = new CharacterRepository(path).List(owner).Single(role => role.Id == current.Id);
            That(JsonSerializer.Serialize(restored) == JsonSerializer.Serialize(current), "restart restores both owners and cargo state");
            That(Query("PRAGMA integrity_check") is "ok" && Query("SELECT count(*) FROM pragma_foreign_key_check") is 0L, "cargo foreign keys and integrity");
            Reject(() => Sql("UPDATE character_inventory_slots SET character_id=NULL,list_type=12 WHERE item_uid=" + material.Uid), "database rejects slot/item ownership mismatch");
            var expiring = current.Inventory.Find(new(InventoryListType.PersonalCargo, 0))!;
            current = Commit(new(true, 0, new(current.Inventory, current.Inventory with
            {
                Items = current.Inventory.Items.Replace(expiring, expiring with { Core = expiring.Core with { ExpireTime = 1, SortLockFlag = 1 } })
                    .Add(new(0, new(InventoryListType.AccountCargo, 239), expiring.Core with { ExpireTime = 1, SortLockFlag = 1 }))
            }, "expiry_fixture")));
            long cargoRevision = current.Inventory.AccountCargo.Revision;
            var selection = new GameSession(repo, towns, owner);
            selection.Select(0);
            That(selection.Selected!.Inventory.Find(new(InventoryListType.PersonalCargo, 0)) is null &&
                selection.Selected.Inventory.Find(new(InventoryListType.AccountCargo, 239)) is null &&
                selection.Selected.Inventory.AccountCargo.Revision == cargoRevision + 1, "selection clears expired personal and shared cargo despite sorting locks");
            current = selection.Selected;
            That(repo.List(owner)[1].Inventory.Items.All(item => item.Position.List != InventoryListType.AccountCargo), "expired shared assets cannot reappear on sibling");
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject(() => Commit(cargo.Gold(current, 1, false)), "stale generation cannot withdraw");
            var purchaseFixture = current with { Inventory = current.Inventory with { AccountCargo = current.Inventory.AccountCargo with { Capacity = 8 } } };
            Reject(() => InventoryService.Commit(repo, owner, purchaseFixture,
                cashShop.Buy(purchaseFixture, new(0, [new(100534, 0)]), out _)), "旧owner不能提交账号购买");

            Character Commit(InventoryResult result)
            {
                That(result.Accepted, "planned operation accepted");
                return InventoryService.Commit(repo, owner, current, result);
            }
            InventoryResult Move(InventorySlot from, InventorySlot to, int count = 0) => items.Move(current, new(from,
                current.Inventory.Find(from)?.Core.ItemId ?? 0, count, to, current.Inventory.Find(to)?.Core.ItemId ?? 0, 0, -1, 0, 0));
            object? Query(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True");
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
            void Sql(string sql) => Query(sql);
            string Snapshot() => JsonSerializer.Serialize(repo.List(owner)) + Query("SELECT count(*) FROM inventory_audit_log") +
                ":" + Query("SELECT count(*) FROM currency_audit_log") + ":" + Query("SELECT count(*) FROM cargo_audit_log");
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {Count - checks} cargo assertions");
    }

    // 只验证正式仓储合同；发奖路由与716真实资源由对应资源测试验证。
    static void VerifyAccountCommon45Persistence()
    {
        string root = Path.Combine(Path.GetTempPath(), "account-common45-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("common45-account", () => { });
            var room = new Location(1, 1, 100, 100);
            var core = new ItemCore(490013716, GameWorld.Items.ItemKind.Material, 7) { Extension97 = 1234, Attr = 32 };
            var first = repo.Create(owner, new("CommonFirst", room, room)
            {
                Inventory = InventorySnapshot.Empty with { Items = [new(0, new(InventoryListType.Main, 145), core)] }
            });
            var sibling = repo.Create(owner, new("CommonSecond", room, room));
            var oldMain = first.Inventory.Find(new(InventoryListType.Main, 145))!;
            That(Query("SELECT count(*) FROM account_cargo") is 0L, "首次45提交前未预建账号仓库行");
            var sharedSlot = new InventorySlot(InventoryListType.AccountCommon, 0);
            first = repo.CommitInventory(owner, first, new(first.Inventory,
                first.Inventory with { Items = first.Inventory.Items.Add(new(0, sharedSlot, core)) }, "common45_first"));
            var shared = first.Inventory.Find(sharedSlot)!;
            That(shared.Uid > 0 && shared.Core == core && first.Inventory.AccountCargo.Revision == 1 &&
                Query("SELECT inventory_items_revision FROM account_cargo") is long itemsRevision && itemsRevision > 0 &&
                Query($"SELECT count(*) FROM character_inventory_items WHERE item_uid={shared.Uid} AND character_id IS NULL") is 1L &&
                Query($"SELECT count(*) FROM character_inventory_slots WHERE item_uid={shared.Uid} AND character_id IS NULL AND list_type=45") is 1L,
                "首个45物品同事务创建账号行，保完整核心并更新共享CAS与库存标记");
            That(first.Inventory.Find(oldMain.Position) == oldMain, "新45提交不搬迁或合并旧Main实例");
            sibling = repo.LoadCharacter(owner, sibling.Slot);
            That(sibling.Inventory.Find(sharedSlot) == shared && sibling.Inventory.AccountCargo == first.Inventory.AccountCargo,
                "同账号另一角色加载相同45实例UID、完整核心及共享revision");
            var stale = sibling;
            var changed = shared with { Core = core with { Value = 6 } };
            first = repo.CommitInventory(owner, first, new(first.Inventory,
                first.Inventory with { Items = first.Inventory.Items.Replace(shared, changed) }, "common45_consume"));
            string stable = Snapshot();
            DfoServer.Testing.TestCheck.Reject<InvalidOperationException>(() => repo.CommitInventory(owner, stale,
                new(stale.Inventory, stale.Inventory with { Items = stale.Inventory.Items.Replace(shared, shared with { Core = core with { Value = 5 } }) }, "common45_stale")),
                "另一角色旧共享版本不能覆盖45已提交的数量");
            That(Snapshot() == stable, "45旧版本拒绝不改变资产、角色或任何审计");
            first = repo.CommitInventory(owner, first, new(first.Inventory, first.Inventory with
            {
                AccountCargo = first.Inventory.AccountCargo with { Capacity = 8 },
                Items = first.Inventory.Items.Add(new(0, new(InventoryListType.AccountCargo, 0), new(3175, GameWorld.Items.ItemKind.Material, 3)))
            }, "common45_cargo12"));
            That(first.Inventory.Find(sharedSlot) == changed && first.Inventory.Find(new(InventoryListType.AccountCargo, 0)) is not null,
                "12和45的同号槽在同账号持久化后同时存在");
            Sql("CREATE TRIGGER fail_common45 BEFORE INSERT ON cargo_audit_log BEGIN SELECT RAISE(FAIL,'common45 late failure'); END");
            string before = Snapshot();
            var failing = first.Inventory with
            {
                Gold = first.Inventory.Gold + 1,
                Items = first.Inventory.Items.Replace(changed, changed with { Core = changed.Core with { Value = 4 } })
                    .Add(new(0, new(InventoryListType.AccountCommon, 1), core))
            };
            DfoServer.Testing.TestCheck.Reject<SqliteException>(() => repo.CommitInventory(owner, first,
                new(first.Inventory, failing, "common45_rollback")), "45末段仓库审计失败整笔拒绝");
            That(Snapshot() == before, "45末段失败回滚两角色、共享数量、新UID、自增、金币、库存标记及全部审计");
            Sql("DROP TRIGGER fail_common45");
            var reopened = new CharacterRepository(path);
            That(JsonSerializer.Serialize(reopened.List(owner)) == JsonSerializer.Serialize(repo.List(owner)) &&
                reopened.LoadCharacter(owner, sibling.Slot).Inventory.Find(sharedSlot) == changed &&
                reopened.LoadCharacter(owner, first.Slot).Inventory.Find(oldMain.Position) == oldMain,
                "仓储重开恢复共享45、12及旧Main原实例，不依赖内存快照");

            object? Query(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True");
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
            void Sql(string sql) => Query(sql);
            string Snapshot() => JsonSerializer.Serialize(repo.List(owner)) + ":" +
                Query("SELECT group_concat(item_uid||':'||COALESCE(character_id,0)||':'||item_core,'|') FROM (SELECT * FROM character_inventory_items ORDER BY item_uid)") + ":" +
                Query("SELECT group_concat(account||':'||revision||':'||inventory_items_revision,'|') FROM account_cargo") + ":" +
                Query("SELECT group_concat(id||':'||revision||':'||inventory_items_revision,'|') FROM (SELECT * FROM characters ORDER BY id)") + ":" +
                Query("SELECT count(*) FROM inventory_audit_log") + ":" + Query("SELECT count(*) FROM currency_audit_log") + ":" +
                Query("SELECT count(*) FROM cargo_audit_log") + ":" +
                Query("SELECT group_concat(name||':'||seq,'|') FROM (SELECT * FROM sqlite_sequence ORDER BY name)");
        }
        finally { TestFiles.DeleteDirectory(root, true); }
    }

    static void VerifyAccountPurchases(Character source, ItemResources resources)
    {
        // 匹配资源独立核对的31档矩阵，商品号与历史工具模板号分别断言。
        (ushort From, ushort To, int Product, int Tool, int Price)[] expected =
        [
            (0, 1, -1, 0, 0), (1, 8, -1, 0, 0), (8, 16, 100534, 2681921, 2000),
            (16, 24, -1, 0, 0), (24, 32, -1, 0, 0), (32, 40, 102045, 2682968, 2000),
            (40, 48, 102108, 2683046, 2000), (48, 56, 102316, 2683324, 2500), (56, 64, 102659, 2683926, 2500),
            (64, 72, 103665, 490700152, 3000), (72, 80, 103666, 490700153, 3000),
            (80, 88, 103667, 490700154, 4000), (88, 96, 103668, 490700155, 4000),
            (96, 104, 103669, 490700156, 5000), (104, 112, 103670, 490700157, 5000),
            (112, 120, 103671, 490700158, 6000), (120, 128, 104893, 490702625, 6000),
            (128, 136, 104894, 490702626, 7000), (136, 144, 104895, 490702627, 7000),
            (144, 152, 104896, 490702628, 8000), (152, 160, 104897, 490702629, 8000),
            (160, 168, 104898, 490702630, 9000), (168, 176, 104899, 490702631, 9000),
            (176, 184, 104900, 490702632, 10000), (184, 192, 104901, 490702633, 10000),
            (192, 200, 104902, 490702634, 11000), (200, 208, 104903, 490702635, 11000),
            (208, 216, 104904, 490702636, 12000), (216, 224, 104905, 490702637, 12000),
            (224, 232, 104906, 490702638, 13000), (232, 240, 104907, 490702639, 13000)
        ];
        That(resources.Cargo.AccountTiers.Count == expected.Length, "账号金库资源不丢失任何档位");
        foreach (var row in expected)
        {
            var tier = resources.Cargo.Next(row.From);
            That(tier is not null && tier.Capacity == row.To && tier.ProductId == row.Product && tier.ToolItemId == row.Tool && tier.CeraCost == row.Price,
                "31档真实商品、模板和点券矩阵 " + row.To);
        }
        var shop = new InventoryCashShopService(resources.Items, resources.CashShop, resources.GoldLimits, resources.EpicPieces, resources.Cargo);
        var cargo = new InventoryCargoService(resources.Items, resources.Cargo, resources.GoldLimits);
        var role = source with { Inventory = source.Inventory with { Items = [], AccountCargo = source.Inventory.AccountCargo with { Capacity = 8 } } };
        InventoryResult Buy(Character value, params int[] products) => shop.Buy(value, new(0, products.Select(id => new CashShopPurchaseItem(id, 0)).ToArray()), out _);
        That(!cargo.AccountUpgrade(role, false).Accepted, "裸306不能无商品凭据扣付费下一档");
        foreach (var products in new[] { new[] { 102045 }, new[] { 100534, 100534 }, new[] { 100534, int.MaxValue }, new[] { 100534, 102045 } })
            That(!Buy(role, products).Accepted, "未来档、重复商品、后续无效商品及跨材料档混单整笔拒绝");
        var poor = role with { Inventory = role.Inventory with { AccountWallet = role.Inventory.AccountWallet.Set(CurrencyKind.Cera, 1999) } };
        That(!Buy(poor, 100534).Accepted, "点券不足不扩容不发工具");
        var tool = new InventoryItem(100000, new(InventoryListType.Main, 3), new(2681921, GameWorld.Items.ItemKind.Consumable, 2));
        var historical = poor with { Level = 1, Inventory = poor.Inventory with { Items = [tool] } };
        var redeemed = Buy(historical, 100534);
        That(redeemed.Accepted && redeemed.Plan!.After.AccountCargo.Capacity == 16 &&
            redeemed.Plan.After.AccountWallet == historical.Inventory.AccountWallet && redeemed.Plan.After.Find(tool.Position)?.Core.Count == 1,
            "历史合法工具在商城主动购买兑换一张免二次点券，后续升级不增加开通等级门槛");
        var direct = cargo.AccountUpgrade(historical, false);
        That(direct.Accepted && direct.Plan!.After.AccountCargo.Capacity == 16 && direct.Plan.After.Find(tool.Position)?.Core.Count == 1 &&
            direct.Plan.After.AccountWallet == historical.Inventory.AccountWallet, "306也仅兑一张对应预付工具");
        foreach (var invalid in new[]
        {
            tool with { Core = tool.Core with { ItemId = 2682968 } },
            tool with { Core = tool.Core with { Kind = GameWorld.Items.ItemKind.Material } },
            tool with { Core = tool.Core with { EquipmentLockId = 1 } },
            tool with { Core = tool.Core with { ExpireTime = 1 } },
            tool with { Position = new(InventoryListType.PersonalCargo, 0) }
        })
        {
            var value = historical with { Inventory = historical.Inventory with { Items = [invalid] } };
            That(!Buy(value, 100534).Accepted && !cargo.AccountUpgrade(value, false).Accepted && value.Inventory.Items.Single() == invalid,
                "未来档、锁、过期及其他容器工具不充当凭据，余额不足零变化");
        }
        var passed = role with { Inventory = role.Inventory with { AccountCargo = role.Inventory.AccountCargo with { Capacity = 40 }, Items = [tool] } };
        That(!cargo.AccountUpgrade(passed, false).Accepted, "商城成功后跟随306不能推进下一付费档或消费旧档工具");
        That(!Buy(passed, 100534).Accepted && !Buy(role with { Inventory = role.Inventory with { AccountCargo = role.Inventory.AccountCargo with { Capacity = 240 } } }, 104907).Accepted,
            "已过档与满级拒绝重复收费");
        var chain = Buy(role with { Inventory = role.Inventory with { AccountCargo = role.Inventory.AccountCargo with { Capacity = 32 } } }, 102045, 102108);
        That(chain.Accepted && chain.Plan!.After.AccountCargo.Capacity == 48 && chain.Plan.After.AccountWallet.Get(CurrencyKind.Cera) == role.Inventory.AccountWallet.Get(CurrencyKind.Cera) - 4000 &&
            chain.Plan.After.Items.IsEmpty, "连续合法付费档按计划状态推进且各扣一次，不发主工具");
        var mixedCatalog = GameWorld.Items.CashShopCatalog.Parse("[item etc]\n100534 2681921 1 0 0 2000 0 0 0\n700001 1108 1 0 0 7 0 0 0",
            "[bonus item]\n100534 3299 2\n[/bonus item]");
        var mixedShop = new InventoryCashShopService(resources.Items, mixedCatalog, resources.GoldLimits, resources.EpicPieces, resources.Cargo);
        var mixed = mixedShop.Buy(role, new(0, [new(100534, 0), new(700001, 0)]), out var mixedReceipt);
        That(mixed.Accepted && mixed.Plan!.After.AccountCargo.Capacity == 16 &&
            mixed.Plan.After.AccountWallet.Get(CurrencyKind.Cera) == role.Inventory.AccountWallet.Get(CurrencyKind.Cera) - 2007 &&
            mixed.Plan.After.Items.Single(item => item.Core.ItemId == 3299).Core.Count == 2 &&
            mixed.Plan.After.Items.Single(item => item.Core.ItemId == 1108).Core.Count == 1 && mixed.Plan.After.Items.Length == 2 &&
            mixedReceipt!.Bonuses[100534].Single().ItemId == 3299, "账号直扩保留真实附赠及同车普通商品，合并扣费不吞奖励");
        var historicalBonus = mixedShop.Buy(historical, new(0, [new(100534, 0)]), out _);
        That(historicalBonus.Accepted && historicalBonus.Plan!.After.AccountWallet == historical.Inventory.AccountWallet &&
            historicalBonus.Plan.After.Items.Single(item => item.Core.ItemId == 3299).Core.Count == 2,
            "历史工具免点券仍完整规划商品附赠");
        var full = role with { Inventory = role.Inventory with { Items = Enumerable.Range(0, 56).Select(index =>
            new InventoryItem(index + 1, new(InventoryListType.Main, checked((short)(index + 65))), new(1108, GameWorld.Items.ItemKind.Consumable, 1))).ToImmutableArray() } };
        var fullPurchase = Buy(full, 100534);
        That(fullPurchase.Accepted && fullPurchase.Plan!.After.AccountCargo.Capacity == 16 && fullPurchase.Plan.OverflowItems.IsEmpty &&
            fullPurchase.Plan.After.Items.SequenceEqual(full.Inventory.Items), "账号商城扩容不依赖Main空格，不另发工具或溢出邮件");
        var expires = tool with { Core = tool.Core with { ExpireTime = checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120) } };
        var expiring = historical with { Inventory = historical.Inventory with { Items = [expires] } };
        var expiringPlan = Buy(expiring, 100534).Plan!;
        That(expiringPlan.CharacterPrecondition!.Matches(expiring, expires.ExpireTime - 1) &&
            !expiringPlan.CharacterPrecondition.Matches(expiring, expires.ExpireTime), "历史券期限在提交时复验");
    }
}
