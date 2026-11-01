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

internal static class GuildNpcPassiveBuffTests
{
    public static void RunCore()
    {
        int before = Count;
        string directory = Path.Combine(Path.GetTempPath(), "guild-passive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("guardian-member", static () => { });
            var location = new Location(38, 1, 450, 234, 0);
            var character = repository.Create(owner, new("Guardian", location, location));
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True");
            db.Open();
            void Sql(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            Sql($"""
                INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix)
                VALUES(1,'守护公会','guardian',{character.Id},1000);
                INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{character.Id},1,1000);
                """);
            var towns = new TownCatalog([], location, location, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var session = new GameSession(repository, towns, owner);
            session.Select(character.Slot); session.MarkReady();
            using var invitations = new GuildInvitations();
            var clock = new PassiveClock();
            var management = new GuildManagementService(repository, new(), invitations, new(), clock: clock);
            var economy = new GuildEconomyService(repository, null!, null!, management, clock);
            using var npcs = new GuildNpcService(repository, GuildNpcTests.Synthetic(), GuildParameterTests.Synthetic(),
                management, clock: clock, benefits: Benefits());
            GuildBuffProjectionReceipt Plan(bool force = false) => session.PlanGuildBuffProjection(economy, force, npcs)!;
            That(npcs.GuardianBuff(session.CaptureGuildManagement()!) is { Value: 1276, ExpiresUnix: 0 },
                "守护石缺省一级按资源授予永久公会属性");
            var first = Plan();
            That(first.Added is [{ Value: 1276, ExpiresUnix: 0 }] && first.Deadline is null && first.IsCurrent(),
                "首次投影含守护石永久属性且不误用属性值作为期限");
            var unchanged = Plan();
            That(unchanged.Added.Length == 0 && unchanged.Removed.Length == 0 && first.IsCurrent(),
                "相同规划不重复发包也不撤销仍待发送的首次回执");
            var oldSnapshot = session.Selected;
            session.RefreshGuildManagement(repository, 1);
            That(!ReferenceEquals(oldSnapshot, session.Selected) && first.IsCurrent(),
                "无关不可变角色快照替换不丢弃已规划增益");
            Sql($"INSERT INTO guild_npc_progress(guild_id,npc_id,level,upgrade_target,upgrade_until,contract_until) VALUES(1,{GuildNpcCatalog.Guardian},2,0,0,0)");
            var upgraded = Plan();
            That(upgraded.Removed.Contains(1276u) && upgraded.Added is [{ Value: 1277 }] && !first.IsCurrent(),
                "守护石实际升级删除旧档并授予新档，旧回执不得迟到覆盖");
            var forced = Plan(true);
            That(forced.Added is [{ Value: 1277 }] && forced.Removed.Contains(1276u) &&
                forced.Removed.Contains(1277u) && !upgraded.IsCurrent(),
                "强制恢复覆盖中途未送达的档位变化，清除选角期间旧属性");
            session.ReturnToRoster(); session.Select(character.Slot); session.MarkReady();
            var reselected = Plan();
            That(reselected.Added is [{ Value: 1277 }] && !forced.IsCurrent(),
                "同角色重新选角按新epoch恢复永久属性");
            Sql($"DELETE FROM guild_members WHERE character_id={character.Id}");
            var left = Plan();
            That(left.Removed.Contains(1277u) && left.Added.Length == 0 && !reselected.IsCurrent() &&
                npcs.GuardianBuff(session.CaptureGuildManagement()!) is null,
                "退会清除守护石属性，不能沿角色旧快照继续授予");
        }
        finally { TestFiles.DeleteDirectory(directory, true); }
        Console.WriteLine($"PASS {Count - before} guild guardian passive assertions");
    }

    static GuildNpcBenefitCatalog Benefits()
    {
        var strings = new List<string>(); var tokens = new List<PvfToken>();
        void Text(byte type, string value) { strings.Add(value); tokens.Add(new(type, strings.Count - 1)); }
        Text(3, "[guild trainer buff interval time]"); tokens.Add(new(0, 180));
        Text(3, "[npc benefit info]");
        for (int level = 1; level <= 2; level++)
        {
            tokens.Add(new(0, 1003 + (level - 1) * 5)); tokens.Add(new(0, checked((int)GuildNpcCatalog.Guardian)));
            tokens.Add(new(0, level)); Text(7, "common"); Text(7, "guild various buff");
            Text(7, "none"); tokens.Add(new(0, 0)); Text(7, "none"); tokens.Add(new(0, 0));
            tokens.Add(new(0, 0)); tokens.Add(new(0, 2)); tokens.Add(new(0, 1275 + level)); tokens.Add(new(0, 120));
        }
        Text(3, "[/npc benefit info]");
        return new(tokens, id => strings[id], new HashSet<int> { 1276, 1277 });
    }

    sealed class PassiveClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(1700000000);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new PassiveTimer();
        sealed class PassiveTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
