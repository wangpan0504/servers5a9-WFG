using static DfoServer.Testing.TestCheck;
using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;

namespace DfoServer;

/// <summary>
/// 建会的**会话级提交路径**：与拍卖行上架同一形态 —— 提交后的快照安装回会话、产出带提交回执的常规差量，
/// 投影据此发布金币差额。不需要 PVF（空城镇目录 + 合成参数），因此归入核心自测。
/// </summary>
internal static class GuildCommitTests
{
    static readonly Location Room = new(38, 1, 450, 234, 0);

    public static void Run()
    {
        int checks = Count;
        Commit().GetAwaiter().GetResult();
        Console.WriteLine($"PASS {Count - checks} guild create session commit/publication assertions");
    }

    static async Task Commit()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-guild-commit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var owners = new AccountOwners();
            using var owner = owners.Acquire("guildfounder", () => { });
            var repository = new CharacterRepository(path);
            var towns = new TownCatalog(new Dictionary<(byte, byte), TownArea>(), Room, Room, new TownDock(0, [], []),
                [], new Dictionary<uint, uint[]>());
            var pieces = new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]");
            var created = repository.Create(owner, new("GuildFounder", Room, null)
            {
                Level = 95, Inventory = InventorySnapshot.Empty with { Gold = 1_000_000 }
            });
            var session = new GameSession(repository, towns, owner);
            session.Select(created.Slot);
            session.MarkReady();
            var planned = session.Selected!;
            var directory = new GuildDirectory(repository, GuildParameterTests.Synthetic());

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var (error, plan) = directory.PlanCreate(planned, "BUG公会", "bug-guild", now);
            That(error == GuildCreateError.None && plan is not null, "会话内规划建会成功（金币 1,000,000 ≥ 花费 300,000）");

            var commit = session.CreateGuild(directory, planned, plan!);
            That(commit.Guild.Error == GuildCreateError.None && commit.Guild.Character is not null && commit.Guild.GuildId > 0,
                "★ 建会提交成功并带回提交后的角色快照与公会 id");
            That(ReferenceEquals(session.Selected, commit.Guild.Character),
                "★ 提交后的快照安装回会话：否则下一笔背包操作会以陈旧 revision 被写事务拒绝（与上架同一条教训）");
            That(commit.Inventory is { Receipt: not null, WholeListRefresh: false },
                "★ 建会产出常规差量 + 提交回执（只改金币，不需要整列重建）");
            That(session.CanPublishInventory(commit.Inventory!.Receipt!),
                "★ 回执绑定刚安装的快照与 owner/generation/选角 epoch，投影因此允许发布");
            That(commit.Inventory.Receipt!.Character.Inventory.Gold == 700_000,
                "回执里的快照就是扣费后的余额");

            var stored = repository.List(owner).Single();
            That(stored.Inventory.Gold == 700_000 && stored.Revision == planned.Revision + 1,
                "独立读库确认金币扣了 300000 且 revision 前进一次");
            That(repository.GuildOf(stored.Id) == commit.Guild.GuildId, "公会对角色的归属已落库");

            // 真实投影 + 真实发送队列：建会后的刷新必须有回执且不抛异常。
            var stats = new CharacterStatComputer(new CharacterCatalog(new Dictionary<byte, CharacterProfile>()));
            var projection = new InventoryProjection(stats, new TownProjection(towns, stats, null, pieces), pieces);
            using var stop = new CancellationTokenSource();
            using var stream = new MemoryStream();
            var sent = new List<string>();
            await using var sender = new GameConnectionSender(owner, stream, stop, 0, (_, _, _, _, action) => sent.Add(action));
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), owner, session, sender);
            bool threw = false;
            try
            {
                await projection.RefreshAsync(context, commit.Inventory);
            }
            catch (Exception failure)
            {
                threw = true;
                Console.WriteLine("建会后的背包刷新抛异常：" + failure);
            }
            That(!threw, "★ 建会成功后背包刷新**不抛异常**（回执由会话在安装快照时产出）");
            That(sent.Contains("inventory_committed_delta"), "★ 建会后的刷新以常规差量发布（金币差额）");

            // 陈旧计划：拿提交前的快照再提交一次，必须被拒且不产生第二次扣费。
            var stale = session.CreateGuild(directory, planned, plan!);
            That(stale.Guild.Error == GuildCreateError.StalePlan && stale.Inventory is null,
                "★ 陈旧计划（提交前的旧快照）被拒绝：不安装快照、也不产出可发布的刷新请求");
            That(repository.List(owner).Single().Inventory.Gold == 700_000, "陈旧计划没有第二次扣费");
        }
        finally
        {
            foreach (string file in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(file))
                    File.Delete(file);
            Directory.Delete(root, recursive: true);
        }
    }
}
