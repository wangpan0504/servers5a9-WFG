using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using DfoServer.Development;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Projection;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EquipmentQualityInvestmentTests
{
    public static void RunCore()
    {
        int before = Count;
        // 独立Python闭式LCG向量；不调用被测resolver生成期望。固定区前后每个边界均包含。
        (int Seed, int Tier, int? Percent)[] vectors =
        [
            (-2147483648, 4, null),
            (-2147483647, 0, null),
            (-1000000000, 2, null),
            (-999999998, 1, null),
            (-123456789, 3, null),
            (-2, 3, null),
            (-1, 1, null),
            (0, 4, null),
            (1, 0, null),
            (2, 3, null),
            (3, 1, null),
            (12345, 1, null),
            (123456789, 2, null),
            (999999889, 1, null),
            (999999890, 4, null),
            (999999891, 0, null),
            (999999892, 4, null),
            (999999893, 0, null),
            (999999894, 0, 1),
            (999999895, 0, 2),
            (999999896, 0, 3),
            (999999897, 0, 4),
            (999999898, 0, 5),
            (999999899, 0, 6),
            (999999900, 0, 7),
            (999999901, 0, 8),
            (999999902, 0, 9),
            (999999903, 0, 10),
            (999999904, 0, 11),
            (999999905, 0, 12),
            (999999906, 0, 13),
            (999999907, 0, 14),
            (999999908, 0, 15),
            (999999909, 0, 16),
            (999999910, 0, 17),
            (999999911, 0, 18),
            (999999912, 0, 19),
            (999999913, 1, 20),
            (999999914, 1, 21),
            (999999915, 1, 22),
            (999999916, 1, 23),
            (999999917, 1, 24),
            (999999918, 1, 25),
            (999999919, 1, 26),
            (999999920, 1, 27),
            (999999921, 1, 28),
            (999999922, 1, 29),
            (999999923, 1, 30),
            (999999924, 1, 31),
            (999999925, 1, 32),
            (999999926, 1, 33),
            (999999927, 1, 34),
            (999999928, 1, 35),
            (999999929, 1, 36),
            (999999930, 1, 37),
            (999999931, 1, 38),
            (999999932, 1, 39),
            (999999933, 2, 40),
            (999999934, 2, 41),
            (999999935, 2, 42),
            (999999936, 2, 43),
            (999999937, 2, 44),
            (999999938, 2, 45),
            (999999939, 2, 46),
            (999999940, 2, 47),
            (999999941, 2, 48),
            (999999942, 2, 49),
            (999999943, 2, 50),
            (999999944, 2, 51),
            (999999945, 2, 52),
            (999999946, 2, 53),
            (999999947, 2, 54),
            (999999948, 2, 55),
            (999999949, 2, 56),
            (999999950, 2, 57),
            (999999951, 2, 58),
            (999999952, 2, 59),
            (999999953, 3, 60),
            (999999954, 3, 61),
            (999999955, 3, 62),
            (999999956, 3, 63),
            (999999957, 3, 64),
            (999999958, 3, 65),
            (999999959, 3, 66),
            (999999960, 3, 67),
            (999999961, 3, 68),
            (999999962, 3, 69),
            (999999963, 3, 70),
            (999999964, 3, 71),
            (999999965, 3, 72),
            (999999966, 3, 73),
            (999999967, 3, 74),
            (999999968, 3, 75),
            (999999969, 3, 76),
            (999999970, 3, 77),
            (999999971, 3, 78),
            (999999972, 3, 79),
            (999999973, 4, 80),
            (999999974, 4, 81),
            (999999975, 4, 82),
            (999999976, 4, 83),
            (999999977, 4, 84),
            (999999978, 4, 85),
            (999999979, 4, 86),
            (999999980, 4, 87),
            (999999981, 4, 88),
            (999999982, 4, 89),
            (999999983, 4, 90),
            (999999984, 4, 91),
            (999999985, 4, 92),
            (999999986, 4, 93),
            (999999987, 4, 94),
            (999999988, 4, 95),
            (999999989, 4, 96),
            (999999990, 4, 97),
            (999999991, 4, 98),
            (999999992, 4, 99),
            (999999993, 1, 20),
            (999999994, 3, null),
            (999999995, 4, 80),
            (999999996, 2, 40),
            (999999997, 2, null),
            (999999998, 4, 100),
            (999999999, 3, 60),
            (1000000000, 1, null),
            (1000000001, 1, null),
            (1000000002, 4, null),
            (1000000003, 3, null),
            (1000000004, 0, null),
            (2147483646, 3, null),
            (2147483647, 1, null),
        ];
        foreach (var (seed, tier, percent) in vectors)
            That(EquipmentQualitySeedResolver.Resolve(seed) is { } q && q.Seed == seed && q.Tier == tier &&
                q.FixedTargetPercent == percent && q.DisplayPercent is null, $"独立数学向量：{seed}");
        var templates = EquipmentInvestmentScore.Slots.ToDictionary(slot => 1000 + slot,
            slot => new ItemTemplate(1000 + slot, ItemKind.Equipment, (byte)slot, 0, 0, 1, 0, 0, 999, "[free]", ushort.MaxValue, 0, "零属性模板"));
        var weapon = Item(12, 999999998);
        InventoryItem Item(short slot, int seed) => new(slot, new(InventoryListType.Equipment, slot), new(1000 + slot, ItemKind.Equipment, seed));
        CharacterInvestmentSnapshot Snapshot(params InventoryItem[] items) => CharacterInvestmentScore.Capture(
            new Character(1, 0, "品级测试", 9, default!, null) { Level = 95, Inventory = new([.. items]) }, 1000);
        CharacterInvestmentResult Calculate(CharacterInvestmentSnapshot snapshot, int version = 23) => CharacterInvestmentScore.Calculate(snapshot,
            id => templates[id], (_, _, _, _) => false, _ => false, _ => null, _ => null, _ => null,
            _ => throw new InvalidOperationException("空收集箱不得读目录"), (_, _) => false,
            version >= 21 ? _ => throw new InvalidOperationException("空气息不得读目录") : null,
            version >= 22 ? (_, _) => throw new InvalidOperationException("零次元不得读目录") : null,
            includeQualitySeedTier: version >= 23);
        int Quality(CharacterInvestmentResult score) => score.Parts.Where(p => p.Module == "quality").Sum(p => p.Score);
        foreach (var (seed, tier, percent) in vectors)
        {
            var score = Calculate(Snapshot(weapon with { Core = weapon.Core with { Value = seed } }));
            That(score is { Profile: CharacterInvestmentScore.QualityProfile, Partial: false, Score: not null } &&
                Quality(score) == 25 * tier && score.Score == 200 + 25 * tier &&
                score.Parts.Single(p => p.Module == "quality").Quality == new EquipmentQualitySeedTier(seed, tier, percent),
                $"完整int32已提交seed仅加五档分，不用Grade或零属性推断UI：{seed}");
        }
        var full = Snapshot(EquipmentInvestmentScore.Slots.Select(slot => Item(slot, 999999998)).ToArray());
        That(Quality(Calculate(full)) == 1200 && Calculate(full).Score == 2500, "12槽每件100，武器不二次乘权且不更改底座1300");
        foreach (int version in new[] { 20, 21, 22 })
        {
            var old = Calculate(full, version);
            That(old.Score == 1300 && Quality(old) == 0 && old.Parts.All(p => p.Quality is null) &&
                old.Profile == (version == 20 ? CharacterInvestmentScore.Profile : version == 21 ? CharacterInvestmentScore.ChronicleProfile : CharacterInvestmentScore.DimensionEnergyProfile),
                "旧版本仍按原合同可复算：" + version);
        }
        var expired = Snapshot(weapon with { Core = weapon.Core with { ExpireTime = 1000 } });
        That(Calculate(expired).Score == 0 && Quality(Calculate(expired)) == 0, "到期等号排除宿主及品质");
        That(Calculate(Snapshot(weapon, weapon with { Position = new(InventoryListType.Equipment, 14) })).Status == "Invalid", "重复UID不得收分");
        That(Calculate(Snapshot(weapon, weapon with { Uid = 99 })).Status == "Invalid", "重复槽不得收分");
        var mismatch = Snapshot(weapon with { Core = weapon.Core with { ItemId = 1014 } });
        That(Calculate(mismatch).Partial && Quality(Calculate(mismatch)) == 0, "模板实际槽不匹配排除品质");
        That(Calculate(Snapshot(weapon with { Core = weapon.Core with { Kind = ItemKind.Material } })).Status == "Invalid", "实例Kind不能借标准槽混入");
        var saved = templates[1012]; templates[1012] = saved with { Kind = ItemKind.Creature };
        That(Calculate(Snapshot(weapon)).Partial && Quality(Calculate(Snapshot(weapon))) == 0, "模板Kind不匹配排除品质");
        templates[1012] = saved;
        foreach (short slot in new short[] { 13, 24, 30 })
        {
            templates[1000 + slot] = saved with { Id = 1000 + slot, EquipmentSlot = (byte)slot };
            That(Quality(Calculate(Snapshot(Item(slot, 999999998)))) == 0 &&
                Calculate(Snapshot(Item(slot, 999999998))).Parts.All(p => p.Quality is null), "称号及非标准装备不解释seed：" + slot);
        }
        var pet = new InventoryItem(999999998, new(InventoryListType.Equipment, 26), new(1026, ItemKind.Creature, 999999998))
        { Creature = new() { Level = 1 } };
        templates[1026] = saved with { Id = 1026, Kind = ItemKind.Creature, EquipmentSlot = 26 };
        var avatar = new InventoryItem(999999999, new(InventoryListType.Equipment, 0), new(1000, ItemKind.Avatar, 999999999)) { Avatar = new() };
        templates[1000] = saved with { Id = 1000, Kind = ItemKind.Avatar, EquipmentSlot = 0, Grade = 0 };
        var nonEquipment = Calculate(Snapshot(pet, avatar));
        That(Quality(nonEquipment) == 0 && nonEquipment.Parts.All(p => p.Quality is null), "宠物/时装Value即使形似固定seed也不解释");
        That(full.Worn.All(i => i.Core.Value == 999999998), "评分不写真实seed");
        using (var fixture = new MailboxPersistenceTests.Fixture())
        {
            int[] seeds = [0, 1, 999999893, 999999894, 999999992, 999999993, 999999994, 999999995, 999999996, 999999997, 999999998, int.MaxValue];
            var stored = EquipmentInvestmentScore.Slots.Select((slot, index) => Item(slot, seeds[index]) with { Uid = 0 }).ToImmutableArray();
            var created = fixture.Repository.Create(fixture.SenderOwner, new("QualityHero", fixture.Sender.Position, null) { Level = 95, Inventory = new(stored) });
            fixture.SenderOwner.Dispose();
            string databaseBefore = fixture.Snapshot();
            string response = DevelopmentGm.QueryCharacterScore(fixture.Repository, fixture.Owners, null,
                DevelopmentRequest.Parse("character-score QualityHero", "sender"), snapshot => Calculate(snapshot));
            using var json = JsonDocument.Parse(response[3..]); var root = json.RootElement;
            int expected = seeds.Sum(seed => 25 * vectors.Single(v => v.Seed == seed).Tier);
            That(root.GetProperty("Profile").GetString() == CharacterInvestmentScore.QualityProfile &&
                root.GetProperty("CharacterId").GetUInt16() == created.Id && root.GetProperty("Score").GetInt32() == 1300 + expected &&
                root.GetProperty("Status").GetString() == "Complete", "真实SQLite保存seed经GM只读冻结查询加档位分");
            var quality = root.GetProperty("Parts").EnumerateArray().Where(p => p.GetProperty("Quality").ValueKind == JsonValueKind.Object).ToArray();
            That(quality.Length == 12 && quality.Select(p => p.GetProperty("Quality").GetProperty("Seed").GetInt32()).SequenceEqual(seeds) &&
                quality.All(p => p.GetProperty("Quality").GetProperty("DisplayPercent").ValueKind == JsonValueKind.Null), "SQLite各slot原seed及未知显示百分比完整保留");
            That(fixture.Snapshot() == databaseBefore, "只读评分不改变数据库、revision或实际物品");
        }
        using (var fixture = new CharacterScoreNoticeTests.Fixture())
        {
            var capture = fixture.Session.CaptureCharacterScoreNotice("notice", "NoticeHero");
            byte[]? body = null;
            var sent = CharacterScoreProjection.Send(fixture.Session, capture,
                snapshot => fixture.Calculate(snapshot) with { Profile = CharacterInvestmentScore.QualityProfile }, _ => null,
                (_, bytes, _, _) => { body = bytes; return true; }, "0123456789abcdef0123456789abcdef");
            That(sent.Status == "queued" && Encoding.ASCII.GetString(body!).Contains("|v2.3|", StringComparison.Ordinal), "机器版本v2.3只确认入队");
            CharacterScoreProjection.Send(fixture.Session, capture,
                snapshot => fixture.Calculate(snapshot) with { Profile = CharacterInvestmentScore.QualityProfile }, _ => null,
                (_, bytes, _, _) => { body = bytes; return true; });
            That(Encoding.UTF8.GetString(body!).Contains(CharacterInvestmentScore.QualityProfile, StringComparison.Ordinal), "摘要同步v2.3");
        }
        Console.WriteLine($"EquipmentQualityInvestmentTests: {Count - before} passed");
    }
}
