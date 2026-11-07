using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ActionConsumableTests
{
    public static void RunCore()
    {
        var item = new ItemTemplate(9000,ItemKind.Consumable,0,1,0,100,0,0,0,"[trade]",ushort.MaxValue,0,"action");
        var raw = new StackableFile(9000,"action.stk",new("[waste]",[0]),100,null,null,1,null,[])
        { ActionDeclaration = new("[rent point]",[50]), HasUseActionPacket=true,DeclaredUseActionPacket=1,
            HasActionUsablePlace=true,DeclaredActionUsablePlaces=["[village]"] };
        var file = new RecoveryConsumableFile(9000,"action.stk",raw,1000,1,null,null,["cool time","total usable count"],[])
            { TotalUsableCount=1 };
        var definition = ActionConsumableDefinition.Parse(file,item)!;
        That(definition is { Label:"[rent point]", Parameters:[50], DailyLimit:1, CoolTime:1000 },
            "动作消费只接现有业务且参数完整的资源，日次数和冷却不硬编码到物品ID");
        That(ActionConsumableDefinition.Parse(file with { Stackable = raw with { ActionDeclaration = new("[add mileage]",[50]) } }, item)
            is { Label: "[add mileage]", Parameters: [50] }, "积分道具按面额注册独立账号货币，不当作点券或租赁点");
        var book = raw with { ActionDeclaration = new("[story book]", [3108]), HasUseActionPacket = false, DeclaredUseActionPacket = null };
        That(ActionConsumableDefinition.Parse(file with { Stackable = book }, item) is { Label: "[story book]", Parameters: [3108] } &&
            new[] { Array.Empty<int>(), new[] { 0 }, new[] { 30000 }, new[] { 3108, 1 } }.All(args =>
                ActionConsumableDefinition.Parse(file with { Stackable = book with { ActionDeclaration = new("[story book]", args) } }, item) is null),
            "故事簿复用动作消费定义，只接受一个合法任务号，允许原生默认515入口");
        var ridingRaw = raw with
        {
            ActionDeclaration = new("[riding]", [376, 60000]),
            DeclaredActionUsablePlaces = ["[dungeon]", "[dont use exercise]", "[dont use assult]"]
        };
        var ridingFile = file with
        {
            Stackable = ridingRaw,
            CoolTime = 180000,
            CoolGroup = 10,
            TotalUsableCount = null,
            DeclaredFields = ["cool time", "cooltime group"],
            LimitDungeons = [114, 115, 140],
            ImpossibleContents = ["raid dungeon"]
        };
        var riding = ActionConsumableDefinition.Parse(ridingFile, item)!;
        That(riding is { Label: "[riding]", Parameters: [376, 60000], CoolTime: 180000, CoolGroup: 10, RaidUseForbidden: true } &&
            riding.AllowsDungeon(100) && !riding.AllowsDungeon(114) && !riding.AllowsDungeon(140) && !riding.AllowsDungeon(100, inRaid: true) &&
            ActionConsumableDefinition.Parse(ridingFile with
            {
                Stackable = ridingRaw with { HasUseActionPacket = false, DeclaredUseActionPacket = null },
                ActionUsableDungeons = [60, 61],
                DeclaredFields = ["cool time", "cooltime group", "action usable dungeon"]
            }, item) is { Label: "[riding]" } restrictedRiding &&
            restrictedRiding.AllowsDungeon(60) && !restrictedRiding.AllowsDungeon(62) &&
            new[] { Array.Empty<int>(), new[] { 376 }, new[] { 0, 60000 }, new[] { 376, 0 }, new[] { 376, 60000, 1 } }.All(args =>
                ActionConsumableDefinition.Parse(ridingFile with { Stackable = ridingRaw with { ActionDeclaration = new("[riding]", args) } }, item) is null),
            "骑乘变身药水按[riding]双正整数参数、副本白名单/禁用副本与团本限制解析，支持默认或显式use action packet");
        var clock = new ConsumableUseSessionTests.FakeClock();
        var slot = new InventorySlot(InventoryListType.Main,65);
        var source = new InventoryItem(77,slot,new(9000,ItemKind.Consumable,2));
        var character = new Character(1,0,"Action",1,new(1,0,1,1),null) { Inventory = new([source]) };
        var clientQualified = definition with { Item = item with { Jobs = 1 << 3 } };
        That(InventoryActionConsumableService.Plan(character,slot,clientQualified,"[village]",clock).Accepted &&
            !clientQualified.Item.AllowsUse(character.Level,character.Job),
            "源STK不复验usable job，装备适配查询仍保留职业匹配");
        That(!InventoryActionConsumableService.Plan(character,slot,clientQualified with
            { Item = clientQualified.Item with { UseForbidden = true } },"[village]",clock).Accepted &&
            !InventoryActionConsumableService.Plan(character,slot,clientQualified with
            { Item = clientQualified.Item with { MinimumLevel = 2 } },"[village]",clock).Accepted &&
            !InventoryActionConsumableService.Plan(character,slot,clientQualified with
            { Item = clientQualified.Item with { MaximumLevel = 0 } },"[village]",clock).Accepted,
            "委派职业资格不放宽禁用及源STK等级范围");
        var paid = InventoryActionConsumableService.Plan(character,slot,definition,"[village]",clock);
        That(paid.Accepted && paid.Plan!.After.Find(slot)!.Core.Count == 1 && paid.Plan.After.ConsumableStates.Length == 1 &&
            paid.Plan.After.DailyBoosterUses.Length == 1 && character.Inventory.Find(slot)!.Core.Count == 2,
            "只读规划同时扣一件、保存冷却与日次数，不修改原快照");
        That(!InventoryActionConsumableService.Plan(character with { Inventory = paid.Plan!.After },slot,definition,"[village]",clock).Accepted &&
            InventoryActionConsumableService.Plan(character,slot,definition,"[dungeon]",clock).Accepted,
            "重复冷却仍拒绝；资产规划不再另设地点阻止，地点只由统一Session门禁检查");
        var crossed = ConsumableStatePolicy.CrossBoundary(paid.Plan.After,ConsumableMaintenance.Reselect,clock.GetUtcNow().ToUnixTimeMilliseconds());
        That(crossed.ConsumableStates.IsEmpty && crossed.DailyBoosterUses.Length == 1,
            "不保留的会话冷却在重选清理，但日额度不能被重选重置");
        var effect = definition with { Label="[creature effect]", CoolTime=0, DailyLimit=0, EffectMilliseconds=600000 };
        var active = InventoryActionConsumableService.Plan(character,slot,effect,"[village]",clock);
        That(active.Accepted && active.Plan!.After.ConsumableStates is [{ Kind:ConsumableStateKind.Effect }] &&
            active.Plan.After.ConsumableStates[0].UntilMilliseconds == clock.GetUtcNow().ToUnixTimeMilliseconds()+600000,
            "宠物特效按PVF毫秒期限写已有持续账本，不另建宠物在线计时状态");
        var extensionRaw = raw with { ActionDeclaration = new("[extend item period]", [0, 1]),
            HasUseActionPacket = false, DeclaredUseActionPacket = 0, DeclaredActionLimitIndexes = [9100], DeclaredActionResultLimitIndexes = [] };
        var extensionFile = file with { Stackable = extensionRaw };
        var extension = ActionConsumableDefinition.Parse(extensionFile, item)!;
        That(extension is { Label: "[extend item period]", Parameters: [0, 1] } && extension.AllowedTargets.SetEquals([9100]),
            "延期读取普通装备模式、天数与目标白名单，不把首参数0当永久");
        foreach (var invalid in new[] { extensionRaw with { DeclaredActionLimitIndexes = [] },
            extensionRaw with { ActionDeclaration = new("[extend item period]", [1, 1]) },
            extensionRaw with { ActionDeclaration = new("[extend item period]", [0, 0]) } })
            That(ActionConsumableDefinition.Parse(extensionFile with { Stackable = invalid }, item) is null,
                "延期缺失白名单、未知模式和非正天数保持拒绝");
        var catalog = new ItemCatalog(null!, new Dictionary<int,string>(), new Dictionary<int,string>(), 0);
        var equipment = new ItemTemplate(9100, ItemKind.Equipment, 14, 95, 20, 1, 0, 2, 60, "[trade]", ushort.MaxValue, 0, "period")
            { Expiration = new(Days: 7) };
        catalog.PrefillTemplates(new Dictionary<int,ItemTemplate> { [9100] = equipment });
        var target = new InventoryItem(78, new(InventoryListType.Main, 9), new(9100,ItemKind.Equipment,123456)
            { Attr=11, EnchantCardId=1234, Random0=new(1,2,3), Extension27=888,
                ExpireTime=(int)clock.GetUtcNow().ToUnixTimeSeconds()+7*86400 });
        var role = character with { Inventory = new([source,target]) };
        var service = new InventoryPeriodExtensionService(catalog,time:clock);
        var extended = service.Plan(role,slot,target.Position,extension,true);
        That(extended.Accepted && extended.Plan!.After.Find(slot)!.Core.Count==1 &&
            extended.Plan.After.Find(target.Position)==target with { Core=target.Core with { ExpireTime=target.ExpireTime+86400 } } &&
            role.Inventory.Find(target.Position)==target && extended.Plan.After.ConsumableStates.Length==1 && extended.Plan.After.DailyBoosterUses.Length==1,
            "从原截止增加一天，源消费、冷却和日次数同计划，保留目标UID与完整属性；不额外要求角色达到目标穿戴等级");
        foreach (var rejected in new[] { target with { Core=target.Core with { ExpireTime=0 } },
            target with { Core=target.Core with { ExpireTime=(int)clock.GetUtcNow().ToUnixTimeSeconds() } },
            target with { Core=target.Core with { ItemId=9101 } }, target with { Core=target.Core with { EquipmentLockId=1 } } })
            That(!service.Plan(role with { Inventory=new([source,rejected]) },slot,target.Position,extension,true).Accepted,
                "永久、已到期、非白名单或安全锁目标不消费延期券");
        foreach (var periodic in new[] { equipment.Expiration with { DailyDelete = 1 },
            equipment.Expiration with { WeeklyDeleteDay = 0 }, equipment.Expiration with { WeeklyDeleteDay = 6 } })
        {
            catalog.PrefillTemplates(new Dictionary<int,ItemTemplate> { [9100] = equipment with { Expiration = periodic } });
            That(!service.Plan(role,slot,target.Position,extension,true).Accepted &&
                role.Inventory.Find(slot) == source && role.Inventory.Find(target.Position) == target,
                "延期不能越过每日或每周删除，周日0同样拒绝且不扣源/改目标");
        }
        catalog.PrefillTemplates(new Dictionary<int,ItemTemplate> { [9100] = equipment });
        var nearLimit = target with { Core=target.Core with { ExpireTime=ItemExpiration.MaximumUnixTime-10 } };
        That(service.Plan(role with { Inventory=new([source,nearLimit]) },slot,target.Position,
                extension with { Parameters=[0,int.MaxValue] },true).Plan!.After.Find(target.Position)!.ExpireTime==ItemExpiration.MaximumUnixTime,
            "延期越过2037安全截止时夹到上限，天数乘法不溢出也不拒绝正常超期请求");
        var expiringSource = source with { Core=source.Core with { ExpireTime=(int)clock.GetUtcNow().ToUnixTimeSeconds()+2 } };
        var expiringRole = role with { Inventory=new([expiringSource,target]) };
        var pending = service.Plan(expiringRole,slot,target.Position,extension,true);
        That(pending.Accepted && !pending.Plan!.CharacterPrecondition!.Matches(expiringRole,expiringSource.ExpireTime),
            "延期规划仍以源券、目标和日次数重置的最早期限复验，不能越期提交");
        Console.WriteLine("PASS 动作消费源、冷却、日次数、宠物特效及装备延期核心断言");
    }
}
