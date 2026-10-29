using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PvpGradeProjectionTests
{
    public static void RunCore()
    {
        int before = Count;
        var point = new Location(10, 4, 10, 10);
        var character = new Character(1, 0, "等级投影", 0, point, point)
        {
            Level = 85, GrowType = 17,
            DungeonState = CharacterDungeonState.NewOrdinary with { ProficiencyLevel = 137, ProficiencyExperience = 123456789 }
        };
        byte[] initial = TownPackets.Actor(character, 0, utcSeconds: 1000);
        byte[] terra = TownPackets.Actor(character, 20, utcSeconds: 1000);
        int gradeOffset = 130 + CharacterName.Encoding.GetByteCount(character.Name);
        That(initial.Length == terra.Length && Enumerable.Range(0, initial.Length)
            .Where(index => initial[index] != terra[index]).SequenceEqual(new[] { gradeOffset }),
            "泰拉石四星20只改变Actor普通等级后的首个u8，不改包长、熟练、技能或外观");
        That(terra[gradeOffset] == 20 && terra[gradeOffset + 1] == 0 && terra[gradeOffset - 1] == 85 &&
            terra[gradeOffset - 2] == 17 && terra[gradeOffset + 2] == 0,
            "决斗等级与普通等级、职业、未知辅助字节、场景字节保持独立");

        string root = Path.Combine(Path.GetTempPath(), "S5A9-pvp-grade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var owners = new AccountOwners();
            using var lease = owners.Acquire("grade-owner", () => { });
            var created = repository.Create(lease, new NewCharacter("GradeOwner", point, point));
            var map = new TownMap("grade-fixture.map", [new(new(0, 0, 100, 100), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(10, 4)] = new(10, 4, 1, [], [], [], point, map, [], []) },
                point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var calculator = new PvpRatingCalculator(PvpRatingTests.Fixture());
            var store = new Ratings(lease, created.Id) { State = calculator.Initial() with { Experience = 12000, HighestGrade = 1 } };
            var session = new GameSession(repository, towns, lease, pvpRatings: new(store, calculator));
            session.Select(0);
            var selected = session.Selected!;
            That(session.ReadPvpGrade(selected) == 1, "选角初始化尚未Ready也读取所属账号真实评分");
            session.MarkReady();
            var snapshot = session.PublicCharacter(selected.Id)!;
            That(snapshot.PvpGrade == 1 && ReferenceEquals(snapshot.Character, selected) && ReferenceEquals(session.Selected, selected),
                "公开等级捕获不替换Selected或成员引用");
            session.ReturnToRoster();
            That(!snapshot.IsCurrent() && session.PublicCharacter(selected.Id) is null,
                "返回选角后旧公开快照仍按原epoch失效");
            store.State = store.State with { Experience = 0 };
            session.Select(0); session.MarkReady();
            That(session.PublicCharacter(selected.Id)!.PvpGrade == 0 && !snapshot.IsCurrent(),
                "重新选角读取新持久评分且旧快照不能复活");
            Reject<InvalidOperationException>(() => session.ReadPvpGrade(selected with { Id = 65535 }),
                "不能借本账号捕获其他角色评分");
            using var replacement = owners.Acquire("grade-owner", () => { });
            That(session.PublicCharacter(selected.Id) is null,
                "旧generation公开查询不发布等级");
            Reject<InvalidOperationException>(() => session.ReadPvpGrade(selected), "旧generation不能读评分");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
        Console.WriteLine($"PASS {Count - before} 决斗等级Actor投影及选角身份断言");
    }

    sealed class Ratings(AccountOwners.Lease expectedOwner, ushort expectedCharacter) : IPvpRatingStore
    {
        public required PvpRatingState State { get; set; }
        public PvpRatingSnapshot? ReadPvpRating(AccountOwners.Lease owner, ushort characterId)
        {
            That(ReferenceEquals(owner, expectedOwner) && characterId == expectedCharacter && owner.IsCurrent,
                "评分源只接受当前账号及角色");
            return new(1, State);
        }
        public PvpRatingClaim? ReadPvpRatingClaim(AccountOwners.Lease owner, ushort characterId, Guid matchId) => throw new InvalidOperationException();
        public Guid[] ReadPendingPvpRatings(AccountOwners.Lease owner, ushort characterId, int limit) => throw new InvalidOperationException();
        public PvpRatingReceipt CommitPvpRating(AccountOwners.Lease owner, PvpRatingPlan plan) => throw new InvalidOperationException();
    }
}
