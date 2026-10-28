using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Guilds;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GuildEconomyTests
{
    public static void RunCore()
    {
        int before = Count;
        That(GuildEconomyRequests.TryPurchase([3, 0, 0, 0, 0, 0, 0, 0], out uint content) && content == 3 &&
            !GuildEconomyRequests.TryPurchase([3, 0, 0, 0], out _) &&
            !GuildEconomyRequests.TryPurchase([3, 0, 0, 0, 1, 0, 0, 0], out _),
            "761必须完整消费内容编号和空DSTR，不接受尾随宣传文本");
        Bytes(GuildEconomyPackets.AddedBuffs([new(3, 167, 1100), new(5, 169, 0)], 1000),
            Testing.TcpPeer.Build(writer =>
            {
                writer.Write((byte)2); writer.Write(167u); writer.Write(0u); writer.Write(100u); writer.Write(0u);
                writer.Write(169u); writer.Write(0u); writer.Write(0u); writer.Write(0u);
            }), "487公会属性使用原生buff参数，永久与限时实例分别表达");
        var history = GuildEconomyPackets.History([new(1700000000, "测试会员", 8, 0, 400)]);
        That(history.Length == 53 && history[0] == 1 && BitConverter.ToUInt32(history, 1) == 1 &&
            history[39] == 8 && BitConverter.ToInt32(history, 45) == 400,
            "747每条48字节，积分礼袋kind8不误用内容购买扣款标志");
        var template = new ItemTemplate(987654, ItemKind.Consumable, 0, 1, 0, 1000, 0, 2, 1, "[trade]", ushort.MaxValue, 0, "fixture");
        var raw = new StackableFile(template.Id, "stackable/custom/guild.stk", new("[waste]", []), 1000, null, null, 1, null, [])
            { ActionDeclaration = new("[add guildmileage]", [400]), DeclaredUseActionPacket = 1, HasUseActionPacket = true,
                HasActionUsablePlace = true, DeclaredActionUsablePlaces = ["[village]"] };
        That(GuildMileageGiftDefinition.Parse(raw, template) is { Amount: 400 } && GuildMileageGiftDefinition.Action == 146 &&
            GuildMileageGiftDefinition.Parse(raw with { DeclaredUseActionPacket = 0 }, template) is null &&
            GuildMileageGiftDefinition.Parse(raw with { ActionDeclaration = new("[add mileage]", [400]) }, template) is null,
            "95公会礼袋按146与独立资源标签识别，不能混账号里程或旧版145");
        Repository(template);
        Console.WriteLine($"PASS {Count - before} 公会内容、积分、福利与耐久回执断言");
    }

    static void Repository(ItemTemplate template)
    {
        string root = Path.Combine(Path.GetTempPath(), "guild-economy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path); var owners = new AccountOwners();
            using var owner = owners.Acquire("f-master", static () => { });
            using var outsider = owners.Acquire("f-other", static () => { });
            var room = new Location(38, 1, 450, 234, 0);
            var slot = new InventorySlot(InventoryListType.Main, 68);
            var master = repository.Create(owner, new("FMaster", room, room)
                { Inventory = new([new(0, slot, new(template.Id, ItemKind.Consumable, 3))]) });
            var other = repository.Create(outsider, new("FOther", room, room));
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            void Sql(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            long Number(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
            Sql($"""
                INSERT INTO guilds(guild_id,name,address,master_character_id,public_funds,created_at_unix)
                VALUES(1,'经济公会','economy',{master.Id},2000000,1000);
                INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{master.Id},0,1000);
                """);
            var towns = new TownCatalog([], room, room, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var session = new GameSession(repository, towns, owner); session.Select(master.Slot); session.MarkReady();
            var actor = session.CaptureGuildManagement()!;
            long now = 1700000000;
            string Key(int id) => id.ToString("x64");
            GuildContentDefinition Content(uint id, byte type, int value, int days, int price, byte mode = 0) =>
                new(id, type, value, days, mode, 0, 0, price);
            GuildEconomyCommit Buy(GuildContentDefinition definition, int key, long? time = null) =>
                repository.PurchaseGuildContent(actor, definition, Key(key), time ?? now, 56);
            var exp = Content(3, 1, 10, 1, 200);
            long assets = Number("SELECT sum(revision) FROM characters");
            That(repository.ReadGuildMemberLimit(owner, master.Id) == 300 &&
                repository.ReadGuildEconomy(outsider, master.Id, now) is null && repository.ReadGuildKillBonus(other.Id, now) == 0,
                "旧会缺省容量300，只读按账号授权且无会籍不授予福利");
            That(Buy(exp, 1).Success && repository.ReadGuildKillBonus(master.Id, now + 86399) == 10 &&
                repository.ReadGuildKillBonus(master.Id, now + 86400) == 0,
                "经验福利真实到期，边界时刻不再授予击杀加成");
            long funds = Number("SELECT public_funds FROM guilds WHERE guild_id=1");
            Sql($"INSERT INTO guild_request_claims VALUES('{Key(200)}',1,'{owner.Account}',{master.Id})");
            That(Buy(exp, 200).Error == 14 && Number("SELECT public_funds FROM guilds WHERE guild_id=1") == funds,
                "仓库已经兑现的连接请求不能再改作福利购买扣费");
            That(Buy(exp, 1).Replayed && Number("SELECT public_funds FROM guilds WHERE guild_id=1") == funds &&
                Buy(Content(2, 3, 167, 1, 200), 1).Error == 14,
                "耐久回执绑定操作与内容，重复购买不收费也不能换内容重用身份");
            That(Buy(exp, 2, now + 100).Success &&
                Number("SELECT expires_unix FROM guild_benefits WHERE type=1") == now + 172800,
                "续购从原有效期限延长，不覆盖为现在加一天");
            That(Buy(Content(1, 5, 169, -1, 36000), 3).Success && Buy(Content(1, 5, 169, -1, 36000), 4).Error == 9,
                "永久属性只购买一次，重复的新请求不收费");
            That(Buy(Content(17, 6, 700, -1, 200000), 5).Error == 105 &&
                Buy(Content(16, 6, 500, -1, 100000), 6).Success &&
                repository.ReadGuildMemberLimit(owner, master.Id) == 500 && Buy(Content(16, 6, 500, -1, 100000), 7).Error == 116,
                "成员扩容按300到500到700到1000前置推进，不能越级或重复收费");
            That(Buy(Content(11, 4, 16, -1, 10000), 8).Success &&
                Number("SELECT capacity FROM guild_cargo_state WHERE guild_id=1") == 24,
                "内容仓库扩容是16格增量，并更新E唯一容量事实源");
            Sql("UPDATE guild_cargo_state SET capacity=56 WHERE guild_id=1");
            That(Buy(Content(12, 4, 8, -1, 5000), 9).Error == 13, "仓库满容量拒绝扣积分");
            That(Buy(Content(13, 7, 10, -1, 300, 1), 10).Success &&
                Buy(Content(14, 7, 100, -1, 2850, 1), 11).Success &&
                Number("SELECT value FROM guild_benefits WHERE type=7") == 110,
                "95传送次数接受Days=-1并累加，不照搬S5A3的9999限定");
            using (var tx = db.BeginTransaction())
            {
                That(repository.ConsumeGuildTeleportInTransaction(db, tx, owner, master.Id, 1, 1000),
                    "传送在外层位置事务中只扣一次公共次数");
                tx.Rollback();
            }
            That(Number("SELECT value FROM guild_benefits WHERE type=7") == 110, "传送外层失败连同次数扣除回滚");
            Sql("CREATE TRIGGER fail_economy_receipt BEFORE INSERT ON guild_economy_receipts BEGIN SELECT RAISE(ABORT,'test'); END");
            funds = Number("SELECT public_funds FROM guilds WHERE guild_id=1");
            long expiry = Number("SELECT expires_unix FROM guild_benefits WHERE type=1");
            Reject<SqliteException>(() => Buy(exp, 12), "真实SQL末段回执失败");
            That(Number("SELECT public_funds FROM guilds WHERE guild_id=1") == funds &&
                Number("SELECT expires_unix FROM guild_benefits WHERE type=1") == expiry,
                "收费、福利、历史与回执在同一事务回滚");
            Sql("DROP TRIGGER fail_economy_receipt");
            That(Number("SELECT count(*) FROM guild_request_claims WHERE request_id='" + Key(12) + "'") == 0,
                "末段SQL失败也回滚统一请求归属，原操作可以按原身份恢复");
            for (int i = 20; i < 75; i++) That(Buy(exp, i).Success, "跨历史显示窗口的独立有效购买");
            That(repository.ReadGuildMileageHistory(owner, master.Id).Length == 50 && Buy(exp, 1).Replayed &&
                Number("SELECT count(*) FROM guild_economy_receipts WHERE request_id='" + Key(1) + "'") == 1,
                "50条展示历史裁剪后原始耐久回执仍防止重复扣费");
            That(Number("SELECT sum(revision) FROM characters") == assets && Number("SELECT sum(gold) FROM characters") == 0,
                "公会内容只扣公共积分，不触碰角色资产revision或金币");

            var expected = repository.List(owner).Single(); var source = expected.Inventory.Find(slot)!;
            var deleted = InventoryDeleteService.Plan(expected.Inventory, slot, source.Core.Value, 1);
            var giftPlan = new GuildMileageGiftPlan(expected, source, new(template, 400),
                new(expected.Inventory, deleted.Plan!.After, "guild_mileage_gift", new(expected, requirePosition: true)));
            funds = Number("SELECT public_funds FROM guilds WHERE guild_id=1");
            var gift = repository.CommitGuildMileageGift(actor, giftPlan, Key(100), now);
            That(gift.Success && gift.Character?.Inventory.Find(slot)?.Core.Count == 2 &&
                Number("SELECT public_funds FROM guilds WHERE guild_id=1") == funds + 400 &&
                repository.ReplayGuildMileageGift(actor, Key(100), slot.Index, now)?.Replayed == true &&
                repository.ReplayGuildMileageGift(actor, Key(100), 69, now)?.Error == 14,
                "礼袋同事务扣一件与增加积分，重放须绑定角色、公会和源槽");
            var provider = new GuildExperienceBonusProvider(repository);
            That(provider.Capture(new(expected, ExperienceEvent.Kill, 1, now, false)).Rates is [{ Source: ExperienceBonusSource.Guild, Percent: 10 }] &&
                provider.Capture(new(expected, ExperienceEvent.Clear, 1, now, false)).Rates.IsEmpty,
                "公会10%只进入真实击杀经验分项，不给通关和任务加成");
            Sql($"DELETE FROM guild_members WHERE character_id={master.Id}");
            That(provider.Capture(new(expected, ExperienceEvent.Kill, 1, now, false)).Rates.IsEmpty,
                "退会后旧角色快照不能继续授予经验福利");
            session.End();
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
