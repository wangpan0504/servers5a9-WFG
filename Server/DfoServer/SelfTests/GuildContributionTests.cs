using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using DfoServer.Network.Builders;
using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GuildContributionTests
{
    public static void RunCore()
    {
        int before = Count;
        long boundary = new DateTimeOffset(2026, 10, 29, 6, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeSeconds();
        That(GuildContributionRules.WeekStart(boundary) == boundary &&
            GuildContributionRules.WeekStart(boundary - 1) == boundary - 7 * 86400 &&
            GuildContributionRules.WeekStart(boundary + 7 * 86400 - 1) == boundary,
            "贡献周在本地周四06:00精确切换");
        That(new[] { 0, 1, 1000, 1001, 3000, 3001, 5000, 5001, 8000, 8001, 10000, 10001, 15000, 15001 }
            .Select(GuildContributionRules.Coins).SequenceEqual(new[] { 0, 300, 300, 500, 500, 700, 700, 1000, 1000, 1500, 1500, 2000, 2000, 2500 }),
            "周币七个资源区间边界逐项匹配，不按固定比例伪造兑换");
        That(GuildContributionRules.Points(GuildContributionSource.Login) == 100 &&
            GuildContributionRules.Points(GuildContributionSource.Chat) == 50 &&
            GuildContributionRules.CoinLimit(0) == 10000 && GuildContributionRules.CoinLimit(4) == 30000,
            "自然每日贡献及现代持有上限使用本版规则");
        That(new[] { 0, 1, 2, 3, 4 }.Select(count => GuildContributionRules.Points(GuildContributionSource.RecommendedDungeon, 0, count))
                .SequenceEqual(new[] { 10, 10, 15, 30, 40 }) &&
            new uint[] { 8500, 8503, 8504, 8505, 8507, 8508 }.Select(id => GuildContributionRules.Points(GuildContributionSource.GuildDungeon, id, 1))
                .SequenceEqual(new[] { 60, 80, 80, 100, 100, 150 }),
            "同会人数2/3/4使用1.5/3/4倍，六张公会图采用独立贡献分值");
        Reject<InvalidDataException>(() => GuildContributionRules.Points(GuildContributionSource.GuildDungeon, 8501, 1),
            "未声明公会图不能沿用相邻图分值");
        Reject<InvalidDataException>(() => GuildContributionRules.Points(GuildContributionSource.SpecialDungeon, 0, 5),
            "非法队伍人数不能超发贡献");
        string path = Path.Combine(Path.GetTempPath(), "guild-contribution-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("guild-contrib", static () => { });
            using var other = owners.Acquire("guild-contrib-other", static () => { });
            var room = new Location(38, 1, 100, 100);
            var member = repository.Create(owner, new("ContribOne", room, room)
                { DungeonState = CharacterDungeonState.NewOrdinary });
            var foreign = repository.Create(other, new("ContribTwo", room, room));
            var towns = new TownCatalog(new Dictionary<(byte, byte), TownArea>(), room, room,
                new TownDock(0, [], []), [], new Dictionary<uint, uint[]>());
            GuildManagementIdentity Identity(AccountOwners.Lease lease, Character character) =>
                new(new GameSession(repository, towns, lease), lease, character.Id, character.Name, 1, new(2, 10), () => lease.IsCurrent);
            var actor = Identity(owner, member);
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            void Sql(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            long Number(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
            Sql($"""
                INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix)
                VALUES(1,'贡献公会','contrib',{member.Id},1000);
                INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{member.Id},1,1000)
                """);
            That(repository.ObserveGuildContribution(actor, GuildContributionSource.Login, boundary - 100).Points == 100 &&
                repository.ObserveGuildContribution(actor, GuildContributionSource.Login, boundary - 99).Points == 0 &&
                repository.ObserveGuildContribution(actor, GuildContributionSource.Chat, boundary - 98).Points == 50 &&
                Number("SELECT contribution FROM guild_members") == 150,
                "真实登录与聊天每日独立去重，只累加一次当前会籍贡献");
            var panel = repository.ReadGuildPanel(owner, member.Id)!;
            panel = panel with { Members = [panel.Members.Single() with { Contribution = 456, WeeklyContribution = 123 }] };
            byte[] projected = GuildPackets.Members(panel, 0, 1, true, false);
            That(BinaryPrimitives.ReadUInt32LittleEndian(projected.AsSpan(projected.Length - 14)) == 123 &&
                BinaryPrimitives.ReadUInt32LittleEndian(projected.AsSpan(projected.Length - 10)) == 0 &&
                BinaryPrimitives.ReadUInt32LittleEndian(projected.AsSpan(projected.Length - 6)) == 456,
                "成员行贡献在原生+188及+190对应位置投影，中间字段保持独立");
            That(repository.ObserveGuildContribution(Identity(owner, foreign), GuildContributionSource.Chat, boundary - 98).Error == 3 &&
                Number("SELECT count(*) FROM guild_contribution_events") == 2,
                "错误账号不能借其他角色记录每日贡献");
            That(repository.ObserveGuildContribution(Identity(other, foreign), GuildContributionSource.Login, boundary - 98).Success &&
                Number("SELECT count(*) FROM guild_contribution_events WHERE guild_id IS NULL AND points=0") == 0,
                "非会员登录不抢占尚未发生的公会每日登录资格");
            int planned = 0;
            That(repository.SettleGuildContribution(actor, member, boundary - 1, (_, _) => throw new InvalidOperationException()).Success,
                "周四六点前不提前结算本周贡献");
            That(repository.SettleGuildContribution(actor, member, boundary, (_, amount) => { planned = amount; return null; }).Error == 13 &&
                planned == 300 && Number("SELECT count(*) FROM guild_contribution_settlements") == 0,
                "周界到达按真实累计150点规划300币，背包放不下不消费结算资格");
            Sql("CREATE TRIGGER contribution_abort AFTER INSERT ON guild_contribution_events BEGIN SELECT RAISE(ABORT,'contribution rollback'); END");
            Reject<SqliteException>(() => repository.ObserveGuildContribution(actor, GuildContributionSource.Login, boundary),
                "注入贡献事件失败真实抛出存储异常");
            That(Number("SELECT contribution FROM guild_members") == 150 && Number("SELECT count(*) FROM guild_contribution_events") == 2,
                "事件写失败连同先前累计贡献更新一起回滚");
            Sql("DROP TRIGGER contribution_abort");
            That(repository.ObserveGuildContribution(actor, GuildContributionSource.Login, boundary).Points == 100,
                "下个业务日可自然获得新的每日贡献");
            InventoryPlan Grant(Character character, int amount) => new(character.Inventory,
                character.Inventory with { Items = character.Inventory.Items.Add(new(0, new(InventoryListType.Main, 121),
                    new(GuildContributionRules.CoinItemId, ItemKind.Material, amount))) },
                "guild_weekly_coins", new(character));
            Sql("CREATE TRIGGER settlement_abort AFTER INSERT ON guild_contribution_settlements BEGIN SELECT RAISE(ABORT,'weekly rollback'); END");
            Reject<SqliteException>(() => repository.SettleGuildContribution(actor, member, boundary, Grant),
                "注入周币回执失败真实抛出存储异常");
            That(repository.LoadCharacter(owner, member.Slot).Inventory.Items.IsEmpty &&
                Number("SELECT count(*) FROM guild_contribution_settlements") == 0 &&
                repository.LoadCharacter(owner, member.Slot).Revision == member.Revision,
                "发币与周回执处于同一事务，后段失败不留资产或提前消费资格");
            Sql("DROP TRIGGER settlement_abort");
            var paid = repository.SettleGuildContribution(actor, member, boundary, Grant);
            That(paid is { Success: true, Coins: 300, Character: not null } &&
                paid.Character.Inventory.Find(new(InventoryListType.Main, 121)) is { Uid: > 0, Core.Count: 300 } &&
                repository.SettleGuildContribution(actor, member, boundary + 1, (_, _) => throw new InvalidOperationException()).Coins == 0 &&
                Number("SELECT count(*) FROM guild_contribution_settlements") == 1,
                "周币按真实库存事务分配UID，重复结算与断线重试不重复到账");
            Sql($"UPDATE guild_members SET joined_at_unix={boundary - 90},contribution=0 WHERE character_id={member.Id}");
            That(repository.ObserveGuildContribution(actor, GuildContributionSource.Chat, boundary - 80).Points == 0 &&
                repository.SettleGuildContribution(actor, member, boundary, (_, _) => throw new InvalidOperationException()).Success,
                "退会重入不能刷同日聊天，也不能领取旧会籍周币");
            var reopened = new CharacterRepository(path);
            That(reopened.ObserveGuildContribution(actor, GuildContributionSource.Login, boundary).Points == 0 &&
                Number("SELECT public_funds FROM guilds") == 0 && repository.LoadCharacter(owner, member.Slot).Revision == member.Revision + 1,
                "重开保留去重回执，贡献不混入公共积分，仅真实发币推进一次资产revision");
            var capped = repository.Create(other, new("CapMember", room, room)
            {
                DungeonState = CharacterDungeonState.NewOrdinary,
                Inventory = new([new(0, new(InventoryListType.Main, 121),
                    new(GuildContributionRules.CoinItemId, ItemKind.Material, 9900))])
            });
            Sql($"""
                INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix)
                VALUES(2,'上限公会','contrib-cap',{capped.Id},1000);
                INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(2,{capped.Id},1,1000)
                """);
            var cappedActor = Identity(other, capped);
            repository.ObserveGuildContribution(cappedActor, GuildContributionSource.Login, boundary - 100);
            That(repository.SettleGuildContribution(cappedActor, capped, boundary, (_, _) => throw new InvalidOperationException()).Error == 22 &&
                Number("SELECT count(*) FROM guild_contribution_settlements WHERE guild_id=2") == 0 &&
                repository.List(other).Single(character => character.Id == capped.Id).Inventory.Find(new(InventoryListType.Main, 121))!.Core.Count == 9900,
                "持有上限不足保留整周待结算资格，不吞币、不超发也不调用发币规划");
            var currentMember = repository.LoadCharacter(owner, member.Slot);
            var frozenEntry = repository.ReserveDungeonEntry(owner, currentMember, 65536);
            That(frozenEntry.GuildMembership == new GuildDungeonMembership(member.Id, 1, boundary - 90),
                "无费用单人入场在尝试号同一事务冻结当前会籍");
            Guid instance = Guid.NewGuid();
            var entries = repository.CommitPartyDungeonEntry(new[]
            {
                new PartyDungeonReservation(owner, frozenEntry.Character, 65536,
                    new(null, 0, new(member.Id, 0, frozenEntry.Character.DungeonState!.LastAttempt + 1, instance, 1), 1)),
                new PartyDungeonReservation(other, capped, 65536,
                    new(null, 0, new(capped.Id, 0, 1, instance, 1), 1))
            });
            That(entries.All(entry => entry.GuildMemberships.Length == 2) &&
                entries[0].GuildMemberships.SequenceEqual(entries[1].GuildMemberships) &&
                entries[0].GuildMemberships[0].GuildId == 1 && entries[0].GuildMemberships[1].GuildId == 2,
                "组队入场在全员费用事务冻结各自真实会籍，不把不同公会当成同会成员");
            Sql("DELETE FROM guilds WHERE guild_id=1");
            That(Number("SELECT count(*) FROM guild_contribution_events WHERE character_id=" + member.Id) == 3 &&
                Number("SELECT count(*) FROM pragma_foreign_key_check") == 0 && entries[0].GuildMemberships[0].GuildId == 1,
                "解散保留每日去重墓碑，避免通过新建公会重复刷贡献");
            using var replacement = owners.Acquire(owner.Account, static () => { });
            Reject<InvalidOperationException>(() => repository.ObserveGuildContribution(actor, GuildContributionSource.Chat, boundary),
                "账号被接管后旧owner不能记贡献");
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
        Console.WriteLine($"PASS {Count - before} 公会贡献来源、日周边界及结算事务断言");
    }
}
