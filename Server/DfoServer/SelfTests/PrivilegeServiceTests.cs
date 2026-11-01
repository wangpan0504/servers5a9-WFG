using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.PrivilegeServices;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class PrivilegeServiceTests
{
    const string Rules = """
        [now season]
        1
        [teleport cooltime]
        5
        [daily login reward]
        0 1108 1 1 1108 2
        [/daily login reward]
        [accumulate login reward]
        [season]
        0
        [reward]
        5 1108 3 15 1108 4 25 1108 5
        [/reward]
        [/accumulate login reward]
        [accumulate login reward]
        [season]
        1
        [reward]
        5 1108 6 15 1108 7 25 1108 8
        [/reward]
        [/accumulate login reward]
        """;

    public static void RunCore()
    {
        That(ReadNativeReward(PrivilegeServicePackets.RewardCommitted()) == (true, (ushort)0, 0),
            "1640公共成功标记消费后仍有完整4字节供原生领取回调读取");
        That(ReadNativeReward(PrivilegeServicePackets.RewardCapacityRejected()) == (false, (ushort)0, 3),
            "1640满包拒绝经公共失败码后保留原生私有错误3");
        Reject<EndOfStreamException>(() => ReadNativeReward(new byte[4]),
            "旧4字节零值不能同时满足公共失败信封和私有回调，必须拒绝截断");
        var product = new CashShopProduct(700911, 490703912, 1, 100, "privilege service", 30);
        var catalog = PrivilegeServiceCatalog.Parse(Rules, [product]);
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var state = PrivilegeServicePolicy.Purchase(PrivilegeServiceState.Empty, product, catalog, now);
        That(state.IsActive(now.ToUnixTimeSeconds()) && !state.IsActive(state.StartedAt - 1) &&
            state.IsActive(state.ExpiresAt - 1) && !state.IsActive(state.ExpiresAt) &&
            state.ExpiresAt - state.StartedAt == 30 * 86400 && state.LoginDays == 1,
            "礼遇期限按真实购买时刻计算，起止边界和购买日登录各计一次");
        Reject<InvalidOperationException>(() => PrivilegeServicePolicy.Purchase(state, product, catalog, now),
            "有效期内不叠加购买");
        var next = state.ObserveLogin(state.StartedAt + 86400, state.LastLoginDay + 1);
        That(next.LoginDays == 2 && next.ObserveLogin(state.StartedAt + 86401, next.LastLoginDay) == next &&
            next.ObserveLogin(state.StartedAt + 86402, next.LastLoginDay - 1) == next &&
            next.ObserveLogin(state.ExpiresAt, next.LastLoginDay + 1) == next,
            "重复登录、时钟回退和已过期登录不增加天数");
        var renewed = PrivilegeServicePolicy.Purchase(state, product, catalog, DateTimeOffset.FromUnixTimeSeconds(state.ExpiresAt));
        That(renewed.LoginDays == 1 && renewed.ClaimedMilestones == 0 && renewed.DailyClaimDay == 0,
            "到期重新购买建立新一期领取资格");
        byte[] info = PrivilegeServicePackets.Information(state with { LoginDays = 5, ClaimedMilestones = 1 }, now);
        That(info.Length == 21 && BinaryPrimitives.ReadInt32LittleEndian(info) == 0 &&
            BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(4)) == state.ExpiresAt &&
            info[8] == 0 && BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(9)) == 5 &&
            BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(13)) == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(17)) == 1,
            "1644原生21字节布局与绝对期限、登录天数及领取位图一致");
        That(BinaryPrimitives.ReadInt32LittleEndian(PrivilegeServicePackets.Information(PrivilegeServiceState.Empty, now)) == 2 &&
            BinaryPrimitives.ReadInt32LittleEndian(PrivilegeServicePackets.Information(state, now.AddDays(30))) == 1,
            "原生区分从未购买及已过期状态");
        byte[] request = Enumerable.Repeat((byte)0xCD, 21).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(13), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(17), 15);
        That(PrivilegeServiceRequests.Reward(request) == new PrivilegeRewardRequest(1, 15),
            "领取仅解析原生明确初始化的动作与天数，不信前13字节栈内容");
        Reject<InvalidDataException>(() => PrivilegeServiceRequests.Reward(request.AsSpan(1)), "领取拒绝截断帧");
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(13), 2);
        Reject<InvalidDataException>(() => PrivilegeServiceRequests.Reward(request), "未知领取动作不得伪造成功");
        Reject<InvalidDataException>(() => PrivilegeServiceCatalog.Parse(Rules.Replace("25 1108 8", "15 1108 8"), [product]),
            "累计天数重复拒绝资源装配");
        Reject<InvalidDataException>(() => (state with { ExpiresAt = state.StartedAt }).Validate(), "非法期限不能保存");
        Console.WriteLine("PASS privilege service rules and native wire");
    }

    // 对照028F82A0公共游标及00978C80消费者，独立按实际读取顺序验证完整帧。
    static (bool Success, ushort Error, int Result) ReadNativeReward(byte[] body)
    {
        using var stream = new MemoryStream(body);
        using var reader = new BinaryReader(stream);
        bool success = reader.ReadByte() != 0;
        ushort error = success ? (ushort)0 : reader.ReadUInt16();
        int result = reader.ReadInt32();
        That(stream.Position == stream.Length, "1640公共及私有消费者完整消费，不遗留多余字段");
        return (success, error, result);
    }

    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources resources, PvfArchive archive)
    {
        var product = resources.CashShop.Products.Values.Single(row => row.Section == "privilege service");
        That(product is { ItemId: 490703912, Count: 1, DurationDays: 30 } && resources.PrivilegeServices.CurrentSeason == 1 &&
            resources.PrivilegeServices.AccumulatedRewards[1].Select(row => row.Days).SequenceEqual(new[] { 5, 15, 25 }),
            "真实礼遇商品、期限、季及累计天数来自匹配资源");
        string root = Path.Combine(Path.GetTempPath(), "s5a9-privilege-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("privilege", () => { });
            var character = repository.Create(owner, new("Privilege", towns.Room, towns.InitialReturn)
            {
                Skills = characters.Get(0).Skills,
                Inventory = InventoryRewardGrantService.CreateStarter(characters.Get(0))
            });
            character = InventoryService.Commit(repository, owner, character,
                new CurrencyService(resources.GoldLimits).Grant(character, CurrencyKind.Cera, checked(product.CeraPrice * 3)));
            var other = repository.Create(owner, new("NoPrivilege", towns.Room, towns.InitialReturn)
                { Inventory = InventoryRewardGrantService.CreateStarter(characters.Get(0)) });
            var clock = new LukeRaidClockTests.Clock();
            var service = new InventoryCashShopService(resources.Items, resources.CashShop, resources.GoldLimits,
                resources.EpicPieces, resources.Cargo, clock: clock, privileges: resources.PrivilegeServices);
            var buy = new CashShopPurchaseRequest(0, [new(product.CommodityId, 0)]);
            var plan = service.Buy(character, buy, out var receipt);
            That(plan.Accepted && receipt?.Products.Single() == product && plan.Plan!.After.Items.SequenceEqual(character.Inventory.Items),
                "购买礼遇只激活角色资格，不误发可交易契约实体");
            Execute("CREATE TRIGGER fail_privilege BEFORE INSERT ON character_privilege_service BEGIN SELECT RAISE(ABORT,'privilege rollback'); END");
            Reject<SqliteException>(() => InventoryService.Commit(repository, owner, character, plan), "礼遇账本写入失败回滚点券及库存");
            That(repository.LoadCharacter(owner, character.Slot).Inventory.AccountWallet.Get(CurrencyKind.Cera) == product.CeraPrice * 3 &&
                repository.LoadCharacter(owner, character.Slot).Inventory.PrivilegeService == PrivilegeServiceState.Empty,
                "失败不留下扣款、资格或领取状态");
            Execute("DROP TRIGGER fail_privilege");
            character = InventoryService.Commit(repository, owner, character, plan);
            That(!service.Buy(character, buy, out _).Accepted &&
                repository.LoadCharacter(owner, other.Slot).Inventory.PrivilegeService == PrivilegeServiceState.Empty,
                "重复购买及其他角色不继承资格");
            var rewards = new PrivilegeServiceRewards(resources.PrivilegeServices, resources.Items, resources.GoldLimits, resources.EpicPieces, clock);
            var daily = rewards.Claim(character, 0, 0);
            That(daily.Accepted, "真实每日奖励可规划");
            Execute("CREATE TRIGGER fail_privilege_claim BEFORE UPDATE ON character_privilege_service BEGIN SELECT RAISE(ABORT,'claim rollback'); END");
            Reject<SqliteException>(() => InventoryService.Commit(repository, owner, character, daily), "领取账本失败回滚奖励实体及资格");
            That(repository.LoadCharacter(owner, character.Slot).Revision == character.Revision &&
                repository.LoadCharacter(owner, character.Slot).Inventory.PrivilegeService.DailyClaimDay == 0,
                "晚失败不吞当日领取机会");
            Execute("DROP TRIGGER fail_privilege_claim");
            character = InventoryService.Commit(repository, owner, character, daily);
            That(!rewards.Claim(character, 0, 0).Accepted && !rewards.Claim(character, 1, 5).Accepted &&
                !rewards.Claim(other, 0, 0).Accepted, "重复、未达累计天数和无资格均拒绝");
            clock.Advance(4 * 86400);
            for (int day = 1; day <= 4; day++)
            {
                var state = character.Inventory.PrivilegeService;
                character = repository.CommitInventory(owner, character, new(character.Inventory,
                    character.Inventory with { PrivilegeService = state.ObserveLogin(state.StartedAt + day * 86400, state.LastLoginDay + 1) }, "fixture_login"));
            }
            character = InventoryService.Commit(repository, owner, character, rewards.Claim(character, 1, 5));
            var restored = new CharacterRepository(path).LoadCharacter(owner, character.Slot);
            That(restored.Inventory.PrivilegeService is { LoginDays: 5, ClaimedMilestones: 1 } &&
                !rewards.Claim(restored, 1, 5).Accepted, "重开仓储恢复累计登录及领取位图，重放不再发奖");
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject<InvalidOperationException>(() => InventoryService.Commit(repository, owner, restored,
                rewards.Claim(restored, 0, 0)), "旧owner不能提交新一期领取");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
        Console.WriteLine("PASS privilege purchase, claim and persistence resources");
        RunRaidRewards(towns, characters, resources, archive);

        void Execute(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + path); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
    }
}
