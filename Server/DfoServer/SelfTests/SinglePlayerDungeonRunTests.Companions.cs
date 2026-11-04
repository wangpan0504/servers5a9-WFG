using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class SinglePlayerDungeonRunTests
{
    static void VerifyCompanionProfiles(PvfArchive pvf)
    {
        var catalog = SupportCompanionCatalog.Load(pvf);
        That(pvf.Contains(SupportCompanionCatalog.ResourcePath) ? !catalog.Profiles.IsEmpty :
            ReferenceEquals(catalog, SupportCompanionCatalog.Empty) && !catalog.EqualTiers,
            "可选档次目录存在时必须完整，缺省时保留基础模板且不授予档次选择");
        var registry = DungeonCatalog.ParseDirectory(pvf.ReadTokens("aicharacter/aicharacter.lst"), pvf.ResolveString, "aicharacter");
        foreach (var row in new (int Id, int Job, int Grow, string Path)[] {
            (20900, 4, 1, "aicharacter/priest/heal_soldier/heal_soldier.aic"),
            (9986, 1, 1, "aicharacter/support/guangbing/guangbing.aic"),
            (74990, 14, 1, "aicharacter/support/femalecrusader/femalecrusader.aic"),
            (19973, 3, 5, "aicharacter/support/enchantress/enchantress.aic") })
        {
            That(registry.TryGetValue(row.Id, out var path) && path.Equals(row.Path, StringComparison.OrdinalIgnoreCase) && pvf.Contains(row.Path),
                "累计候选四类基础支援兵仍有各自真实注册脚本，不因缺可选档次目录丢失");
            var tokens = pvf.ReadTokens(row.Path);
            var minimum = PvfAiCharacterScript.MinimumInfo(tokens, pvf.ResolveString);
            That(minimum.Length >= 6 && minimum[1].Type == 0 && minimum[1].Value == row.Job &&
                minimum[2].Type == 0 && minimum[2].Value == row.Grow && minimum[5].Type == 0 && minimum[5].Value == 95 &&
                tokens.Count(token => token.Type == 3 && pvf.ResolveString(token.Value) == "[equipment]") == 1 &&
                tokens.Count(token => token.Type == 3 && pvf.ResolveString(token.Value) == "[skill]") == 1,
                "基础支援兵保留95级职业、转职、装备及技能资源合同");
        }
        foreach (int baseId in new[] { 20900, 9986, 74990, 19973 })
        foreach (string tier in new[] { "entry", "graduate", "huge" })
        {
            if (!catalog.HasProfile(baseId, tier))
            {
                That(catalog.Pick(baseId, tier, _ => throw new InvalidOperationException()) is null,
                    "未注册的职业档次不抽其它职业模板");
                continue;
            }
            var expected = catalog.Profiles.Where(profile => profile.BaseId == baseId &&
                (catalog.EqualTiers || profile.Tier == tier)).ToArray();
            for (int ordinal = 0; ordinal < expected.Length; ++ordinal)
            {
                int choice = ordinal;
                var actual = catalog.Pick(baseId, tier, count =>
                {
                    That(count == expected.Length, "统一强度目录按同职业完整装备池抽取，旧目录保留档次池");
                    return choice;
                });
                That(actual == expected[ordinal] && actual is { Level: 95 },
                    "每个真实模板均可抽中并冻结95级身份，不在同次入场重抽装备");
            }
        }
        That(catalog.Pick(20900, "invalid", _ => throw new InvalidOperationException()) is null &&
            catalog.Pick(1234, "entry", _ => throw new InvalidOperationException()) is null,
            "三档统一标记不放行未知档次或非支援兵身份");
    }

    static void VerifyCompanionStartRoomIdentity()
    {
        // 同一MAP可以在迷宫多个格子复用；模板编号不能代替出生房间身份。
        var start = new DungeonMap("map", new(0, 0), 1, "map/fixture-companion.map");
        var reused = start with { Position = new(1, 0) };
        var boss = new DungeonMap("boss", new(2, 0), 3, "map/fixture-boss.map");
        var maze = new DungeonMaze(0, 3, 1, "companion-room-identity", [start.Position], [boss.Position],
            [start, reused, boss], []);
        var rows = new[] { DungeonCompanion.WangFuguiId, DungeonCompanion.GuangbingId };
        var entry = new DungeonEntryPlan(99, 17, 3, 0, byte.MaxValue, 0, maze, start, boss)
            { CompanionMapRows = [.. rows], CompanionProfiles = [
                new(DungeonCompanion.WangFuguiId, "graduate", 20901, "fixture", "aicharacter/fixture-profile.aic", 147)] };
        var actors = rows.Select(id => new DungeonRoomAiCharacter(id, [id, 100, 200, 0, 0, 0],
            "[character]", "[normal]", "aicharacter/fixture-companion.aic", 95)).ToArray();
        var first = DungeonMonsterPlan.FromRoom(new(start, [], [], []) { AiCharacters = actors }, 99, 10,
            companionRows: rows, companionProfiles: entry.CompanionProfiles);
        var secondRoom = new DungeonRoomDefinition(reused, [], [], []) { AiCharacters = actors };
        var second = DungeonMonsterPlan.FromRoom(secondRoom, 99, 12, companionRows: []);
        var route = new DungeonFixedRoute([first, second, BossPlan(boss, 20)],
            [new(start.Position, reused.Position), new(reused.Position, boss.Position)]);
        var authority = new FixtureCombatAuthority();
        That(SinglePlayerDungeonRun.TryCreate(entry, 1, 100, route, authority, out var created, out _),
            "起始房间与其它格复用MAP模板时，只生成一次辅助和三四仔的路线通过完整复验");
        var duplicated = DungeonMonsterPlan.FromRoom(secondRoom, 99, 12, companionRows: rows, companionProfiles: entry.CompanionProfiles);
        That(!SinglePlayerDungeonRun.TryCreate(entry, 1, 100,
            route with { Rooms = [first, duplicated, BossPlan(boss, 20)] }, authority, out _, out _),
            "相同MAP编号的非起始格不能通过注入第二组支援兵的复验");
        var run = created!;
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), reused.Position).Outcome == DungeonRunOutcome.Accepted,
            "起始房间的友方支援兵不阻挡正常过门");
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        That(run.CurrentMonsterPlan.Monsters.Count == 0 && first.Monsters.All(actor =>
            run.PublishedFriendlyActor(actor.ObjectKey) == actor),
            "过门不创建同模板第二组支援兵，原演员对象key仍属于本次实例");
        That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), start.Position).Outcome == DungeonRunOutcome.Accepted &&
            run.Snapshot.MapPayload == DungeonMapPayload.Cached,
            "回访起始房间使用缓存，不重发支援兵生成列表");
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        var carried = run.LivingPublishedCompanions();
        That(carried.Length == 2 && carried.Any(actor => actor.MonsterId == 20901 && actor.CandidateLevel == 147),
            "只冻结已发布且仍存活的两名专用支援兵，保留原档次模板与等级");
        var nextEntry = entry with { CompanionMapRows = [], HelperRotation = new(0, 0, null, null, 0, 1, null) };
        var nextRoute = route with { Rooms = [
            DungeonMonsterPlan.FromRoom(first.Room, 99, 30, companionRows: []), second, BossPlan(boss, 40)] };
        That(SinglePlayerDungeonRun.TryCreate(nextEntry, 2, 200, nextRoute, authority, out var next, out _) &&
            !next!.TryCarryCompanions(carried, 11) && next.TryCarryCompanions(carried, 30) &&
            !next.TryCarryCompanions(carried, 30),
            "助手轮换只绑定一次旧演员，分配下限不能覆盖任何继承key");
        That(next!.CurrentMonsterPlan.Monsters.Count == 0 && carried.All(actor =>
            next.PublishedFriendlyActor(actor.ObjectKey) == actor),
            "新START_MAP没有第二组支援兵，服务端仍按原key和完整模板认领演员");
        next.CompleteLoading(next.Snapshot.Identity, Guid.NewGuid());
        That(next.Move(run.Snapshot.Identity, Guid.NewGuid(), reused.Position).Outcome == DungeonRunOutcome.Rejected,
            "继承演员不复用旧副本实例的操作身份");
        var retired = carried[0];
        next.RetireNonCombatObject(retired.ObjectKey);
        That(next.LivingPublishedCompanions().Length == 1 &&
            !next.HasLivingFriendlyActor(retired.MonsterId, next.Snapshot) && run.LivingPublishedCompanions().Length == 2,
            "本轮退场只更新新实例墓碑，下一轮排除退场支援兵，不修改旧实例冻结状态");
        var survivor = next.LivingPublishedCompanions();
        That(SinglePlayerDungeonRun.TryCreate(nextEntry, 3, 300, nextRoute, authority, out var third, out _) &&
            third!.TryCarryCompanions(survivor, 30) && third.LivingPublishedCompanions().SequenceEqual(survivor),
            "连续助手轮换继续继承幸存演员而不补生已退场者");
        That(SinglePlayerDungeonRun.TryCreate(nextEntry, 3, 300,
            nextRoute with { Rooms = [nextRoute.Rooms[0], second, BossPlan(boss, survivor[0].ObjectKey)] },
            authority, out var colliding, out _) && !colliding!.TryCarryCompanions(survivor, 30),
            "实际路线与继承演员key碰撞时拒绝接管");
    }
}
