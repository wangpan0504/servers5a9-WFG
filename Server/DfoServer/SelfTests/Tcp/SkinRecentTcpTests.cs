using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class SkinRecentTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var assets = resources.ItemResources;
        var clock = new DailyBoosterTestClock(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        int now = checked((int)clock.GetUtcNow().ToUnixTimeSeconds());
        int emote = assets.Skins.Emoticons.Order().First();
        That(assets.Skins.Weapons.ContainsKey(32000) && assets.Skins.Cutscenes.Contains(30069) &&
            assets.Skins.DamageFonts.Contains(3) && assets.Skins.Token(10303302) is { Type: 2, SkinId: 8 } &&
            assets.Skins.Token(490010773) is { Type: 0, SkinId: 420000001, AccountWide: true },
            "最近获得专项使用正式资源的五类皮肤和真实入库券");
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "skin-recent"), resources: resources, clock: clock);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var repo = new CharacterRepository(fixture.Database);
            repo.Create(owner, new("RecentOwner", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 75, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([Token(65, 10303302), Token(66, 490010773)])
            });
            repo.Create(owner, new("RecentEmpty", resources.Towns.Room, resources.Towns.InitialReturn)
            { Level = 75, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills });
        }
        // 模拟升级前存档：没有Recent字段，也含已过期和已不在目录的历史所有权。
        fixture.Sql("UPDATE characters SET weapon_skin_state=$p0 WHERE id=1", JsonSerializer.Serialize(new
        {
            Owned = new[] { 32000 }, Selected = 0,
            DamageFonts = new[] { new SkinOwnership(3, now + 5) },
            Cutscenes = new[] { new SkinOwnership(30069, 0), new(30068, now - 1), new(99999999, 0) },
            Emoticons = new[] { new SkinOwnership(emote, 0) }
        }));
        SkinStorageKey[] legacy = [new(1, 30069), new(2, 3), new(3, emote), new(4, 32000)];
        SkinStorageKey[] withFont = [new(2, 8), .. legacy];
        SkinStorageKey[] withAccount = [new(0, 420000001), .. withFont];
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            SelectOwner(peer, legacy);
            string before = SkinJson();
            peer.Barrier();
            That(SkinJson() == before && State().Recent.IsEmpty,
                "旧存档最近获得用有效已拥有项补齐，排除默认、过期、目录外项且不写虚假时间");
            peer.Send(7); peer.Expect(1, 7, [1]); peer.Expect(0, 2);
            peer.Select(1, recentSkins: []);
            peer.Barrier();
            That(peer.RecentSkins.Length == 0, "同连接切至空角色时1559空列表清掉上一角色记录");
            peer.Send(7); peer.Expect(1, 7, [1]); peer.Expect(0, 2);
            SelectOwner(peer, legacy);
            Use(peer, 65);
            peer.Expect(1, 515, [1, 65, 0, 0, 199, 0, 0, 0]);
            peer.Expect(0, 1557, FontOwned());
            ExpectRecent(peer, withFont);
            peer.Expect(0, 1558, [2, 1, 0, 0, 0]);
            peer.Barrier();
            That(State().Recent is [{ Key.Type: 2, Key.SkinId: 8, AcquiredAt: var ticks }] && ticks == clock.GetUtcNow().UtcTicks,
                "真实入库记录时间与所有权同次保存，旧项没有伪造时间");
            string saved = Snapshot();
            Use(peer, 65);
            peer.Expect(1, 515, [0, 0, 0, 65, 0, 0, 199]);
            peer.Barrier(packet => That(packet.Opcode != 1559, "拒绝请求不能回放虚假新获得"));
            That(Snapshot() == saved, "失效来源不改最近记录、revision或资产");
            fixture.Sql("CREATE TRIGGER recent_account_fail BEFORE INSERT ON account_skin_storage BEGIN SELECT RAISE(ABORT,'recent account rollback'); END");
            Use(peer, 66); peer.Closed();
            That(Snapshot() == saved && Shared().Recent.IsEmpty,
                "账号仓末段写入失败时扣券、角色revision及最近历史全部回滚");
            fixture.Sql("DROP TRIGGER recent_account_fail");
        }
        using (var peer = Open(fixture))
        {
            SelectOwner(peer, withFont, font: true);
            Use(peer, 66);
            peer.Expect(1, 515, [1, 66, 0, 0, 199, 0, 0, 0]);
            peer.Expect(0, 1557, Build(w =>
            {
                w.Write((byte)0); w.Write((ushort)1); w.Write(20000); w.Write(0);
                w.Write((ushort)1); w.Write(420000001); w.Write(0);
            }));
            ExpectRecent(peer, withAccount);
            peer.Expect(0, 1558, Build(w => { w.Write((byte)0); w.Write(20000); }));
            peer.Barrier();
            That(Shared().Recent is [{ Key.Type: 0, Key.SkinId: 420000001 }] && State().Recent is [{ Key.Type: 2 }],
                "账号和角色的真实获得历史分别保存在原归属仓库，不复制另一侧数据");
            That(Shared().Recent[0].AcquiredAt > State().Recent[0].AcquiredAt,
                "同一时钟刻度连续入库仍按实际先后排序，不被账号与角色合并顺序颠倒");
            peer.Send(7); peer.Expect(1, 7, [1]); peer.Expect(0, 2);
            peer.Select(1, accountSkins: Shared(), recentSkins: [new(0, 420000001)]);
            peer.Barrier();
            That(State(2).Recent.IsEmpty, "同账号其他角色只共享账号获得，角色历史不串档");
        }
        await fixture.StopAsync();
        clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.StartAsync("recent-reopen");
        using (var peer = Open(fixture))
        {
            SelectOwner(peer, [new(0, 420000001), new(2, 8), new(1, 30069), new(3, emote), new(4, 32000)],
                font: true, expired: true, account: Shared());
            peer.Barrier();
            That(State().Recent.Length == 1 && Shared().Recent.Length == 1,
                "服务重建恢复实际获得顺序，并按当前期限过滤旧补齐项");
        }

        InventoryItem Token(short slot, int id) => new(0, new(InventoryListType.Main, slot),
            InventoryRewardGrantService.Create(assets.Items.Get(id), shopExpiration: true));
        SkinStorageState State(int id = 1) => JsonSerializer.Deserialize<SkinStorageState>((string)fixture.Query(
            "SELECT weapon_skin_state FROM characters WHERE id=$p0", id)[0][0])!;
        SkinStorageState Shared() => fixture.Query("SELECT state_json FROM account_skin_storage WHERE account=$p0",
            fixture.Options.DevelopmentAccount) is [[string json]] ? JsonSerializer.Deserialize<SkinStorageState>(json)! : SkinStorageState.Empty;
        string SkinJson() => fixture.Snapshot("SELECT weapon_skin_state FROM characters ORDER BY id");
        string Snapshot() => fixture.Snapshot("SELECT revision,weapon_skin_state FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM account_skin_storage ORDER BY account") +
            fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");
        void SelectOwner(TcpPeer peer, SkinStorageKey[] expected, bool font = false, bool expired = false, SkinStorageState? account = null) =>
            peer.Select(weaponSkins: ([32000], 0),
                damageFonts: (expired ? [new(8, 0)] : font ? [new(3, now + 5), new(8, 0)] : [new(3, now + 5)], 1),
                cutscenes: ([new(30069, 0)], [30000]), emoticons: ([new(emote, 0)], []),
                accountSkins: account, recentSkins: expected);
        byte[] FontOwned() => Build(w =>
        {
            w.Write((byte)2); w.Write((ushort)3); w.Write(1); w.Write(0);
            w.Write(3); w.Write(now + 5); w.Write(8); w.Write(0); w.Write((ushort)0);
        });
    }

    static void Use(TcpPeer peer, short slot) => peer.Send(515, Build(w =>
    { w.Write(slot); w.Write((byte)0); w.Write(0); w.Write(199); w.Write(0); w.Write(0); }));
    static void ExpectRecent(TcpPeer peer, SkinStorageKey[] expected) => peer.Expect(0, 1559, Build(w =>
    { w.Write(checked((byte)expected.Length)); foreach (var key in expected) { w.Write(key.Type); w.Write(key.SkinId); } }));
}
