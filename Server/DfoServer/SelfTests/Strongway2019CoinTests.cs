using DfoServer.Game.Dungeons;
using DfoServer.Game.Strongway2019;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class Strongway2019CoinTests
{
    public static void RunCatalogResources(PvfArchive pvf)
    {
        int before = Count;
        var dungeons = DungeonCatalog.Load(pvf);
        foreach (var (id, maximum) in new[] { (5359, 12), (5363, 4), (5360, 4) })
        {
            for (byte difficulty = 0; difficulty < 5; difficulty++)
                That(dungeons.Get(id).PersonalCoinInfoLimit(difficulty) == maximum,
                    "真实coin-info五难度表为目标个人普通复活上限：" + id + "/" + difficulty);
            That(dungeons.Get(id).PersonalCoinInfoLimit(5) is null, "未知难度不借用其它行的复活上限");
        }
        Console.WriteLine($"PASS {Count - before} 首期真实coin-info个人上限目录");
    }

    public static void RunResources(PvfArchive pvf, TownCatalog towns, ItemResources items)
    {
        int before = Count;
        var activity = new Strongway2019Catalog(pvf);
        var growth = CharacterGrowthCatalog.Load(pvf);
        var dungeons = DungeonCatalog.Load(pvf); var rooms = DungeonRoomCatalog.Load(pvf);
        var drops = DungeonMonsterDropCatalog.Load(pvf);
        foreach (var (id, maximum) in new[] { (5359u, 12), (5363u, 4) })
        {
            using var f = new Strongway2019MatchingTests.Fixture(towns, activity, growth);
            var team = f.Team(reviveCoins: 30);
            var service = new PartyDungeonService(f.Parties, f.Repository);
            That(service.BeginSelection(team[0]).Length == 4 && service.Enter(team[0], id, 2, dungeons, rooms,
                drops, items.Items, items.EpicPieces, null).Length == 4, "复活回归沿首期真实四人准入入场：" + id);
            var instance = team[0].PartyDungeon!;
            foreach (var session in team) instance.CompleteLoading(session);
            That(team.All(session => session.CurrentFormalDungeonScene()!.Entry.CoinLimit == maximum),
                "仅已通过首期Admission的原Entry冻结coin-info个人上限：" + id);
            var identity = team[0].CurrentFormalDungeonScene()!.Snapshot.Identity;
            for (int used = 1; used < maximum; used++)
            {
                That(team[0].ReportFormalCharacterDeath() is not null &&
                    team[0].ReviveFormalCharacter(team[0].Selected!.Id) is { Accepted: true } &&
                    team[0].Selected!.Inventory.ReviveCoins == 30 - used,
                    "本人普通复活每次只消费一币且在上限内成功：" + used);
                That(team[0].ReviveFormalCharacter(team[0].Selected!.Id) is null,
                    "已复活重复请求不再扣币或消耗次数");
            }
            var next = team[0].CurrentFormalDungeonScene()!.Entry.Maze.MapSpecification![1].Position;
            That(instance.Move(team[0], checked((byte)next.X), checked((byte)next.Y), 0).Length == 4,
                "已用次数后沿原四人清空开场房真实过房");
            foreach (var session in team) instance.CompleteLoading(session);
            That(team[0].CurrentFormalDungeonScene()!.Snapshot.Identity.InstanceId == identity.InstanceId &&
                team[0].CurrentFormalDungeonScene()!.Snapshot.Identity.TaskAttempt == identity.TaskAttempt &&
                team[0].ReportFormalCharacterDeath() is not null &&
                team[0].ReviveFormalCharacter(team[0].Selected!.Id) is { Accepted: true } &&
                team[0].Selected!.Inventory.ReviveCoins == 30 - maximum,
                "跨房保留同一原Run，最后一次个人额度仍可使用");
            team[0].ReportFormalCharacterDeath();
            var exhausted = team[0].Selected!;
            That(team[0].ReviveFormalCharacter(exhausted.Id) is { Accepted: false } && ReferenceEquals(team[0].Selected, exhausted) &&
                exhausted.Inventory.ReviveCoins > 0, "本人达到资源上限后即使仍有币也拒绝，不改变资产");
            That(team[1].ReportFormalCharacterDeath() is not null &&
                team[1].ReviveFormalCharacter(team[1].Selected!.Id) is { Accepted: true } && team[1].Selected!.Inventory.ReviveCoins == 29,
                "另一原队员仍有独立个人额度，不共享前一成员的次数");
            That(team[0].ReviveFormalCharacter(team[1].Selected!.Id) is null,
                "保持既有本人key复活边界，不扩展未实现的跨成员代付");
        }
        Console.WriteLine($"PASS {Count - before} 首期简单英雄个人复活上限、过房保留与队员独立");
    }
}
