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
        byte[] weakPlayer = DfoServer.Network.Builders.TownPackets.Player(weak, baseStats, 1);
        Check(weakPlayer[36 + 84] == 10,
            $"全量Player保留真实10%虚弱，实际={weakPlayer[36 + 84]}");
        byte[] leveledPlayer = DfoServer.Network.Builders.TownPackets.Player(weak, baseStats, 37);
        Check(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(leveledPlayer.AsSpan(leveledPlayer.Length - 11)) == 37 &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(leveledPlayer.AsSpan(leveledPlayer.Length - 7)) == 0,
            "Player尾部tail[10..13]写冒险团等级供02B644B0安装，tail[14..17]胶囊计量保持0");
        var now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        byte[] expectedStats = Convert.FromHexString(
            "662100001E1400007B002D0043005900F6FF1400E2FF28000000000000000000000000000000000000000000000000000000000000000000000000000053070000000D02200300006400640070176400580200006400000000");
        byte[] normalPlayer = DfoServer.Network.Builders.TownPackets.Player(
            weak with { Inventory = weak.Inventory with { StaminaUntil = 0 } }, baseStats, 1, utcNow: now);
        var diamond = weak with { Inventory = weak.Inventory with { StaminaUntil = now.ToUnixTimeSeconds() + 600,
            AccountWallet = weak.Inventory.AccountWallet.SetContractExpiry(56, now.ToUnixTimeSeconds() + 1) } };
        byte[] diamondPlayer = DfoServer.Network.Builders.TownPackets.Player(diamond, baseStats, 1, utcNow: now);
        Check(diamondPlayer.AsSpan(36, 89).SequenceEqual(expectedStats) && diamond.Inventory.StaminaUntil > 0,
            "有效黑钻的全量Player以100%投影存量误罚，不在只读构包中改写期限或缩放属性");
        Check(DfoServer.Network.Builders.TownPackets.Player(diamond, baseStats, 1, utcNow: now.AddSeconds(1))[36 + 84] == 10,
            "黑钻到期端点不能继续掩盖未经用例修复的存量期限");
        foreach (var (seconds, expected) in new (long, byte)[]
            { (600, 10), (541, 10), (540, 19), (481, 19), (1, 91), (0, 100), (-1, 100) })
        {
            long until = now.ToUnixTimeSeconds() + seconds;
            var character = weak with { Inventory = weak.Inventory with { StaminaUntil = until } };
            var originalInventory = character.Inventory;
            for (int refresh = 0; refresh < 3; refresh++)
            {
                byte[] player = DfoServer.Network.Builders.TownPackets.Player(character, baseStats, 1, utcNow: now);
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

        string[] addStrings = ["[max level]", "[add initial value stat]", "[add growtype stat]",
            "[HP MAX]", "[MP MAX]", "[physical attack]", "[physical defense]", "[magical attack]", "[magical defense]",
            "[dark resistance]", "[light resistance]", "[fire resistance]", "[water resistance]", "[move speed]", "[unknown]",
            "[/add initial value stat]", "[/add growtype stat]"];
        PvfToken[] addTokens =
        [
            new(3, 0), new(0, 70),
            new(3, 1), new(3, 3), new(0, 14), new(3, 4), new(0, 15),
            new(3, 5), new(2, unchecked((int)0x40066666)), new(3, 6), new(0, 2),
            new(3, 7), new(2, unchecked((int)0x40066666)), new(3, 8), new(0, 2),
            new(3, 9), new(0, 12), new(3, 10), new(0, 12), new(3, 11), new(0, 12), new(3, 12), new(0, 12),
            new(3, 13), new(0, 20), new(3, 15),
            new(3, 2), new(3, 3), new(0, 14), new(3, 4), new(0, 15),
            new(3, 5), new(2, unchecked((int)0x40066666)), new(3, 6), new(0, 2),
            new(3, 7), new(2, unchecked((int)0x40066666)), new(3, 8), new(0, 2), new(3, 16)
        ];
        var additional = ParseAdditional(addTokens);
        Check(additional.MaxLevel == 70 && additional.InitialFields.Count == 11 && additional.GrowthFields.Count == 6 &&
            BitConverter.SingleToInt32Bits(additional.InitialFields[CharacterAttributeField.PhysicalAttack]) == 0x40066666 &&
            BitConverter.SingleToInt32Bits(additional.GrowthFields[CharacterAttributeField.MagicalAttack]) == 0x40066666,
            "chn_add_stat.etc解析保留70级上限与2.1f位精度");
        Check(CharacterStatComputer.ComputeLevelBaseStats(definition, 0, 1, additional).PhysicalAttack == 21 &&
            CharacterStatComputer.ComputeLevelBaseStats(definition, 0, 19, additional).PhysicalAttack == 864 + 21 * 19 &&
            CharacterStatComputer.ComputeLevelBaseStats(definition, 1, 70, additional).MaxHp == 1800 + 500 * 52 + 140 * 70 &&
            CharacterStatComputer.ComputeLevelBaseStats(definition, 1, 71, additional).MaxHp == 1800 + 500 * 53 + 140 * 70 &&
            CharacterStatComputer.ComputeLevelBaseStats(definition, 1, 95, additional).PhysicalAttack == 48 * 17 + 1470 &&
            CharacterStatComputer.ComputeLevelBaseStats(definition, 1, 95, additional).DarkResistance == -80 &&
            CharacterStatComputer.ComputeLevelBaseStats(definition, 1, 95, additional).MoveSpeed == 200,
            "额外初始属性与1..70级额外成长属性在70级封顶且不覆盖71..95级CHR正常成长");
        RejectRange(() => CharacterStatComputer.ComputeLevelBaseStats(definition, 0, 1, additional with { MaxLevel = 0 }));
        RejectRange(() => CharacterStatComputer.ComputeLevelBaseStats(definition, 0, 1, additional with { MaxLevel = 96 }));
        Reject(() => ParseAdditional([new(3, 1), new(3, 3), new(0, 14), new(3, 2), new(3, 3), new(0, 14)]));
        Reject(() => ParseAdditional([new(3, 0), new(0, 0), new(3, 1), new(3, 3), new(0, 14), new(3, 2), new(3, 3), new(0, 14)]));
        Reject(() => ParseAdditional([new(3, 0), new(0, 70), new(3, 1), new(3, 3), new(0, 14), new(3, 1), new(3, 3), new(0, 14), new(3, 2), new(3, 3), new(0, 14)]));
        Reject(() => ParseAdditional([new(3, 0), new(0, 70), new(3, 1), new(3, 3), new(0, 14), new(3, 3), new(0, 14), new(3, 2), new(3, 3), new(0, 14)]));
        Reject(() => ParseAdditional([new(3, 0), new(0, 70), new(3, 14), new(0, 1), new(3, 1), new(3, 3), new(0, 14), new(3, 2), new(3, 3), new(0, 14)]));

        Console.WriteLine("PASS 属性资源分块、chn_add_stat.etc、float32、负值与缺项回归");

        CharacterAttributeDefinition Parse(PvfToken[] input) => CharacterAttributeDefinition.Parse("fixture.chr", input, id => strings[id]);
        CharacterAdditionalStatDefinition ParseAdditional(PvfToken[] input) => CharacterAdditionalStatDefinition.Parse("character/chn_add_stat.etc", input, id => addStrings[id]);
    }

    public static void RunResources(CharacterCatalog characters)
    {
        var additional = characters.AdditionalStats;
        Check(additional.ScriptPath == "character/chn_add_stat.etc" && additional.MaxLevel == 70 &&
            additional.InitialFields.Count == 11 && additional.GrowthFields.Count == 6 &&
            additional.InitialFields[CharacterAttributeField.MaxHp] == 14 &&
            additional.InitialFields[CharacterAttributeField.MaxMp] == 15 &&
            BitConverter.SingleToInt32Bits(additional.InitialFields[CharacterAttributeField.PhysicalAttack]) == 0x40066666 &&
            additional.InitialFields[CharacterAttributeField.PhysicalDefense] == 2 &&
            BitConverter.SingleToInt32Bits(additional.InitialFields[CharacterAttributeField.MagicalAttack]) == 0x40066666 &&
            additional.InitialFields[CharacterAttributeField.MagicalDefense] == 2 &&
            additional.InitialFields[CharacterAttributeField.FireResistance] == 12 &&
            additional.InitialFields[CharacterAttributeField.WaterResistance] == 12 &&
            additional.InitialFields[CharacterAttributeField.DarkResistance] == 12 &&
            additional.InitialFields[CharacterAttributeField.LightResistance] == 12 &&
            additional.InitialFields[CharacterAttributeField.MoveSpeed] == 20 &&
            additional.GrowthFields[CharacterAttributeField.MaxHp] == 14 &&
            additional.GrowthFields[CharacterAttributeField.MaxMp] == 15 &&
            BitConverter.SingleToInt32Bits(additional.GrowthFields[CharacterAttributeField.PhysicalAttack]) == 0x40066666 &&
            additional.GrowthFields[CharacterAttributeField.PhysicalDefense] == 2 &&
            BitConverter.SingleToInt32Bits(additional.GrowthFields[CharacterAttributeField.MagicalAttack]) == 0x40066666 &&
            additional.GrowthFields[CharacterAttributeField.MagicalDefense] == 2,
            "真实character/chn_add_stat.etc装载70级上限、11项额外初始属性与6项额外成长属性");
        var computer = new CharacterStatComputer(characters);
        for (byte job = 0; job <= 15; job++)
        {
            var profile = characters.Get(job);
            var definition = characters.GetAttributeDefinition(job);
            Check(definition.Blocks.Count >= 2 &&
                CharacterStatComputer.ComputeLevelBaseStats(definition, 0, 1) == profile.InitialStats,
                "全部16职业装载原始CHR，未叠加chn_add_stat.etc时Lv1与InitialStats一致");
            foreach (var option in profile.GrowthOptions.GrowTypes)
            for (int awakening = 0; awakening <= option.Awakenings.Count; awakening++)
            foreach (byte level in new byte[] { 1, 2, 18, 19, 49, 50, 70, 71, 74, 75, 95 })
            {
                byte growType = checked((byte)(option.Value | awakening << 4));
                var chrOnly = CharacterStatComputer.ComputeLevelBaseStats(definition, growType, level);
                var stats = computer.ComputeLevelBaseStats(job, growType, level);
                int cappedLevel = Math.Min((int)level, 70);
                var expected = chrOnly with
                {
                    MaxHp = checked(chrOnly.MaxHp + (uint)(140 * cappedLevel)),
                    MaxMp = checked(chrOnly.MaxMp + (uint)(150 * cappedLevel)),
                    PhysicalAttack = checked((short)(chrOnly.PhysicalAttack + 21 * cappedLevel)),
                    PhysicalDefense = checked((short)(chrOnly.PhysicalDefense + 20 * cappedLevel)),
                    MagicalAttack = checked((short)(chrOnly.MagicalAttack + 21 * cappedLevel)),
                    MagicalDefense = checked((short)(chrOnly.MagicalDefense + 20 * cappedLevel)),
                    FireResistance = checked((short)(chrOnly.FireResistance + 120)),
                    WaterResistance = checked((short)(chrOnly.WaterResistance + 120)),
                    DarkResistance = checked((short)(chrOnly.DarkResistance + 120)),
                    LightResistance = checked((short)(chrOnly.LightResistance + 120)),
                    MoveSpeed = checked(chrOnly.MoveSpeed + 200)
                };
                var character = OrdinaryCharacter(profile.Skills, level) with { Job = job, GrowType = growType };
                Check(stats == expected && computer.Compute(character) == stats && stats != profile.InitialStats,
                    "全部16职业、转职和两觉在等级边界均合成CHR与chn_add_stat.etc，不回退InitialStats");
            }
        }
        var priest = computer.ComputeLevelBaseStats(14, 1, 19);
        Check(priest.MaxHp == 10850 + 140 * 19 && priest.MaxMp == 4950 + 150 * 19 && priest.PhysicalAttack == 899 + 21 * 19,
            "女圣职者真实CHR在18级后取转职块并叠加chn_add_stat.etc，四维与HP/MP保持各自声明");
        var gunblader = computer.ComputeLevelBaseStats(15, 0x11, 95);
        Check(gunblader.MaxHp == 50850 + 9800 && gunblader.MaxMp == 18150 + 10500 &&
            gunblader.PhysicalAttack == 5087 + 1470 && gunblader.MagicalAttack == 3493 + 1470,
            "枪剑士觉醒编码沿匹配正常基础公式并叠加70级封顶chn_add_stat.etc（+980HP/+1050MP/+147力智），未重复叠加觉醒差异块");
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
        var upgradedPlayer = DfoServer.Network.Builders.TownPackets.Player(upgraded, computer.Compute(upgraded), 1);
        var unknownOwner = upgraded with { DungeonState = null };
        var unknownOwnerPlayer = DfoServer.Network.Builders.TownPackets.Player(unknownOwner, computer.Compute(unknownOwner), 1);
        Check(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(upgradedPlayer.AsSpan(40)) == 1950 &&
            System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(upgradedPlayer.AsSpan(44)) == 165 &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(unknownOwnerPlayer.AsSpan(40)) == 1400,
            "既有Player通知按实际Lv2传已证CHR+chn_add_stat.etc基础，未知存量owner保留旧字段，不重复加入装备/被动总量");
        Check(computer.Compute(unknownOwner) == characters.Get(0).InitialStats &&
            computer.Compute(upgraded with { Learning = CharacterLearningState.Uninitialized }) == characters.Get(0).InitialStats,
            "未知存量附属owner与不完整两树保持旧InitialStats投影");
        Check(level1.MaxHp == 1940 && level1.MaxMp == 1550 && level1.PhysicalAttack == 96 && level1.PhysicalDefense == 95 &&
            level1.MagicalAttack == 66 && level1.MagicalDefense == 65 && level1.FireResistance == 120 && level1.WaterResistance == 120 &&
            level1.DarkResistance == 320 && level1.LightResistance == -80 && level1.MoveSpeed == 8700,
            "Lv1基础投影叠加chn_add_stat.etc额外初始属性");
        Check(level2.MaxHp == 2530 && level2.MaxMp == 1950 && level2.PhysicalAttack == 165 &&
            level2.MagicalAttack == 129 && level2.InventoryLimit == 483000 && level2.MpRecovery == 525 &&
            level2.HitRecovery == 6015 && level2.MoveSpeed == 8700, "Lv2按已证定点整数先转换后累计CHR与额外成长");
        Check(level3.MaxHp == 3120 && level3.MaxMp == 2350 && level3.PhysicalAttack == 234 &&
            level3.DarkResistance == 320 && level3.LightResistance == -80, "Lv3成长及抗性额外初始值保持");
        Check(computer.ComputeLevelBaseStats(0, 0, 19).PhysicalAttack == 75 + 48 * 18 + 21 * 19 &&
            computer.ComputeLevelBaseStats(0, 0, 70).PhysicalAttack == 75 + 48 * 69 + 21 * 70 &&
            computer.ComputeLevelBaseStats(0, 0, 71).PhysicalAttack == 75 + 48 * 70 + 21 * 70 &&
            computer.ComputeLevelBaseStats(0, 0, 95).MaxMp == 1400 + 250 * 94 + 150 * 70,
            "grow0跨18分段、70级chn_add_stat.etc封顶与95上界整数一致");
        Console.WriteLine("PASS 全16职业CHR与chn_add_stat.etc等级基础资源回归");
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
