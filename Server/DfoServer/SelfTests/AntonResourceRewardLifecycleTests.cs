using System.Buffers.Binary;
using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Raids;
using DfoServer.Persistence;
using DfoServer.Network;
using DfoServer.Network.Protocol;
using DfoServer.Network.Projection;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class AntonResourceRewardLifecycleTests
{
    internal static void Run(TcpTestResources resources) => RunAsync(resources).GetAwaiter().GetResult();

    static async Task RunAsync(TcpTestResources resources)
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-anton-reward-kinds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var owners = new AccountOwners(); var leases = new List<AccountOwners.Lease>(); var sessions = new List<GameSession>();
        try
        {
            var items = resources.ItemResources;
            var definition = new RaidStartSettings { StartMinimumOverride = 1 }.Apply(RaidCatalog.Load(resources.Archive))
                .PrepareResources(resources.Archive, SelfTestCatalogs.Dungeons(resources.Archive), items.Items).Find(0)!;
            var script = definition.Resources!.Script;
            var clock = new LukeRaidClockTests.Clock(); var registry = new RaidRegistry(clock);
            var repository = new CharacterRepository(Path.Combine(root, "game.db")); var parties = new PartyService(repository);
            var actors = Enumerable.Range(0, 2).Select(index =>
            {
                var owner = owners.Acquire("anton-reward-kinds-" + index, static () => { }); leases.Add(owner);
                var character = repository.Create(owner, CharacterCreation.Prepare("AntKind" + index, resources.Characters.Get(0),
                    resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get) with
                    { Level = 95, AcceptedStoryLevel = 95, CompletedQuests = [12829, 3779] });
                character = InventoryService.Commit(repository, owner, character, InventoryRewardGrantService.TryGrantBatch(character,
                    [new(script.EntryCost!.ItemId, 2)], items.Items, items.GoldLimits, items.EpicPieces, clock.GetUtcNow()));
                if (index == 1) character = repository.CommitInventory(owner, character, new(character.Inventory, character.Inventory with
                    { AccountWallet = character.Inventory.AccountWallet.SetContractExpiry(128, clock.GetUtcNow().ToUnixTimeSeconds() + 2) }, "anton_pcroom_fixture"));
                var session = new GameSession(repository, resources.Towns, owner, entry: new(new(2, 19), 23, "[raid]"), timeProvider: clock);
                sessions.Add(session); session.CurrentParty = () => parties.Current(session); session.Select(character.Slot); session.MarkReady();
                return session.CaptureRaidActor(registry)!;
            }).ToArray();
            var leader = actors[0]; registry.Create(leader, 0, "Kinds", "Kinds"u8.ToArray(), 20, 1);
            registry.Invite(leader, actors[1], 0);
            That(registry.Respond(actors[1], leader.Character, true).Accepted, "奖励夹具通过真实邀请建立成员身份");
            foreach (var actor in actors) registry.Assign(leader, actor.Character, 1);
            var start = new AntonRaidStart(registry, parties, repository, resources.Towns, items.EpicPieces, clock,
                phaseProgress: true, rewards: new(script, items.Items, items.Boosters));
            That(start.Start(leader, registry.Find(leader)!, definition).Error == 0, "奖励夹具经正式666事务扣票并冻结两人奖励");
            var battle = leader.Session.CurrentRaidBattle(leader)!;
            var policies = actors.Select(actor => battle.RewardPolicy(actor, 0)!).ToArray();
            That(policies[0].Offers.Select(offer => offer.Kind).SequenceEqual(new byte[] { 1, 3 }) &&
                policies[1].Offers.Select(offer => offer.Kind).SequenceEqual(new byte[] { 1, 2, 3 }), "普通与PC房逐人冻结不同牌种，不再把PC房材料塞进团队1");
            clock.Advance(3);
            That(!sessions[1].PremiumPcRoom && ReferenceEquals(policies[1], battle.RewardPolicy(actors[1], 0)),
                "开团后128到期不重抽或撤销已冻结的独立PC房牌");
            That(battle.AntonTeamEntitlements(battle.View).IsDefault, "尚未阶段资格提交时不能拿开团随机计划预填完整权益表");
            foreach (int dungeon in new[] { 210, 213, 215 })
            {
                string symbol = script.Phases[0].Dungeons.Single(row => row.Id == dungeon).HpSymbol;
                int count = Math.Max(1, battle.View.Symbols[symbol]);
                for (int index = 0; index < count; index++)
                {
                    var identity = new DungeonRunIdentity(leader.Character, 0, 1, Guid.NewGuid(), 1);
                    using var reserved = battle.ReserveDungeon(new(battle.Instance, 0, 1), (uint)dungeon, [(leader, identity)]);
                    That(reserved is not null, "奖励夹具按当前Run预留资源HP"); reserved!.Install();
                    That(battle.CompleteAntonReservedDungeon(new(identity, (uint)dungeon, [Guid.NewGuid()])
                        { ClearMap = new("boss", new(0, 0), 77, "fixture/anton-reward-kinds.map") }) is not null, "真实清图事实累计阶段HP");
                    battle.ReleaseDungeon(leader, identity.InstanceId);
                }
            }
            That(battle.PendingAntonSettlement() is not null && battle.AntonTeamEntitlements(battle.View).IsDefault,
                "已封存但未Accept的清图候选不覆盖已持久阶段权益");
            That(battle.CommitPendingAntonSettlement(repository), "真实阶段资格和冻结奖励同事务落库");
            var acceptedView = battle.View;
            var acceptedTable = battle.AntonTeamEntitlements(acceptedView);
            var acceptedCharacters = sessions.Select(session => session.Selected!).ToArray();
            That(acceptedTable.Length == 2 && acceptedTable.Select(row => row.Owner).SequenceEqual(actors.Select(actor => actor.Character).Order()) &&
                acceptedTable.All(row => row.Card == 0 && row.Grants.SequenceEqual(policies[Array.FindIndex(actors, actor => actor.Character == row.Owner)]
                    .Offers.Single(offer => offer.Kind == 3).Grants)),
                "任何人领取资产前完整表包含两个合格owner各自精确kind3权益，Card0沿原冻结值，不混入团队1或PC房2");
            That(battle.AntonTeamEntitlements(acceptedView).SequenceEqual(acceptedTable) &&
                sessions.Select((session, index) => ReferenceEquals(session.Selected, acceptedCharacters[index])).All(value => value) &&
                Scalar("SELECT count(*) FROM character_raid_reward_receipts") == 0,
                "重复读取完整权益不重抽、不授予他人资产、不确认领取且不改在线角色");
            var firstTeamFrames = new List<(byte Kind, ushort Opcode, byte[] Body)>();
            using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            using (var stream = new MemoryStream())
            {
                await using var sender = new GameConnectionSender(actors[0].Owner, stream, stop, 0,
                    (kind, opcode, _, body, _) => firstTeamFrames.Add((kind, opcode, body.ToArray())));
                var context = new GameConnectionContext(new(74, IPAddress.Loopback, stream, stop), actors[0].Owner, sessions[0], sender);
                That(context.AntonResults.PostTeam(context, actors[0], battle, acceptedView),
                    "第一连接在state4前安装完整class2团队表");
                That(context.AntonResults.PostTeam(context, actors[0], battle, acceptedView),
                    "同连接重复Notice只排队一次完整团队表");
                await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            var firstTeam = firstTeamFrames.Where(row => row.Opcode == (ushort)NotiPacketType.RAID_REWARD_LIST).ToArray();
            That(firstTeam.Length == 1 && firstTeam[0].Body[0] == 3 && firstTeam[0].Body[1] == acceptedTable.Length &&
                CompleteTeamBody(firstTeam[0].Body, acceptedTable),
                "第一连接完整class2帧按独立16B读宽包含全部owner/Card0/冻结物品且不含资产回执");
            await RaidMovieResponseTests.ReturnAsync(resources, actors[0], battle, clock);
            foreach (var actor in actors.SkipLast(1)) battle.CompleteAntonMovie(actor, battle.View);
            await RaidMovieResponseTests.FailedFinalAckAsync(actors[0], actors[^1], battle);
            That(battle.View.AntonCardsReady, "两成员真实影片确认后才开放奖励");
            var secondTeamFrames = new List<(byte Kind, ushort Opcode, byte[] Body)>();
            using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            using (var stream = new MemoryStream())
            {
                await using var sender = new GameConnectionSender(actors[0].Owner, stream, stop, 0,
                    (kind, opcode, _, body, _) => secondTeamFrames.Add((kind, opcode, body.ToArray())));
                var context = new GameConnectionContext(new(75, IPAddress.Loopback, stream, stop), actors[0].Owner, sessions[0], sender);
                var opened = battle.View;
                RaidCombatProjection.PostState(context, opened, actors[0], () => sessions[0].CanPublishRaidBattle(actors[0], opened), "anton-team-restore");
                RaidCombatProjection.PostState(context, opened, actors[0], () => sessions[0].CanPublishRaidBattle(actors[0], opened), "anton-team-restore-duplicate");
                await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            var secondTeam = secondTeamFrames.Single(row => row is { Opcode: 600, Body: [3, ..] }).Body;
            int tableAt = secondTeamFrames.FindIndex(row => row is { Opcode: 600, Body: [3, ..] });
            int stateAt = secondTeamFrames.FindIndex(row => row is { Opcode: 587, Body: [4, 0] });
            That(CompleteTeamBody(secondTeam, acceptedTable) && tableAt >= 0 && stateAt > tableAt,
                "同阶段新view恢复逐字段重发完整两人团队表且先于state4，重复Notice不重发，不依赖旧连接游标");
            That(battle.AntonTeamEntitlements(acceptedView).IsDefault && battle.AntonTeamEntitlements(battle.View).SequenceEqual(acceptedTable),
                "原view失效不能重发旧窗口，影片后最新view仍返回同一完整已持久权益");
            var before = sessions.Select(session => session.Selected!).ToArray();
            var receipts = new List<RaidRewardReceipt>();
            That(Claim(0, 2) is null && Claim(0, 0) is null && Claim(0, 3) is null &&
                sessions[0].ClaimAutomaticAntonSquadReward(repository, items.Items, items.GoldLimits, items.EpicPieces, clock.GetUtcNow()) is null,
                "普通成员无PC房资格，空牌背无资产计划，内部小队奖励须等待本人团队牌提交");
            var party = Claim(0, 1)!;
            receipts.Add(party);
            That(party.Prepared.Kind == 1 && party.Prepared.Card == 3 && sessions[0].SelectAntonEmptyCardBack(3)?.Card == 3 &&
                sessions[0].SelectAntonEmptyCardBack(2) is null, "自然1后0确认同一原牌位，换位不能绕过冻结");
            sessions[0].ConfirmRaidRewardPublished(party);
            var squad = sessions[0].ClaimAutomaticAntonSquadReward(repository, items.Items, items.GoldLimits, items.EpicPieces, clock.GetUtcNow());
            That(squad?.Prepared is { Kind: 3, Card: 0 } && battle.View.State == AntonRaidBattleState.Reward,
                "普通成员团队牌后内部自动交付小队3，未领取PC成员仍保持Reward");
            receipts.Add(squad!);
            sessions[0].ConfirmRaidRewardPublished(squad!);
            That(Claim(0, 3) is null, "内部小队3已提交后外部660仍不能重放回执或终结动画");
            var automatic = new List<byte>(); sessions[1].AntonAutoRewardArrived = receipt =>
            { automatic.Add(receipt.Prepared.Kind); receipts.Add(receipt); };
            bool automaticClaimed = false;
            await AntonCardBackProjectionTests.VerifyQueuedAsync(actors[0], battle, root, "same-phase",
                () => automaticClaimed = sessions[1].AutoClaimAntonReward(battle, 0, repository, items.Items,
                    items.GoldLimits, items.EpicPieces, clock.GetUtcNow()), expectedAck: true);
            That(automaticClaimed &&
                automatic.SequenceEqual(new byte[] { 1, 2, 3 }) && battle.View.State == AntonRaidBattleState.Intermission,
                "自动兜底沿真实领取链按团队1、PC房2、小队3提交，然后才进入休息");
            That(receipts.Count == 5 && receipts.All(receipt => receipt.Prepared.Character == receipt.Commit.Character.Id &&
                    receipt.Prepared.Grants.SequenceEqual(policies[Array.FindIndex(actors, actor => actor.Character == receipt.Prepared.Character)]
                        .Offers.Single(offer => offer.Kind == receipt.Prepared.Kind).Grants)) &&
                Scalar("SELECT count(*) FROM character_raid_reward_receipts") == 5 &&
                Scalar("SELECT count(*) FROM character_raid_reward_receipts WHERE kind=0") == 0,
                "五份实际回执各自属于本人冻结计划，SQL没有kind0资产记录");
            That(battle.AntonTeamEntitlements(battle.View).SequenceEqual(acceptedTable),
                "本人资产提交后完整权益表保持不变，不按已领取人数缩减class2列表");
            That(sessions[0].SelectAntonEmptyCardBack(3)?.Card == 3 && sessions[0].SelectAntonEmptyCardBack(2) is null,
                "休息态自然迟到0仍确认原选位，不接受任意牌位");
            for (int index = 0; index < actors.Length; index++)
            {
                var loaded = repository.LoadCharacter(actors[index].Owner, before[index].Slot);
                foreach (var group in policies[index].Offers.SelectMany(offer => offer.Grants).GroupBy(grant => grant.ItemId))
                {
                    long Balance(Character character) => items.EpicPieces.ByPiece.TryGetValue(group.Key, out var piece)
                        ? character.Inventory.EpicPieces.Get(piece.Index)
                        : CurrencyWallet.RewardKind(group.Key) is { } currency ? CurrencyWallet.Get(character.Inventory, currency)
                        : items.Items.Get(group.Key).AccountMaterial is not null ? character.Inventory.AccountWallet.Material(group.Key).Balance
                        : character.Inventory.Items.Where(item => item.Core.ItemId == group.Key).Sum(item => (long)item.Core.Count);
                    long actual = Balance(loaded) - Balance(before[index]), expected = group.Sum(grant => (long)grant.Count);
                    That(actual == expected, $"重新读取SQLite实际资产与各自冻结产物完全一致：角色{loaded.Id} 物品{group.Key} 实际{actual} 预期{expected}");
                }
                That(loaded.Revision == before[index].Revision + policies[index].Offers.Length && sessions[index].Selected!.Revision == loaded.Revision,
                    "每种奖励只增加一次角色版本并安装原Session");
            }
            var revision = sessions[1].Selected!.Revision;
            That(sessions[1].AutoClaimAntonReward(battle, 0, repository, items.Items, items.GoldLimits, items.EpicPieces, clock.GetUtcNow()) &&
                sessions[1].Selected!.Revision == revision, "进入休息后重复到期回调不再发奖");
            await AntonCardBackProjectionTests.VerifyQueuedAsync(actors[1], battle, root, "old-epoch",
                () => sessions[1].ReturnToRoster(), expectedAck: false, card: 0);
            var clockErrors = new List<string>();
            await using (var timers = new LukeRaidClocks(clock, repository, (stage, _) => clockErrors.Add(stage)))
            {
                using var lease = timers.Reserve(battle)!; lease.Activate();
                await AntonCardBackProjectionTests.VerifyQueuedAsync(actors[0], battle, root, "next-phase", () =>
                {
                    That(battle.PrepareAntonNextPhase(actors[0]), "新阶段隔离夹具沿真实团长准备入口推进");
                    clock.Advance(script.Headers.Single(row => row.Tag == "[start delay time]").Values.Single().Number);
                }, expectedAck: false);
                That(battle.View.Phase == 1 && clockErrors.Count == 0, "真实one-shot时钟完成准备再进入下一阶段，无旧牌背ACK泄漏");
            }

            RaidRewardReceipt? Claim(int index, byte kind) => sessions[index].ClaimRaidReward(kind, 3, repository,
                items.Items, items.GoldLimits, items.EpicPieces, clock.GetUtcNow());
            long Scalar(string sql)
            {
                using var db = new SqliteGameDatabase(Path.Combine(root, "game.db")).Open();
                using var command = db.CreateCommand(); command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar());
            }

            static bool CompleteTeamBody(byte[] body, IReadOnlyList<AntonTeamRewardEntitlement> expected)
            {
                if (body.Length < 2 || body[0] != 3 || body[1] != expected.Count) return false;
                int at = 2;
                foreach (var row in expected.OrderBy(row => row.Owner))
                {
                    if (at + 4 > body.Length || BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at)) != row.Owner ||
                        body[at + 2] != 0 || body[at + 3] != row.Grants.Length) return false;
                    at += 4;
                    foreach (var grant in row.Grants)
                    {
                        if (at + 16 > body.Length || body[at] != 0 ||
                            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at + 1)) != grant.ItemId ||
                            BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at + 5)) != grant.Count ||
                            body[at + 7] != 0 || BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at + 8)) != 0 ||
                            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at + 12)) != grant.EnchantUpgrade) return false;
                        at += 16;
                    }
                }
                return at == body.Length;
            }
        }
        finally
        {
            foreach (var session in sessions) session.End();
            foreach (var lease in leases) lease.Dispose();
            TestFiles.DeleteDirectory(root, true);
        }
    }
}
