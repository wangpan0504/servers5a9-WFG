using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonRoomCatalogTests
{
    static void TournamentApcResources(Fixture fixture)
    {
        var aiPaths = Enumerable.Range(20001,15).ToDictionary(id => id,id => $"aicharacter/tournament-{id}.aic");
        var monsterPaths = aiPaths.Keys.ToDictionary(id => id,id => $"monster/tournament-{id}.mob");
        DungeonRoomAiCharacter? Resolve(int id) => aiPaths.TryGetValue(id,out var path)
            ? new(id,Array.AsReadOnly(new[] { id,0,0,0,0,0 }),"[monster]","[normal]",path,50 + id % 15) : null;
        PvfToken[] Table(string kind,int? firstId = null) =>
        [
            .. fixture.Block("tournament enemies",[new(0,1),fixture.String(kind),
                .. aiPaths.Keys.SelectMany((id,index) => new PvfToken[]
                    { new(0,index == 0 ? firstId ?? id : id),new(0,300 + index),fixture.String("opponent-" + id) })]),
            .. fixture.Block("tournament start area",[new(0,1),new(0,950),new(0,270),new(0,2)])
        ];
        var tokens = Table("[apc]");
        var source = DungeonRoomCatalog.ParseTournamentSource(tokens,fixture.Resolve,monsterPaths,
            path => path.StartsWith("aicharacter/",StringComparison.Ordinal),aiPaths,Resolve);
        That(source is { Kind: TournamentEnemyKind.AiCharacter, PartyCount: 1, HasStartArea: true, OpponentX: 950, OpponentY: 270 } &&
            source.Enemies.Count == 15 && source.Enemies.All(enemy => enemy.AiCharacter is
                { Faction: "[monster]", RankKind: "[normal]" } actor && actor.Id == enemy.Code &&
                actor.Level == 50 + enemy.Code % 15 && actor.Path == aiPaths[enemy.Code]),
            "大会APC按独立目录保留真实模板与声明等级，不误取同编号monster或副本等级");
        tokens[3] = new(0,99999);
        That(source.Enemies[0].Code == 20001 && source.Enemies[0].AiCharacter!.Level == 56,
            "大会候选保留解析时来源，输入token修改不改变已冻结对手");
        var rounds = DungeonTournamentPlan.PlanBracket(source,12345);
        That(rounds.Select(round => round.Count).SequenceEqual([16,8,4,2]) &&
            rounds.All(round => round.Count(team => team.Player) == 1) &&
            rounds.SelectMany(round => round).Where(team => !team.Player).SelectMany(team => team.Members)
                .All(enemy => source.Enemies.Contains(enemy) && enemy.AiCharacter is not null),
            "APC四轮对阵沿用已验证候选，不在轮次晋级时重建模板或改变源类别");

        var monsters = DungeonRoomCatalog.ParseTournamentSource(Table("[monster]"),fixture.Resolve,monsterPaths,
            path => path.StartsWith("monster/",StringComparison.Ordinal),aiPaths,
            _ => throw new InvalidOperationException("Monster tournament must not resolve APC templates"));
        That(monsters.Kind == TournamentEnemyKind.Monster && monsters.Enemies.All(enemy => enemy.AiCharacter is null) &&
            monsters.PartyCount == source.PartyCount && monsters.Enemies.Select(enemy => enemy.Code).SequenceEqual(source.Enemies.Select(enemy => enemy.Code)),
            "普通大会继续只查monster目录，不引入APC最低信息依赖");

        Reject<NotSupportedException>(() => DungeonRoomCatalog.ParseTournamentSource(Table("[future]"),fixture.Resolve,monsterPaths,
            _ => true,aiPaths,Resolve),"未知大会源类别不能冒充普通怪物或APC");
        Reject<NotSupportedException>(() => DungeonRoomCatalog.ParseTournamentSource(Table("[apc]"),fixture.Resolve,monsterPaths,
            _ => true),"缺APC目录不能回退同编号monster目录");
        Reject<NotSupportedException>(() => DungeonRoomCatalog.ParseTournamentSource(Table("[apc]"),fixture.Resolve,monsterPaths,
            _ => true,aiPaths),"缺APC最低信息解析器不能补造副本等级");
        Reject<InvalidDataException>(() => DungeonRoomCatalog.ParseTournamentSource(Table("[apc]"),fixture.Resolve,monsterPaths,
            _ => true,new Dictionary<int,string>(),Resolve),"APC未列入自身目录时拒绝，不能借用monster目录");
        Reject<InvalidDataException>(() => DungeonRoomCatalog.ParseTournamentSource(Table("[apc]"),fixture.Resolve,monsterPaths,
            path => path != aiPaths[20001],aiPaths,Resolve),"APC正文缺失时拒绝，不发布部分候选");
        foreach (var invalid in new DungeonRoomAiCharacter?[]
        {
            null,Resolve(20001)! with { Id = 20002 },Resolve(20001)! with { Level = 0 },
            Resolve(20001)! with { Level = 256 },Resolve(20001)! with { Faction = "[character]" },
            Resolve(20001)! with { RankKind = "[boss]" },Resolve(20001)! with { Path = monsterPaths[20001] }
        })
            Reject<InvalidDataException>(() => DungeonRoomCatalog.ParseTournamentSource(Table("[apc]"),fixture.Resolve,monsterPaths,
                _ => true,aiPaths,id => id == 20001 ? invalid : Resolve(id)),
                "无效或错绑APC模板不能生成大会对手");
        var outsideFactory = new Dictionary<int,string>(aiPaths) { [80001] = "aicharacter/outside-factory.aic" };
        Reject<NotSupportedException>(() => DungeonRoomCatalog.ParseTournamentSource(Table("[apc]",80001),fixture.Resolve,monsterPaths,
            _ => true,outsideFactory,Resolve),"超出原生APC工厂职业范围的模板不能生成");
    }
}
