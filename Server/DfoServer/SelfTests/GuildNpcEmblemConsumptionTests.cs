using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Guilds;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GuildNpcEmblemConsumptionTests
{
    sealed class Clock(long unix) : TimeProvider
    {
        public long Unix { get; set; } = unix;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Unix);
    }

    public static void RunCore()
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "guild-emblem-consume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { Run(Path.Combine(root, "game.db")); }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - before} guild NPC emblem consumption assertions");
    }

    static void Run(string path)
    {
        const long now = 1700000000;
        var repository = new CharacterRepository(path); var owners = new AccountOwners();
        using var owner = owners.Acquire("emblem-master", static () => { });
        using var competitor = owners.Acquire("emblem-member", static () => { });
        var location = new Location(38, 1, 450, 234, 0);
        InventorySnapshot Inventory() => new(ImmutableArray.Create(
            new InventoryItem(0, new(InventoryListType.Main, 289), new(1001, ItemKind.Emblem, 30)),
            new InventoryItem(0, new(InventoryListType.Main, 291), new(1004, ItemKind.Emblem, 4))));
        var master = repository.Create(owner, new("EmblemMaster", location, location) { Inventory = Inventory(), Level = 95 });
        var member = repository.Create(competitor, new("EmblemMember", location, location) { Inventory = Inventory(), Level = 95 });
        using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True"); db.Open();
        void Sql(string sql) { using var query = db.CreateCommand(); query.CommandText = sql; query.ExecuteNonQuery(); }
        long Number(string sql) { using var query = db.CreateCommand(); query.CommandText = sql; return Convert.ToInt64(query.ExecuteScalar()); }
        string Snapshot()
        {
            var rows = new List<string>();
            foreach (string table in new[] { "characters", "character_inventory_items", "character_inventory_slots", "inventory_audit_log",
                "guild_settings", "guild_npc_common_benefits", "character_guild_npc_benefits" })
            {
                using var query = db.CreateCommand(); query.CommandText = "SELECT * FROM " + table + " ORDER BY rowid";
                using var reader = query.ExecuteReader();
                while (reader.Read()) rows.Add(table + ":" + string.Join("|", Enumerable.Range(0, reader.FieldCount)
                    .Select(index => reader.GetValue(index) is byte[] bytes ? Convert.ToHexString(bytes) : reader.GetValue(index))));
            }
            return string.Join("\n", rows);
        }
        Sql($"""
            INSERT INTO guilds(guild_id,name,address,master_character_id,public_funds,created_at_unix)
            VALUES(1,'徽章公会','emblem-guild',{master.Id},1000,1000);
            INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{master.Id},1,1000),(1,{member.Id},4,1000);
            INSERT INTO guild_npc_progress(guild_id,npc_id,level,contract_until) VALUES(1,400001066,2,{now + 600});
            INSERT INTO guild_npc_common_benefits(guild_id,effect,benefit_id,remaining,deadline)
            VALUES(1,'emblem compound buff',6003,2,{now + 120});
            INSERT INTO character_guild_npc_benefits(character_id,guild_id,joined_at,effect,benefit_id,remaining,deadline)
            VALUES({master.Id},1,1000,'emblem compound buff',6002,1,{now + 120});
            """);
        var towns = new TownCatalog([], location, location, new(0, [], []), [], new Dictionary<uint, uint[]>());
        GameSession Session(AccountOwners.Lease lease, ushort slot)
        {
            var selected = new GameSession(repository, towns, lease); selected.Select(slot); selected.MarkReady(); return selected;
        }
        var session = Session(owner, master.Slot); var actor = session.CaptureGuildManagement()!;
        var otherSession = Session(competitor, member.Slot); var otherActor = otherSession.CaptureGuildManagement()!;
        var fixture = new Resources(); var npcs = GuildNpcTests.Synthetic(); var catalog = Benefits(); var clock = new Clock(now);
        using var invitations = new GuildInvitations();
        var online = new OnlineSessionDirectory();
        online.Register(owner, session);
        int managementRefreshes = 0;
        GuildManagementNotice? lastNotice = null;
        session.GuildManagementNoticeArrived = notice =>
        {
            lastNotice = notice;
            if (notice.Kind != GuildManagementNoticeKind.Refresh) return;
            managementRefreshes++;
            session.RefreshGuildManagement(repository, notice.GuildId);
        };
        var management = new GuildManagementService(repository, online, invitations, new());
        var benefits = new GuildNpcBenefitService(repository, npcs, catalog, fixture.Pieces, management, clock);
        int rolls = 0;
        var compounds = new InventoryEmblemCompoundService(fixture.Items, fixture.Compounds, fixture.Limits, fixture.Pieces,
            _ => { rolls++; return 0; });
        var request = new EmblemCompoundRequest([new(1001, 289), new(1001, 289)]);
        InventoryResult Compound(EmblemCompoundRequest input, out EmblemCompoundReceipt? receipt) =>
            session.CompoundEmblem(input, compounds, out receipt, out _, benefits);
        long Common() => Number("SELECT remaining FROM guild_npc_common_benefits");
        long Personal() => Number("SELECT remaining FROM character_guild_npc_benefits");
        That(benefits.SelectEmblem(actor) is { Definition.Parameters: [2, 2], Progress.Remaining: 2 } &&
            benefits.SelectEmblem(otherActor) is { Definition.Common: true },
            "公有二级与个人一级同时有效时只选较强公有池，公共次数对同会成员可见");
        string unchanged = Snapshot();
        That(!Compound(new([new(1001, 289)]), out _).Accepted && unchanged == Snapshot() && rolls == 0,
            "徽章请求被规则拒绝时不抽奖、不改背包或消耗福利");
        int beforeRolls = rolls;
        var fallback = session.CompoundEmblem(new([new(1004, 291), new(1004, 291)]), compounds,
            out var ordinary, out _, out var noBenefitCommit, benefits);
        That(fallback.Accepted && ordinary?.BoosterItemId == 9001 && Common() == 2 && Personal() == 1 &&
            rolls == beforeRolls + 1 && noBenefitCommit is null && managementRefreshes == 0 &&
            fallback.Receipt is not null && session.CanPublishInventory(fallback.Receipt),
            "无精确公会grade映射时只抽一次普通池，不消费福利或发布公会变更，库存回执仍有效");

        Sql("CREATE TRIGGER emblem_count_fail BEFORE UPDATE OF remaining ON guild_npc_common_benefits WHEN NEW.remaining<OLD.remaining BEGIN SELECT RAISE(ABORT,'emblem count rollback'); END");
        unchanged = Snapshot(); var selectedBefore = session.Selected; beforeRolls = rolls;
        Reject<SqliteException>(() => Compound(request, out _), "公有徽章合成末段扣次故障");
        That(unchanged == Snapshot() && ReferenceEquals(session.Selected, selectedBefore) && rolls == beforeRolls + 1,
            "公有扣次失败时材料、奖励UID、审计、角色revision和次数全部回滚，不安装计划快照");
        Sql("DROP TRIGGER emblem_count_fail");
        beforeRolls = rolls;
        var first = session.CompoundEmblem(request, compounds, out var special, out _, out var deferredBenefit, benefits);
        That(first.Accepted && special is { BoosterItemId: 9003, RewardItemId: 2003 } && Common() == 1 && Personal() == 1 &&
            rolls == beforeRolls + 1 && deferredBenefit is { Success: true } && managementRefreshes == 0 &&
            first.Receipt is not null && session.CanPublishInventory(first.Receipt),
            "合成返回成功扣次凭据但延后发布福利刷新，ACK和背包投影前库存回执仍属于当前快照");
        var beforePublication = session.Selected;
        benefits.Publish(deferredBenefit!);
        That(managementRefreshes == 1 && lastNotice is { NpcsChanged: true, GuildId: 1 } &&
            !ReferenceEquals(session.Selected, beforePublication) && !session.CanPublishInventory(first.Receipt!) &&
            ReferenceEquals(session.Selected!.Inventory, first.Receipt!.Character.Inventory) &&
            session.Selected.Revision == first.Receipt.Character.Revision,
            "真实公会通知回调会安装新投影快照并使旧库存回执过期，因此只能在ACK和背包发送之后发布");

        // 两个账号先各自冻结最后一次公共资格，只有第一笔提交可以同时获得奖励和扣次。
        var selected = benefits.SelectEmblem(actor)!; var otherSelected = benefits.SelectEmblem(otherActor)!;
        var expected = session.Selected!; var otherExpected = otherSession.Selected!;
        var plan = compounds.Apply(expected, request, out _, out _, DateTimeOffset.FromUnixTimeSeconds(now), selected.Definition.Parameters[0]);
        var otherPlan = compounds.Apply(otherExpected, request, out _, out _, DateTimeOffset.FromUnixTimeSeconds(now), otherSelected.Definition.Parameters[0]);
        var committed = benefits.CommitEmblem(actor, expected, plan.Plan!, selected);
        That(committed.Success && Common() == 0 && Personal() == 1, "最后一次公共福利与第一名成员的合成资产原子提交");
        unchanged = Snapshot();
        That(!benefits.CommitEmblem(otherActor, otherExpected, otherPlan.Plan!, otherSelected).Success &&
            !benefits.CommitEmblem(actor, expected, plan.Plan!, selected).Success && unchanged == Snapshot(),
            "同会第二账号的旧次数token及第一笔重复提交都拒绝，不多发奖励或重复扣次");
        session = Session(owner, master.Slot); actor = session.CaptureGuildManagement()!;
        That(benefits.SelectEmblem(actor) is { Definition.Common: false, Definition.Parameters: [1, 2] },
            "公共次数耗尽后转用仍有效的个人福利，不叠加池等级");
        Sql("CREATE TRIGGER emblem_private_fail BEFORE UPDATE OF remaining ON character_guild_npc_benefits WHEN NEW.remaining<OLD.remaining BEGIN SELECT RAISE(ABORT,'emblem private rollback'); END");
        unchanged = Snapshot(); selectedBefore = session.Selected;
        Reject<SqliteException>(() => Compound(request, out _), "个人徽章合成末段扣次故障");
        That(unchanged == Snapshot() && ReferenceEquals(session.Selected, selectedBefore),
            "个人扣次故障同样完整回滚奖励、材料及资产revision");
        Sql("DROP TRIGGER emblem_private_fail");
        That(Compound(request, out special).Accepted && special is { BoosterItemId: 9002, RewardItemId: 2002 } &&
            Common() == 0 && Personal() == 0 && benefits.SelectEmblem(actor) is null,
            "个人福利只扣自己的最后一次，耗尽后不再选择公会池");
        beforeRolls = rolls;
        That(Compound(request, out ordinary).Accepted && ordinary?.BoosterItemId == 9001 &&
            Common() == 0 && Personal() == 0 && rolls == beforeRolls + 1,
            "两份次数都耗尽后沿用普通合成入口，成功只抽一次");

        Sql("UPDATE guild_npc_common_benefits SET remaining=1");
        selected = benefits.SelectEmblem(actor)!; expected = session.Selected!;
        plan = compounds.Apply(expected, request, out _, out _, DateTimeOffset.FromUnixTimeSeconds(now), selected.Definition.Parameters[0]);
        var changed = repository.CommitInventory(owner, expected, CurrencyService.SetGold(expected, 1).Plan!);
        unchanged = Snapshot();
        That(!benefits.CommitEmblem(actor, expected, plan.Plan!, selected).Success && unchanged == Snapshot() &&
            repository.LoadCharacter(owner, master.Slot).Revision == changed.Revision,
            "规划后其他库存提交改变角色revision时拒绝旧随机结果，福利次数不变");
        session = Session(owner, master.Slot); actor = session.CaptureGuildManagement()!;
        selected = benefits.SelectEmblem(actor)!; expected = session.Selected!;
        plan = compounds.Apply(expected, request, out _, out _, DateTimeOffset.FromUnixTimeSeconds(now), selected.Definition.Parameters[0]);
        Sql($"UPDATE guild_npc_progress SET contract_until={now} WHERE npc_id=400001066"); unchanged = Snapshot();
        That(!benefits.CommitEmblem(actor, expected, plan.Plan!, selected).Success && unchanged == Snapshot() &&
            benefits.SelectEmblem(actor) is null, "规划后契约恰好到期时联合提交拒绝，奖励和次数均不变");
        That(Compound(request, out ordinary).Accepted && ordinary?.BoosterItemId == 9001 && Common() == 1,
            "过期契约不提供公会奖池，普通合成不消耗保留的福利次数");
        Sql($"UPDATE guild_npc_progress SET contract_until={now + 600} WHERE npc_id=400001066");
        selected = benefits.SelectEmblem(actor)!; expected = session.Selected!;
        plan = compounds.Apply(expected, request, out _, out _, DateTimeOffset.FromUnixTimeSeconds(now), selected.Definition.Parameters[0]);
        clock.Unix = now + 120; unchanged = Snapshot();
        That(!benefits.CommitEmblem(actor, expected, plan.Plan!, selected).Success && benefits.SelectEmblem(actor) is null && unchanged == Snapshot(),
            "福利期限恰好到达时不能提交之前规划的公会奖池结果");
        clock.Unix = now;
        selected = benefits.SelectEmblem(actor)!;
        Sql($"UPDATE guild_members SET joined_at_unix=1001 WHERE character_id={master.Id}"); unchanged = Snapshot();
        That(!benefits.CommitEmblem(actor, expected, plan.Plan!, selected).Success && unchanged == Snapshot(),
            "退出重入的新入会代次不能消费旧会籍捕获的公共资格");
        var reopened = new CharacterRepository(path);
        That(reopened.ReadGuildNpcBenefits(actor, now)!.Common is [{ Remaining: 1 }] &&
            reopened.ReadGuildNpcBenefits(actor, now)!.Individual.Length == 0,
            "重开仓储恢复已消费的公共次数，个人福利仍按入会代次隔离");
    }

    static GuildNpcBenefitCatalog Benefits()
    {
        var strings = new List<string>(); var tokens = new List<PvfToken>();
        void Text(byte kind, string value) { strings.Add(value); tokens.Add(new(kind, strings.Count - 1)); }
        void Number(int value) => tokens.Add(new(0, value));
        Text(3, "[guild trainer buff interval time]"); Number(180); Text(3, "[npc benefit info]");
        foreach (var (id, level, scope) in new[] { (6003, 2, "common"), (6002, 1, "individual") })
        {
            Number(id); Number(400001066); Number(level); Text(7, scope); Text(7, "emblem compound buff");
            Text(7, "none"); Number(0); Text(7, "none"); Number(0); Number(2); Number(2); Number(level); Number(2);
        }
        Text(3, "[/npc benefit info]");
        return new(tokens, index => strings[index], new HashSet<int>());
    }

    sealed class Resources
    {
        public GoldLimitCatalog Limits { get; } = new("[gold limit from level]\n0 1000000\n[/gold limit from level]");
        public EpicPieceCatalog Pieces { get; } = new("[piece list]\n50000 50001\n[/piece list]");
        public ItemCatalog Items { get; }
        public EmblemCompoundCatalog Compounds { get; }

        public Resources()
        {
            var stackable = new Dictionary<int, string>
            {
                [9001] = "normal.stk", [9002] = "weak.stk", [9003] = "strong.stk",
                [1001] = "material.stk", [1004] = "material-high.stk", [2001] = "reward.stk",
                [2002] = "reward-weak.stk", [2003] = "reward-strong.stk"
            };
            string Pool(int id) => "[booster info]\n[stackable]\n1 " + id + " 1 1\n[/stackable]\n[/booster info]";
            var texts = new Dictionary<string, string>
            {
                [EmblemCompoundCatalog.RootPath] = "[emblem compound info]\n1 2 9001\n4 2 9001\n[/emblem compound info]",
                ["stackable/normal.stk"] = Pool(2001), ["stackable/weak.stk"] = Pool(2002), ["stackable/strong.stk"] = Pool(2003),
                ["stackable/reward.stk"] = "[stackable type]\n`[avatar emblem]` 0\n[rarity]\n2",
                ["stackable/reward-weak.stk"] = "[stackable type]\n`[avatar emblem]` 0\n[rarity]\n2",
                ["stackable/reward-strong.stk"] = "[stackable type]\n`[avatar emblem]` 0\n[rarity]\n3"
            };
            Compounds = new(path => texts[path], stackable,
                "[guild npc emblem compound info]\n1 1 2 9002\n2 1 2 9003\n[/guild npc emblem compound info]");
            Items = new(null!, new Dictionary<int, string>(), stackable, 0);
            var templates = new[] { Template(1001, 0, 1), Template(1004, 0, 4), Template(2001, 2, 2),
                Template(2002, 2, 2), Template(2003, 3, 3) };
            Items.PrefillTemplates(templates.ToDictionary(item => item.Id));
        }

        static ItemTemplate Template(int id, int rarity, int grade) => new(id, ItemKind.Emblem, 0, 1, 0, 1000, 0, rarity, grade,
            "[trade]", ushort.MaxValue, 0, "emblem", "[avatar emblem] 0");
    }
}
