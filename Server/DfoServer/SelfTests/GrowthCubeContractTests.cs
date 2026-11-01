using System.Collections.Immutable;
using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GrowthCubeContractTests
{
    public static void RunCore()
    {
        var benefits = new GrowthContractBenefits(20, 20, [20, 23, 27, 30]);
        var cubes = new[] { 3033, 3034, 3035, 3036, 3037, 3262 }.Select((id, index) => new CubeContractOption(id,100300084 + index));
        var catalog = new PremiumContractCatalog(new Dictionary<int,PremiumContract>
            { [1] = new(1,84,86400), [2] = new(2,97,3600) }, growth: benefits,growthEquipment: [2312900],cubes: cubes);
        That(catalog.TryAcquisitionContract(1,out _) && catalog.TryAcquisitionContract(2,out _) &&
            !catalog.TryConsumableContract(1,out _) && !catalog.TryConsumableContract(2,out _),
            "获取激活能力独立于遗留实体使用，不自动扩大44使用入口");
        var character = new Character(1,0,"Contract",0,new(1,0,10,10),null)
            { Inventory = InventorySnapshot.Empty with { AccountWallet = AccountWallet.Empty.SetContractExpiry(84,2000).SetContractExpiry(97,2000) } };
        That(GrowthContractPolicy.ExperienceBonus(character,109,benefits,1000) == 21 &&
            GrowthContractPolicy.ExperienceBonus(character,109,benefits,2000) == 0 &&
            GrowthContractPolicy.ExperienceBonus(character,109,null,1000) == 0,
            "经验按基础20%向下取整，精确到期和缺规则不加成");
        That(GrowthContractPolicy.QuestDropRate(character,50,benefits,1000) == 60 &&
            GrowthContractPolicy.QuestDropRate(character,90,benefits,1000) == 100 &&
            GrowthContractPolicy.QuestDropRate(character,0,benefits,1000) == 0,
            "任务掉落增加原概率的20%，不把零概率变奖励，饱和至100");
        That(GrowthContractPolicy.AppliedBonus(100,20,115) == 15 && GrowthContractPolicy.AppliedBonus(100,20,99) == 0,
            "等级上限只投影实际奖励份额，不改基础或组队分支");
        var service = new CubeContractService(catalog);
        Bytes(PremiumPackets.Updated(97, 2000, 1000), [2,0,97,0xE8,3,0,0,0,0,0,0], "66的契约更新使用本版u16/u8/i64，不能复制旧端u32期限");
        for (sbyte type = 0; type < 6; type++)
        {
            var state = CubeContractRequests.Read([1,(byte)type]);
            var planned = service.Select(character,state,1000);
            That(planned.Accepted && planned.Plan!.After.CubeContract == state && planned.Plan.After.AccountWallet.Same(character.Inventory.AccountWallet),
                "保存六种选择不扣晶块、不缩短期限，空钱包不额外收窄原码选择资格");
            Bytes(CubeContractPackets.Result(state,true),[1,1,(byte)type],"535成功回显真实两字段");
            int itemId = catalog.Cubes[type].ItemId;
            var kind = CurrencyWallet.RewardKind(itemId)!.Value;
            var selected = character with { Inventory = character.Inventory with
                { CubeContract = new(0, type), AccountWallet = character.Inventory.AccountWallet.Set(kind, 2) } };
            var consumed = service.Consume(selected, type, 1000);
            That(consumed.Accepted && consumed.Plan!.After.AccountWallet.Get(kind) == 1 &&
                consumed.Plan.Reason == "cube_contract_consume" && consumed.Plan.After.CubeContract == selected.Inventory.CubeContract,
                "六色周期扣款复用钱包，sourceFlag为0仍可启用，消费不改选择或期限");
            That(!service.Consume(selected, (sbyte)((type+1)%6), 1000).Accepted &&
                !service.Consume(selected, type, 2000).Accepted &&
                !service.Consume(selected with { Inventory = selected.Inventory with
                    { AccountWallet = selected.Inventory.AccountWallet.Set(kind, 0) } }, type, 1000).Accepted,
                "旧颜色、到期及不足材料拒绝周期扣款");
        }
        var canceled = CubeContractRequests.Read([0,255]);
        That(canceled == CubeContractState.Empty && service.Select(character,canceled,2000).Accepted,
            "FF取消不会变成黑色，已到期也允许清除选择");
        Bytes(CubeContractPackets.Selection(canceled),[0,255],"901是两个原始字节，不带ACK信封");
        Bytes(CubeContractPackets.Result(new(1,4),false,123),[0,123,0],
            "535失败由公共分派读取u16错误码，不能把选择回显误作1025错误码");
        That(!service.Select(character,new(1,0),2000).Accepted &&
            service.ForClient(character.Inventory with { CubeContract = new(1,0) },2000) == CubeContractState.Empty,
            "精确到期不能开启效果，投影零态但不改存量选择");
        foreach (byte[] bad in new byte[][] { [],[1],[1,6],[1,128],[2,0],[1,0,0] })
        {
            try { CubeContractRequests.Read(bad); throw new Exception("应拒绝畸形535"); }
            catch (InvalidDataException) { That(true,"长度、状态位和类型边界拒绝"); }
        }
        var template = new ItemTemplate(10,ItemKind.Equipment,12,1,10,1,0,3,1,"[trade]",ushort.MaxValue,0,"item");
        var pool = new DungeonItemDropPool([new DungeonWeightedDropItem(template,1)]);
        var generation = new DungeonDropGenerationPlan("test",[new(100,pool,1,1),
            new(100,pool,1,1,ProbabilityDenominator:1000000,GrowthContractEligible:true)]);
        var enhanced = generation.WithGrowthContract(20);
        That(enhanced.Trials[0].Threshold == 100 && enhanced.Trials[1].Threshold == 120 && generation.Trials[1].Threshold == 100 &&
            enhanced.MaximumObjects == generation.MaximumObjects,"只加成被明确标注的独立掉落，不改普通/全局概率、次数或原冻结计划");
        That(new DungeonDropGenerationPlan("test",[new(900000,pool,1,1,ProbabilityDenominator:1000000,GrowthContractEligible:true)])
            .WithGrowthContract(20).Trials[0].Threshold == 1000000,"独立概率到百万分母封顶，不溢出或多发");
        var clear = new DungeonClearExperienceReward(100, 25, 145, 0, "test", "test")
            { GrowthContractExperience = 20 };
        var packet = DungeonSettlementPackets.EmptyCardReward(clear.Applied(137), 1000, 123);
        That(BinaryPrimitives.ReadUInt32LittleEndian(packet) == 100 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == 25 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(49)) == 12 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(85)) == 123 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(194)) == 1000 && packet.Length == 218,
            "35按ARAD的两组成长栏写实际通关与击杀加成，保留基础、评分、实例总经验和尾部读宽");
        var split = new DungeonClearExperienceReward(100, 25, 155, 0, "test", "test")
            { GrowthContractExperience = 20, AdventureExperience = 10 };
        var partlyPaid = split.Applied(137);
        That(partlyPaid is { BaseExperience: 100, AdventureExperience: 10, RankExperience: 25, GrowthContractExperience: 2,
                PotionExperience: 0, TotalExperience: 137 } &&
            BinaryPrimitives.ReadUInt32LittleEndian(DungeonSettlementPackets.EmptyCardReward(partlyPaid, 0)) == 110,
            "冒险团加成紧随基础经验拆分并合并进35基础字段，实际到账不足时按拆分顺序截断");
        var baseOnly = split.Applied(50);
        var adventureEdge = split.Applied(105);
        That(baseOnly is { BaseExperience: 50, AdventureExperience: 0, RankExperience: 0, GrowthContractExperience: 0, PotionExperience: 0 } &&
            adventureEdge is { BaseExperience: 100, AdventureExperience: 5, RankExperience: 0 },
            "基础经验先足额再计入冒险团，未超过基础时其余分项为零");
        var parsed = PremiumContractCatalog.Parse("""
            [type] 84 [target item] 1 [term] 1
            [item] 3 [term] 3 [bonus exp] 99 [/item]
            [bonus exp] 20 [quest item drop rate] 20 [independent drop rate] 20 23 27 30 [/independent drop rate]
            [type] 97 [target item] 2 [term] 1
            """, """
            [premium service] [type] 84 [add equipment list] 2312900 [/add equipment list] [/premium service]
            [premium service] [type] 97 [add selectAble equipment list]
                3033 100300084 3034 100300087 3035 100300086 3036 100300085 3037 100300088 3262 100300089
            [/add selectAble equipment list] [/premium service]
            """);
        That(parsed.TryAcquisitionContract(1, out _) && parsed.TryAcquisitionContract(2, out _) &&
            parsed.Growth!.ExperiencePercent == 20 && parsed.Get(3)!.DurationSeconds == 3 * 86400,
            "子条目只规定各自期限，不污染父成长权益，晶体原序保持");
        foreach (string bad in new[] { "20 23 27", "20 23 27 30 31", "20 -1 27 30", "20 23 `27` 30" })
            Reject<InvalidDataException>(() => PremiumContractCatalog.Parse(
                "[type] 84 [bonus exp] 20 [quest item drop rate] 20 [independent drop rate] " + bad),
                "成长独立概率必须为四项非负整数，不能猜缺省或截断");
        parsed.InspectStackables(new Dictionary<int, string> { [2] = "cube.stk", [3] = "growth3.stk", [3037] = "clear.stk" },
            path => path != "stackable/cube.stk");
        That(parsed.Get(1) == new PremiumContract(1, 84, 86400) && parsed.Get(2) == new PremiumContract(2, 97, 86400) &&
            !parsed.TryAcquisitionContract(1, out _) && !parsed.TryAcquisitionContract(2, out _) &&
            parsed.TryAcquisitionContract(3, out _) && parsed.ResourceWarnings is [{ Type: 97, ConfiguredItems: 1, Reason: "no_contract_stk_available" }],
            "成长缺父STK但仍有3天条目不告警，晶体所有期限缺失仅告警，不抛启动异常或删期限配置");
        That(!new CubeContractService(parsed).Select(character, new(1, 0), 1000).Accepted &&
            new CubeContractService(parsed).Select(character, new(1, 4), 1000).Accepted,
            "缺晶块模板只禁对应颜色，不影响现有期限、其他颜色或服务启动");
        parsed.InspectStackables(new Dictionary<int, string>(), _ => false);
        That(parsed.ResourceWarnings.Select(row => row.Type).SequenceEqual([84, 97]) && parsed.ResourceWarnings[0].ConfiguredItems == 2,
            "两个契约均无STK时各记录一次告警，不逐期限刷屏");
        var partial = PremiumContractCatalog.Parse("[type] 27 [over skill] 5 [target item] 10 [term] 1 " +
            "[item] 11 [term] 60 [is term unit minute] 1 [/item]");
        partial.InspectStackables(new Dictionary<int, string> { [11] = "expert-hour.stk" }, _ => true);
        That(partial.ResourceWarnings.IsEmpty && !partial.TryAcquisitionContract(10, out _) &&
            partial.TryAcquisitionContract(11, out var hourly) && hourly.DurationSeconds == 3600,
            "只有1小时STK也满足该契约资源条件，缺1天版本不阻止启动或触发告警");
    }

    public static void RunResources(ItemResources resources, GameWorld.Towns.TownCatalog towns, GameWorld.Characters.CharacterCatalog characters)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        var catalog = resources.Contracts;
        That(catalog.Growth is { ExperiencePercent: 20, QuestItemDropPercent: 20 } &&
            catalog.Growth.IndependentDropPercent.SequenceEqual([20,23,27,30]) && catalog.GrowthEquipment.SequenceEqual([2312900]),
            "成长完整权益与附加装备来自当前两份PVF声明");
        That(catalog.Cubes.Select(row => row.ItemId).SequenceEqual([3033,3034,3035,3036,3037,3262]) &&
            catalog.Cubes.Select(row => row.EquipmentId).SequenceEqual([100300084,100300087,100300086,100300085,100300088,100300089]),
            "六种选择按premiumserviceeffect原序，不按装备编号排序");
        var character = new Character(1,0,"Contract",0,towns.Room,towns.InitialReturn);
        VerifyAcquisitionIndices(catalog);
        var incomingCore = InventoryRewardGrantService.Create(resources.Items.Get(10000387), utcNow: now);
        foreach (var core in new[] { incomingCore with { Kind = ItemKind.Equipment },
            incomingCore with { ExpireTime = checked((int)now.ToUnixTimeSeconds() - 1) }, incomingCore with { SortLockFlag = 2 } })
            That(!InventoryRewardGrantService.TryGrantItems(character, [new(77, new(InventoryListType.Main,65), core)],
                resources.Items, resources.GoldLimits, resources.EpicPieces, now).Accepted,
                "契约接收仍核验完整核心和期限，不因转为账号资格而吞掉类型或字段错误");
        var owned = new InventoryItem(77, new(InventoryListType.Main,65), incomingCore);
        That(!InventoryRewardGrantService.TryGrantItems(character with { Inventory = new([owned]) }, [owned],
            resources.Items, resources.GoldLimits, resources.EpicPieces, now).Accepted,
            "已有背包UID不能经新接收入口重复激活契约");
        foreach (var definition in catalog.ByItem.Values.Where(row => row.Type is 22 or 27))
            That(catalog.TryConsumableContract(definition.ItemId, out _) &&
                resources.Items.Get(definition.ItemId).AcquisitionContract == definition,
                "霸王、达人每个PVF父子条目均保留原消费能力和资源装配，不遗漏分钟/活动/同名变体");
        foreach (int id in new[] { 490700040, 490700041, 490700042, 490700043, 490700044, 490700045 })
            That(resources.Boosters.Get(id).Available && resources.Boosters.Get(id).Pools
                .SelectMany(pool => pool.Rewards).All(row => catalog.TryConsumableContract(row.ItemId, out _)),
                "同名契约礼包按booster实际产物接入，不能把礼包ID直接硬编码成期限");
        foreach (int id in new[] { 2660348, 2660351 })
            That(!catalog.Recognizes(id) && resources.Items.Get(id).AcquisitionContract is null &&
                !resources.CashShop.Products.Values.Any(product => product.ItemId == id),
                "同名旧etc文件有LST注册，但无当前期限映射和商城商品，不据名称臆造契约能力");
        foreach (int id in resources.CashShop.Products.Values.Where(product =>
            product.Section is "selectable character premium" or "charac premium package")
            .Select(product => product.ItemId).Distinct().Where(id => !catalog.TryAcquisitionContract(id, out _)))
            That(resources.Items.Get(id).IsPremiumService &&
                !InventoryRewardGrantService.TryGrantBatch(character, [new(id, 1)], resources.Items, resources.GoldLimits,
                    resources.EpicPieces, now).Accepted,
                "资源明确声明的未实现角色服务拒绝奖励，不变成无效果实体");
        foreach (var pair in catalog.ByItem.Where(pair => pair.Value.Type is 22 or 27 or 84 or 97))
        {
            That(catalog.TryAcquisitionContract(pair.Key,out var definition),"当前四种契约的全部父子条目均具获取能力");
            var batch = InventoryRewardGrantService.TryGrantBatch(character,[new(pair.Key,2)],resources.Items,resources.GoldLimits,resources.EpicPieces,now);
            var core = InventoryRewardGrantService.TryGrantCore(character,InventoryRewardGrantService.Create(resources.Items.Get(pair.Key),2,now),
                resources.Items.Get(pair.Key),resources.EpicPieces,now);
            That(batch.Accepted && core.Accepted && batch.Plan!.After.Items.IsEmpty && core.Plan!.After.Items.IsEmpty &&
                batch.Plan.After.AccountWallet.ContractExpiry(definition.Type) == now.ToUnixTimeSeconds() + definition.DurationSeconds * 2 &&
                core.Plan.After.AccountWallet.Same(batch.Plan.After.AccountWallet),"任意发奖和地面核心获取按同一个数量倍率激活，不要求空槽");
        }
        string root = Path.Combine(Path.GetTempPath(),"S5A9-cube-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root,"game.db");
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("10000",()=>{});
            var created = repository.Create(owner,CharacterCreation.Prepare("CubeState",characters.Get(0),towns.Room,towns.InitialReturn,null));
            var grant = InventoryRewardGrantService.TryGrantBatch(created,[new(10000387,1),new(2660408,1)],resources.Items,resources.GoldLimits,resources.EpicPieces,now);
            created = InventoryService.Commit(repository,owner,created,grant);
            var service = new CubeContractService(catalog);
            var selected = InventoryService.Commit(repository,owner,created,service.Select(created,new(1,4),now.ToUnixTimeSeconds()));
            That(repository.List(owner).Single().Inventory.CubeContract == new CubeContractState(1,4),"选择经原库存事务保存，重装载恢复");
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path,Pooling = false }.ToString()))
            {
                db.Open();using var cmd=db.CreateCommand();
                cmd.CommandText="CREATE TRIGGER cube_failure BEFORE UPDATE ON cube_premium BEGIN SELECT RAISE(ABORT,'cube failed'); END";cmd.ExecuteNonQuery();
                try { InventoryService.Commit(repository,owner,selected,service.Select(selected,new(0,-1),now.ToUnixTimeSeconds()));throw new Exception("应回滚"); }
                catch (SqliteException) { That(true,"选择写入晚期SQL失败"); }
                That(repository.List(owner).Single().Revision==selected.Revision && repository.List(owner).Single().Inventory.CubeContract==new CubeContractState(1,4),
                    "失败保留角色revision、选择、账号期限及钱包");
                cmd.CommandText="DROP TRIGGER cube_failure";cmd.ExecuteNonQuery();
            }
            repository.Delete(owner,repository.LoadDeletionTarget(owner,created.Slot));
            That(repository.List(owner).Length==0,"角色删除清除cube_premium，账号契约不随角色消失");
        }
        finally { TestFiles.DeleteDirectory(root,true); }
    }

    static void VerifyAcquisitionIndices(PremiumContractCatalog catalog)
    {
        // 直接取27000资源核对的编号和期限，不用解析器输出生成自身的期待值。
        (int Type, long Seconds, int[] Items)[] expected =
        [
            (22, 86400, [30, 35, 2660704, 490700031]),
            (22, 2 * 86400, [741]),
            (22, 3 * 86400, [31]),
            (22, 5 * 86400, [32]),
            (22, 6 * 86400, [2683454]),
            (22, 7 * 86400, [33]),
            (22, 10 * 86400, [2660050, 490700032]),
            (22, 14 * 86400, [2683455]),
            (22, 15 * 86400, [34, 490704739]),
            (22, 30 * 86400, [2660012]),
            (22, 100 * 86400, [490700033]),
            (22, 3600, [10096109]),
            (27, 86400, [43, 47, 2660705, 490700028]),
            (27, 2 * 86400, [742]),
            (27, 3 * 86400, [44]),
            (27, 6 * 86400, [2683452]),
            (27, 7 * 86400, [45]),
            (27, 10 * 86400, [2660051, 490700029]),
            (27, 14 * 86400, [2683453]),
            (27, 15 * 86400, [46, 490704740]),
            (27, 30 * 86400, [2660013]),
            (27, 100 * 86400, [490700030]),
            (27, 3600, [10096110]),
            (84, 86400, [2660408, 2660703, 490700021]),
            (84, 3 * 86400, [2660409]),
            (84, 6 * 86400, [2683456]),
            (84, 7 * 86400, [2660410]),
            (84, 10 * 86400, [490700022]),
            (84, 15 * 86400, [2660411]),
            (84, 30 * 86400, [2683457]),
            (84, 100 * 86400, [490700023]),
            (84, 3600, [10096112]),
            (97, 86400, [10000387, 10000391, 490700034]),
            (97, 3 * 86400, [10000388]),
            (97, 6 * 86400, [2683458]),
            (97, 7 * 86400, [10000389]),
            (97, 10 * 86400, [490700035]),
            (97, 15 * 86400, [10000390, 490704741]),
            (97, 30 * 86400, [2683459]),
            (97, 100 * 86400, [490700036]),
            (97, 3600, [10096113])
        ];
        var definitions = expected.SelectMany(row => row.Items.Select(id => new PremiumContract(id, row.Type, row.Seconds))).ToArray();
        That(definitions.Length == 56 && definitions.Select(row => row.ItemId).Distinct().Count() == 56 &&
            catalog.ByItem.Values.Where(row => row.Type is 22 or 27 or 84 or 97).OrderBy(row => row.ItemId)
                .SequenceEqual(definitions.OrderBy(row => row.ItemId)),
            "当前PVF四种契约获取编号集合完整，既不遗漏父/子项也不混入同名旧文件和礼包编号");
        foreach (var definition in definitions)
            That(catalog.TryAcquisitionContract(definition.ItemId, out var actual) && actual == definition,
                "取得编号、服务类型和原始期限逐项一致: " + definition.ItemId);
    }
}
