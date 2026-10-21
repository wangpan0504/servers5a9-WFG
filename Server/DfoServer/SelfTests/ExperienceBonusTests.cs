using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Currency;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ExperienceBonusTests
{
    public static void RunCore()
    {
        var rules = ExperienceBonusCatalog.Parse("[type] 56 [exp] 5 [item] 1 [exp] 99 [/item] [type] 128 [exp] 20");
        That(rules == new ExperienceBonusCatalog(5, 20), "黑钻与网吧各读本版父exp，不误读子物品或其他版本的10%");
        That(ExperienceBonusCatalog.Parse("[type] 56 [term] 1").BlackDiamondPercent == 0, "缺经验权益不按名字赠送默认比例");
        Throws(() => ExperienceBonusCatalog.Parse("[type] 56 [exp] -1"));
        That(ItemCatalog.ReadExperiencePercent(new PvfDocument("[exp advantage]\n12.5")) == 12.5m &&
            ItemCatalog.ReadExperiencePercent(new PvfDocument("[exp advantage]\n-2")) == 0, "装备比例保留小数，非正声明不发奖励");

        var all = Enum.GetValues<ExperienceBonusSource>().Select(source => new ExperienceBonusRate(source, 19));
        var combined = ExperienceBonuses.Calculate(100, 109, all);
        That(combined.Parts.All(part => part.Amount == 20) && combined.Total == 100 + 20 * combined.Parts.Length,
            "所有来源均独立向下取整，不互相复乘");
        That(combined.Applied(121).Extra == 21 && combined.Applied(99).Extra == 0 &&
            combined.Applied(99).Ordinary == 99, "经验封顶按实际额度分配，分项总和不超到账");
        That(combined.Scale(3).Total == combined.Total * 3, "批量任务按一次奖励分项等比例扩展，不重算资格");
        Throws(() => ExperienceBonuses.Calculate(1, 1, [new(ExperienceBonusSource.Guild, -1)]));
        Throws(() => ExperienceBonuses.Calculate(1, 1, [new(ExperienceBonusSource.Guild, 1), new(ExperienceBonusSource.Guild, 2)]));
        Throws(() => ExperienceBonuses.Calculate(uint.MaxValue, uint.MaxValue, [new(ExperienceBonusSource.Event, 100)]));

        var items = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        ItemTemplate Template(int id, ItemKind kind, byte slot, decimal percent) => new(id, kind, slot, 1, 10, 1, 1, 1, 1,
            "[free]", ushort.MaxValue, 0, "fixture") { ExperiencePercent = percent };
        items.PrefillTemplates(new Dictionary<int, ItemTemplate>
        {
            [1] = Template(1, ItemKind.Equipment, 12, 10),
            [2] = Template(2, ItemKind.Avatar, 0, 5),
            [3] = Template(3, ItemKind.Creature, 26, 2),
            [4] = Template(4, ItemKind.Equipment, 14, 30) with
                { ExperienceForbiddenModules = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "dead tower") },
            [5] = Template(5, ItemKind.Creature, 26, 50) with { CreatureEgg = true }
        });
        var recovery = new RecoveryConsumableCatalog([new(10, 1, 1, 1) { QuestExpBonusPermille = 500 },
            new(11, 1, 1, 1) { QuestExpBonusPermille = 1000 }]);
        var service = new ExperienceBonusService(items, recovery, rules);
        var character = new Character(1, 0, "Bonus", 0, new(1,0,10,10), null)
        {
            Inventory = InventorySnapshot.Empty with
            {
                AccountWallet = AccountWallet.Empty.SetContractExpiry(56, 2000).SetContractExpiry(84, 2000),
                Items = [new(1, new(InventoryListType.Equipment,12), new(1,ItemKind.Equipment,1)),
                    new(2, new(InventoryListType.Equipment,0), new(2,ItemKind.Avatar,2)) { Avatar = new() },
                    new(3, new(InventoryListType.Equipment,26), new(3,ItemKind.Creature,3)) { Creature = new() },
                    new(4, new(InventoryListType.Main,65), new(4,ItemKind.Equipment,1))],
                ConsumableStates = [new(ConsumableStateKind.Effect,10,2000000,7,1),new(ConsumableStateKind.Effect,11,2000000,7,1)]
            },
            ExpBonusUntil = 2000, ExpBonusPermille = 1000
        };
        var growth = new GrowthContractBenefits(20,0,[0,0,0,0]);
        ExperienceBonusRequest Request(ExperienceEvent kind, long now = 1000) => new(character,kind,3,now,true,"dungeon");
        var kill = service.Plan(Request(ExperienceEvent.Kill),100,0,growth);
        That(kill.Total == 237 && kill.Amount(ExperienceBonusSource.Equipment) == 17 &&
            kill.Amount(ExperienceBonusSource.BlackDiamond) == 0, "击杀只叠当前穿戴17%、契约20%和秘药100%，背包与通关权益不混入");
        var clear = service.Plan(Request(ExperienceEvent.Clear),100,20,growth);
        That(clear.Total == 292 && clear.Amount(ExperienceBonusSource.BlackDiamond) == 5 &&
            clear.Amount(ExperienceBonusSource.InternetCafe) == 20 && clear.Amount(ExperienceBonusSource.Avatar) == 2 &&
            clear.Amount(ExperienceBonusSource.Creature) == 5 && clear.Amount(ExperienceBonusSource.Potion) == 120,
            "通关按基础分别计算会员、时装、宠物与契约，保留本版秘药含评分的计算基数");
        var quest = service.Plan(Request(ExperienceEvent.Quest),100,0,growth);
        That(quest.Total == 220 && quest.Amount(ExperienceBonusSource.QuestPotion) == 100 &&
            quest.Amount(ExperienceBonusSource.Potion) == 0, "任务药与普通秘药独立，同族取有效最大值，任务不吃通关会员权益");
        var expired = service.Plan(Request(ExperienceEvent.Quest,2000),100,0,growth);
        That(expired.Total == 100, "药剂及契约到期等号不奖励");
        var noAvatarPet = character with { Inventory = character.Inventory with { Items = [] } };
        That(service.Plan(Request(ExperienceEvent.Clear) with { Character = noAvatarPet },100,0,null)
            .Amount(ExperienceBonusSource.Creature) == 0, "没有真实穿戴宠物不因模板或账号拥有而赠奖励");
        var external = new Provider { Context = new() { PartyMembers = 4, PartyHasAvatar = true,
            Rates = [new(ExperienceBonusSource.Party,25),new(ExperienceBonusSource.Guild,10),new(ExperienceBonusSource.Adventure,10),
                new(ExperienceBonusSource.FatigueBurning,100),new(ExperienceBonusSource.FatigueBuff,5),
                new(ExperienceBonusSource.SeriaBlessing,50),new(ExperienceBonusSource.SeriaWelcome,5),
                new(ExperienceBonusSource.FastGrowth,10),new(ExperienceBonusSource.Event,20),new(ExperienceBonusSource.Charm,3)] } };
        var extended = new ExperienceBonusService(items,recovery,rules,external);
        var frozen = extended.Plan(Request(ExperienceEvent.Clear),100,0,null);
        external.Context = ExperienceBonusContext.Empty;
        That(frozen.Amount(ExperienceBonusSource.Avatar) == 5 && frozen.Amount(ExperienceBonusSource.Guild) == 10 &&
            frozen.Display(frozen.Total).FatigueBurningExperience == 100 &&
            extended.Plan(Request(ExperienceEvent.Clear),100,0,null).Amount(ExperienceBonusSource.Guild) == 0,
            "未来系统提供冻结资格，各来源已可计算与投影；资格移除只影响下一奖励");
        external.Context = new() { Rates = [new(ExperienceBonusSource.GrowthContract, 999)] };
        Throws(() => extended.Plan(Request(ExperienceEvent.Kill),100,0,growth));
        DungeonSettlementTests.RunExperienceBonusCore();
        Console.WriteLine("PASS experience bonus rules and frozen integration contracts");
    }

    static void Throws(Action action)
    {
        try { action(); } catch (Exception error) when (error is InvalidDataException or OverflowException)
        { That(true,"无效经验来源或溢出拒绝"); return; }
        That(false,"无效经验来源必须拒绝");
    }
    sealed class Provider : IExperienceBonusProvider
    {
        public ExperienceBonusContext Context { get; set; } = ExperienceBonusContext.Empty;
        public ExperienceBonusContext Capture(ExperienceBonusRequest request) => Context;
    }
}
