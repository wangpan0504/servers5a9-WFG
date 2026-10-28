using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Game.TagEvildom;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class SessionPlanningTests
{
    // 1394只安装备用角色，主角是客户端已经持有的真实Actor。修理装备、买药与体力恢复只改主角自己的记录，
    // 不能因此把已发布的双Actor判死再要求重登；只有主角身份、共享钱包版本和备用投影到期才算过时。
    static void TagEvildomPairSurvivesPrimaryChange()
    {
        using var fixture = new Fixture();
        fixture.Sql("UPDATE characters SET level=95");
        var first = fixture.Read();
        fixture.Repository.Create(fixture.Owner, new("TagPairSub", fixture.Towns.Room, fixture.Towns.InitialReturn)
            { Level = 95, InitialLearning = first.Learning });
        fixture.Sql("DELETE FROM character_dungeon_state");
        var now = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        var session = new GameSession(fixture.Repository, fixture.Towns, fixture.Owner,
            entry: new(new(2, 46), 53, "[evildom_gathering]"));
        session.MarkRosterPublished(session.PrepareRoster()); session.SelectFromRosterIndex(0); session.MarkReady();
        ImmutableArray<TagEvildomMemberRequest> members = [new(2, 0, 0), new(2, 1, 0), new(0, -1, 0), new(0, -1, 0), new(0, -1, 0), new(0, -1, 0)];
        var snapshot = session.ConfigureTagEvildom(fixture.Repository, members, 0, now).Projection!;
        var characters = session.CaptureTagEvildomCharacters(snapshot)!;
        That(session.MarkTagEvildomPairPublished(fixture.Repository, characters,
            TagEvildomAppearance.Capture(characters.Primary, now), TagEvildomAppearance.Capture(characters.Standby, now), now),
            "夹具安装真实1393/1394双Actor");
        That(session.HasCurrentTagEvildomPair(fixture.Repository, now), "刚安装的双Actor有效");
        // 进度更新不动 characters.revision，按整条记录判等才是真实失效来源；夹具必须证明记录内容确实被改写。
        session.UpdateProgress(new(CharacterProgressField.Story, "planning", first.Name, 10, 0));
        var changedPrimary = fixture.Repository.LoadCharacter(fixture.Owner, first.Slot);
        That(changedPrimary.AcceptedStoryLevel == 10 && changedPrimary.AcceptedStoryLevel != characters.Primary.AcceptedStoryLevel,
            "夹具真实改写主角记录内容");
        That(session.HasCurrentTagEvildomPair(fixture.Repository, now), "主角记录变化不再作废已装双Actor，入场不必重登");
        session.End();
    }

    // 帝国竞技场同源：购票与入场都要求已发布双Actor仍有效，一次装备修理不能让它永久失效。
    static void ImperialArenaPairSurvivesPrimaryChange()
    {
        using var fixture = new Fixture();
        fixture.Sql("UPDATE characters SET level=95");
        var first = fixture.Read();
        fixture.Repository.Create(fixture.Owner, new("EmpireSub", fixture.Towns.Room, fixture.Towns.InitialReturn)
            { Level = 95, InitialLearning = first.Learning });
        fixture.Sql("DELETE FROM character_dungeon_state");
        var now = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        var session = new GameSession(fixture.Repository, fixture.Towns, fixture.Owner,
            entry: new(new(2, 48), 39, "[tag_tournament]"));
        session.MarkRosterPublished(session.PrepareRoster()); session.SelectFromRosterIndex(0); session.MarkReady();
        var seeded = session.EnsureImperialArenaTeam(fixture.Repository, now);
        That(seeded is not null && seeded.State.Configuration.First == first.Id && seeded.State.Configuration.Second != first.Id,
            "帝国首次入城空编队按当前角色为队首自动配队");
        var characters = session.CaptureImperialArenaCharacters(seeded!)!;
        That(session.MarkImperialArenaPairPublished(fixture.Repository, characters,
            TagEvildomAppearance.Capture(characters.Primary, now), TagEvildomAppearance.Capture(characters.Standby, now), now),
            "帝国自动配队后安装真实1393/1394双Actor");
        session.UpdateProgress(new(CharacterProgressField.Story, "planning", first.Name, 10, 0));
        That(fixture.Repository.LoadCharacter(fixture.Owner, first.Slot).AcceptedStoryLevel == 10,
            "夹具真实改写帝国主角记录内容");
        That(session.HasCurrentImperialArenaPair(fixture.Repository, now), "帝国主角记录变化不再作废已装双Actor");
        session.End();
    }
}
