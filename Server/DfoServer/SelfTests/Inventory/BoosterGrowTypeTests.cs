using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer.Testing.Inventory;

internal static class BoosterGrowTypeTests
{
    public static void RunCharacterCore()
    {
        int checks = Count;
        var definition = CharacterPool("`[swordman]` 1 99 1 1106 0.25 2 1112 0.75 1");
        That(definition.Available && definition.Pools.Length == 1 &&
            definition.Pools[0] is { JobTag: "[swordman]", MinimumLevel: 1, MaximumLevel: 99, GrowType: -1, Draws: 1 } &&
            definition.Pools[0].Rewards.SequenceEqual(new BoosterReward[] { new(1106,25,2), new(1112,75,1) }),
            "charactor四字段职业池可解析，不误当五字段grow type或未实现标签：" + definition.PendingRule);
        for (byte job = 0; job < 16; job++)
        {
            var pool = CharacterPool("`" + ItemCatalog.JobTag(job) + "` 1 99 1 1106 100 1").Pools.Single();
            for (int grow = 0; grow <= byte.MaxValue; grow++)
                That(pool.Matches(job,(byte)grow,1) && pool.Matches(job,(byte)grow,95) && pool.Matches(job,(byte)grow,99),
                    "职业池不限制转职或觉醒：" + job + "/" + grow);
            That(!pool.Matches(job,0,0) && !pool.Matches(job,0,100) && !pool.Matches((byte)((job+1)%16),0,95),
                "charactor仍严格匹配职业编号与完整等级区间：" + job);
        }
        That(!new BoosterPool(1,[new(1106,100,1)]) { JobTag = "[swordman]", GrowType = -1 }.Matches(0,0,95),
            "未标识为charactor的JobTag/-1不能借新族规则放行");
        var growZero = Parse("`[swordman]` 1 99 0 1 1106 100 1").Pools.Single();
        That(growZero.Matches(0,0,95) && growZero.Matches(0,0x20,95) && !growZero.Matches(0,1,95) &&
            !Parse("`[swordman]` 1 99 -1 1 1106 100 1").Available,
            "grow type仍用五字段并拒绝-1，分支零不扩大为任意转职");
        foreach (string bad in new[]
        {
            "`[unknown]` 1 99 1 1106 100 1", "`[swordman]` -1 99 1 1106 100 1",
            "`[swordman]` 99 1 1 1106 100 1", "`[swordman]` 1 99 0 1106 100 1",
            "`[swordman]` 1 99 1 1106 100", "`[swordman]` 1 99 1 1106 -1 1",
            "`[swordman]` 1 99 1 1106 100 0", "`[swordman]` 1 99 1 1106 0 1",
            "`[swordman]` 1 99 1 1106 9223372036854775808 1", "1 99 1 1106 100 1"
        })
            That(!CharacterPool(bad).Available,"非法charactor不变成无过滤普通池：" + bad);
        That(!CharacterPool("`[swordman]` 1 99 1").Available,"空职业占位池不制造奖励");
        var repeated = BoosterCatalog.Parse("[stackable type]\n`[booster]` 0\n[booster info]\n" +
            "[charactor]\n`[swordman]` 1 99 1 1106 100 1\n[/charactor]\n" +
            "[charactor]\n`[fighter]` 1 99 1 1112 100 1\n[/charactor]\n[/booster info]");
        That(repeated.Available && repeated.Pools.Length == 2 && repeated.Pools.Count(pool => pool.Matches(0,1,95)) == 1 &&
            repeated.Pools.Count(pool => pool.Matches(1,1,95)) == 1,
            "不同职业的原独立区块不合并、不跨职业抽取");
        Console.WriteLine($"PASS {Count - checks} booster charactor core assertions");
    }

    static BoosterDefinition CharacterPool(string body) =>
        BoosterCatalog.Parse("[stackable type]\n`[booster]` 0\n[booster info]\n[charactor]\n" + body + "\n[/charactor]\n[/booster info]");


    // 只由完整资源入口调用一次；TCP沿普通Booster代表路径验证，不重复1198原行。
    public static void RunCharacterResources(TownCatalog towns, PvfLib.PvfArchive pvf, ItemResources resources)
    {
        int checks = Count;
        const int sourceId = 490704573;
        int[] expectedRows = [109,109,57,61,100,100,78,60,66,60,75,66,74,65,60,58];
        var definition = resources.Boosters.Get(sourceId);
        That(definition.Available && definition.Pools.Length == 16,
            "正式白金徽章礼盒按16个独立charactor池解析：" + definition.PendingRule);
        var stackable = PvfLib.PvfScript.List(pvf.ReadText("stackable/stackable.lst"));
        string sourcePath = "stackable/" + stackable[sourceId];
        string raw = pvf.ReadText(sourcePath);
        var blocks = System.Text.RegularExpressions.Regex.Matches(raw, @"\[charactor\](.*?)\[/charactor\]",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        That(blocks.Count == 16, "真实源保留全部16个charactor原区块");
        var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.FromHours(8));
        var sourceTemplate = resources.Items.Get(sourceId);
        var sourceCore = InventoryRewardGrantService.Create(sourceTemplate, utcNow: now);
        That(sourceTemplate.Expiration.Days == 30 && sourceCore.ExpireTime == now.AddDays(30).ToUnixTimeSeconds(),
            "真实来源按原30天期限创建，不借debug忽略或手造永久源");
        var jobs = new HashSet<byte>();
        var rewardIds = new HashSet<int>();
        int originalRows = 0;
        for (int ordinal = 0; ordinal < 16; ordinal++)
        {
            var pool = definition.Pools[ordinal];
            string[] tokens = System.Text.RegularExpressions.Regex.Matches(blocks[ordinal].Groups[1].Value, @"`[^`]*`|\S+")
                .Select(match => match.Value).ToArray();
            That(tokens.Length == 4 + expectedRows[ordinal] * 3 && pool.Rewards.Length == expectedRows[ordinal] &&
                tokens[0].Trim('`') == pool.JobTag && tokens[1] == "1" && tokens[2] == "99" && tokens[3] == "1" &&
                pool.MinimumLevel == 1 && pool.MaximumLevel == 99 && pool.GrowType == -1 && pool.Draws == 1,
                "每个原职业区块保留四字段头、完整行数及顺序：" + ordinal);
            byte job = checked((byte)Enumerable.Range(0,16).Single(index => ItemCatalog.JobTag((byte)index) == pool.JobTag));
            That(jobs.Add(job), "按JobTag反查职业，原区块顺序不是Job编号：" + pool.JobTag);
            var rows = pool.Rewards.ToArray();
            long unitWeight = rows[0].Weight;
            That(unitWeight > 0 && rows.All(row => row.Weight == unitWeight), "当前所有原行保留同等正相对权重：" + pool.JobTag);
            for (int index = 0; index < rows.Length; index++)
            {
                int at = 4 + index * 3;
                That(rows[index].ItemId == int.Parse(tokens[at]) && rows[index].Count == int.Parse(tokens[at+2]) &&
                    decimal.Parse(tokens[at+1], System.Globalization.CultureInfo.InvariantCulture) == 100 && rows[index].Count == 1,
                    "真实原行逐项保留ID、数量与100相对权重，不去重重复奖励：" + ordinal + "/" + index);
            }
            long bound = checked(rows.Sum(row => row.Weight));
            long sample = 0;
            foreach (var row in rows)
            {
                var random = new CharacterRowRandom(sample,bound);
                var service = new InventoryBoosterService(resources.Items,resources.Boosters,resources.GoldLimits,resources.EpicPieces,random);
                var role = Role(job);
                var before = role.Inventory;
                var opened = service.Open(role,new(65,1),out var receipt,now);
                var expected = InventoryRewardGrantService.Create(resources.Items.Get(row.ItemId),row.Count,utcNow:now);
                That(opened.Accepted && opened.Error == 0 && opened.Plan is not null &&
                    receipt is { ItemId: sourceId, Slot: 65, Rewards: [var received] } &&
                    received.ItemId == row.ItemId && received.Count == row.Count &&
                    opened.Plan.After.Find(Bag(65)) is null && opened.Plan.After.Items.Length == 1 &&
                    opened.Plan.After.Items.Single().Core == expected && expected.Kind == ItemKind.Emblem && expected.ExpireTime == 0 &&
                    role.Inventory == before && role.Inventory.Items.Single().Core == sourceCore && random.Calls == 1,
                    "每条原行沿正式Open扣一源、发一件完整永久Emblem且只抽一次：" + job + "/" + originalRows +
                    "/" + opened.Error + "/" + opened.RejectReason);
                rewardIds.Add(row.ItemId);
                originalRows++;
                sample = checked(sample + row.Weight);
            }
            That(sample == bound, "累计抽样下边界逐行覆盖完整本职业奖池：" + pool.JobTag);
        }
        That(originalRows == 1198 && rewardIds.Count == 989 && jobs.Count == 16,
            "正式Open覆盖16职业1198原行及989目标，重复行保持独立抽样");
        var rejectionRandom = new CharacterRowRandom(0,definition.Pools[0].Rewards.Sum(row => row.Weight));
        var rejectionService = new InventoryBoosterService(resources.Items,resources.Boosters,resources.GoldLimits,resources.EpicPieces,rejectionRandom);
        var baseRole = Role(0);
        foreach (var role in new[] { baseRole with { Level = 0 }, baseRole with { Level = 100 } })
            That(rejectionService.Open(role,new(65,1),out var receipt,now) is { Accepted: false, Error: 19, Plan: null } &&
                receipt is null && role.Inventory == baseRole.Inventory && rejectionRandom.Calls == 0,
                "真实来源或职业池等级越界在抽样前拒绝且不扣源：" + role.Level);
        That(rejectionService.Open(baseRole,new(65,1),out var expiredReceipt,now.AddDays(30)) is
            { Accepted: false, Error: 19, Plan: null } && expiredReceipt is null && rejectionRandom.Calls == 0 &&
            baseRole.Inventory.Items.Single().Core == sourceCore,
            "真实30天源在到期等号精确拒绝，不扣源或抽样");
        var matching = definition.Pools.Single(pool => pool.JobTag == ItemCatalog.JobTag(baseRole.Job));
        long matchingBound = matching.Rewards.Sum(row => row.Weight);
        var firstTemplate = resources.Items.Get(matching.Rewards[0].ItemId);
        var range = ItemSlotBoundService.Range(ItemKind.Emblem,baseRole.Inventory);
        var full = baseRole with { Inventory = baseRole.Inventory with { Items = baseRole.Inventory.Items.AddRange(
            Enumerable.Range(range.Start,range.End-range.Start+1).Select(slot => new InventoryItem(slot+100,
                Bag((short)slot),InventoryRewardGrantService.Create(firstTemplate,firstTemplate.StackLimit,now)))) } };
        var capacityRandom = new CharacterRowRandom(0,matchingBound);
        var capacityService = new InventoryBoosterService(resources.Items,resources.Boosters,resources.GoldLimits,resources.EpicPieces,capacityRandom);
        var fullBefore = full.Inventory;
        That(capacityService.Open(full,new(65,1),out var fullReceipt,now) is
            { Accepted: false, Error: 4, Plan: null } && fullReceipt is null && full.Inventory == fullBefore &&
            full.Inventory.Find(Bag(65))!.Core == sourceCore && capacityRandom.Calls == 1,
            "Emblem页每格填至真实堆叠上限后整单拒绝，原源与全部库存不变");
        int missingOwn = matching.Rewards.Last(row => row.ItemId != matching.Rewards[0].ItemId).ItemId;
        var ownIds = matching.Rewards.Select(row => row.ItemId).ToHashSet();
        int missingOther = definition.Pools.Where(pool => pool.JobTag != matching.JobTag).SelectMany(pool => pool.Rewards)
            .First(row => !ownIds.Contains(row.ItemId)).ItemId;
        foreach (var sample in new[] { (Id: missingOwn, Own: true), (Id: missingOther, Own: false) })
        {
            // 独立名册只移除一个真实候选；共享资源缓存与PVF本身保持原样。
            var isolatedItems = new ItemCatalog(pvf,new Dictionary<int,string>(),
                stackable.Where(row => row.Key != sample.Id).ToDictionary(row => row.Key,row => row.Value),0);
            That(!isolatedItems.TryGet(sample.Id,out _), "独立名册确实缺少指定真实候选：" + sample.Id);
            var candidateRandom = new CharacterRowRandom(0,matchingBound);
            var candidateService = new InventoryBoosterService(isolatedItems,resources.Boosters,resources.GoldLimits,resources.EpicPieces,candidateRandom);
            var opened = candidateService.Open(baseRole,new(65,1),out var candidateReceipt,now);
            if (sample.Own)
                That(opened is { Accepted: false, Error: 19, Plan: null } && candidateReceipt is null &&
                    candidateRandom.Calls == 0 && baseRole.Inventory.Items.Single().Core == sourceCore,
                    "本职业非命中有权候选缺模板也在抽样前整单拒绝，不丢奖或缩池");
            else
                That(opened.Accepted && candidateReceipt is { Rewards: [var reward] } &&
                    reward.ItemId == matching.Rewards[0].ItemId && reward.Count == 1 && candidateRandom.Calls == 1 &&
                    opened.Plan!.After.Items.Single().Core == InventoryRewardGrantService.Create(firstTemplate,utcNow:now) &&
                    baseRole.Inventory.Items.Single().Core == sourceCore,
                    "仅异职业池缺模板不阻塞本职业完整永久奖励，不能预检全16池");
        }
        CharacterPersistence(Role(15) with { GrowType = 0x23 },resources,now);
        Console.WriteLine($"PASS {Count-checks} booster charactor resource assertions; jobs={jobs.Count}, originalRows={originalRows}, uniqueRewards={rewardIds.Count}");

        Character Role(byte job) => new(1,0,"CharactorRow",0,towns.Room,towns.InitialReturn)
        {
            Job = job, GrowType = 0, Level = 95, Inventory = new([new(1,Bag(65),sourceCore)])
        };
    }

    static void CharacterPersistence(Character role, ItemResources resources, DateTimeOffset now)
    {
        string root = Path.Combine(Path.GetTempPath(),"s5a9-charactor-box-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool complete = false;
        try
        {
            string path = Path.Combine(root,"game.db");
            var repo = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("charactor-box",() => { });
            var source = role.Inventory.Items.Single();
            repo.Create(owner,new(role.Name,role.Position,role.Return)
            { Job = role.Job, GrowType = role.GrowType, Level = role.Level,
                Inventory = new([source with { Uid = 0 }],Gold:12345) });
            var saved = new CharacterRepository(path).List(owner).Single();
            That(saved.Inventory.Items.Single().Uid > 0 && saved.Inventory.Items.Single().Core == source.Core &&
                saved.GrowType == 0x23, "真实30天源先取得持久UID，再以非零职业觉醒角色规划");
            var pool = resources.Boosters.Get(source.Core.ItemId).Pools.Single(pool => pool.JobTag == ItemCatalog.JobTag(saved.Job));
            long bound = pool.Rewards.Sum(row => row.Weight);
            string before = Snapshot();
            var expiryClock = new DailyBoosterTestClock(now.AddDays(30).AddSeconds(-1));
            var expiryRandom = new CharacterRowRandom(0,bound);
            var expiryService = new InventoryBoosterService(resources.Items,resources.Boosters,resources.GoldLimits,resources.EpicPieces,
                expiryRandom,clock:expiryClock);
            var expiring = expiryService.Open(saved,new(65,1),out _);
            That(expiring.Accepted && expiring.Plan!.CharacterPrecondition!.QualificationExpiresAt == source.Core.ExpireTime &&
                expiryRandom.Calls == 1, "真实源到期前一秒可规划，提交资格绑定原30天截止");
            expiryClock.Advance(TimeSpan.FromSeconds(1));
            bool expired = false;
            try { repo.CommitInventory(owner,saved,expiring.Plan!); }
            catch (InventoryQualificationExpiredException) { expired = true; }
            That(expired && Snapshot() == before && expiryRandom.Calls == 1,
                "提交时恰到真实源期限拒绝，库存、槽、钱包、审计、UID序列与revision全不变");
            var clock = new DailyBoosterTestClock(now);
            var random = new CharacterRowRandom(0,bound);
            var service = new InventoryBoosterService(resources.Items,resources.Boosters,resources.GoldLimits,resources.EpicPieces,random,clock:clock);
            var opened = service.Open(saved,new(65,1),out var receipt);
            That(opened.Accepted && receipt is { Rewards: [var reward] } && reward.ItemId == pool.Rewards[0].ItemId && random.Calls == 1,
                "本族SQL计划只抽一次并冻结真实职业奖励");
            var plan = opened.Plan!;
            var expected = plan.After.Items.Single();
            Sql("CREATE TRIGGER fail_charactor_reward BEFORE INSERT ON character_inventory_slots WHEN NEW.character_id=" + saved.Id +
                " AND NEW.list_type="+(int)expected.Position.List+" AND NEW.slot_index="+expected.Position.Index+
                " BEGIN SELECT RAISE(ABORT,'charactor rollback'); END");
            bool failed = false;
            try { repo.CommitInventory(owner,saved,plan); } catch (SqliteException) { failed = true; }
            That(failed && Snapshot() == before && random.Calls == 1,
                "奖励槽晚期SQL失败完整回滚源、核心、revision、槽、钱包、审计与UID序列");
            Sql("DROP TRIGGER fail_charactor_reward");
            var committed = repo.CommitInventory(owner,saved,plan);
            var recovered = new CharacterRepository(path).List(owner).Single();
            var actual = recovered.Inventory.Items.Single();
            That(recovered.Revision == saved.Revision+1 && recovered.Inventory.Find(Bag(65)) is null &&
                actual.Uid > 0 && actual.Uid != saved.Inventory.Items.Single().Uid && actual.Position == expected.Position && actual.Core == expected.Core &&
                actual.Core.Kind == ItemKind.Emblem && actual.Core.ExpireTime == 0 && recovered.Inventory.Gold == 12345 &&
                JsonSerializer.Serialize(recovered) == JsonSerializer.Serialize(committed) && random.Calls == 1,
                "移除故障后提交同一冻结计划，重开完整恢复永久Emblem与正UID，源只扣一次且不重抽");
            before = Snapshot();
            That(service.Open(recovered,new(65,1),out var replay) is { Accepted: false, Error: 19, Plan: null } &&
                replay is null && Snapshot() == before && random.Calls == 1,
                "耗尽源重放不再发奖，不改持久状态或再次抽取");
            complete = true;

            string Snapshot() => JsonSerializer.Serialize(new CharacterRepository(path).List(owner)) + ":" +
                Rows("SELECT * FROM character_inventory_items ORDER BY item_uid") + ":" +
                Rows("SELECT * FROM character_inventory_slots ORDER BY character_id,list_type,slot_index") + ":" +
                Rows("SELECT * FROM account_wallets ORDER BY account") + ":" +
                Rows("SELECT * FROM inventory_audit_log ORDER BY audit_id") + ":" +
                Rows("SELECT * FROM currency_audit_log ORDER BY audit_id") + ":" +
                Rows("SELECT * FROM sqlite_sequence ORDER BY name");
            string Rows(string sql)
            {
                using var db = new SqliteConnection("Data Source="+path+";Pooling=False"); db.Open();
                using var command = db.CreateCommand(); command.CommandText = sql;
                using var reader = command.ExecuteReader();
                var rows = new List<object[]>();
                while (reader.Read()) { var values = new object[reader.FieldCount]; reader.GetValues(values); rows.Add(values); }
                return JsonSerializer.Serialize(rows);
            }
            void Sql(string sql)
            {
                using var db = new SqliteConnection("Data Source="+path+";Pooling=False"); db.Open();
                using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
            }
        }
        finally
        {
            if (complete) Directory.Delete(root,true);
            else Console.Error.WriteLine("charactor SQL failure fixture retained: "+root);
        }
    }

    sealed class CharacterRowRandom(long sample, long expectedBound) : Random
    {
        public int Calls { get; private set; }
        public override long NextInt64(long maxValue)
        {
            if (++Calls != 1 || maxValue != expectedBound || sample < 0 || sample >= maxValue)
                throw new InvalidOperationException("charactor抽样次数、总权重或原行累计边界不符");
            return sample;
        }
    }

    public static void Run(TownCatalog towns, ItemResources itemResources)
    {
        int checks = Count;
        FirstGrowQualification(towns, itemResources);
        var catalog = itemResources.Items;
        var now = DateTimeOffset.UtcNow;
        var parsed = Parse("`[swordman]` 50 99 1 1 1106 0.25 2 1112 0.75 1");
        That(parsed.Available && parsed.Pools.Length == 1 && parsed.Pools[0] is { Draws: 1, JobTag: "[swordman]", MinimumLevel: 50,
            MaximumLevel: 99, GrowType: 1 } &&
            parsed.Pools[0].Rewards.SequenceEqual(new BoosterReward[] { new(1106, 25, 2), new(1112, 75, 1) }),
            "转职奖池保留职业、等级、分支和小数权重");
        That(parsed.Pools[0].Matches(0, 1, 50) && parsed.Pools[0].Matches(0, 0x21, 99) &&
            !parsed.Pools[0].Matches(0, 0, 50) && !parsed.Pools[0].Matches(0, 1, 49) && !parsed.Pools[0].Matches(15, 1, 50),
            "转职筛池用 JobTag、GrowType低4位和等级，不把觉醒高位当成另一分支");
        That(Parse("`[at gunner]` 1 99 0 1 1106 100 1").Pools[0].JobTag == "[at gunner]", "空格职业名保持完整标签");
        foreach (string bad in new[] { "`[unknown]` 1 99 0 1 1106 100 1", "`[swordman]` 1 99 0 0 1106 100 1",
            "`[swordman]` 1 99 16 1 1106 100 1", "`[swordman]` 99 1 0 1 1106 100 1", "`[swordman]` 1 99 0 1 1106 100",
            "1 99 0 1 1106 100 1" })
            That(!Parse(bad).Available, "非法转职奖池不能截成无过滤普通盒: " + bad);
        That(Parse("`[swordman]` 1 99 0 1").Pools.IsDefaultOrEmpty, "空转职占位不制造奖励");
        That(BoosterCatalog.Parse("[stackable type]\n`[booster]` 0\n[booster info]\n[hide booster info]\n[/hide booster info]\n[grow type]\n`[swordman]` 1 99 0 1 1106 100 1\n[/grow type]\n[/booster info]").Available,
            "hide booster info 只影响客户端预览，不把转职池标成未实现");

        var soul = itemResources.Boosters.Get(490704570);
        var copy = itemResources.Boosters.Get(490704415);
        That(soul.Available && copy.Available && soul.Pools.Length == 70 && copy.Pools.Length == 70 &&
            soul.Pools.All(pool => pool.JobTag is not null && pool.Draws == 1) &&
            !soul.Pools.Any(pool => pool.JobTag == "[gun blader]"),
            "灵魂武器礼盒按70段转职池解析，枪剑士没有可抽池");
        That(soul.Pools.Single(pool => pool.JobTag == "[swordman]" && pool.GrowType == 1).Rewards.Length == 137 &&
            soul.Pools.Single(pool => pool.JobTag == "[swordman]" && pool.GrowType == 0).Rewards.Length == 116,
            "鬼剑士不同转职分支保留独立奖池长度");
        var plat = itemResources.Boosters.Get(490705076);
        That(plat.Available && plat.Pools.Length == 75 && plat.Pools.Any(pool => pool.JobTag == "[gun blader]"),
            "白金徽章礼盒共用同一转职行宽");

        var random = new BoosterTestsRandom();
        var service = new InventoryBoosterService(catalog, itemResources.Boosters, itemResources.GoldLimits, itemResources.EpicPieces, random);
        var role = new Character(1, 0, "SoulBox", 0, towns.Room, towns.InitialReturn)
        {
            Job = 0, GrowType = 1, Level = 50, Inventory = new([new(1, Bag(65), Core(490704570))])
        };
        var opened = service.Open(role, new(65, 1), out var receipt, now);
        That(opened.Accepted && receipt is { ItemId: 490704570, Slot: 65, Rewards: [{ ItemId: 101000814, Count: 1 }] } &&
            opened.Plan!.After.Find(Bag(65)) is null &&
            catalog.Get(101000814).Kind == ItemKind.Equipment &&
            opened.Plan.After.Items.Single(item => item.Core.ItemId == 101000814).Core ==
                InventoryRewardGrantService.Create(catalog.Get(101000814), utcNow: now),
            "匹配转职池扣除礼盒并发放一件完整灵魂武器");
        That(service.CountMatchingPools(role with { Job = 15, GrowType = 0 }, 490704570) == (70, 0),
            "枪剑士真实转职池仍为零匹配，与源职业资格独立核对");
        That(service.CountMatchingPools(role with { Level = 0 }, 490704570) == (70, 0),
            "等级越界真实转职池仍为零匹配，与源最低等级资格独立核对");
        That(service.Open(role with { Job = 15, GrowType = 0 }, new(65, 1), out _) is
            { Accepted: false, Error: 19, Plan: null, RejectReason: "booster_source_ineligible" },
            "真实源职业资格排除枪剑士，优先拒绝且不扣源");
        That(service.Open(role with { Level = 0 }, new(65, 1), out _) is
            { Accepted: false, Error: 19, Plan: null, RejectReason: "booster_source_ineligible" },
            "低于真实源最低等级时优先拒绝，不抽其他职业或分支");

        var platService = new InventoryBoosterService(catalog, itemResources.Boosters, itemResources.GoldLimits, itemResources.EpicPieces,
            random, debugSettings: new GameDebugSettings { IgnoreCashShopAbsoluteExpiration = true });
        var platRole = role with { GrowType = 1, Inventory = new([new(1, Bag(65), Core(490705076, shopExpiration: true))]) };
        var platOpen = platService.Open(platRole, new(65, 1), out var platReceipt, now);
        That(platOpen.Accepted && platReceipt is { ItemId: 490705076, Rewards: [{ ItemId: 2540007, Count: 1 }] } &&
            platOpen.Plan!.After.Find(Bag(65)) is null,
            "白金徽章礼盒按同一筛池抽取匹配分支");
        Console.WriteLine($"PASS {Count - checks} booster grow-type assertions");

        ItemCore Core(int id, bool shopExpiration = false) =>
            InventoryRewardGrantService.Create(catalog.Get(id), utcNow: now, shopExpiration: shopExpiration);
    }

    static void FirstGrowQualification(TownCatalog towns, ItemResources resources)
    {
        const string ordinary = "[stackable type]\n`[booster]` 0\n[booster info]\n[etc]\n1 1106 100 1\n[/etc]\n[/booster info]\n";
        const string gate = "[usable first growtype]\n3 4\n[/usable first growtype]\n";
        foreach (string body in new[] { ordinary, "[stackable type]\n`[usable cera package]` 0\n[package data]\n1106 1\n[/package data]\n",
            "[stackable type]\n`[upgradable legacy]` 1\n[int data]\n1106 100 1\n[/int data]\n" })
        {
            var definition = BoosterCatalog.Parse(body + gate);
            That(definition.Available && definition.FirstGrowTypes.SequenceEqual(new byte[] { 3, 4 }) &&
                definition.AllowsFirstGrowType(3) && definition.AllowsFirstGrowType(0x24) && !definition.AllowsFirstGrowType(2) &&
                !definition.AllowsFirstGrowType(0), "首转根资格保留到普通、商城及袖珍罐定义，不依赖奖励类型");
        }
        foreach (string invalid in new[] { "[usable first growtype]", "[usable first growtype]\n[/usable first growtype]",
            "[usable first growtype]\n6\n[/usable first growtype]", "[usable first growtype]\n-1\n[/usable first growtype]",
            "[usable first growtype]\n256\n[/usable first growtype]", "[usable first growtype]\n3 3\n[/usable first growtype]",
            "[usable first growtype]\n`3`\n[/usable first growtype]", gate + gate,
            "[booster info]\n" + gate + "[/booster info]" })
            That(!BoosterCatalog.Parse(ordinary + invalid).Available, "不忽略空、重复、未闭合、嵌套或非法首转限制");
        That(BoosterCatalog.Parse(ordinary + "[explain]\n`[usable first growtype]只是文字`\n").Available,
            "字符串中的资格伪标签不改变正常礼包");
        foreach (int id in new[] { 490005505, 490005504, 490006667, 490006668, 490006882, 490011456, 490703963, 490703964 })
            That(resources.Boosters.Get(id) is { Available: true, FirstGrowTypes.IsEmpty: false }, "实际首转普通或组合礼包完整解析 " + id);
        var epic = BoosterCatalog.Parse("[set epic exchange instance type]\n[stackable type]\n`[booster selection]` 0\n" +
            "[booster category num]\n2 4 9\n[booster selection num]\n1\n[booster select category]\n0 0\n" +
            "[equipment]\n101000783 1\n[/equipment]\n[/booster select category]\n" + gate);
        That(epic.Available && epic.SetsEpicExchangeInstanceType && epic.FirstGrowTypes.SequenceEqual(new byte[] { 3, 4 }) &&
            epic.AllowsFirstGrowType(3) && epic.AllowsFirstGrowType(0x24) && !epic.AllowsFirstGrowType(0) && !epic.AllowsFirstGrowType(2),
            "合法史诗选择盒同时保留来源声明与首转门禁，不能按旧未实现状态一律拒绝");

        var now = DateTimeOffset.UtcNow;
        var random = new BoosterTestsRandom();
        var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits, resources.EpicPieces,
            random, new GameDebugSettings { IgnoreCashShopAbsoluteExpiration = true }, resources.Contracts);
        var template = resources.Items.Get(490011456);
        var source = new InventoryItem(1, Bag(65), InventoryRewardGrantService.Create(template, utcNow: now, shopExpiration: true));
        var role = new Character(1, 0, "FirstGrow", 0, towns.Room, towns.InitialReturn)
            { Job = 0, GrowType = 0x25, Level = 95, Inventory = new([source]) };
        int calls = random.Calls;
        foreach (byte branch in new byte[] { 0, 1, 4 })
            That(service.Open(role with { GrowType = branch }, new(65, 1), out _, now) is { Accepted: false, Plan: null } && random.Calls == calls,
                "错误分支在随机及扣源前拒绝 " + branch);
        var opened = service.Open(role, new(65, 1), out var receipt, now);
        That(opened.Accepted && receipt is { Rewards: [{ ItemId: 10305616, Count: 200 }] } &&
            opened.Plan!.After.Find(Bag(65)) is null && role.Inventory.Items.Single() == source &&
            opened.Plan.CharacterPrecondition!.MatchesIdentity(role) &&
            !opened.Plan.CharacterPrecondition.MatchesIdentity(role with { GrowType = 4 }),
            "真实第五分支礼盒发完整200奖励，觉醒保留资格，旧计划不能绑定转职后的角色");
        foreach (int id in new[] { 490703963, 490703964 })
        {
            var item = new InventoryItem(1, Bag(65), InventoryRewardGrantService.Create(resources.Items.Get(id), utcNow: now, shopExpiration: true));
            var owner = role with { Job = id == 490703963 ? (byte)0 : (byte)3, GrowType = 0x25, Inventory = new([item]) };
            That(!service.OpenCeraPackage(owner with { GrowType = 4 }, new(65, []), out _, now).Accepted,
                "商城组合包同样拒绝错误首转，不扣源或发部分奖励 " + id);
            var result = service.OpenCeraPackage(owner, new(65, []), out var rewards, now);
            That(result.Accepted && rewards!.Rewards.Sum(row => row.Count) == 18 && result.Plan!.After.Items.All(row => row.Core.ItemId != id) &&
                !result.Plan.CharacterPrecondition!.MatchesIdentity(owner with { GrowType = 4 }),
                "真实首转商城组合礼包完整发放18件并绑定首转资格 " + id);
        }
    }

    static BoosterDefinition Parse(string body) =>
        BoosterCatalog.Parse("[stackable type]\n`[booster]` 0\n[booster info]\n[grow type]\n" + body + "\n[/grow type]\n[/booster info]");
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);

    sealed class BoosterTestsRandom : Random
    {
        public int Calls { get; private set; }
        public override long NextInt64(long maxValue) { Calls++; return 0; }
    }
}
