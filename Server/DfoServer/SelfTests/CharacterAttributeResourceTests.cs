using DfoServer.GameWorld.Characters;
using PvfLib;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Towns;
using System.Collections.Immutable;

namespace DfoServer;

internal static class CharacterAttributeResourceTests
{
    public static void RunCore()
    {
        var weak = OrdinaryCharacter([], 19);
        weak = weak with { Inventory = weak.Inventory with { StaminaUntil = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600 } };
        var baseStats = new CharacterStats(8550, 5150, 123, 45, 67, 89, -10, 20, -30, 40,
            480000, 525, 800, 100, 100, 6000, 100, 600);
        byte[] weakPlayer = DfoServer.Network.Builders.TownPackets.Player(weak, baseStats);
        Check(weakPlayer[36 + 84] == 10,
            $"全量Player保留真实10%虚弱，实际={weakPlayer[36 + 84]}");
        var now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        byte[] expectedStats = Convert.FromHexString(
            "662100001E1400007B002D0043005900F6FF1400E2FF28000000000000000000000000000000000000000000000000000000000000000000000000000053070000000D02200300006400640070176400580200006400000000");
        byte[] normalPlayer = DfoServer.Network.Builders.TownPackets.Player(
            weak with { Inventory = weak.Inventory with { StaminaUntil = 0 } }, baseStats, utcNow: now);
        foreach (var (seconds, expected) in new (long, byte)[]
            { (600, 10), (541, 10), (540, 19), (481, 19), (1, 91), (0, 100), (-1, 100) })
        {
            long until = now.ToUnixTimeSeconds() + seconds;
            var character = weak with { Inventory = weak.Inventory with { StaminaUntil = until } };
            var originalInventory = character.Inventory;
            for (int refresh = 0; refresh < 3; refresh++)
            {
                byte[] player = DfoServer.Network.Builders.TownPackets.Player(character, baseStats, utcNow: now);
                Check(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(player.AsSpan(32)) == 89 &&
                    player[36 + 84] == expected, $"Player完整包89B虚弱边界{seconds}s={expected}，重复刷新{refresh}");
                expectedStats[84] = expected;
                Check(player.AsSpan(36, 89).SequenceEqual(expectedStats), "基础HP/MP、能力及保留字段未按虚弱缩放");
                player[36 + 84] = 100;
                Check(player.AsSpan().SequenceEqual(normalPlayer), "全量Player只改变已证虚弱字节，其余布局不变");
                Check(ReferenceEquals(character.Inventory, originalInventory) && character.Inventory.StaminaUntil == until,
                    "构包和反复刷新不修改原期限或库存快照");
            }
        }
        Check(normalPlayer[36 + 84] == 100, "无期限仍投影100%");
        Console.WriteLine("PASS Player真实虚弱、同档刷新、到期与89B独立字节回归");

        string[] strings = ["[initial value]", "[HP MAX]", "[dark resistance]", "[growtype 1]",
            "[physical attack]", "[growtype 2]", "[awakening 1]", "[skill]", "[/skill]"];
        PvfToken[] tokens =
        [
            new(3, 0), new(3, 1), new(2, BitConverter.SingleToInt32Bits(180)),
            new(3, 2), new(0, -20), new(3, 3), new(3, 4), new(2, unchecked((int)0x4099999A)),
            new(3, 7), new(0, 179), new(0, 7), new(3, 8),
            new(3, 5), new(3, 1), new(0, 50), new(3, 6), new(3, 1), new(0, 55)
        ];
        var definition = Parse(tokens);
        Check(definition.Blocks.Count == 4 && definition.Blocks[0].ResourceGrowType == 0 &&
            definition.Blocks[1].ResourceGrowType == 1 && definition.Blocks[2].ResourceGrowType == 2 &&
            definition.Blocks[3].ResourceGrowType == 2 && definition.Blocks[3].Awakening == 1,
            "资源块编号与觉醒父块保留");
        Check(definition.Blocks[0].Fields[CharacterAttributeField.DarkResistance] == -20 &&
            !definition.Blocks[0].Fields.ContainsKey(CharacterAttributeField.FireResistance), "负声明与缺项分开");
        Check(BitConverter.SingleToInt32Bits(definition.Blocks[1].Fields[CharacterAttributeField.PhysicalAttack]) == 0x4099999A,
            "float32成长声明位精度不走两位小数文本");
        tokens[2] = new(0, 999);
        Check(definition.Blocks[0].Fields[CharacterAttributeField.MaxHp] == 180, "声明快照不借用输入数组");
        Check(CharacterStatComputer.ComputeLevelBaseStats(definition, 1, 18).MaxHp == 1800 &&
            CharacterStatComputer.ComputeLevelBaseStats(definition, 1, 19).MaxHp == 2300 &&
            CharacterStatComputer.ComputeLevelBaseStats(definition, 1, 50).MaxHp == 17800 &&
            CharacterStatComputer.ComputeLevelBaseStats(definition, 0x11, 50).MaxHp == 17800,
            "匹配客户端正常基础18级分段、转职块与觉醒声明独立，不套旧版50级再分段");
        Check(CharacterStatComputer.ComputeLevelBaseStats(definition, 0, 19).PhysicalAttack == 864,
            "float32声明先转十倍定点再按等级累计，不丢失4.8精度");
        RejectRange(() => CharacterStatComputer.ComputeLevelBaseStats(definition, 0, 0));
        RejectRange(() => CharacterStatComputer.ComputeLevelBaseStats(definition, 0, 96));
        RejectRange(() => CharacterStatComputer.ComputeLevelBaseStats(definition, 0x30, 95));
        Reject(() => Parse([new(3, 0), new(3, 1), new(0, 1), new(0, 2)]));
        Reject(() => Parse([new(3, 0), new(3, 1), new(2, unchecked((int)0x7FC00000))]));
        Reject(() => Parse([new(3, 0), new(3, 1), new(0, 1), new(3, 1), new(0, 2)]));
        Reject(() => Parse([new(3, 0), new(3, 3), new(3, 3)]));

        Console.WriteLine("PASS 属性资源分块、float32、负值与缺项回归");

        CharacterAttributeDefinition Parse(PvfToken[] input) => CharacterAttributeDefinition.Parse("fixture.chr", input, id => strings[id]);
    }

    public static void RunResources(CharacterCatalog characters)
    {
        var computer = new CharacterStatComputer(characters);
        for (byte job = 0; job <= 15; job++)
        {
            var profile = characters.Get(job);
            Check(characters.GetAttributeDefinition(job).Blocks.Count >= 2 &&
                computer.ComputeLevelBaseStats(job, 0, 1) == profile.InitialStats,
                "全部16职业装载原始CHR，Lv1字节与现有创建一致");
            foreach (var option in profile.GrowthOptions.GrowTypes)
            for (int awakening = 0; awakening <= option.Awakenings.Count; awakening++)
            foreach (byte level in new byte[] { 1, 2, 18, 19, 49, 50, 74, 75, 95 })
            {
                byte growType = checked((byte)(option.Value | awakening << 4));
                var stats = computer.ComputeLevelBaseStats(job, growType, level);
                var character = OrdinaryCharacter(profile.Skills, level) with { Job = job, GrowType = growType };
                Check(computer.Compute(character) == stats &&
                    (level == 1 || stats != profile.InitialStats),
                    "全部职业、转职和两觉在等级边界均使用本职业成长，不回退InitialStats");
            }
        }
        var priest = computer.ComputeLevelBaseStats(14, 1, 19);
        Check(priest.MaxHp == 10850 && priest.MaxMp == 4950 && priest.PhysicalAttack == 899,
            "女圣职者真实CHR在18级后取转职块，四维与HP/MP保持各自声明");
        Check(computer.ComputeLevelBaseStats(15, 0x11, 95).MaxHp == 50850 &&
            computer.ComputeLevelBaseStats(15, 0x11, 95).MaxMp == 18150,
            "枪剑士觉醒编码沿匹配正常基础公式，未将觉醒差异块当永久属性重复叠加");
        var blocks = characters.GetAttributeDefinition(0).Blocks;
        var initial = blocks.Single(block => block.ResourceGrowType == 0);
        var growth = blocks.Single(block => block.ResourceGrowType == 1 && block.Awakening == 0);
        Check(initial.Fields[CharacterAttributeField.MaxHp] == 180 && initial.Fields[CharacterAttributeField.MaxMp] == 140 &&
            initial.Fields[CharacterAttributeField.DarkResistance] == 20 && initial.Fields[CharacterAttributeField.LightResistance] == -20,
            "job0匹配初始资源");
        Check(growth.Fields[CharacterAttributeField.MaxHp] == 45 && growth.Fields[CharacterAttributeField.MaxMp] == 25 &&
            BitConverter.SingleToInt32Bits(growth.Fields[CharacterAttributeField.PhysicalAttack]) == 0x4099999A,
            "job0普通成长资源声明");
        var level1 = computer.ComputeLevelBaseStats(0, 0, 1);
        var level2 = computer.ComputeLevelBaseStats(0, 0, 2);
        var level3 = computer.ComputeLevelBaseStats(0, 0, 3);
        var upgraded = OrdinaryCharacter(characters.Get(0).Skills, 2) with
        { Learning = OrdinaryCharacter(characters.Get(0).Skills, 2).Learning with { Experience = 1000 } };
        Check(computer.Compute(upgraded) == level2, "新Ordinary实际owner的生产基础投影合同");
        var upgradedPlayer = DfoServer.Network.Builders.TownPackets.Player(upgraded, computer.Compute(upgraded));
        var unknownOwner = upgraded with { DungeonState = null };
        var unknownOwnerPlayer = DfoServer.Network.Builders.TownPackets.Player(unknownOwner, computer.Compute(unknownOwner));
        Check(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(upgradedPlayer.AsSpan(40)) == 1650 &&
            System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(upgradedPlayer.AsSpan(44)) == 123 &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(unknownOwnerPlayer.AsSpan(40)) == 1400,
            "既有Player通知按实际Lv2传已证CHR基础，未知存量owner保留旧字段，不重复加入装备/被动总量");
        Check(computer.Compute(unknownOwner) == level1 &&
            computer.Compute(upgraded with { Learning = CharacterLearningState.Uninitialized }) == level1,
            "未知存量附属owner与不完整两树保持旧投影");
        Check(level1 == characters.Get(0).InitialStats, "Lv1基础投影保留原匹配字节");
        Check(level2.MaxHp == 2250 && level2.MaxMp == 1650 && level2.PhysicalAttack == 123 &&
            level2.MagicalAttack == 87 && level2.InventoryLimit == 483000 && level2.MpRecovery == 525 &&
            level2.HitRecovery == 6015, "Lv2按已证定点整数先转换后累计");
        Check(level3.MaxHp == 2700 && level3.MaxMp == 1900 && level3.PhysicalAttack == 171 &&
            level3.DarkResistance == 200 && level3.LightResistance == -200, "Lv3成长及抗性默认保持");
        Check(computer.ComputeLevelBaseStats(0, 0, 19).PhysicalAttack == 75 + 48 * 18 &&
            computer.ComputeLevelBaseStats(0, 0, 95).MaxMp == 1400 + 250 * 94, "grow0跨18分段与95上界整数一致");
        Console.WriteLine("PASS 全16职业CHR等级基础资源回归");
    }

    private static void Check(bool result, string message)
    {
        if (!result)
            throw new InvalidOperationException("FAIL 属性资源: " + message);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("FAIL 属性资源未拒绝非法声明");
    }

    private static void RejectRange(Action action)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new InvalidOperationException("FAIL 属性切片未拒绝未知范围");
    }


    private static Character OrdinaryCharacter(CharacterSkill[] skills, byte level) => new(1, 0, "属性", 4, new Location(0, 0, 0, 0, 0), null)
    {
        Level = level, Skills = skills.ToArray(), DungeonState = CharacterDungeonState.NewOrdinary,
        Learning = new(0, Enumerable.Range(0, 2).Select(context => new CharacterSkillTree((byte)context, 0, 0,
            skills.Select((skill, index) => new LearnedSkill((byte)index, skill.Id, skill.Level)).ToImmutableArray())).ToImmutableArray())
    };
}
