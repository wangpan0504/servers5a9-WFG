using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Network;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class EmoticonTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int[] ids = resources.ItemResources.Skins.Emoticons.Order().Take(5).ToArray();
        That(ids.Length == 5, "正式资源提供五个真实类别3表情，含未拥有样本");
        await PublicationAsync(Path.Combine(root, "emoticon-publication"), resources, ids);
        var clock = new DailyBoosterTestClock(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
        int expiry = checked((int)clock.GetUtcNow().ToUnixTimeSeconds() + 10);
        SkinOwnership[] owned = [new(ids[0], 0), new(ids[1], expiry), new(ids[3], expiry - 20)];
        var accountSkins = SkinStorageState.Empty with { Emoticons = [new(ids[2], 0), new(ids[3], 0)] };
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "emoticon"), resources: resources, clock: clock);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        foreach (var (account, name) in new[] { ("10000", "EmoticonA"), ("20001", "EmoticonB"), ("20002", "EmoticonC") })
        {
            using var owner = owners.Acquire(account, () => { });
            var created = repository.Create(owner, new(name, resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 75, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills
            });
            if (account == "10000")
            {
                fixture.Sql("UPDATE characters SET weapon_skin_state=$p0 WHERE id=$p1",
                    JsonSerializer.Serialize(SkinStorageState.Empty with { Emoticons = [.. owned] }), created.Id);
                fixture.Sql("INSERT INTO account_skin_storage(account,state_json) VALUES($p0,$p1)", account, JsonSerializer.Serialize(accountSkins));
            }
        }
        await fixture.StartAsync();
        using var alpha = Open(fixture);
        using var beta = Open(fixture, "20001");
        var channels = ChannelTcpTests.Endpoints(fixture);
        using var gamma = Open(fixture, "20002", gamePort: channels[11], channelNumber: 11);
        alpha.Send(1559, Build(w => w.Write(ids[0])));
        alpha.Select(emoticons: (owned.Where(row => row.ExpiresAt == 0 || row.ExpiresAt >= expiry).ToArray(), []), accountSkins: accountSkins);
        beta.Select(); gamma.Select();
        string Snapshot() => fixture.Snapshot() + fixture.Snapshot("SELECT * FROM account_skin_storage ORDER BY account") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");
        string before = Snapshot();
        foreach (int id in ids.Take(4))
        {
            alpha.Send(1559, Build(w => w.Write(id)));
            alpha.Expect(0, 1535, Build(w => { w.Write(alpha.Id); w.Write(id); }));
            alpha.Barrier(); beta.Barrier(); gamma.Barrier();
        }
        That(Snapshot() == before, "本人播放角色及账号表情，账号永久条目覆盖角色过期条目；私人房间彼此隔离且不写资产");
        alpha.Send(1426); Settle(alpha);
        beta.Send(1426); Settle(beta); Settle(alpha);
        gamma.Send(1426); Settle(gamma);
        before = Snapshot();
        for (int i = 0; i < 3; i++)
        {
            alpha.Send(1559, Build(w => w.Write(ids[0])));
            byte[] expected = Build(w => { w.Write(alpha.Id); w.Write(ids[0]); });
            alpha.Expect(0, 1535, expected); beta.Expect(0, 1535, expected);
        }
        alpha.Barrier(); beta.Barrier(); gamma.Barrier();
        That(Snapshot() == before, "同屏玩家与本人收到完整6B通知，另一频道不收；普通城镇不强加10秒冷却");
        foreach (var body in new byte[][] { [], [0], new byte[3], new byte[5],
            Build(w => w.Write(0)), Build(w => w.Write(-1)), Build(w => w.Write(ids[4])), Build(w => w.Write(20000)) })
        {
            alpha.Send(1559, body); alpha.Barrier(); beta.Barrier(); gamma.Barrier();
        }
        beta.Send(1559, Build(w => w.Write(ids[0]))); beta.Barrier(); alpha.Barrier();
        clock.Advance(TimeSpan.FromSeconds(10));
        alpha.Send(1559, Build(w => w.Write(ids[1]))); alpha.Barrier(); beta.Barrier(); gamma.Barrier();
        That(Snapshot() == before, "非法长度/编号、错类别、未拥有及精确到期均无播放和写入，连接仍可继续");
        alpha.Send(1559, Build(w => w.Write(ids[2])));
        alpha.Expect(0, 1535, Build(w => { w.Write(alpha.Id); w.Write(ids[2]); }));
        beta.Expect(0, 1535, Build(w => { w.Write(alpha.Id); w.Write(ids[2]); }));
        alpha.Barrier(); beta.Barrier(); gamma.Barrier();
    }

    static void Settle(TcpPeer peer)
    {
        peer.SendBarrier();
        for (int i = 0; i < 64; i++)
        {
            var packet = peer.Read();
            if (packet is { Kind: 1, Opcode: 1347 }) return;
            That(packet.Kind == 0 && packet.Opcode is 2 or 3 or 4 or 22 or 23 or 24,
                "场景收尾只消费明确的演员及位置通知，不能隐藏表情或ACK");
        }
        throw new InvalidOperationException("Emoticon scene barrier did not complete");
    }

    static async Task PublicationAsync(string root, TcpTestResources resources, int[] ids)
    {
        var point = new Location(1, 0, 10, 10);
        var map = new TownMap("emoticon.map", [new(new(0, 0, 20, 20), [], [])], [], false, []);
        var towns = new TownCatalog(new() { [(1, 0)] = new(1, 0, 1, [], [], [], point, map, [], []) },
            point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
        var repository = new CharacterRepository(Path.Combine(root, "game.db"));
        var owners = new AccountOwners();
        using var sourceOwner = owners.Acquire("emoticon-source", () => { });
        using var targetOwner = owners.Acquire("emoticon-target", () => { });
        repository.Create(sourceOwner, new("EmoteSource", point, point));
        repository.Create(sourceOwner, new("EmoteOther", point, point));
        repository.Create(targetOwner, new("EmoteTarget", point, point));
        var source = new GameSession(repository, towns, sourceOwner);
        var target = new GameSession(repository, towns, targetOwner);
        source.Select(0); source.MarkReady(); target.Select(0); target.MarkReady();
        That(source.PlanAndCommitInventory(character => new(true, 0, new(character.Inventory, character.Inventory with
        {
            Skins = SkinStorageState.Empty with { Emoticons = [new(ids[0], 0)] }
        }, "emoticon_test_grant", new(character, null)))).Accepted, "延迟发布夹具通过真实库存提交取得表情所有权");
        var assets = resources.ItemResources;
        var skins = new InventorySkinStorageService(assets.Items, assets.Skins, assets.GoldLimits, assets.EpicPieces, assets.Contracts);
        using var stop = new CancellationTokenSource();
        using var stream = new DfoServer.GameSenderBudgetTests.HeldStream();
        await using var sender = new GameConnectionSender(targetOwner, stream, stop, 0, (_, _, _, _, _) => { });
        target.EmoticonArrived = notice => sender.Post(0, 1535, SkinStoragePackets.Emoticon(notice.MemberId, notice.SkinId), "emoticon_test", notice.Guard);
        var held = sender.Send(0, 2, [1], "held");
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var moving = source.CaptureEmoticon(skins, ids[0])!;
        That(target.TryDeliverEmoticon(moving), "当前同屏投递入慢连接队列");
        source.Move(new(11, 10, 0, 0, 0, 0));
        That(moving.Guard(), "同地图移动及其revision不会吞掉已排队表情");
        stream.Release.TrySetResult(); await held; await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        That(stream.Frames.Count == 2, "慢发送队列恢复后真实写出表情");
        EmoticonNotice? queued = null;
        target.EmoticonArrived = notice => queued = notice;
        That(target.TryDeliverEmoticon(source.CaptureEmoticon(skins, ids[0])!), "捕获接收者守卫");
        source.ReturnToRoster(); source.Select(1); source.MarkReady();
        That(!queued!.Guard() && source.CaptureEmoticon(skins, ids[0]) is null,
            "源换角使旧播放失效，新角色不继承表情资格");
        source.ReturnToRoster(); source.Select(0); source.MarkReady();
        That(target.TryDeliverEmoticon(source.CaptureEmoticon(skins, ids[0])!), "再次捕获同屏接收者");
        target.ReturnToRoster(); target.Select(0); target.MarkReady();
        That(!queued!.Guard(), "目标重选同一角色也使旧epoch播放失效");
        var stale = source.CaptureEmoticon(skins, ids[0])!;
        using var replacement = owners.Acquire("emoticon-source", () => { });
        That(!stale.Guard() && !target.TryDeliverEmoticon(stale), "账号接管后旧generation不能发布表情");
    }
}
