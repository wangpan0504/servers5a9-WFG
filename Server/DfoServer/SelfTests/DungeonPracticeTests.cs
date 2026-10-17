using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonPracticeTests
{
    public static void RunCore()
    {
        int before = Count;
        var map = new DungeonMap("boss", new(0,0), 1, "map/practice.map");
        var maze = new DungeonMaze(0,1,1,"AA",[new(0,0)],[new(0,0)],[map],[]);
        var definition = new DungeonDefinition(3,"dungeon/practice.dgn",1,1,
            [maze, maze with { SourceOrdinal = 1 }, maze with { SourceOrdinal = 2, QuestConnectionTokens = [new(0,0),new(0,3145),new(0,-1)] }],[]);
        var accepted = new ActiveMainQuest(0,3145,1) { AcceptanceId = Guid.NewGuid() };
        var character = new Character(17,0,"Practice",0,new Location(38,2,100,200),null)
            { Level = 16, ActiveMainQuests = [accepted] };
        That(DungeonEntryPlan.TryCreate(definition,character,3,0,0,out var entry,out _,draw: count => count - 1,practice: true) &&
            entry is { IsPractice: true, IsQuestReplay: false, UsesFatigue: false, RouteQuestId: 0, ActiveQuestSlot: byte.MaxValue,
                QuestAcceptances.IsEmpty: true, InitializationCounterSupported: false, Maze.SourceOrdinal: 1 },
            "练习只抽普通路线，保持资源ordinal，不借主线或回放授权");
        That(!QuestHuntProgress.Bound(accepted,entry!), "练习不绑定正式任务接取");
        That(entry!.HasFreeCombatConsumption && !(entry with { IsPractice = false, DisableClearReward = true }).HasFreeCombatConsumption,
            "免耗由明确玩法模式决定，普通地图只有禁止翻牌标记不能取得免扣资格");
        foreach (var sections in new DungeonRawSection[][] { [new("prohibit practice",[new(0,1)])],
            [new("prohibit practice",[])], [new("prohibit practice",[new(0,-1)])], [new("tutorial dungeon",[new(0,1)])] })
            That(!DungeonEntryPlan.TryCreate(definition with { RawSections = sections },character,3,0,0,out _,out _,practice: true),
                "禁止练习或未知字段形状不能降级为普通入场");
        That((definition with { RawSections = [new("prohibit practice",[new(0,0)])] }).AllowsPractice,
            "显式零与省略prohibit practice均允许练习");
        That(!DungeonEntryPlan.TryCreate(definition,character,3,3145,0,out _,out _,practice: true) &&
            !DungeonEntryPlan.TryCreate(definition,character,3,0,0,out _,out _,practice: true,hellParty: true) &&
            !DungeonEntryPlan.TryCreate(definition,character,3,0,0,out _,out _,practice: true,hellParty: true,hellLegend: true) &&
            !DungeonEntryPlan.TryCreate(definition,character,3,0,0,out _,out _,practice: true,hellLegend: true) &&
            !DungeonEntryPlan.TryCreate(definition,character,3,0,0,out _,out _,practice: true,isReplay: true),
            "练习不混合正式任务、普通/传奇深渊或剧情回放");
        var withHell = definition with { Mazes = [maze with { HellParty = new(99,new(0,0))
            { RequiredItemCount = 999, LegendMapId = 100, LegendRequiredCount = 999, AppearRate = 100 } }] };
        That(DungeonEntryPlan.TryCreate(withHell,character,3,0,0,out var practiceWithHell,out _,practice: true) &&
            practiceWithHell is { HellParty: null, IsHellLegend: false, HellPartyMode: 0, UsesFatigue: false } &&
            practiceWithHell.StartMap == map && practiceWithHell.BossMap == map,
            "带深渊、传奇、密门和票数声明的地图仍只练习普通路线，不要求持有入场票");
        byte[] practiceMap = DungeonPackets.StartMap(practiceWithHell!,123);
        That(practiceMap[7] == 0 && practiceMap[20] == 0 && practiceMap[22] == byte.MaxValue,
            "练习不下发深渊模式或雾标志，不激活深渊房间");
        byte[] info = DungeonPackets.Information(entry!);
        That(info.Length == 36 && info[12] == 1 && info[13] == 0 && info[7] == 1,
            "练习模式写NOTI28 offset12，不挤占迷宫、列表数量或深渊字段");
        info[12] = 0;
        Bytes(info,DungeonPackets.Information(entry! with { IsPractice = false }),"普通入场其余字段保持原布局");
        Bytes(DungeonSettlementPackets.PracticeCleared(123456),[0x40,0xE2,1,0,0],
            "本版NOTI255明确为时间u32和无附加奖励u8，不复制旧端空254");
        var inventory = InventorySnapshot.Empty with { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.ClearCube,20) };
        var request = InventoryRequests.Delete(Convert.FromHexString("1000000010001A0A080210E60218DD172005200001"));
        foreach (byte type in new byte[] { 2,3 })
        {
            var actual = request with { Items = [request.Items[0] with { Type = type }] };
            var kept = InventoryDeleteService.PlanBatch(inventory,actual,preserveMaterials: true);
            That(kept.Accepted && !kept.Plan!.Changed && kept.Plan.PreserveMaterials,
                "练习技能及装备脚本材料保持原钱包，不产生库存写入");
            byte[] ack = InventoryPackets.Delete(actual,kept);
            That(ack[13] == 0 && ack[^3] == type,"练习ACK18携带实际删除0，不仅服务端余额保持");
            That(!InventoryDeleteService.PlanBatch(inventory,actual with { Items = [actual.Items[0] with { Count = 21 }] },
                preserveMaterials: true).Accepted,"练习免扣仍要求源材料足额，不能伪造消耗资格");
            That(InventoryDeleteService.PlanBatch(inventory,actual).Plan!.After.AccountWallet.Get(CurrencyKind.ClearCube) == 15,
                "练习豁免不泄漏到普通材料请求");
        }
        Console.WriteLine($"PASS {Count-before} 练习资源、路线隔离及协议断言");
    }
}
