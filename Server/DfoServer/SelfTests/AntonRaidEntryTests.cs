using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AntonRaidEntryTests
{
    // 借用开战用例已提交的20人实例，不另起数据库或复制在线状态。
    internal static void RunCore(RaidRegistry registry, PartyService parties, RaidActor[] actors, RaidScript script)
    {
        var maze = new DungeonMaze(0, 1, 1, "BBEE", [new(0, 0)], [new(0, 0)], [], []);
        var definitions = ImmutableDictionary<int, DungeonDefinition>.Empty.Add(210,
            new(210, "dungeon/anton/fixture.dgn", 1, 1, [maze], []));
        var catalog = new RaidDungeonCatalog(script, definitions, [], new Dictionary<int, string>(), _ => true,
            dungeonPaths: ImmutableDictionary<int, string>.Empty.Add(210, "dungeon/anton/fixture.dgn"));
        var leader = actors[0].Session;
        var party = parties.Current(leader)!;
        That(!new RaidStartSettings().AntonFormalEntryEnabled, "安徒恩正式进图配置缺省关闭");
        var disabled = new AntonRaidDungeonService(registry, parties, catalog, false);
        That(disabled.Select(leader, 210, party, out var reason).Length == 0 && reason == "anton-entry-waiting-client-resources" &&
            leader.CurrentDungeonSelection is null, "关闭时不安装授权，保留原战况表行为");
        var missing = new RaidDungeonCatalog(script, ImmutableDictionary<int, DungeonDefinition>.Empty, [32200],
            new Dictionary<int, string>(), _ => false);
        That(new AntonRaidDungeonService(registry, parties, missing, true).Select(leader, 210, party, out reason).Length == 0 &&
            reason == "anton-entry-missing-maps", "开关不能绕过MAP缺项门禁");
        var service = new AntonRaidDungeonService(registry, parties, catalog, true);
        foreach (int id in new[] { 211, 218, int.MaxValue })
            That(service.Select(leader, id, party, out reason).Length == 0 && reason == "anton-entry-dungeon-not-open",
                "首阶段只接受实际Open的210，隐藏/跨阶段/未知DGN拒绝");
        That(service.Select(actors[1].Session, 210, party, out _).Length == 0 &&
            service.Select(leader, 210, party with { }, out _).Length == 0 &&
            service.Select(actors[20].Session, 210, party, out _).Length == 0,
            "非分队长、非当前Party引用及团外角色不能签发授权");
        var selected = service.Select(leader, 210, party, out reason);
        That(selected.Length == 4 && reason is null && party.Members.All(member => member.Actor.Session.HasFormalRaidSelection),
            "正常开战后的原分队整体取得同Battle授权");
        var battle = leader.CurrentRaidBattle(actors[0])!;
        var admission = leader.CurrentDungeonSelection!.Raid!.Admission;
        That(admission.Allows(210) && !admission.Allows(211) &&
            !(admission with { InstanceId = Guid.NewGuid() }).Current && !(admission with { Phase = 1 }).Current,
            "授权锁定原实例和合法阶段，不由复制令牌改实例或阶段");
        var first = Bind(actors[..4]);
        var identity = new RaidDungeonIdentity(battle.Instance, 0, 1);
        using (var pending = battle.ReserveDungeon(identity, 210, first))
        {
            That(pending is not null && battle.ReserveDungeon(identity, 210, Bind(actors[..4])) is null,
                "同分队只能预留一个Run，未提交Dispose撤销本次占用");
        }
        using (var pending = battle.ReserveDungeon(identity, 210, first)!) pending.Install();
        That(battle.CompleteDungeon(new(first[0].Identity, 210, [Guid.NewGuid()])
            { ClearMap = new("boss", new(0, 0), 1, "fixture.map") }) is null && battle.View.Revision == 0,
            "入口绑定不授予旧单人清图阶段推进能力");
        var second = Bind(actors[4..8]);
        using (var parallel = battle.ReserveDungeon(identity with { Squad = 2 }, 210, second))
            That(parallel is not null, "黑雾资源允许多分队独立Run，仍分别持有原分队");
        foreach (var row in first) battle.ReleaseDungeon(row.Actor, row.Identity.InstanceId);
        var next = Bind(actors[..4]);
        using (var pending = battle.ReserveDungeon(identity, 210, next)!) pending.Install();
        foreach (var row in first) battle.ReleaseDungeon(row.Actor, row.Identity.InstanceId);
        That(battle.ReserveDungeon(identity, 210, Bind(actors[..4])) is null, "旧Run迟到退出不能释放新Run占用");
        foreach (var row in next) battle.ReleaseDungeon(row.Actor, row.Identity.InstanceId);
        leader.CancelRaidDungeonSelection(selected);
        That(party.Members.All(member => member.Actor.Session.CurrentDungeonSelection?.Raid is null),
            "取消只清本次授权，保留可继续使用的MapSelect上下文");
        foreach (var member in party.Members) member.Actor.Session.ReturnFromDungeonSelection(restorePosition: false);
        That(service.Select(leader, 210, party, out _).Length == 4, "释放和取消后原分队可以重新选择");
        var replacement = leader.CurrentDungeonSelection;
        leader.CancelRaidDungeonSelection(selected);
        That(ReferenceEquals(replacement, leader.CurrentDungeonSelection), "旧选择清理不能覆盖新选择");
        var latest = party.Members.Select(member => new PartyDungeonNotice(PartyDungeonNoticeKind.RaidSelection, member, party.Members,
            Selection: new(member.Actor.Session.Selected!, member.Actor.Owner.Generation, member.Actor.Epoch,
                member.Actor.Session.CurrentDungeonSelection!.Id))).ToArray();
        leader.CancelRaidDungeonSelection(latest);
        foreach (var member in party.Members) member.Actor.Session.ReturnFromDungeonSelection(restorePosition: false);

        static (RaidActor Actor, DungeonRunIdentity Identity)[] Bind(RaidActor[] group)
        {
            Guid run = Guid.NewGuid();
            return group.Select(actor => (actor, new DungeonRunIdentity(actor.Character, 0, 1, run, 1))).ToArray();
        }
    }
}
