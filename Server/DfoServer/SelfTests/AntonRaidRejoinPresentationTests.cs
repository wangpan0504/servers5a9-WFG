using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Party;
using DfoServer.Game.Quests;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

// 复用正式入口同轮资源和数据库；有界阻塞真实发送泵，独立验证排队后场景失效。
internal static class AntonRaidRejoinPresentationTests
{
    internal static async Task VerifyAsync(TcpTestFixture fixture, TcpTestResources resources, RaidDefinition definition)
    {
        foreach (string scenario in new[] { "written", "giveup", "owner", "dead" })
        {
            var owners = new AccountOwners();
            var repository = new CharacterRepository(fixture.Database);
            var registry = new RaidRegistry();
            var candidates = new AntonRaidRejoin(true);
            registry.BindRejoin(candidates);
            var parties = new PartyService(repository);
            var entry = new AntonRaidDungeonService(registry, parties, definition.Resources!.EntryCatalog!, true);
            var dungeons = new PartyDungeonService(parties, repository);
            var main = SelfTestCatalogs.Dungeons(resources.Archive);
            var rooms = SelfTestCatalogs.Rooms(resources.Archive);
            var stats = new CharacterStatComputer(resources.Characters);
            var town = new TownProjection(resources.Towns, stats, null, resources.ItemResources.EpicPieces);
            using var log = new ServerLog(Path.Combine(fixture.Options.LogDirectory, "rejoin-" + scenario));
            var handler = new DungeonHandler(log, main, rooms, resources.Growth,
                SelfTestCatalogs.KillExperience(resources.Archive), town, new CharacterLevelProjection(stats),
                partyDungeons: dungeons, partyStats: stats, parties: parties, antonRaid: entry);
            var commands = new GameCommandRegistry();
            handler.Register(commands); commands.Seal();
            var leases = new List<AccountOwners.Lease>();
            var sessions = new List<GameSession>();
            try
            {
                GameSession Open(int index, bool create)
                {
                    string account = "anton-present-" + scenario + index;
                    var lease = owners.Acquire(account, () => { }); leases.Add(lease);
                    if (create)
                    {
                        var point = new Location(38, 0, 629, 196);
                        var candidate = CharacterCreation.Prepare("AP" + scenario + index, resources.Characters.Get(0), point, point, resources.Learning.Get);
                        var learning = SkillPointLedger.Synchronize(0, 0, 85, candidate.InitialLearning with
                            { Experience = resources.Growth.GetLevelStartExperience(85) }, resources.Learning);
                        var character = repository.Create(lease, candidate with { Level = 85, AcceptedStoryLevel = 85,
                            CompletedQuests = [12829, 3779], InitialLearning = learning });
                        var items = resources.ItemResources;
                        InventoryService.Commit(repository, lease, character,
                            new InventoryService(items.Items, items.GoldLimits, items.EpicPieces).Grant(character, [new(10096296, 2)]));
                    }
                    var session = new GameSession(repository, resources.Towns, lease, entry: new(new(2, 19), 23, "[raid]"));
                    session.CurrentParty = () => parties.Current(session);
                    session.HasMultiplePartyMembers = () => parties.HasMultipleMembers(session);
                    session.Select(0); session.MarkReady(); sessions.Add(session);
                    if (create && scenario == "dead")
                        That(session.MutateInventory(character => new(true, 0,
                            new(character.Inventory, character.Inventory with { ReviveCoins = index == 1 ? 3 : 2 },
                                "anton_rejoin_coins"))).Accepted, "死亡重入夹具通过原owner准备本人复活币");
                    return session;
                }
                var leader = Open(0, true);
                var original = Open(1, true);
                var actors = new[] { leader.CaptureRaidActor(registry)!, original.CaptureRaidActor(registry)! };
                registry.Create(actors[0], 0, "present", "present"u8.ToArray(), 20, 1);
                registry.Invite(actors[0], actors[1], 0);
                That(registry.Respond(actors[1], actors[0].Character, true).Accepted, "重入发送泵夹具按正式邀请入团");
                foreach (var actor in actors) registry.Assign(actors[0], actor.Character, 1);
                var start = new AntonRaidStart(registry, parties, repository, resources.Towns, resources.ItemResources.EpicPieces);
                That(start.Start(actors[0], registry.Find(actors[0])!, definition with { StartMinimum = 1 }).Receipt is not null,
                    "重入发送泵夹具按正式事务开战");
                uint key = registry.Find(actors[0])!.Key;
                That(entry.Select(leader, 210, parties.Current(leader), out _).Length == 2 &&
                    dungeons.Enter(leader, 210, 2, main, rooms, null, null, null, null).Length == 2,
                    "重入发送泵夹具沿原Battle准入进入两人原Run");
                var run = leader.PartyDungeon!;
                run.CompleteLoading(leader); run.CompleteLoading(original);
                DungeonDeathReturnReceipt? originalDeath = null, leaderDeath = null;
                if (scenario == "dead")
                {
                    That(original.ReportFormalCharacterDeath() is not null &&
                        original.ReviveFormalCharacter(original.Selected!.Id) is { Accepted: true },
                        "死亡重入前在原共享Run真实消费一次复活额度");
                    That(original.ReportFormalCharacterDeath() is not null && leader.ReportFormalCharacterDeath() is not null,
                        "两名成员均按正式生命接口进入死亡状态");
                    originalDeath = original.CurrentDungeonDeathReturn(); leaderDeath = leader.CurrentDungeonDeathReturn();
                    var originalHp = run.ReportAntonHp(original, 17, [73]);
                    That(originalDeath is { DeathSerial: 2 } && leaderDeath is { DeathSerial: 1 } &&
                        originalHp.Length == 2 && originalHp.All(notice => notice.AntonHp!.UsedCoins == 1),
                        "死亡重入前保留真实DeathSerial和已提交共享额度");
                }
                original.PrepareAntonRejoinDisconnect();
                run.Detach(original); parties.Detach(original); original.End(); leases[1].Dispose();
                var current = Open(1, false);
                var actorNow = current.CaptureRaidActor(registry)!;
                var recovery = registry.RestoreAnton(actorNow, key, candidates, parties);
                That(recovery is { Restored: true, Scene: not null } && current.HasAntonRejoinPresentation &&
                    current.Phase == SessionPhase.Dungeon && current.DungeonSnapshot!.Phase == SinglePlayerDungeonPhase.Dungeon,
                    "重入只增加本人呈现屏障，保持合法Session和原业务Run阶段");
                var scene = recovery.Scene!.Scene!;
                var identity = scene.Snapshot.Identity;
                string assets = fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY character_id,item_uid") +
                    fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var stream = new GameSenderBudgetTests.HeldStream();
                var frames = new List<(byte Kind, ushort Opcode, byte[] Body)>();
                await using var sender = new GameConnectionSender(actorNow.Owner, stream, stop, 0,
                    (kind, opcode, _, body, _) => frames.Add((kind, opcode, body.ToArray())));
                var context = new GameConnectionContext(new(301, IPAddress.Loopback, stream, stop), actorNow.Owner, current, sender);
                handler.PreparePartyContext(context);
                var held = sender.Send(1, 1347, [1], "anton_rejoin_held");
                try
                {
                    await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    await PartyDungeonProjection.PublishAsync([recovery.Scene!]);
                    That(!current.AntonRejoinSceneWritten && run.CompleteLoading(current).Length == 0 && current.HasAntonRejoinPresentation,
                        "29仅入队而未实际Written时，提前37不能消费呈现屏障或发布30");
                    That(current.ReportFormalCharacterDeath() is null && current.ReviveFormalCharacter(actorNow.Character) is null &&
                        current.SelectAntonEmptyCardBack(0) is null && current.RaidMovieSnapshot() is null &&
                        current.ClaimRaidReward(1, 0, null!, null!, null!, null!, fixture.UtcNow) is null,
                        "重入实际确认前死亡、复活、牌背、影片和奖励请求均不能写业务或资产");
                    bool inventoryOperation = false;
                    That(!current.MutateInventory(_ => { inventoryOperation = true; return InventoryResult.Reject(); }, requireTown: false).Accepted &&
                        !current.PlanAndCommitInventory(_ => { inventoryOperation = true; return InventoryResult.Reject(); }, requireTown: false).Accepted &&
                        !inventoryOperation && current.UseConsumable(new(new(InventoryListType.Main, 121), 0, 10096296, 0), null!).Outcome == ConsumableUseOutcome.Rejected &&
                        current.DiscardFormalDungeonItem(new(0, 0, 0, 121, 1, 0), null!).Error == 19,
                        "呈现屏障在用户库存规划前拒绝移动、穿脱、删除、消耗和丢弃，不调用资产operation");
                    That(!current.ChangeSkillTree(0).Success && !current.BeginAutoCombo(0, null!).Success &&
                        current.ChangeQuest(0, QuestLifecycleAction.Accept, null!, null!, null!, null!) is null &&
                        current.FinishQuest(0, ushort.MaxValue, resources.Quests, null!, null!, null!, null!, null!).Receipt is null,
                        "未确认呈现前拒绝技能、任务接取和交付，不能替换冻结场景的角色快照");
                    if (scenario == "giveup")
                    {
                        using (sender.QueuePublication(() => actorNow.IsCurrent))
                            That(await commands.DispatchAsync(context, new Packet(1, 42, [])) == CommandResult.Handled,
                                "重入呈现等待中真实42仍可安全返回候战室");
                        That(current.DungeonSnapshot is null && !current.HasAntonRejoinPresentation,
                            "真实42清理原副本后旧呈现身份立即失效");
                    }
                    else if (scenario == "owner")
                    {
                        leases.Add(owners.Acquire(actorNow.Owner.Account, () => { }));
                        That(!actorNow.IsCurrent, "排队中账号接管使原重入owner失效");
                    }
                    stream.Release.TrySetResult();
                    try { await Task.WhenAll(held, sender.DrainAsync()).WaitAsync(TimeSpan.FromSeconds(3)); }
                    catch (InvalidOperationException) when (scenario == "owner") { }
                    catch (IOException) when (scenario == "owner") { }
                    if (scenario is "written" or "dead")
                    {
                        That(current.AntonRejoinSceneWritten && frames.Count(row => row.Opcode == 29) == 1,
                            "只有真实29写出后的回调才授予加载确认资格");
                        var notices = run.CompleteLoading(current);
                        That(notices is [{ Kind: PartyDungeonNoticeKind.Loaded }] &&
                            ReferenceEquals(notices[0].Recipient.Actor.Session, current) &&
                            current.DungeonSnapshot!.Identity == identity && !current.HasAntonRejoinPresentation &&
                            run.CompleteLoading(current).Length == 0,
                            "纯呈现37只释放本人且不推进原Run epoch，重复37不重放");
                        That(fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY character_id,item_uid") +
                            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") == assets,
                            "真实Written和呈现37不重扣票、疲劳或写资产审计");
                        if (scenario == "dead")
                        {
                            ushort ownKey = current.Selected!.Id, leaderKey = leader.Selected!.Id;
                            That(notices[0].AntonRejoinDeaths.Order().SequenceEqual(new[] { ownKey, leaderKey }.Order()) &&
                                current.CurrentDungeonDeathReturn() is { } restoredDeath &&
                                restoredDeath.DeathSerial == originalDeath!.DeathSerial && restoredDeath.InstanceId == originalDeath.InstanceId &&
                                leader.CurrentDungeonDeathReturn() == leaderDeath && current.Selected.Inventory.ReviveCoins == 2,
                                "死亡角色恢复保留本人及幸存者原死亡事实，不新建DeathSerial或扣币");
                            await PartyDungeonProjection.PublishAsync(notices);
                            await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3));
                            var lifeFrames = frames.Where(row => row is { Kind: 0, Opcode: 32 }).ToArray();
                            That(frames.FindIndex(row => row is { Kind: 0, Opcode: 30 }) >= 0 && lifeFrames.Length == 2 &&
                                lifeFrames.All(row => row.Body.Length == 4 && row.Body[2] == 0 && row.Body[3] == 0) &&
                                lifeFrames.Select(row => TcpPeer.U16(row.Body)).Order().SequenceEqual(new[] { ownKey, leaderKey }.Order()) &&
                                frames.FindLastIndex(row => row is { Kind: 0, Opcode: 30 }) < frames.FindIndex(row => row is { Kind: 0, Opcode: 32 }),
                                "仅新客户端在30后收到全队两条真实DIE_STATE死亡呈现");
                            var before = run.ReportAntonHp(current, 17, [73]);
                            That(before.Length == 2 && before.All(notice => notice.AntonHp!.UsedCoins == 1),
                                "668和呈现37继承原共享池已消费额度");
                            That(current.ReviveFormalCharacter(ownKey) is { Accepted: true } &&
                                current.Selected!.Inventory.ReviveCoins == 1 && current.CurrentDungeonDeathReturn() is null &&
                                current.ReviveFormalCharacter(ownKey) is null && current.Selected.Inventory.ReviveCoins == 1,
                                "重入真实死角色仅首次复活消费本人一币，重复复活不再次扣款");
                            var after = run.ReportAntonHp(current, 17, [73]);
                            That(after.Length == 2 && after.All(notice => notice.AntonHp!.UsedCoins == 2) &&
                                leader.CurrentDungeonDeathReturn() == leaderDeath && leader.Selected!.Inventory.ReviveCoins == 2 &&
                                current.DungeonSnapshot!.Identity == identity && ReferenceEquals(current.PartyDungeon, run),
                                "恢复后只增加原共享池一次消费，幸存者死亡及钱包、原Run和epoch不变");
                        }
                    }
                    else That(frames.All(row => row.Opcode is not (27 or 28 or 29)) &&
                        (scenario != "owner" || stream.Frames.Count == 1),
                        "42或owner接管后释放真实发送泵，不写旧场景也不调用29完成回调");
                }
                finally { stream.Release.TrySetResult(); }
            }
            finally
            {
                foreach (var session in sessions) try { session.End(); } catch (InvalidOperationException) { }
                foreach (var lease in leases) lease.Dispose();
            }
        }
    }
}
