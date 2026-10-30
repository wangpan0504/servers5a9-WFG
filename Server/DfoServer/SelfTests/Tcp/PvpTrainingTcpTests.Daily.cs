using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Pvp;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class PvpTrainingTcpTests
{
    static async Task DailyAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        var clock = new DailyBoosterTestClock(DateTimeOffset.Parse("2026-10-29T05:59:59Z"), TimeZoneInfo.Utc);
        await using var f = new TcpTestFixture(options, Path.Combine(root, "pvp-daily"), resources: resources, clock: clock);
        var repo = new CharacterRepository(f.Database, resources.Quests);
        var rating = new PvpRatingSettlement(repo, new(new PvpRatingCatalog(resources.Archive)));
        var catalog = new PvpMissionCatalog(resources.Archive);
        var book = new PvpMissionBook(catalog, repo, rating, resources.ItemResources.Items,
            resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces, clock, _ => 0);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire("daily-atomic", () => { }))
        using (var opponentOwner = owners.Acquire("daily-opponent", () => { }))
        {
            var character = repo.Create(owner, Seed("DailyAtomic"));
            var initial = book.Plan(owner, character, null);
            That(initial.DailyChanged && !initial.Inventory.Plan!.Changed && initial.Missions.IsEmpty,
                "首次分配无资产变化仍是必须持久化的业务变化");
            character = book.Commit(owner, initial);
            var stored = repo.ReadPvpMissionState(owner, character.Id, book.Period).Daily;
            That(stored is not null, "pvp_daily_allocation_persisted: 首次无奖励分配必须实际提交");
            var saved = stored!;
            var job = saved.Entries.Single(row => row.Mission.Condition == "winning job").Mission.Jobs[0];
            var opponent = repo.Create(opponentOwner, CharacterCreation.Prepare("DailyEnemy", resources.Characters.Get(job),
                new(10, 0, 474, 234), resources.Towns.InitialReturn, resources.Learning.Get));
            var reopened = new CharacterRepository(f.Database, resources.Quests);
            That(PvpDailyMissionDay.Same(saved, reopened.ReadPvpMissionState(owner, character.Id, book.Period).Daily) &&
                !book.Plan(owner, character, null).DailyChanged, "仓储重开及同日重规划保留原始分配与奖励，不重抽");
            for (int round = 0; round < 2; round++)
            {
                var oldSource = book.Plan(owner, character, null);
                for (int at = 0; at < 6; at++)
                {
                    var match = new PvpArenaResultRecord(Guid.NewGuid(), Guid.NewGuid(), 101, PvpArenaMode.Individual,
                        [new(owner.Account, character.Id, 0, 0, PvpArenaOutcome.Win) { Job = character.Job, Kills = 1 },
                         new(opponentOwner.Account, opponent.Id, 1, 1, PvpArenaOutcome.Lose) { Job = job }], false, book.Period.Start + 100 + at);
                    repo.CommitPvpArenaResult(owner, match);
                    if (at == 0) That(repo.ReadPvpMissionState(owner, character.Id, book.Period).DailyMatches.IsEmpty,
                        "只有整局事实而评分尚未到账时，不能提前推进日常");
                    rating.Settle(owner, character.Id, match.MatchId);
                }
                var plan = book.Plan(owner, character, null);
                That(plan.DailyAfter!.Entries.Count(row => row.Claimed) == 3 && plan.Inventory.Plan!.Changed &&
                    plan.DailyAfter.Entries.Where(row => row.Mission.Grade == 4).All(row => !row.Claimed),
                    "六场权威普通胜局完成普通三组，普通击杀不代替总力日常");
                string before = AtomicSnapshot();
                Reject<InvalidOperationException>(() => book.Commit(owner, oldSource), "来源增加后旧规划不得提交");
                That(before == AtomicSnapshot(), "过期来源规划不改变资产及领取位");
                f.Sql("CREATE TRIGGER daily_fail BEFORE UPDATE ON character_pvp_daily_missions BEGIN SELECT RAISE(ABORT,'daily fail'); END");
                Reject<SqliteException>(() => book.Commit(owner, plan), "每日领取写入失败，已执行资产变更必须回滚");
                That(before == AtomicSnapshot(), "pvp_daily_atomic_rollback: 资产、UID、奖励位、邮件和审计完整回滚");
                f.Sql("DROP TRIGGER daily_fail");
                character = book.Commit(owner, plan);
                That(character.Inventory.Items.Where(row => row.Core.ItemId == 10092640).Sum(row => row.Core.Count) == (round + 1) * 200 &&
                    character.Inventory.Items.All(row => row.Uid > 0), "每日三项实际发放200证明且保留真实UID，次日可再领同ID");
                before = AtomicSnapshot();
                Reject<InvalidOperationException>(() => book.Commit(owner, plan), "每日已领取规划不得重复提交");
                That(before == AtomicSnapshot() && !book.Plan(owner, character, null).DailyChanged &&
                    !book.Plan(owner, character, null).Inventory.Plan!.Changed, "同日重放无新奖励、邮件或领取更新");
                if (round != 0) continue;
                var expired = book.Plan(owner, character, null);
                clock.Advance(TimeSpan.FromDays(1));
                Reject<InvalidOperationException>(() => book.Commit(owner, expired), "跨服务日旧规划拒绝");
                That(before == AtomicSnapshot(), "跨日拒绝保持旧账本和资产");
                var tomorrow = book.Plan(owner, character, null);
                That(tomorrow.DailyAfter!.Entries.All(row => !row.Claimed) && tomorrow.Before.DailyMatches.IsEmpty &&
                    tomorrow.DailyAfter.Entries.Select(row => row.Mission.Id).SequenceEqual(saved.Entries.Select(row => row.Mission.Id)),
                    "新日同任务ID独立领取位，旧日秒级事实全部隔离");
                character = book.Commit(owner, tomorrow);
            }
            That(f.Query("SELECT count(*) FROM character_pvp_daily_missions WHERE character_id=$p0", character.Id) is [[2L]],
                "保留两日独立账本，不覆盖旧日领取凭据");
            var oldOwnerPlan = book.Plan(owner, character, null);
            string oldOwnerState = AtomicSnapshot();
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject<InvalidOperationException>(() => book.Commit(owner, oldOwnerPlan), "旧generation不能提交每日账本");
            That(oldOwnerState == AtomicSnapshot(), "接管后旧owner不写资产、每日领取或分配");
        }
        // 使用另一真实连接验证无请求日切；静态时钟临近下一次六点。
        using (var owner = owners.Acquire("daily-live", () => { }))
        {
            var character = repo.Create(owner, Seed("DailyLive"));
            // 新建连接角色的疲劳日戳与虚拟时钟一致，避免墙钟日期改变日切前提。
            f.Sql("UPDATE character_fatigue_reset_days SET service_day=$p0 WHERE character_id=$p1",
                FatigueDailyReset.Day(clock.GetUtcNow(), clock.LocalTimeZone), character.Id);
        }
        await f.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(f);
        string Saved(ushort id) => f.Snapshot("SELECT * FROM character_pvp_daily_missions WHERE character_id=$p0 ORDER BY service_day", id);
        using (var peer = Open(f, "daily-live", gamePort: endpoints[55], channelNumber: 55))
        {
            peer.Select(prepared: true); peer.Barrier();
            string saved = Saved(peer.Id);
            peer.Send(7); peer.Expect(1, 7, [1]); peer.Expect(0, 2);
            peer.Select(prepared: true); peer.Barrier();
            That(saved == Saved(peer.Id), "同连接小退选角保持当天原分配");
            await clock.WaitForOneShotTimersAsync(2);
            clock.Advance(TimeSpan.FromSeconds(1));
            var notices = new List<WirePacket>();
            while (notices.Count(row => row is { Kind: 0, Opcode: 579 }) < 2)
            {
                That(notices.Count < 200, "日切通知有界，等待真实579完成");
                notices.Add(peer.Read());
            }
            await clock.WaitForOneShotTimersAsync(2);
            notices.AddRange(PartyDungeonTcpTests.Drain(peer));
            That(notices.All(row => row.Kind == 0 && row.Opcode is 21 or 36 or 356 or 541 or 550 or 574 or 746 or 1343 or 1349 or 1536 or 578 or 579) &&
                notices.Single(row => row.Opcode == 36).Body.Length == 10 &&
                notices.Where(row => row.Opcode == 579).All(row => row.Body.Length == 56 && row.Body[1] == 3) &&
                notices.Where(row => row.Opcode == 579).Select(row => row.Body[0]).SequenceEqual(new byte[] { 2, 4 }) &&
                notices.Count(row => row.Opcode == 578) == 1, "无需请求即发送578及两组579，普通/总力均刷新且保留其他日切通知合同");
            That(f.Query("SELECT count(*) FROM character_pvp_daily_missions WHERE character_id=$p0", peer.Id) is [[2L]] && saved != Saved(peer.Id),
                "在线日切先持久分配再发布，旧日账本保留");
            peer.Barrier();
            peer.Send(3, [1]); peer.Expect(1, 3, [1]); peer.Closed();
        }
        using (var peer = Open(f, "daily-live", gamePort: endpoints[101], channelNumber: 101))
        {
            peer.Select(prepared: true); peer.Barrier();
            That(f.Query("SELECT count(*) FROM character_pvp_daily_missions WHERE character_id=$p0", peer.Id) is [[2L]],
                "跨频道重连恢复当前日原任务，不追加或重抽");
        }
        await f.StopAsync();
        That(clock.ActiveTimers == 0, "日常接线沿用原连接日切时钟，停服释放全部定时器");

        NewCharacter Seed(string name)
        {
            var seed = CharacterCreation.Prepare(name, resources.Characters.Get(0), new(10, 0, 474, 234),
                resources.Towns.InitialReturn, resources.Learning.Get);
            return seed with { Level = 60, AcceptedStoryLevel = 60, InitialLearning = SkillPointLedger.Synchronize(0, 0, 60,
                seed.InitialLearning with { Experience = SelfTestCatalogs.Growth(resources.Archive, resources.Learning).GetLevelStartExperience(60) }, resources.Learning) };
        }
        string AtomicSnapshot() => string.Join('|', new[] { "characters", "character_inventory_items", "character_inventory_slots",
            "character_pvp_daily_missions", "character_pvp_mission_rewards", "character_pvp_mission_facts", "mailbox_messages",
            "mailbox_attachments", "currency_audit_log", "sqlite_sequence" }.Select(table => f.Snapshot("SELECT * FROM " + table + " ORDER BY rowid")));
    }
}
