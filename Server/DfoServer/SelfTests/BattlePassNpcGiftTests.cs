using DfoServer.Game.BattlePass;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassNpcGiftTests
{
    public static void RunCore()
    {
        int checks = Count;
        var at = new DateTimeOffset(2026, 10, 29, 7, 0, 0, TimeSpan.FromHours(8));
        var source = new InventoryItem(11, new(InventoryListType.Main, 177), new(1001, ItemKind.Quest, 2) { Marker16 = 319 });
        var spare = source with { Uid = 13, Position = new(InventoryListType.Main, 178) };
        var before = new InventorySnapshot([source, spare], Gold: 1000);
        var paid = before with { Items = [source with { Core = source.Core with { Value = 1 } }, spare] };
        var updated = new NpcFavorPoint(17, 100) { GiftDay = ShopPurchaseCount.Today(at), GiftCount = 1 };
        var after = paid with { NpcFavor = [updated] };
        var gift = new NpcFavorGift(-1, 1001, 1, 100, 100);
        var request = new NpcFavorRequest(2, 17, 0, 177);
        var fact = new NpcGiftFact(Guid.NewGuid(), at, TimeProvider.System, request, gift, 17, 40000, 5,
            source, null, null, updated, before, paid, after);
        var plan = new InventoryPlan(before, after, "npc_favor_gift") { NpcGift = fact };
        That(fact.Matches(plan, after), "原实体礼物精确UID/完整core扣一与好感日期次数匹配");
        That(ReferenceEquals(plan.WithAfter(after).NpcGift, fact), "WithAfter保原赠礼事实与已抽点数");
        That(!(fact with { Id = Guid.Empty }).Matches(plan, after), "空赠礼来源拒绝");
        That(!(fact with { Request = request with { Operation = 1 } }).Matches(plan, after), "染色操作不能计为赠礼");
        That(!(fact with { Source = spare }).Matches(plan, after), "同ID另一UID不能替换原赠礼源");
        That(!(fact with { Gift = gift with { Count = 2 } }).Matches(plan, after), "原礼物消费数量不可少扣");
        That(!(fact with { Gift = gift with { Minimum = 101, Maximum = 101 } }).Matches(plan, after), "原点数范围不可替换");
        That(!(fact with { Paid = before }).Matches(plan, after), "未消费原礼物不计赠礼");
        That(!fact.Matches(plan, after with { Gold = 999 }), "实体赠礼不得额外扣金币");
        That(!fact.Matches(plan, after with { Items = [paid.Items[0] with { Core = paid.Items[0].Core with { Marker16 = 0 } }, spare] }), "叠回礼物完整core不能丢失");
        That(!fact.Matches(plan, after with { Items = [paid.Items[0], spare with { Uid = 19 }] }), "同ID备用礼物完整UID保留");
        That(!fact.Matches(plan, after with { NpcFavor = [updated with { GiftCount = 2 }] }), "赠礼每日次数不可额外增加");
        That(!fact.Matches(plan, after with { NpcFavor = [updated with { Colors = 1 }] }), "赠礼不能顺便染色");
        That(!fact.Matches(plan.WithAfter(after with { Gold = 999 }), after), "旧事实不得挪用于新After");
        var cubeBefore = CurrencyWallet.Set(before, CurrencyKind.WhiteCube, 200);
        var cubePaid = CurrencyWallet.Set(cubeBefore, CurrencyKind.WhiteCube, 100);
        var cubeAfter = cubePaid with { NpcFavor = [updated] };
        var cubeGift = new NpcFavorGift(-1, 3034, 100, 100, 300);
        var cubeFact = fact with { Request = new(0, 17, 36, 355), Gift = cubeGift, Source = null,
            Currency = CurrencyKind.WhiteCube, Before = cubeBefore, Paid = cubePaid, After = cubeAfter };
        var cubePlan = new InventoryPlan(cubeBefore, cubeAfter, "npc_favor_gift") { NpcGift = cubeFact };
        var cubeActual = cubeAfter with { AccountWallet = cubeAfter.AccountWallet with { Revision = 1 } };
        That(cubeFact.Matches(cubePlan, cubeActual), "原晶块赠礼扣100并仅允许原writer钱包revision+1");
        That(!cubeFact.Matches(cubePlan, cubeAfter), "真实晶块提交必须有钱包版本推进");
        That(!(cubeFact with { Request = cubeFact.Request with { SourceIndex = 354 } }).Matches(cubePlan, cubeActual), "虚拟晶块槽必须匹配原资源币种");
        That(!(cubeFact with { Paid = cubeBefore }).Matches(cubePlan, cubeActual), "未扣晶块不计赠礼");
        That(!cubeFact.Matches(cubePlan, CurrencyWallet.Set(cubeActual, CurrencyKind.BlackCube, 1)), "原钱包其他币种不得凭空改变");
        var definition = new BattlePassMission(24, 1, 100, 25, "", -1);
        That(BattlePassNpcGiftSource.Matches(definition), "仅原daily24定义接线");
        That(!BattlePassNpcGiftSource.Matches(definition with { RawTypeCode = 24 }) &&
            !BattlePassNpcGiftSource.Matches(definition with { RawParameter = 0 }) &&
            !BattlePassNpcGiftSource.Matches(definition with { RequiredCount = 2 }) &&
            !BattlePassNpcGiftSource.Matches(definition with { Experience = 1 }), "其他raw/参数/数量/XP不泛接");
        Console.WriteLine($"PASS BattlePassNpcGiftTests core ({Count - checks} checks)");
    }
}
