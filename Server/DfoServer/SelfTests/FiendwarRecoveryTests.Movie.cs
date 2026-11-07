using DfoServer.Game.Accounts;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Raids;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class FiendwarRecoveryTests
{
    // 实际开战和SQLite阶段/奖励事务；终结借用明确的合成合同，不充当战斗或客户端验收。
    internal static void RunMovieResources(string root, TcpTestResources resources)
    {
        int before = Count;
        foreach (string mode in new[] { "cards", "ineligible", "ineligible-timeout", "offline", "nonzero-offline", "old-owner" })
            CheckMovie(Path.Combine(root, mode), resources, mode);
        Console.WriteLine($"Fiendwar movie/recovery SQLite: {Count - before} passed");
    }

    static void CheckMovie(string root, TcpTestResources resources, string mode)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        var repository = new CharacterRepository(path, resources.Quests);
        var owners = new AccountOwners();
        using var owner = owners.Acquire("fw-movie", () => { });
        var clock = new LukeRaidClockTests.Clock(new(2026, 11, 3, 6, 1, 0, TimeSpan.FromHours(8)));
        var definition = new RaidStartSettings { StartMinimumOverride = 1 }.Apply(RaidCatalog.Load(resources.Archive))
            .PrepareResources(resources.Archive, SelfTestCatalogs.Dungeons(resources.Archive), resources.ItemResources.Items).Find(2)!;
        var items = resources.ItemResources;
        Seed(repository, owner, resources, "FwMovie", definition.Resources!.Script.EntryCost!.ItemId, clock.GetUtcNow());
        var session = Session(repository, owner, resources, clock);
        var registry = new RaidRegistry(clock);
        var parties = new PartyService(repository);
        session.CurrentParty = () => parties.Current(session);
        var actor = session.CaptureRaidActor(registry)!;
        var resource = definition.Resources.Fiendwar!;
        var rewards = new FiendwarRewards(mode.StartsWith("ineligible", StringComparison.Ordinal)
            ? resource with { MinimumClears = 2 } : resource, items.Items, items.Boosters);
        // 与正式Runtime一致注入有界Luke时钟；没有timer lease时Start虽能扣票，结算锚点不会激活。
        var timers = new LukeRaidClocks(clock, repository, static (_, _) => { });
        var start = new AntonRaidStart(registry, parties, repository, resources.Towns, items.EpicPieces, clock,
            timers: timers, fiendwarRewards: rewards);
        Guid instance = Guid.Empty;
        bool ended = false;
        try
        {
            That(registry.Create(actor, 2, "Movie", "Movie"u8.ToArray(), 8) is not null &&
                start.Start(actor, registry.Find(actor)!, definition).Error == 0, "漩涡电影夹具沿正式开战扣票及原生命周期");
            var battle = session.CurrentRaidBattle(actor)!;
            instance = battle.Instance;
            // 测试时钟的结算锚点使用单调时间戳；只推进UTC会让ElapsedTime仍为零。
            clock.AdvanceMillisecondsWithoutCallbacks(7_000);
            if (mode == "cards")
            {
                // 合成终杀已提交、资格事务未返回的窗口，位置仍在黑白桥。
                var beforeReturn = battle.View;
                typeof(AntonRaidBattle).GetField("current", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .SetValue(battle, beforeReturn with { Fiendwar = beforeReturn.Fiendwar! with { Cleared = true },
                        FiendwarPositions = beforeReturn.FiendwarPositions.SetItem(actor.Character, 31) });
            }
            var candidate = FiendwarSharedLimitTests.CaptureSettledContract(battle, actor);
            That(candidate.Seconds == 7, "漩涡独立总时钟从实际开战起算，冻结结算耗时不为零");
            if (mode == "cards")
            {
                using var returned = battle.ReserveFiendwarReturn(actor, null);
                That(returned is not null, "资格事务等待期间可预留合法终局返回");
                returned!.Install();
            }
            var committed = repository.CommitLukePhase(candidate);
            battle.AcceptLukeSettlement(candidate, committed);
            var view = battle.View;
            if (mode == "cards")
                That(battle.FiendwarPosition(actor.Character) == battle.FiendwarBaseArea,
                    "资格事务回执不能用冻结桥位置覆盖已经提交的返回位置");
            var eligibility = committed.Members.Single();
            var entitlements = battle.FiendwarTeamEntitlements(view);
            That(eligibility.Eligible
                ? entitlements is [var teamRow] && teamRow.Owner == actor.Character &&
                    teamRow.Offer == eligibility.Offers.Single(offer => offer.Kind == 0)
                : entitlements.IsEmpty, "团队页只投影原事务冻结的合格权益，不制造无资格成员奖励");
            That(battle.FiendwarTeamEntitlements(view with { Revision = view.Revision + 1 }).IsDefault,
                "旧视图不能发布新实例团队页");
            That(candidate.Week == FiendwarRewards.Cycle.Key(clock.GetUtcNow()) && candidate.Week == 20261103,
                "漩涡终结冻结周二周期，不使用卢克周日或面板周四周期");
            That(view.State == AntonRaidBattleState.Reward && battle.CanConfirmFiendwarMovie(actor, view) &&
                !battle.FiendwarMovieCardsReady(view) && !battle.CanClaimRaidReward(actor, 0) &&
                !battle.CanClaimRaidReward(actor, 0, automaticLuke: true), "成功终结先保留演出，提前手动和未到期自动领取均拒绝");
            That(!battle.CompleteFiendwarMovie(actor, view with { Revision = view.Revision + 1 }),
                "复制或过期视图不能完成真实电影");
            if (mode == "old-owner")
            {
                var movie = battle.CaptureMovie(actor)!.Value;
                using var replacement = owners.Acquire(owner.Account, () => { });
                bool queued = false;
                That(!battle.CanConfirmFiendwarMovie(actor, view) && !battle.CompleteFiendwarMovie(actor, view) &&
                    battle.ConfirmMovie(actor, view, movie.Event, true, () => queued = true) is null && !queued,
                    "被接管旧owner不能确认电影或开放牌面");
                return;
            }
            if (mode is "cards" or "ineligible")
            {
                if (mode == "cards")
                {
                    That(session.RaidStatusSnapshot() is { Battle.Kind: RaidKind.Fiendwar },
                        "返城使用的当前团本快照包含漩涡，实际24写后必须进入补演出分支");
                    // 上面的领域预留只移动战场占用；原电影仍要求真实Session位置也提交到候战房。
                    if (session.NeedsFiendwarFinalReturn())
                        That(session.ReturnFromFiendwarBattlefield() is not null, "场景回归先沿原Session用例提交最终候战位置");
                    view = battle.View;
                    RaidMovieResponseTests.ReturnAsync(resources, actor, battle, clock).GetAwaiter().GetResult();
                }
                That(battle.CompleteFiendwarMovie(actor, view), "真实659完成通知解除电影义务");
                if (mode == "cards")
                {
                    That(!battle.CompleteFiendwarMovie(actor, view) && battle.FiendwarMovieCardsReady(view) &&
                        battle.CanClaimRaidReward(actor, 0), "重复确认不再推进，有资格成员才能沿原660领取");
                    foreach (var offer in eligibility.Offers)
                    {
                        var claimed = session.ClaimRaidReward(offer.Kind, 3, repository, items.Items, items.GoldLimits, items.EpicPieces, clock.GetUtcNow());
                        That(claimed is { Prepared.Card: 3 } && claimed.Prepared.Grants.SequenceEqual(offer.Grants),
                            "非零卡位只移动冻结原产物的展示位置，不重抽或替换奖励");
                        That(session.ClaimRaidReward(offer.Kind, 2, repository, items.Items, items.GoldLimits, items.EpicPieces, clock.GetUtcNow()) is null,
                            "同kind换卡重试不能另造权益");
                        That(session.ClaimRaidReward(offer.Kind, 3, repository, items.Items, items.GoldLimits, items.EpicPieces, clock.GetUtcNow()) is { Commit.Duplicate: true },
                            "原非零卡位重试只重放唯一已提交回执");
                    }
                }
                else
                    That(!eligibility.Eligible && battle.View.State == AntonRaidBattleState.Completed &&
                        !battle.FiendwarMovieCardsReady(battle.View), "全员无资格也先演出再完成，不创建空卡或假奖励");
                return;
            }

            if (mode == "nonzero-offline")
            {
                That(battle.CompleteFiendwarMovie(actor, view), "非零卡位断线回归先完成真实电影");
                var first = eligibility.Offers[0];
                That(session.ClaimRaidReward(first.Kind, 3, repository, items.Items, items.GoldLimits, items.EpicPieces, clock.GetUtcNow()) is { Prepared.Card: 3 },
                    "原660已提交非零卡但ACK未确认，另一kind尚未领取");
            }

            // 正常离线释放选角与原owner；自动收尾重新取得空闲账号，不能接管另一在线owner。
            session.End();
            ended = true;
            owner.Dispose();
            That((mode == "nonzero-offline" || !battle.FiendwarMovieCardsReady(battle.View)) && !battle.FiendwarRewardExpired,
                "断线保留此前真实电影状态，不冒充确认或提前到期");
            var automatic = new LukeAutomaticRewards(repository, owners, new(), (lease, id) =>
            {
                var role = repository.LoadCharacter(lease, 0);
                return role.Id == id ? role : null;
            }, items.Items, items.GoldLimits, items.EpicPieces, clock);
            automatic.SettleAsync(battle).GetAwaiter().GetResult();
            That(battle.View.State == AntonRaidBattleState.Reward, "截止前无资格或离线成员仍保持有界电影等待");
            clock.Advance(141);
            That(battle.FiendwarRewardExpired, "漩涡采用资源20秒演出加原120秒选牌期限");
            if (mode is "offline" or "nonzero-offline")
            {
                using var recoveryOwner = owners.Acquire("fw-movie", () => { });
                var role = repository.LoadCharacter(recoveryOwner, 0);
                var offer = eligibility.Offers[0];
                var target = new LukeRewardRecoveryTarget(role.Id, battle.Instance, 0, offer.Kind, offer.Card)
                    { RaidType = RaidKind.Fiendwar };
                Reject<InvalidDataException>(() => repository.ReadLukeRewardRecovery(recoveryOwner, role, target with { RaidType = RaidKind.Luke }),
                    "同instance不能借用默认卢克类型读取漩涡资格");
                ChangeSource("fixture/wrong-source");
                try { Reject<InvalidDataException>(() => repository.ReadLukeRewardRecovery(recoveryOwner, role, target), "原开战source不匹配拒绝离线补发"); }
                finally { ChangeSource(candidate.Source); }
                var saved = repository.ReadLukeRewardRecovery(recoveryOwner, role, target);
                That(saved.Prepared.RaidType == RaidKind.Fiendwar && saved.Prepared.FinalPhase &&
                    saved.Prepared.Week == candidate.Week && saved.Event == candidate.Event && saved.Anchor == eligibility.Anchor,
                    "离线补发严格保留type2单阶段、原周界及event/anchor");
            }
            automatic.SettleAsync(battle).GetAwaiter().GetResult();
            That(battle.View.State == AntonRaidBattleState.Completed, "断线到期经原奖励事务完整收尾，不依赖客户端659");
            string receipts = ReceiptFacts();
            automatic.SettleAsync(battle).GetAwaiter().GetResult();
            That(ReceiptFacts() == receipts, "重复到期回调不重发资产或增加回执");
            using var checkOwner = owners.Acquire("fw-movie", () => { });
            var current = repository.LoadCharacter(checkOwner, 0);
            foreach (var offer in eligibility.Offers)
            {
                var target = new LukeRewardRecoveryTarget(current.Id, battle.Instance, 0, offer.Kind, offer.Card)
                    { RaidType = RaidKind.Fiendwar };
                if (eligibility.Eligible)
                    That(repository.ReadLukeRewardRecovery(checkOwner, current, target) is { Claimed: true } saved &&
                        (mode != "nonzero-offline" || saved.Prepared.Card == 3),
                        "每个原kind均有实际唯一已提交回执");
                else
                    Reject<InvalidDataException>(() => repository.ReadLukeRewardRecovery(checkOwner, current, target), "无资格到期不产生补发权益");
            }
        }
        finally
        {
            if (!ended) session.End();
            timers.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        void ChangeSource(string source)
        {
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE character_raid_runs SET source=$source WHERE instance_id=$instance";
            command.Parameters.AddWithValue("$source", source); command.Parameters.AddWithValue("$instance", instance.ToString("N"));
            command.ExecuteNonQuery();
        }

        string ReceiptFacts()
        {
            using var db = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly;Pooling=False"); db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT json_group_array(json_object('c',character_id,'i',instance_id,'p',plan_json,'m',mail_ids)) FROM character_raid_reward_receipts";
            return Convert.ToString(command.ExecuteScalar())!;
        }
    }
}
