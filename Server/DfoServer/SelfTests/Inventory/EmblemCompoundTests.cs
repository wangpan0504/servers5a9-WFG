using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class EmblemCompoundTests
{
    static readonly string[] JobNames =
    [
        "swordman", "fighter", "gunner", "mage", "priest", "at gunner", "thief", "at fighter",
        "at mage", "demonic swordman", "creator mage", "at swordman", "knight", "demonic lancer",
        "at priest", "gun blader"
    ];

    public static void RunCore()
    {
        int checks = Count;
        var parsed = EmblemCompoundRequests.Read(Body((1001, 289), (1002, 290)));
        That(parsed.Inputs.SequenceEqual([new(1001, 289), new(1002, 290)]),
            "CMD256按count解析itemId与u16槽位");
        foreach (byte[] malformed in new byte[][]
        {
            [], [1], [2, 1], [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            [2, 1, 0, 0, 0, 0x60, 1, 2, 0, 0, 0, 0x22, 1],
            [6]
        })
            Reject<InvalidDataException>(() => EmblemCompoundRequests.Read(malformed),
                "CMD256拒绝件数、长度、编号和槽位无效的正文");

        var mappings = EmblemCompoundCatalog.ParseMappings("""
            [emblem compound info]
            1 3 9001 9002
            2 2 9003
            [/emblem compound info]
            """);
        That(mappings.Count == 3 && mappings[(1, 2)] == 9001 &&
            mappings[(1, 3)] == 9002 && mappings[(2, 2)] == 9003,
            "emblemcompound按grade与实际件数展开booster映射");
        foreach (string invalid in new[]
        {
            "[emblem compound info]\n1",
            "[emblem compound info]\n1 6 1 2 3 4 5",
            "[emblem compound info]\n1 2 9001 1 2 9002",
            "[emblem compound info]\n"
        })
            Reject<InvalidDataException>(() => EmblemCompoundCatalog.ParseMappings(invalid),
                "徽章合成映射拒绝截断、越界、重复和空表");

        var fixture = new Fixture();
        var pool = fixture.Compounds.For(1, 2);
        That(pool.BoosterItemId == 9001 &&
            pool.Rewards.Length == JobNames.Length + 2 &&
            pool.Rewards[0] is { ItemId: 2001, Weight: 10, Rarity: 2 } &&
            Enumerable.Range(0, JobNames.Length).All(job =>
                pool.Rewards[job + 1] is { Weight: 1, Rarity: 6 } &&
                pool.Rewards[job + 1].ItemId == 2100 + job &&
                pool.Rewards[job + 1].PlatinumJobs.SetEquals([checked((byte)job)])) &&
            pool.Rewards[^1].ItemId == 2200 && pool.Rewards[^1].PlatinumJobs.SetEquals([0, 9]),
            "booster保留原权重与rarity，并将16个技能职业及显式多职业标签逐项独立解析");
        var compatible = EmblemCompoundCatalog.ParsePool("""
            [booster info]
            [stackable]
            1
            [/stackable]
            [emblem]
            1 2001 7 2
            [/emblem]
            [/booster info]
            """, 9002, _ => "[stackable type]\n`[avatar emblem]` 0\n[rarity]\n2",
            new Dictionary<int, string> { [2001] = "normal.stk" });
        That(compatible.Length == 1 && compatible[0] is { ItemId: 2001, Weight: 7, Count: 2 },
            "空stackable占位不遮蔽后续奖池，并兼容参考资源的emblem段");
        Reject<InvalidDataException>(() => EmblemCompoundCatalog.ParsePool(
            "[booster info]\n[stackable]\n1 2001 7 1\n[/stackable]\n[/booster info]", 9003,
            _ => "[stackable type]\n`[material]` 0\n[rarity]\n2",
            new Dictionary<int, string> { [2001] = "material.stk" }),
            "booster候选必须由实际stackable type确认为avatar emblem");
        Reject<InvalidDataException>(() => EmblemCompoundCatalog.ParsePool(
            "[booster info]\n[stackable]\n2 2001 7 1\n[/stackable]\n[/booster info]", 9004,
            _ => "[stackable type]\n`[avatar emblem]` 0\n[rarity]\n2",
            new Dictionary<int, string> { [2001] = "normal.stk" }),
            "单行ACK玩法拒绝静默截断多抽booster");

        var request = new EmblemCompoundRequest([new(1001, 289), new(1002, 290)]);
        var ordinary = fixture.Service(Rolls(0)).Apply(fixture.Character(1, (289, 1001, 1), (290, 1002, 1)),
            request, out var ordinaryReceipt, out var ordinaryRejection);
        That(ordinary.Accepted && ordinaryRejection is null && ordinaryReceipt is
            { MinimumGrade: 1, InputCount: 2, RewardItemId: 2001, RewardCount: 1, PlatinumReroll: false },
            "混合grade使用最低grade映射，普通结果不做职业过滤");
        That(ordinary.Plan!.After.Find(new(InventoryListType.Main, 289))?.Core.ItemId == 2001 &&
            ordinary.Plan.After.Find(new(InventoryListType.Main, 290)) is null,
            "材料先删除后允许奖励复用刚腾出的徽章槽");

        for (byte job = 0; job < JobNames.Length; job++)
        {
            var platinum = fixture.Service(Rolls(10, 0)).Apply(
                fixture.Character(job, (289, 1001, 1), (290, 1002, 1)), request,
                out var platinumReceipt, out var platinumRejection);
            That(platinum.Accepted && platinumRejection is null && platinumReceipt is
                { PlatinumReroll: true } && platinumReceipt.RewardItemId == 2100 + job,
                $"职业{job}只命中自身skill levelup标签，不与其他职业串池");
        }
        foreach (byte job in new byte[] { 0, 9 })
        {
            var shared = fixture.Service(Rolls(10, 1)).Apply(
                fixture.Character(job, (289, 1001, 1), (290, 1002, 1)), request,
                out var sharedReceipt, out var sharedRejection);
            That(shared.Accepted && sharedRejection is null && sharedReceipt is { RewardItemId: 2200 },
                $"职业{job}可命中资源显式列出的多职业白金候选");
        }
        var noProfession = fixture.Service(Rolls(10)).Apply(
            fixture.Character(16, (289, 1001, 1), (290, 1002, 1)), request,
            out var deniedReceipt, out var deniedReason);
        That(noProfession is { Accepted: false, Error: 22, Plan: null } &&
            deniedReceipt is null && deniedReason == "platinum-job-pool",
            "首次白金命中但同池没有当前基础职业候选时整笔拒绝");

        var duplicate = new EmblemCompoundRequest([new(1001, 289), new(1001, 289)]);
        var duplicateResult = fixture.Service(Rolls(0)).Apply(
            fixture.Character(0, (289, 1001, 3)), duplicate, out _, out _);
        That(duplicateResult.Accepted &&
            duplicateResult.Plan!.After.Find(new(InventoryListType.Main, 289))?.Core.Count == 1,
            "同一槽重复两次按数量2扣除，不误删整堆");

        var full = fixture.FullCharacter();
        var fullResult = fixture.Service(Rolls(0)).Apply(full, duplicate, out var fullReceipt, out var fullReason);
        That(fullResult is { Accepted: false, Error: 23, Plan: null } &&
            fullReceipt is null && fullReason == "emblem-capacity" &&
            full.Inventory.Find(new(InventoryListType.Main, 289))?.Core.Count == 3,
            "徽章页满且输入堆未清空时回23，原快照不扣材料");

        That(Convert.ToHexString(EmblemCompoundPackets.Success(
                new(9001, 1, 2, 2001, 1, false))) == "0101D107000001000000" &&
            Convert.ToHexString(EmblemCompoundPackets.Failure()) == "001600" &&
            Convert.ToHexString(EmblemCompoundPackets.Failure(23)) == "001700",
            "成功ACK是单行itemId/count，失败按本版消费者写u16错误码");
        Console.WriteLine($"PASS {Count - checks} 徽章合成协议、资源、职业重抽及领域事务断言");
    }

    public static void RunResources(ItemResources resources)
    {
        var entries = resources.EmblemCompounds.Entries.ToArray();
        That(entries.Length > 0 && entries.All(entry => entry.Key.Count is >= 2 and <= 5 &&
            entry.Key.Grade > 0 && !entry.Value.Rewards.IsDefaultOrEmpty),
            "现场emblemcompound映射提供2至5件的正grade奖池");
        var rewards = entries.SelectMany(entry => entry.Value.Rewards).DistinctBy(reward => reward.ItemId).ToArray();
        foreach (var reward in rewards)
        {
            var template = resources.Items.Get(reward.ItemId);
            That(template.Kind == ItemKind.Emblem && template.Rarity == reward.Rarity &&
                reward.Weight > 0 && reward.Count > 0,
                "现场徽章合成booster的所有候选均为正权重avatar emblem");
        }
        var platinumJobs = rewards.Where(reward => reward.Rarity == 6)
            .SelectMany(reward => reward.PlatinumJobs).ToHashSet();
        That(Enumerable.Range(0, JobNames.Length).All(job => platinumJobs.Contains(checked((byte)job))),
            "现场白金候选覆盖16个独立skill levelup职业标签");
        var rewardsById = rewards.ToDictionary(reward => reward.ItemId);
        That(rewardsById.TryGetValue(2540050, out var mage) && mage.PlatinumJobs.SetEquals([3]) &&
            rewardsById.TryGetValue(2540158, out var atMage) && atMage.PlatinumJobs.SetEquals([8]),
            "现场2540050仅属于mage、2540158仅属于at mage，不因usable job为all而串池");
    }

    static byte[] Body(params (int ItemId, short Slot)[] inputs)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(checked((byte)inputs.Length));
        foreach (var input in inputs)
        {
            writer.Write(input.ItemId);
            writer.Write(input.Slot);
        }
        return stream.ToArray();
    }

    static Func<long, long> Rolls(params long[] values)
    {
        var queue = new Queue<long>(values);
        return _ => queue.Dequeue();
    }

    sealed class Fixture
    {
        readonly GoldLimitCatalog limits = new("[gold limit from level]\n0 1000000\n[/gold limit from level]");
        readonly EpicPieceCatalog pieces = new("[piece list]\n50000 50001\n[/piece list]");
        public ItemCatalog Items { get; }
        public EmblemCompoundCatalog Compounds { get; }

        public Fixture()
        {
            var stackable = new Dictionary<int, string>
            {
                [9001] = "booster.stk", [1001] = "grade1.stk", [1002] = "grade2.stk",
                [2001] = "normal.stk", [2200] = "shared.stk"
            };
            for (int job = 0; job < JobNames.Length; job++)
                stackable.Add(2100 + job, $"job-{job}.stk");
            var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [EmblemCompoundCatalog.RootPath] = "[emblem compound info]\n1 5 9001 9001 9001 9001\n2 2 9001\n[/emblem compound info]",
                ["stackable/grade1.stk"] = Emblem(0, 1),
                ["stackable/grade2.stk"] = Emblem(0, 2),
                ["stackable/normal.stk"] = Emblem(2, 2),
                ["stackable/shared.stk"] = Emblem(6, 3, "swordman", "demonic swordman")
            };
            string poolRows = string.Join(" ", new[] { "2001 10 1" }
                .Concat(Enumerable.Range(0, JobNames.Length).Select(job => $"{2100 + job} 1 1"))
                .Append("2200 1 1"));
            texts["stackable/booster.stk"] =
                $"[booster info]\n[stackable]\n1 {poolRows}\n[/stackable]\n[/booster info]";
            for (int job = 0; job < JobNames.Length; job++)
                texts[$"stackable/job-{job}.stk"] = Emblem(6, 3, JobNames[job]);
            Compounds = new(path => texts[path], stackable);
            Items = new(null!, new Dictionary<int, string>(), stackable, 0);
            var templates = new List<ItemTemplate>
            {
                Template(1001, 0, 1), Template(1002, 0, 2), Template(2001, 2, 2),
                Template(2200, 6, 3)
            };
            templates.AddRange(Enumerable.Range(0, JobNames.Length)
                .Select(job => Template(2100 + job, 6, 3)));
            Items.PrefillTemplates(templates.ToDictionary(template => template.Id));
        }

        public InventoryEmblemCompoundService Service(Func<long, long> random) =>
            new(Items, Compounds, limits, pieces, random);

        public Character Character(byte job, params (short Slot, int ItemId, int Count)[] rows) =>
            new(1, 0, "Emblem", 0, new(1, 0, 10, 10), null)
            {
                Job = job,
                Level = 95,
                Inventory = new(rows.Select((row, index) => new InventoryItem(index + 1,
                    new(InventoryListType.Main, row.Slot),
                    new(row.ItemId, ItemKind.Emblem, row.Count))).ToImmutableArray())
            };

        public Character FullCharacter()
        {
            var rows = Enumerable.Range(289, 63).Select(slot => new InventoryItem(slot,
                new(InventoryListType.Main, checked((short)slot)),
                new(1001, ItemKind.Emblem, slot == 289 ? 3 : 1))).ToImmutableArray();
            return new(1, 0, "FullEmblem", 0, new(1, 0, 10, 10), null)
                { Job = 0, Level = 95, Inventory = new(rows) };
        }

        static ItemTemplate Template(int id, int rarity, int grade) =>
            new(id, ItemKind.Emblem, 0, 1, 0, 1000, 0, rarity, grade,
                "[trade]", ushort.MaxValue, 0, "emblem", "[avatar emblem] 0");

        static string Emblem(int rarity, int grade, params string[] jobs) =>
            $"[stackable type]\n`[avatar emblem]` 0\n[rarity]\n{rarity}\n[grade]\n{grade}\n" +
            (jobs.Length == 0 ? "" : "[enchant]\n[skill levelup]\n" +
                string.Join("\n", jobs.Select(job => $"`[{job}]` 1 1")) +
                "\n[/skill levelup]\n[/enchant]");
    }
}
