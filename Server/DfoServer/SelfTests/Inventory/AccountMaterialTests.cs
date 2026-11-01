using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Infrastructure;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Persistence;
using DfoServer.Game.Raids;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class AccountMaterialTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.FromHours(8));
    const int Coin = 10308807;

    public static void RunCore()
    {
        int week = AccountMaterialService.RaidWeek(Now);
        var reset = AccountMaterialService.WeekStart(week).AddDays(7);
        That(AccountMaterialService.RaidWeek(reset.AddTicks(-1)) == week &&
            AccountMaterialService.RaidWeek(reset) == week + 7, "账号材料周四六点边界独立于团队次数周");
        var definition = new AccountMaterialDefinition(4000, 4000, 1300);
        var inventory = InventorySnapshot.Empty;
        foreach (ushort character in new ushort[] { 1, 2, 3, 4 })
            inventory = AccountMaterialService.Grant(inventory, character, Coin, 2000, definition, Now, true).Plan!.After;
        var state = inventory.AccountWallet.Material(Coin);
        That(state.Balance == 4000 && state.RaidEarned == 4000 && state.EarnedBy(4, week) == 100 &&
            state.EarnedBy(1, week) == 1300, "多人及多角色合并账号额度且保留角色额度和部分余量");
        inventory = inventory with { AccountWallet = inventory.AccountWallet.SetMaterial(Coin, state with { Balance = 0 }) };
        var exhausted = AccountMaterialService.Grant(inventory, 1, Coin, 150, definition, Now, true).Plan!;
        That(exhausted.After.AccountWallet.Material(Coin).Balance == 0 && exhausted.After.AccountWallet.Material(Coin).RaidEarned == 4000,
            "消费余额不返还本周获得额度，达到上限不阻断其他奖励");
        var renewed = AccountMaterialService.Grant(inventory, 1, Coin, 150, definition, reset, true).Plan!;
        That(renewed.After.AccountWallet.Material(Coin) is { Balance: 150, RaidEarned: 150 } next &&
            next.EarnedBy(1, week + 7) == 150 && next.EarnedBy(2, week + 7) == 0, "跨周重置额度而非余额");
        That(!AccountMaterialService.Grant(inventory, 1, Coin, 4001, definition, Now, false).Accepted,
            "普通购买领取按持有上限足额获取或拒绝，不静默吞掉数量");
        var ordinary = AccountMaterialService.Grant(inventory, 1, Coin, 10, definition, Now, false).Plan!.After;
        That(ordinary.AccountWallet.Material(Coin) is { Balance: 10, RaidEarned: 4000 }, "非攻坚获取不消耗攻坚周额度");
        var many = AccountWallet.Empty;
        for (int id = 1; id <= 256; id++) many = many.SetMaterial(id, new(id));
        var frames = AccountMaterialPackets.Balances(many).ToArray();
        That(frames.Length == 2 && frames[0][0] == 0 && frames[0][1] == 255 && frames[0].Length == 2042 &&
            frames[1][0] == 1 && frames[1][1] == 1 && frames[1].Length == 10 &&
            BinaryPrimitives.ReadInt32LittleEndian(frames[1].AsSpan(2)) == 256, "563先全量后增量分片，不截断u8行数");
        That(AccountMaterialPackets.Balances(AccountWallet.Empty).Single().SequenceEqual(new byte[] { 0, 0 }) &&
            !AccountMaterialPackets.Balances(many, many).Any(), "空账号清旧缓存，无余额变化不伪造差量");
        var deleted = AccountMaterialPackets.Balances(AccountWallet.Empty, AccountWallet.Empty.SetMaterial(Coin, new(10))).Single();
        That(deleted.Length == 10 && deleted[0] == 1 && deleted[1] == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(deleted.AsSpan(6)) == 0, "563消费至零明确删除客户端旧项");
        var limits = new Dictionary<int, AccountMaterialDefinition> { [Coin] = definition };
        var accountBody = AccountMaterialPackets.Limits(ordinary.AccountWallet, 1, limits, Now, true);
        var characterBody = AccountMaterialPackets.Limits(ordinary.AccountWallet, 1, limits, Now, false);
        That(accountBody.Length == 14 && BinaryPrimitives.ReadUInt16LittleEndian(accountBody) == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(accountBody.AsSpan(2)) == Coin &&
            BinaryPrimitives.ReadInt32LittleEndian(accountBody.AsSpan(6)) == 4000 &&
            BinaryPrimitives.ReadInt32LittleEndian(accountBody.AsSpan(10)) == 4000 &&
            BinaryPrimitives.ReadInt32LittleEndian(characterBody.AsSpan(6)) == 1300 &&
            BinaryPrimitives.ReadInt32LittleEndian(characterBody.AsSpan(10)) == 1300, "1460/1461各自完整三u32行，角色与账号不混用");
        Console.WriteLine("PASS account material core: 10 checks");
    }

    public static void Run(TownCatalog towns, ItemResources resources)
    {
        var template = resources.Items.Get(Coin);
        That(template.AccountMaterial is { HoldingLimit: 4000, WeeklyAccountLimit: 4000, WeeklyCharacterLimit: 1300 },
            "不渝材料容器、持有和两级额度来自匹配资源");
        string root = Path.Combine(Path.GetTempPath(), "s5a9-account-material-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("materials", () => { });
            var character = repository.Create(owner, new("MaterialOne", towns.Room, towns.InitialReturn)
                { Inventory = InventorySnapshot.Empty with { Items = [new(0, new(InventoryListType.Main, 121), new(Coin, ItemKind.Material, 4200))] } });
            using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                db.Open();
                using var command = db.CreateCommand();
                var historical = new PreparedRaidReward(Guid.NewGuid(), 0, 1, 0, character.Id, "fixture/account-material", [new(Coin, 150)],
                    true, 20261027) { RaidType = RaidKind.Fiendwar };
                command.CommandText = """
                    INSERT INTO character_raid_reward_receipts(character_id,account,instance_id,phase,kind,card,week_key,final_phase,plan_json,mail_ids,created_at,raid_type)
                    VALUES($id,'materials',$instance,0,1,0,20261027,1,$plan,'[]',$now,2)
                    """;
                command.Parameters.AddWithValue("$id", character.Id);
                command.Parameters.AddWithValue("$instance", historical.Instance.ToString("N"));
                command.Parameters.AddWithValue("$plan", JsonSerializer.Serialize(historical));
                command.Parameters.AddWithValue("$now", Now.ToUnixTimeSeconds());
                command.ExecuteNonQuery();
            }
            var legacy = AccountMaterialService.Legacy(character, resources.Items);
            character = repository.ReconcileAccountMaterials(owner, character, legacy, resources.Items.RaidAccountMaterialLimits, Now);
            That(character.Inventory.Items.IsEmpty && character.Inventory.AccountWallet.Material(Coin) is { Balance: 4200, RaidEarned: 150 },
                "旧实体超上限存量不截断，当前周历史真实领奖回填额度但不重复发余额");
            var unchanged = repository.ReconcileAccountMaterials(owner, character, AccountMaterialService.Legacy(character, resources.Items),
                resources.Items.RaidAccountMaterialLimits, Now);
            That(unchanged.Revision == character.Revision && unchanged.Inventory.AccountWallet.Same(character.Inventory.AccountWallet),
                "重复选角迁移不复制余额或重复重建周额度");
            var second = repository.Create(owner, new("MaterialTwo", towns.Room, towns.InitialReturn));
            That(second.Inventory.AccountWallet.Material(Coin).Balance == 4200, "另一角色读取同一账号余额");
            var spent = InventoryMaterialConsumptionService.Plan(character.Inventory, [new(Coin, 3000)], resources.EpicPieces, Now);
            character = InventoryService.Commit(repository, owner, character, spent);
            var grant = InventoryRewardGrantService.TryGrantBatch(character, [new(Coin, 150)], resources.Items, resources.GoldLimits,
                resources.EpicPieces, Now, raidReward: true);
            character = InventoryService.Commit(repository, owner, character, grant);
            That(character.Inventory.Items.IsEmpty && character.Inventory.AccountWallet.Material(Coin) is { Balance: 1350, RaidEarned: 300 },
                "真实发奖无需背包格，扣料不返周额度，获得计入原账号账本");
            bool staleRejected = false;
            try { InventoryService.Commit(repository, owner, second, InventoryMaterialConsumptionService.Plan(second.Inventory,
                [new(Coin, 1)], resources.EpicPieces, Now)); }
            catch (InvalidOperationException) { staleRejected = true; }
            That(staleRejected, "其他角色旧钱包快照不能覆盖新余额");
            using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "CREATE TRIGGER reject_material_audit BEFORE INSERT ON account_material_audit BEGIN SELECT RAISE(ABORT,'fixture'); END";
                command.ExecuteNonQuery();
            }
            bool rolledBack = false;
            try { InventoryService.Commit(repository, owner, character, InventoryRewardGrantService.TryGrantBatch(character,
                [new(Coin, 100)], resources.Items, resources.GoldLimits, resources.EpicPieces, Now, raidReward: true)); }
            catch (SqliteException) { rolledBack = true; }
            var reloaded = repository.LoadCharacter(owner, character.Slot);
            That(rolledBack && reloaded.Revision == character.Revision &&
                reloaded.Inventory.AccountWallet.Same(character.Inventory.AccountWallet), "审计后段失败回滚材料、额度和两个revision");
            using var other = new AccountOwners().Acquire("other-materials", () => { });
            That(repository.LoadAccountWallet(other).Materials.IsEmpty, "账号材料不会串到其他账号");
            Console.WriteLine("PASS account material resources/persistence: 8 checks");
            RunBooster(towns, resources, repository, path);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    static void RunBooster(TownCatalog towns, ItemResources resources, CharacterRepository repository, string path)
    {
        using var owner = new AccountOwners().Acquire("material-boxes", () => { });
        var character = repository.Create(owner, new("MatBoxes", towns.Room, towns.InitialReturn)
        {
            Inventory = InventorySnapshot.Empty with { Items = [new(0, new(InventoryListType.Main, 65),
                InventoryRewardGrantService.Create(resources.Items.Get(10306817), 4, Now))] }
        });
        var clock = new LukeRaidClockTests.Clock(Now);
        var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits,
            resources.EpicPieces, clock: clock);
        var opened = service.Open(character, new(65, 1), out var receipt);
        That(opened.Accepted && receipt!.Rewards.SequenceEqual(new BoosterGrantedItem[] { new(Coin, 150) }),
            "真实150礼盒走普通160开盒规则，回执展示足额实际材料");
        using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            // 前段资源回归设置的真实审计故障保持生效，验证源盒与材料一起回滚。
            bool rolledBack = false;
            try { InventoryService.Commit(repository, owner, character, opened); }
            catch (SqliteException) { rolledBack = true; }
            var reloaded = repository.LoadCharacter(owner, character.Slot);
            That(rolledBack && reloaded.Revision == character.Revision &&
                reloaded.Inventory.AccountWallet.Same(character.Inventory.AccountWallet) &&
                reloaded.Inventory.Items.SequenceEqual(character.Inventory.Items), "开盒审计失败同时回滚源盒、余额、额度和revision");
            command.CommandText = "DROP TRIGGER reject_material_audit";
            command.ExecuteNonQuery();
        }
        character = InventoryService.Commit(repository, owner, character, opened);
        int week = AccountMaterialService.RaidWeek(Now);
        That(character.Inventory.AccountWallet.Material(Coin) is { Balance: 150, RaidEarned: 150 } &&
            character.Inventory.Find(new(InventoryListType.Main, 65))?.Core.Count == 3,
            "开盒提交同时扣一盒并累计同一账号和本角色额度");
        var near = WithMaterial(new(1150, week, 1150,
            ImmutableSortedDictionary<ushort, int>.Empty.Add(character.Id, 1150)));
        var exact = service.Open(near, new(65, 1), out _);
        That(exact.Accepted && exact.Plan!.After.AccountWallet.Material(Coin) is { Balance: 1300, RaidEarned: 1300 },
            "角色周额度恰好足额时允许完整兑换");
        That(!service.Open(near with { Inventory = exact.Plan!.After }, new(65, 1), out _).Accepted &&
            !service.Open(near, new(65, 2), out _).Accepted, "角色满额和批量超过余量均拒绝整笔，保留未兑换盒");
        That(!service.Open(WithMaterial(new(0, week, 3900)), new(65, 1), out _).Accepted &&
            !service.Open(WithMaterial(new(4000)), new(65, 1), out _).Accepted,
            "账号周余量和持有空间不足时不吞礼盒，不把原150显示为虚假到账");
        var reset = AccountMaterialService.WeekStart(week).AddDays(7);
        var resetClock = new LukeRaidClockTests.Clock(reset.AddSeconds(-1));
        var resetService = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits,
            resources.EpicPieces, clock: resetClock);
        var stale = resetService.Open(character, new(65, 1), out _);
        resetClock.Advance(1);
        bool expired = false;
        try { InventoryService.Commit(repository, owner, character, stale); }
        catch (InventoryQualificationExpiredException) { expired = true; }
        That(expired && repository.LoadCharacter(owner, character.Slot).Inventory.AccountWallet.Same(character.Inventory.AccountWallet),
            "周四六点前规划不能在重置后以旧额度提交或扣盒");
        character = InventoryService.Commit(repository, owner, character, resetService.Open(character, new(65, 1), out _));
        That(character.Inventory.AccountWallet.Material(Coin) is { Balance: 300, RaidEarned: 150 } next && next.RaidWeek == week + 7,
            "重置后重新开盒只重置获得额度，保留既有余额并正常扣盒");
        Console.WriteLine("PASS account material booster: 9 checks");

        Character WithMaterial(AccountMaterialBalance state) => character with { Inventory = character.Inventory with
            { AccountWallet = character.Inventory.AccountWallet.SetMaterial(Coin, state) } };
    }
}
