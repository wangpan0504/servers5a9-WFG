using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 复用已建立的十二个当前角色，单独验证领域屏障；清图及存储回执是显式合成输入。
internal static class PreyTeamPhaseReturnTests
{
    internal static void RunMembers(RaidScript script, RaidActor[] actors, LukeRaidCompletionPolicy completion)
    {
        int before = Count;
        That(actors.Length == 12, "普通阶段屏障覆盖完整十二人名册");
        var battle = new AntonRaidBattle(script, kind: RaidKind.Prey, completion: completion, preyGuide: false);
        foreach (var actor in actors) That(battle.JoinStart(actor), "阶段屏障登记当前普通成员");
        Prepare(battle);
        var sanctuary = script.Phases[0].Dungeons.Where(row => Type(row) == "sanctuary").ToArray();
        int tower = script.Phases[0].Dungeons.First(row => Type(row) == "magic tower").Id;
        var other = Enter(battle, actors[11], tower, 3);
        var completed = new List<(RaidActor Actor, DungeonRunIdentity Identity)>();
        foreach (var dungeon in sanctuary)
        {
            var run = Enter(battle, actors[0], dungeon.Id, 1);
            completed.Add(run);
            var fact = new DungeonClearFact(run.Identity, checked((uint)dungeon.Id), [Guid.NewGuid()])
                { ClearMap = new DungeonMap("boss", new(0, 0), 77, "fixture/prey-phase-return.map") };
            That(battle.CompletePreyDungeon([(run.Actor, fact)]) is not null, "正式绑定Run的合成清图事实推进圣地");
            if (dungeon != sanctuary[^1]) battle.ReleaseDungeon(run.Actor, run.Identity.InstanceId);
        }
        var last = completed[^1];
        var candidate = battle.PendingLukeSettlement()!;
        That(candidate is { Members.Length: 12 } && battle.HasPendingTransition && battle.View.State == AntonRaidBattleState.Fighting,
            "四圣地完成密封十二人资格，仍等待原魔塔与末图Run退出");
        That(!battle.RequiresLukeCompletedPhaseReturn(actors[11], battle.Instance, 0), "资格未提交前不自动退出其他队的原Run");
        long revision = battle.View.Revision;
        var accepted = CommitDomainOnly(battle, candidate);
        That(accepted is not null && battle.View.Revision > revision &&
            battle.RequiresLukeCompletedPhaseReturn(actors[11], battle.Instance, 0),
            "资格提交后发布新的退场view，进行中的其他小队可进入既有退出执行器");
        battle.ReleaseDungeon(last.Actor, last.Identity.InstanceId);
        That(battle.HasPendingTransition && battle.RequiresLukeCompletedPhaseReturn(actors[11], battle.Instance, 0),
            "最后圣地退出不能跳过仍在魔塔的原队伍");
        battle.ReleaseDungeon(other.Actor, other.Identity.InstanceId);
        That(!battle.HasPendingTransition && battle.View.State == AntonRaidBattleState.Reward && battle.View.Phase == 0,
            "所有原Run退出后才显示已提交的一阶段奖励，不无限停留pending");

        foreach (var offer in candidate.RewardsFor(actors[0].Character).Offers)
            battle.ConfirmReward(actors[0], new(battle.Instance, 0, offer.Kind, offer.Card, actors[0].Character,
                completion.Source, offer.Grants, false, candidate.Week) { RaidType = RaidKind.Prey });
        That(battle.AdvanceAfterRewards(battle.Instance, battle.View.Revision) is not null && battle.View.Phase == 1,
            "真实领域领奖确认后可继续第二阶段");
        int nextDungeon = script.Phases[1].Dungeons.Single(row => Type(row) == "boss").Id;
        var next = Enter(battle, actors[0], nextDungeon, 1);
        var unfinished = Enter(battle, actors[11], script.Phases[1].Dungeons
            .First(row => row.InitialState == RaidDungeonState.Open && row.Id != nextDungeon).Id, 3);
        That(!battle.RequiresLukeCompletedPhaseReturn(actors[0], battle.Instance, 0) &&
            battle.ReleaseDungeon(actors[0], last.Identity.InstanceId) is null,
            "旧阶段回调和旧Run释放不能退出新阶段新Run");

        // 共享HP归零是明确的规则输入，只测试终局资格/退出屏障，不声称模拟真实Boss伤害。
        That(battle.SetMechanism(battle.Instance, battle.View.Revision, "[isys hp]", 0) is not null,
            "终阶段规则接收合成HP归零输入");
        var final = battle.PendingLukeSettlement()!;
        That(final is { FinalPhase: true, Members.Length: 12 } &&
            !battle.RequiresLukeCompletedPhaseReturn(actors[0], battle.Instance, 1), "终局同样等待阶段资格提交");
        That(CommitDomainOnly(battle, final) is not null && actors.All(actor =>
                battle.RequiresLukeCompletedPhaseReturn(actor, battle.Instance, 1) &&
                battle.LukeEligibility(actor.Character, 1) is { Eligible: false }),
            "终局已提交后全体十二人均须返回，零贡献、奖励不合格及无Run成员不被过滤");
        battle.ReleaseDungeon(next.Actor, next.Identity.InstanceId);
        That(battle.HasPendingTransition && battle.RequiresLukeCompletedPhaseReturn(unfinished.Actor, battle.Instance, 1),
            "本体队退出不能跳过另一队未清图的支援Run");
        battle.ReleaseDungeon(unfinished.Actor, unfinished.Identity.InstanceId);
        That(!battle.HasPendingTransition && battle.View.State == AntonRaidBattleState.Reward &&
            actors.All(actor => battle.LukeEligibility(actor.Character, 1) is { Eligible: false }),
            "无合格终局奖励的合成成员退出原Run后仍等待本人CG，不伪造领奖义务");
        foreach (var actor in actors)
            That(battle.CompletePreyMovie(actor, battle.View), "十二名原成员各自确认终局CG结束");
        That(battle.AdvanceAfterRewards(battle.Instance, battle.View.Revision) is not null &&
            battle.View.State == AntonRaidBattleState.Completed,
            "无领奖义务且全员CG结束后完成原终局");

        var failure = new AntonRaidBattle(script, kind: RaidKind.Prey, completion: completion, preyGuide: false);
        foreach (var actor in actors) failure.JoinStart(actor);
        Prepare(failure);
        var active = Enter(failure, actors[0], tower, 1);
        failure.SetMechanism(failure.Instance, failure.View.Revision, "[slenikon hp]", 1);
        failure.ExpireLukeTimer(failure.Instance, new(9001, 0), failure.View.TimerGenerations[new(9001, 0)]);
        That(failure.View.PhaseExpired && failure.View.PreyExpelledRuns.Contains(active.Identity.InstanceId) &&
            failure.PendingLukeSettlement() is null && !failure.RequiresLukeCompletedPhaseReturn(actors[0], failure.Instance, 0),
            "船体失败沿既有过期/指定Run退场，不密封成功资格或误用成功奖励屏障");
        Console.WriteLine($"PASS {Count - before} 普雷普通十二人阶段资格与多队退出领域断言");
    }

    // 复用已有SQLite和选角会话；阶段清图/资格为显式领域输入，位置和临时Party使用正式业务入口。
    internal static void RunWaiting(RaidScript script, RaidActor[] actors, AntonRaidBattle battle, AntonRaidStart start,
        RaidRegistry raids, PartyService parties, CharacterRepository repository, TownCatalog towns, string database)
    {
        int before = Count;
        var completedQuests = new HashSet<int> { 8935 };
        Prepare(battle);
        foreach (var dungeon in script.Phases[0].Dungeons.Where(row => Type(row) == "sanctuary"))
        {
            var run = Enter(battle, actors[0], dungeon.Id, 1);
            var fact = new DungeonClearFact(run.Identity, checked((uint)dungeon.Id), [Guid.NewGuid()])
                { ClearMap = new DungeonMap("boss", new(0, 0), 77, "fixture/prey-waiting.map") };
            That(battle.CompletePreyDungeon([(run.Actor, fact)]) is not null, "等待区用例沿原Run登记合成首阶段清图");
            battle.ReleaseDungeon(run.Actor, run.Identity.InstanceId);
        }
        var first = battle.PendingLukeSettlement()!;
        CommitDomainOnly(battle, first);
        foreach (var offer in first.RewardsFor(actors[0].Character).Offers)
            battle.ConfirmReward(actors[0], new(battle.Instance, 0, offer.Kind, offer.Card, actors[0].Character,
                first.RewardsFor(actors[0].Character).Source, offer.Grants, false, first.Week) { RaidType = RaidKind.Prey });
        That(battle.AdvanceAfterRewards(battle.Instance, battle.View.Revision) is not null && battle.View.Phase == 1,
            "等待区用例沿原阶段屏障进入P1，不改变原PVF或入场门禁");
        foreach (var actor in actors.Take(5)) Move(actor, 3);
        foreach (var (actor, area) in actors.Take(4).Select((actor, index) => (actor, area: index < 2 ? (byte)4 : (byte)5)))
        {
            Move(actor, area);
            parties.Publish(parties.UpdatePreyBoardingAsync(actor.Session).GetAwaiter().GetResult());
        }
        var left = parties.Current(actors[0].Session)!;
        var right = parties.Current(actors[2].Session)!;
        That(left is { Members.Length: 2, PreyBoardingArea: 4 } && right is { Members.Length: 2, PreyBoardingArea: 5 } && left.Id != right.Id &&
            actors.All(actor => actor.Session.PartyDungeon is null) && actors[4].Session.Selected!.Position.Area == 3 &&
            actors[5].Session.Selected!.Position.Area == 2, "左右各两名实际玩家组成真实临时Party，另有甲板闲置与已在备战区成员");
        var roster = raids.Find(actors[0])!;
        var originalReturns = actors.Select(actor => actor.Session.Selected!.Return).ToArray();
        string inventory = Inventory(), ledger = Ledger();
        battle.SetMechanism(battle.Instance, battle.View.Revision, "[isys hp]", 0);
        var final = battle.PendingLukeSettlement()!;
        CommitDomainOnly(battle, final);
        That(final.FinalPhase && final.Members.All(member => member.CommittedRuns == 0) && actors.All(actor =>
                battle.LukeEligibility(actor.Character, 1) is { Eligible: false } &&
                battle.RequiresLukeCompletedPhaseReturn(actor, battle.Instance, 1)),
            "末阶段零贡献和无领奖资格仍为原攻坚在线成员签发返回条件");
        Sql($"CREATE TRIGGER prey_waiting_failure BEFORE UPDATE ON characters WHEN NEW.id={actors[0].Character} AND NEW.area=2 BEGIN SELECT RAISE(ABORT,'prey waiting rollback'); END");
        string failedSnapshot = ClientSettingsSnapshots.Capture(database, true);
        var selected = actors[0].Session.Selected;
        That(start.ReturnPreyWaiting(actors[0], Guid.NewGuid(), completedPhase: true) is null,
            "错误实例不能借终局等待区入口提交位置或解除临时Party");
        Reject<SqliteException>(() => start.ReturnPreyWaiting(actors[0], battle.Instance, completedPhase: true),
            "等待区位置提交失败传回原SQL错误");
        That(ClientSettingsSnapshots.Capture(database, true) == failedSnapshot && ReferenceEquals(actors[0].Session.Selected, selected) &&
            ReferenceEquals(parties.Current(actors[0].Session), left) && ReferenceEquals(parties.Current(actors[1].Session), left) &&
            ReferenceEquals(parties.Current(actors[2].Session), right),
            "位置SQL失败保留全部数据库、Selected及左右临时Party，不提前解除其他成员");
        Sql("DROP TRIGGER prey_waiting_failure");
        foreach (var actor in actors)
            That(start.ReturnPreyWaiting(actor, battle.Instance, completedPhase: true) is { Character.Position.Town: 59, Character.Position.Area: 2 },
                "原调用重试及其余无Run成员均提交到同一内侧备战区");
        That(actors.All(actor => parties.Current(actor.Session) is null &&
                repository.LoadCharacter(actor.Owner, 0)!.Position is { Town: 59, Area: 2 }) &&
            Inventory() == inventory && Ledger() == ledger && actors.Select((actor, index) => actor.Session.Selected!.Return == originalReturns[index]).All(same => same),
            "左右临时Party全部解除且持久位置均为59/2，原库存、费用生命周期及外部Return保持");
        var kept = raids.Find(actors[0])!;
        That(kept.Key == roster.Key && kept.Started?.InstanceId == battle.Instance && kept.Members.Length == roster.Members.Length &&
            roster.Members.All(member => kept.Members.Single(row => row.Actor == member.Actor).PreyPosition == member.PreyPosition),
            "最终等待区收尾保留原攻坚队、全员及整局分队标记");
        string saved = ClientSettingsSnapshots.Capture(database, true);
        That(start.ReturnPreyWaiting(actors[0], battle.Instance, completedPhase: true) is not null &&
            ClientSettingsSnapshots.Capture(database, true) == saved, "同一终局重复等待区提交幂等，不重写位置或资产");
        Console.WriteLine($"PASS {Count - before} 普雷终局无Run全员返回、真实临时Party与位置SQL回滚断言");

        void Move(RaidActor actor, byte area)
        {
            var from = actor.Session.Selected!.Position;
            var target = towns.Entrance(from, new(59, area, 0, 0), completedQuests) ??
                towns.Spawn(new(59, area, 0, 0), completedQuests)!;
            That(actor.Session.Travel(new(target.Town, target.Area, target.X, target.Y, 5, from.Town, from.Area, 0)),
                "临时Party用例沿真实普雷城镇路由提交位置");
        }
        string Inventory() => JsonSerializer.Serialize(actors.Select(actor => repository.LoadCharacter(actor.Owner, 0)!.Inventory));
        string Ledger()
        {
            using var db = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "SELECT * FROM character_raid_runs ORDER BY character_id,instance_id";
            using var reader = command.ExecuteReader(); var rows = new List<object[]>();
            while (reader.Read()) { var row = new object[reader.FieldCount]; reader.GetValues(row); rows.Add(row); }
            return JsonSerializer.Serialize(rows);
        }
        void Sql(string text)
        {
            using var db = new SqliteConnection($"Data Source={database};Pooling=False");
            db.Open(); using var command = db.CreateCommand(); command.CommandText = text; command.ExecuteNonQuery();
        }
    }

    static string? Type(RaidDungeon dungeon) => dungeon.Declarations.FirstOrDefault(row => row.Tag == "[type]")?.Values.FirstOrDefault()?.Text;

    static void Prepare(AntonRaidBattle battle) => That(battle.ExpireLukeTimer(battle.Instance, new(9000, 0),
        battle.View.TimerGenerations[new(9000, 0)]) is not null, "完成普通准备期");

    static (RaidActor Actor, DungeonRunIdentity Identity) Enter(AntonRaidBattle battle, RaidActor actor, int dungeon, int partyId)
    {
        var identity = new DungeonRunIdentity(actor.Character, 0, 1, Guid.NewGuid(), 1);
        using var reservation = battle.ReserveDungeon(new(battle.Instance, battle.View.Phase, 0, RaidKind.Prey)
            { PreyGuide = false, PreyPartyId = checked((ushort)partyId) },
            checked((uint)dungeon), [(actor, identity)]);
        That(reservation is not null, "合成Run保留本Battle、阶段、实际队伍及唯一实例身份");
        reservation!.Install();
        return (actor, identity);
    }

    static AntonRaidBattleChange? CommitDomainOnly(AntonRaidBattle battle, AntonRaidBattle.LukePhaseSettlement candidate) =>
        battle.AcceptLukeSettlement(candidate, new(candidate.Members.Select(member =>
        {
            var rewards = candidate.RewardsFor(member.Character);
            bool eligible = member.Present && member.CommittedRuns >= candidate.Policy.MinimumCommittedRuns;
            return new RaidPhaseEligibility(candidate.Instance, RaidKind.Prey, candidate.Phase, candidate.Event,
                candidate.Anchor.ToUnixTimeSeconds(), member.Character, candidate.FinalPhase && member.Present,
                eligible, eligible ? "eligible" : "phase-participation", rewards.Source, candidate.Week, rewards.Offers);
        }).ToImmutableArray()));
}
