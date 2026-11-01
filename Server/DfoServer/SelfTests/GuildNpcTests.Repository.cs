using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Guilds;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class GuildNpcTests
{
    internal static GuildNpcCatalog Synthetic()
    {
        var strings = new List<string>(); var tokens = new List<PvfToken>();
        void Text(byte type, string text) { strings.Add(text); tokens.Add(new(type, strings.Count - 1)); }
        void Tag(string text) => Text(3, "[" + text + "]");
        void Word(string text) => Text(7, text);
        void Number(int value) => tokens.Add(new(0, value));
        Tag("npc info");
        for (int index = 0; index < 8; index++)
        {
            Tag("npc index"); Number(checked((int)GuildNpcCatalog.Guardian + index));
            Tag("contract type"); Word(index < 4 ? "free" : "contract");
            Tag("level info");
            for (int level = 1; level <= 2; level++)
            {
                Number(level); Word(level == 1 ? "low class" : "mid class");
                Word("low class"); Number(index == 0 ? 10 : 1);
                Word("public funds"); Number(level == 1 ? 100 : 0);
                Word("guild crystal"); Number(level == 1 ? 2 : 0); Number(level == 1 ? 1 : 0);
            }
            Tag("/level info"); Tag("/npc index");
        }
        Tag("/npc info"); Tag("npc contract info");
        Number(1); Number(7); Number(300); Number(1); Number(90); Number(3000);
        Tag("/npc contract info");
        // 真实文件在其它表复用index但只给训练表写闭合，不能改变NPC目标段的嵌套规则。
        Tag("npc string indexs"); Tag("index"); Number(1); Tag("index"); Number(2);
        Tag("/npc string indexs"); Tag("training buff info"); Tag("index"); Number(3);
        Tag("/index"); Tag("/training buff info");
        return new(tokens, id => strings[id]);
    }

    static void Repository()
    {
        string directory = Path.Combine(Path.GetTempPath(), "guild-npc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "game.db");
            var catalog = Synthetic(); var parameters = GuildParameterTests.Synthetic();
            var repository = new CharacterRepository(path); var owners = new AccountOwners();
            using var owner = owners.Acquire("npc-master", static () => { });
            var location = new Location(38, 1, 450, 234, 0);
            var character = repository.Create(owner, new("NpcMaster", location, location));
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True"); db.Open();
            void Sql(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            long Number(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
            string Snapshot() => string.Join(":", Number("SELECT public_funds FROM guilds WHERE guild_id=1"),
                Number("SELECT guild_crystal FROM guilds WHERE guild_id=1"), Number("SELECT count(*) FROM guild_npc_receipts"),
                Number("SELECT count(*) FROM guild_request_claims"), Number("SELECT count(*) FROM guild_npc_progress"));
            Sql($"""
                INSERT INTO guilds(guild_id,name,address,master_character_id,level,tier,experience,public_funds,guild_crystal,created_at_unix)
                VALUES(1,'NPC公会','npc-guild',{character.Id},10,0,1010,10000,20,1000);
                INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{character.Id},1,1000);
                """);
            var towns = new TownCatalog([], location, location, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var session = new GameSession(repository, towns, owner); session.Select(character.Slot); session.MarkReady();
            var actor = session.CaptureGuildManagement()!;
            const long now = 1700000000;
            string Key(int value) => value.ToString("x64");
            GuildNpcCommit Upgrade(uint npc, int key, long time = now) => repository.UpgradeGuildNpc(actor, catalog, npc, 0, Key(key), time);
            That(repository.ReadGuildNpcs(actor, catalog, now) is { Npcs.Length: 8 } initial &&
                initial.Npcs.All(row => row.Level == 1 && row.UpgradeUntil == 0), "旧公会读取完整NPC缺省名册");
            string unchanged = Snapshot();
            That(!repository.UpgradeGuildNpc(actor, catalog, GuildNpcCatalog.Guardian, 1, Key(1), now).Success &&
                unchanged == Snapshot(), "升级不接受客户端自报目标级别或非零payload");
            Sql($"INSERT INTO guild_request_claims VALUES('{Key(99)}',2,'{owner.Account}',{character.Id})");
            unchanged = Snapshot();
            That(!Upgrade(GuildNpcCatalog.Guardian, 99).Success && unchanged == Snapshot(),
                "已用于其它公会资产的请求不能改作NPC扣费");
            var first = Upgrade(GuildNpcCatalog.Guardian, 2);
            That(first.Success && Number("SELECT public_funds FROM guilds WHERE guild_id=1") == 9900 &&
                Number("SELECT guild_crystal FROM guilds WHERE guild_id=1") == 18 &&
                first.State!.Npcs.Single(row => row.NpcId == GuildNpcCatalog.Guardian).UpgradeUntil == now + 86400,
                "守护石扣公共资金和公共结晶并持久1天deadline，不立即假升档");
            unchanged = Snapshot();
            That(Upgrade(GuildNpcCatalog.Guardian, 2).Replayed && !Upgrade(GuildNpcCatalog.Guardian, 3).Success &&
                !repository.ContractGuildNpc(actor, catalog, GuildNpcCatalog.Guardian, 0, Key(2), now).Success && unchanged == Snapshot(),
                "重复精确请求幂等，升级中或换动作复用原序号不重复扣费");
            Sql("CREATE TRIGGER npc_receipt_fail BEFORE INSERT ON guild_npc_receipts BEGIN SELECT RAISE(ABORT,'npc receipt rollback'); END");
            Reject<SqliteException>(() => Upgrade(GuildNpcCatalog.Guardian + 1, 4), "NPC末段回执写入故障");
            That(unchanged == Snapshot(), "NPC末段失败时公款、结晶、进度和统一请求claim全部回滚");
            Sql("DROP TRIGGER npc_receipt_fail");
            var reopened = new CharacterRepository(path);
            That(reopened.CompleteGuildNpcUpgrades(catalog, parameters, now + 86399).Length == 0 &&
                Number("SELECT tier FROM guilds WHERE guild_id=1") == 0, "重开仓储后未到期不提前晋阶");
            var completed = reopened.CompleteGuildNpcUpgrades(catalog, parameters, now + 86400);
            That(completed.Length == 1 && completed[0].Npcs.Single(row => row.NpcId == GuildNpcCatalog.Guardian) is
                { Level: 2, UpgradeUntil: 0 } && Number("SELECT tier FROM guilds WHERE guild_id=1") == 1 &&
                Number("SELECT experience FROM guilds WHERE guild_id=1") == 1010 &&
                reopened.CompleteGuildNpcUpgrades(catalog, parameters, now + 86400).Length == 0,
                "到期守护石在同事务完成NPC级别与公会晋阶，保留经验且重试不重复完成");
            var contract = repository.ContractGuildNpc(actor, catalog, GuildNpcCatalog.Guardian + 4, 0, Key(5), now + 86400);
            long funds = Number("SELECT public_funds FROM guilds WHERE guild_id=1");
            That(contract.Success && contract.State!.Npcs.Single(row => row.NpcId == GuildNpcCatalog.Guardian + 4).ContractUntil == now + 8 * 86400 &&
                repository.ContractGuildNpc(actor, catalog, GuildNpcCatalog.Guardian + 4, 0, Key(5), now + 86400).Replayed &&
                Number("SELECT public_funds FROM guilds WHERE guild_id=1") == funds,
                "契约费用与持久有效期原子提交，精确重放不再次续期或扣费");
            unchanged = Snapshot();
            That(!repository.ContractGuildNpc(actor, catalog, GuildNpcCatalog.Guardian + 4, 1, Key(6), now + 86400).Success &&
                unchanged == Snapshot(), "契约续期超过90天上限拒绝且不扣公款");
            Sql($"UPDATE guild_members SET joined_at_unix=1001 WHERE character_id={character.Id}");
            That(!repository.ContractGuildNpc(actor, catalog, GuildNpcCatalog.Guardian + 4, 0, Key(5), now + 86400).Success &&
                unchanged == Snapshot(), "退出重入后的新会籍不能重放旧NPC资产回执");
            using var memberOwner = owners.Acquire("npc-member", static () => { });
            var member = repository.Create(memberOwner, new("NpcMember", location, location));
            Sql($"""
                INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{member.Id},2,1000);
                INSERT INTO guild_roles(guild_id,grade,permissions,name) VALUES(1,2,{GuildRoleRules.NpcManagementPermission},'NPC管理员');
                """);
            var memberSession = new GameSession(repository, towns, memberOwner); memberSession.Select(member.Slot); memberSession.MarkReady();
            var memberActor = memberSession.CaptureGuildManagement()!;
            unchanged = Snapshot();
            That(!repository.UpgradeGuildNpc(memberActor, catalog, GuildNpcCatalog.Guardian, 0, Key(10), now + 86400).Success &&
                unchanged == Snapshot(), "获得NPC管理权限的非会长仍不能替公会执行守护石晋阶");
            var managed = repository.UpgradeGuildNpc(memberActor, catalog, GuildNpcCatalog.Guardian + 1, 0, Key(11), now + 86400);
            That(managed.Success, "NPC权限位18允许合法授权成员升级普通NPC");
            unchanged = Snapshot();
            Sql("CREATE TRIGGER npc_completion_fail AFTER UPDATE ON guild_npc_progress BEGIN SELECT RAISE(ABORT,'npc completion rollback'); END");
            Reject<SqliteException>(() => reopened.CompleteGuildNpcUpgrades(catalog, parameters, now + 2 * 86400),
                "NPC到期完成SQL故障");
            That(unchanged == Snapshot() && Number($"SELECT level FROM guild_npc_progress WHERE npc_id={GuildNpcCatalog.Guardian + 1}") == 1 &&
                Number($"SELECT upgrade_until FROM guild_npc_progress WHERE npc_id={GuildNpcCatalog.Guardian + 1}") == now + 2 * 86400,
                "到期完成故障保留原级别和deadline，重开仍可恢复而不丢升级");
            Sql("DROP TRIGGER npc_completion_fail");
            That(new CharacterRepository(path).CompleteGuildNpcUpgrades(catalog, parameters, now + 2 * 86400).Length == 1 &&
                Number($"SELECT level FROM guild_npc_progress WHERE npc_id={GuildNpcCatalog.Guardian + 1}") == 2,
                "到期完成故障解除后由重开仓储恢复原升级");
            Sql("UPDATE guild_roles SET permissions=0 WHERE guild_id=1 AND grade=2");
            unchanged = Snapshot();
            That(!repository.ContractGuildNpc(memberActor, catalog, GuildNpcCatalog.Guardian + 4, 0, Key(12), now + 2 * 86400).Success &&
                unchanged == Snapshot(), "NPC管理权限被撤销后旧会话下一次请求不能继续扣公款续约");
        }
        finally { TestFiles.DeleteDirectory(directory, true); }
    }
}
