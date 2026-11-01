using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Guilds;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GuildNpcCraftTests
{
    public static void RunResources(PvfArchive pvf)
    {
        var catalog = new GuildNpcCraftCatalog(pvf);
        That(catalog.CrystalMaximum == 5000 && catalog.Definitions.Count == 5 && catalog.Find(1004) is
            { Level: 1, PublicFunds: 10000, DurationSeconds: 86400, ItemId: 490013121, Amount: 1 } &&
            catalog.Find(1024) is { Level: 5, PublicFunds: 200000, Amount: 10 },
            "守护石从纯净福利声明读取五级生产，1440分钟转换为86400秒而非猜测时间");
    }
    public static void RunCore()
    {
        int before = Count;
        byte[] request = new byte[69];
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(13), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(21), 1004);
        That(GuildNpcRequests.TryParse(request, out var parsed) && parsed is { Action: 4, NpcId: 0, Argument: 1004 },
            "原生守护石生产使用action4/npcId0/benefitId");
        request[68] = 1;
        That(!GuildNpcRequests.TryParse(request, out _), "生产请求拒绝额外非零参数");
        request[68] = 0; request[17] = 1;
        That(!GuildNpcRequests.TryParse(request, out _), "生产请求不接受伪造NPC字段");
        byte[] row = GuildNpcPackets.Benefit(new(1004, 1700086400));
        That(row.Length == 12 && BinaryPrimitives.ReadUInt32LittleEndian(row) == 1004 &&
            BinaryPrimitives.ReadInt16LittleEndian(row.AsSpan(4)) == 0 &&
            BinaryPrimitives.ReadInt32LittleEndian(row.AsSpan(8)) == 1700086400,
            "1772精确12字节索引/未消费short/对齐/绝对截止时间");
        byte[] all = GuildNpcPackets.BenefitsAll([new(1004, 1700086400)]);
        That(all.Length == 1080 && all.AsSpan(0, 12).SequenceEqual(row) && all.AsSpan(12).IndexOfAnyExcept((byte)0) == -1 &&
            GuildNpcPackets.BenefitsAll([]).All(value => value == 0),
            "1773完整40公有与50个人段，空快照清除上次生产状态");
        Repository();
        Console.WriteLine($"PASS {Count - before} guardian craft assertions");
    }
    static void Repository()
    {
        string directory = Path.Combine(Path.GetTempPath(), "guild-craft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "game.db");
            var repository = new CharacterRepository(path); var owners = new AccountOwners();
            using var owner = owners.Acquire("craft-master", static () => { });
            var location = new Location(38, 1, 450, 234, 0);
            var character = repository.Create(owner, new("CraftMaster", location, location));
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True"); db.Open();
            void Sql(string text) { using var command = db.CreateCommand(); command.CommandText = text; command.ExecuteNonQuery(); }
            long Number(string text) { using var command = db.CreateCommand(); command.CommandText = text; return Convert.ToInt64(command.ExecuteScalar()); }
            string Snapshot() => string.Join(":", Number("SELECT public_funds FROM guilds WHERE guild_id=1"),
                Number("SELECT guild_crystal FROM guilds WHERE guild_id=1"), Number("SELECT count(*) FROM guild_npc_crafts"),
                Number("SELECT count(*) FROM guild_npc_craft_receipts"), Number("SELECT count(*) FROM guild_request_claims"),
                Number("SELECT count(*) FROM guild_npc_craft_completions"));
            long originalGold = Number("SELECT gold FROM characters WHERE id=" + character.Id);
            Sql($"""
                INSERT INTO guilds(guild_id,name,address,master_character_id,public_funds,guild_crystal,created_at_unix)
                VALUES(1,'生产公会','craft-guild',{character.Id},1000,0,1000);
                INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{character.Id},1,1000);
                """);
            var towns = new TownCatalog([], location, location, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var session = new GameSession(repository, towns, owner); session.Select(character.Slot); session.MarkReady();
            var actor = session.CaptureGuildManagement()!;
            var npcs = GuildNpcTests.Synthetic();
            var crafts = new GuildNpcCraftCatalog([new(1004, 1, 100, 60, 490013121, 3), new(1009, 2, 200, 60, 490013121, 5)], 10);
            const long now = 1700000000;
            string Key(int value) => value.ToString("x64");
            GuildNpcCommit Start(int key, uint benefit = 1004, long time = now) => repository.StartGuildNpcCraft(actor, npcs, crafts, benefit, Key(key), time);
            GuildNpcCommit Cancel(int key, uint benefit = 1004) => repository.CancelGuildNpcCraft(actor, npcs, crafts, benefit, Key(key), now);
            string unchanged = Snapshot();
            That(!Start(1, 1009).Success && unchanged == Snapshot(), "生产只接受当前守护石等级的真实福利");
            Sql($"INSERT INTO guild_request_claims VALUES('{Key(99)}',2,'{owner.Account}',{character.Id})");
            unchanged = Snapshot();
            That(!Start(99).Success && unchanged == Snapshot(), "生产不能复用其他资产领域已占用的请求");
            Sql("CREATE TRIGGER craft_receipt_fail BEFORE INSERT ON guild_npc_craft_receipts BEGIN SELECT RAISE(ABORT,'craft receipt rollback'); END");
            Reject<SqliteException>(() => Start(2), "生产末段回执故障");
            That(unchanged == Snapshot(), "生产失败公款、状态、receipt与claim全部回滚");
            Sql("DROP TRIGGER craft_receipt_fail");
            var first = Start(2);
            That(first.Success && first.State!.Crafts is [{ BenefitId: 1004, Deadline: now + 60 }] &&
                Number("SELECT public_funds FROM guilds WHERE guild_id=1") == 900 &&
                Number("SELECT guild_crystal FROM guilds WHERE guild_id=1") == 0,
                "生产只扣一次公款并持久截止时间，不提前增加结晶或伪发背包材料");
            unchanged = Snapshot();
            That(Start(2).Replayed && !Start(3).Success && !Start(2, 1009).Success && unchanged == Snapshot(),
                "精确生产重放幂等，生产中不能重开或换福利复用序号");
            var reopened = new CharacterRepository(path);
            That(reopened.ReadGuildNpcs(actor, npcs, now)!.Crafts is [{ Deadline: now + 60 }] &&
                reopened.CompleteGuildNpcCrafts(npcs, crafts, now + 59).Length == 0,
                "重开仓储恢复生产快照且不到期不发奖励");
            Sql("UPDATE guilds SET guild_crystal=9 WHERE guild_id=1"); unchanged = Snapshot();
            That(reopened.CompleteGuildNpcCrafts(npcs, crafts, now + 60).Length == 0 && unchanged == Snapshot(),
                "到期结晶容量不足保留完整生产，不能截断奖励或丢失已付费用");
            Sql("UPDATE guilds SET guild_crystal=0 WHERE guild_id=1"); unchanged = Snapshot();
            Sql("CREATE TRIGGER craft_complete_fail BEFORE INSERT ON guild_npc_craft_completions BEGIN SELECT RAISE(ABORT,'craft completion rollback'); END");
            Reject<SqliteException>(() => reopened.CompleteGuildNpcCrafts(npcs, crafts, now + 60), "到期生产末段账本故障");
            That(unchanged == Snapshot(), "到期故障时结晶入账、生产清除与完成账本原子回滚");
            Sql("DROP TRIGGER craft_complete_fail");
            var completed = reopened.CompleteGuildNpcCrafts(npcs, crafts, now + 60);
            That(completed is [{ Crafts.Length: 0 }] && Number("SELECT guild_crystal FROM guilds WHERE guild_id=1") == 3 &&
                Number("SELECT count(*) FROM guild_npc_craft_completions") == 1 &&
                new CharacterRepository(path).CompleteGuildNpcCrafts(npcs, crafts, now + 60).Length == 0,
                "重开后到期只入账一次公共结晶，清生产并写完整完成账本");
            unchanged = Snapshot();
            That(Start(2).Replayed && unchanged == Snapshot(), "生产完成后重放开始请求不能再扣钱或重建生产");
            That(!Cancel(5).Success && unchanged == Snapshot(), "生产完成后取消不扣款、不返款、不改结晶");
            Sql("UPDATE guilds SET public_funds=1000,guild_crystal=0 WHERE guild_id=1");
            That(Start(10).Success, "为取消验证启动新的生产");
            unchanged = Snapshot();
            That(!Cancel(10).Success && !Cancel(11, 1009).Success && unchanged == Snapshot(),
                "取消不能复用开始请求或指定另一福利");
            Sql("CREATE TRIGGER craft_cancel_fail BEFORE INSERT ON guild_npc_craft_receipts WHEN NEW.action=5 BEGIN SELECT RAISE(ABORT,'craft cancel rollback'); END");
            Reject<SqliteException>(() => Cancel(11), "取消末段回执故障");
            That(unchanged == Snapshot(), "取消故障时生产、回执及统一请求归属全部回滚");
            Sql("DROP TRIGGER craft_cancel_fail");
            var cancelled = Cancel(11);
            That(cancelled.Success && cancelled.State!.Crafts.Length == 0 &&
                Number("SELECT public_funds FROM guilds WHERE guild_id=1") == 900 &&
                Number("SELECT guild_crystal FROM guilds WHERE guild_id=1") == 0 &&
                Number($"SELECT count(*) FROM guild_npc_craft_receipts WHERE action=5 AND craft_id='{Key(10)}'") == 1,
                "本版取消不返费用、不发结晶，并记录被取消的生产身份");
            That(Start(12).Success, "取消后允许重新付费开始生产");
            unchanged = Snapshot();
            That(Cancel(11).Replayed && unchanged == Snapshot() && Number("SELECT count(*) FROM guild_npc_crafts") == 1,
                "旧取消回执重放不能清除后来重新开始的生产");
            That(Cancel(13).Success && new CharacterRepository(path).CompleteGuildNpcCrafts(npcs, crafts, now + 60).Length == 0 &&
                Number("SELECT public_funds FROM guilds WHERE guild_id=1") == 800 &&
                Number("SELECT guild_crystal FROM guilds WHERE guild_id=1") == 0,
                "取消与完成共用写事务门禁，取消后到期及重开不能补发结晶");
            Sql($"INSERT INTO guild_npc_progress(guild_id,npc_id,level,upgrade_target,upgrade_until) VALUES(1,{GuildNpcCatalog.Guardian},1,2,{now + 60})");
            unchanged = Snapshot();
            That(!Start(14).Success && unchanged == Snapshot(), "守护石升级中不能开始生产，拒绝不扣费");
            Sql("DELETE FROM guild_npc_progress WHERE guild_id=1");
            unchanged = Snapshot();
            Sql($"UPDATE guild_members SET joined_at_unix=1001 WHERE character_id={character.Id}");
            That(!Start(2).Success && unchanged == Snapshot(), "退出重入的新会籍不能重放旧生产回执");
            Sql("UPDATE guilds SET guild_crystal=8 WHERE guild_id=1"); unchanged = Snapshot();
            That(!Start(4).Success && unchanged == Snapshot(), "开始生产时检查完整产量空间且拒绝不扣公款");
            That(Number("SELECT gold FROM characters WHERE id=" + character.Id) == originalGold,
                "公共生产不改玩家金币和背包资产");
        }
        finally { TestFiles.DeleteDirectory(directory, true); }
    }
}
