using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class GuildContributionTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int before = Count;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "guild-contribution"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        ushort id;
        using (var owner = owners.Acquire("10000", static () => { }))
            id = repository.Create(owner, new("CoinMember", resources.Towns.Room, resources.Towns.InitialReturn)
            { Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0)) }).Id;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), lastWeek = GuildContributionRules.WeekStart(now) - 7 * 86400;
        fixture.Sql("""
            INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix)
            VALUES(97,'周币测试','g-coins',$p0,1600000000);
            INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix,contribution)
            VALUES(97,$p0,0,1600000000,150);
            INSERT INTO guild_contribution_events(event_id,character_id,guild_id,joined_at_unix,source,
                business_day_unix,week_start_unix,points,created_at_unix)
            VALUES('previous-week-fixture',$p0,97,1600000000,1,$p1,$p1,150,$p1)
            """, id, lastWeek);
        fixture.Sql("CREATE TRIGGER weekly_tcp_abort AFTER INSERT ON guild_contribution_settlements BEGIN SELECT RAISE(ABORT,'weekly TCP rollback'); END");
        fixture.Sql("""
            INSERT INTO guild_hongbao_records(guild_id,sender_account,sender_name,item_id,total,remaining,portions,taken,created_at,expires_at)
            VALUES(97,'10000','CoinMember',2700001,500,500,5,0,$p0,$p1)
            """, now - 86401, now - 1);
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            Enter(peer);
            That(Drain(peer).All(packet => packet.Opcode != 1726) &&
                fixture.Query("SELECT count(*) FROM guild_contribution_settlements") is [[0L]],
                "周币末段SQL失败保持入城可用，不发成功提示且保留原周结算资格");
            using var owner = owners.Acquire("10000", static () => { });
            That(repository.List(owner).Single().Inventory.Items.All(item => item.Core.ItemId != GuildContributionRules.CoinItemId),
                "回执失败后已规划的周币也真实回滚，不留半次资产");
            That(repository.List(owner).Single().Inventory.AccountWallet.Get(CurrencyKind.TokenCera) == 500 &&
                fixture.Query("SELECT refunded,remaining FROM guild_hongbao_records") is [[1L, 0L]],
                "贡献结算SQL失败不跳过独立红包退款，真实Ready仍原子退回发行账号500代币券");
        }
        fixture.Sql("DROP TRIGGER weekly_tcp_abort");
        using (var peer = Open(fixture))
        {
            Enter(peer);
            var ready = Drain(peer);
            var awarded = ready.Where(packet => packet.Opcode == 1726).ToArray();
            That(awarded is [{ Body.Length: 4 }] && BinaryPrimitives.ReadUInt32LittleEndian(awarded[0].Body) == 300,
                "真实Ready结算上一周贡献，仅到账后发送1726实际300币提示");
            using (var owner = owners.Acquire("10000", static () => { }))
            {
                var character = repository.List(owner).Single();
                That(character.Inventory.Items.Where(item => item.Position.List == InventoryListType.Main &&
                    item.Core.ItemId == GuildContributionRules.CoinItemId).Sum(item => item.Core.Count) == 300 &&
                    character.Inventory.Items.Where(item => item.Core.ItemId == GuildContributionRules.CoinItemId).All(item => item.Uid > 0),
                    "周币落入本版490011648真实背包实例，使用正常持久UID");
            }
            That(fixture.Query("SELECT contribution FROM guild_members WHERE character_id=$p0", id) is [[250L]],
                "本次自然登录增加100贡献，不把旧币奖励算作贡献或公共积分");
            peer.Send(17, Chat("贡献聊天一"));
            var chat = Drain(peer).Where(packet => packet.Opcode == 65).ToArray();
            byte[] expectedChat = Build(writer =>
            {
                writer.Write(new byte[] { 6, 0, 0, 0 });
                writer.Write(GuildApplicationTests.Text("CoinMember"));
                writer.Write((byte)2);
                writer.Write(GuildApplicationTests.Text("贡献聊天一"));
                writer.Write(0u);
            });
            That(chat.Length == 1 && chat[0].Body.AsSpan().SequenceEqual(expectedChat) &&
                fixture.Query("SELECT contribution FROM guild_members WHERE character_id=$p0", id) is [[300L]],
                "真实公会聊天发送成功后增加当日50贡献");
            peer.Send(17, Chat("贡献聊天二"));
            Drain(peer);
            That(fixture.Query("SELECT contribution FROM guild_members WHERE character_id=$p0", id) is [[300L]] &&
                fixture.Query("SELECT count(*) FROM guild_contribution_settlements WHERE guild_id=97") is [[1L]] &&
                fixture.Query("SELECT public_funds FROM guilds WHERE guild_id=97") is [[0L]],
                "重复聊天不刷贡献，周币回执唯一且不混入公会公共积分");
        }
        using (var peer = Open(fixture))
        {
            Enter(peer);
            That(Drain(peer).All(packet => packet.Opcode != 1726) &&
                fixture.Query("SELECT contribution FROM guild_members WHERE character_id=$p0", id) is [[300L]] &&
                fixture.Query("SELECT balance FROM account_currency_balances WHERE account='10000' AND currency_kind=$p0", (int)CurrencyKind.TokenCera) is [[500L]],
                "同日重登不重复贡献、不重发已结算周币");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] &&
            fixture.Query("SELECT count(*) FROM pragma_foreign_key_check") is [[0L]], "贡献周币TCP隔离库完整");
        Console.WriteLine($"PASS {Count - before} 公会自然贡献与周币到账TCP断言");
    }

    static byte[] Chat(string text) => Build(writer =>
    {
        writer.Write((byte)6); writer.Write((ushort)3); writer.Write(0u); writer.Write(GuildApplicationTests.Text(text));
    });
    static List<(ushort Opcode, byte[] Body)> Drain(TcpPeer peer)
    {
        peer.Send(156, GuildApplicationTests.Text("GCoinBarrier"));
        var packets = new List<(ushort, byte[])>();
        for (int guard = 0; guard < 256; guard++)
        {
            var packet = peer.Read();
            if (packet.Kind == 1 && packet.Opcode == 156) { Bytes(packet.Body, [1], "贡献查重哨兵成功"); return packets; }
            if (packet.Kind == 0) packets.Add((packet.Opcode, packet.Body));
        }
        throw new InvalidOperationException("Guild contribution did not reach request barrier");
    }
    static void Enter(TcpPeer peer)
    {
        peer.Send(8, [255, 255, 2]); peer.Send(4, Build(writer => writer.Write((ushort)0)));
        for (int guard = 0; guard < 256; guard++)
            if (peer.Read() is { Kind: 0, Opcode: 124 }) { peer.ReadWeeklyDungeon(); return; }
        throw new InvalidOperationException("Guild contribution TCP did not enter town");
    }
}
