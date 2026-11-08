using System.Collections.Immutable;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 魔界大战螺旋王座领主（黑眼之夏勒·弗兹）专属地面掉落计划。
internal sealed record EvildomWarSarpozaDropPlan(byte Mode, (byte Job, byte Grow, byte Level) Profession, ItemCatalog Items)
{
    public const int UnstableTalismanFragmentId = 490014022;
    public const int RandomRuneBoxId = 10310784;
    public const int ShiningRuneRoughStoneId = 10310787;

    public int MaximumObjects => Mode switch
    {
        2 => 14,
        0 => 22,
        1 => 27,
        _ => throw new InvalidDataException("无效魔界大战模式")
    };

    public ImmutableArray<DungeonGroundContent> Generate(Func<int, int> sample, DateTimeOffset now)
    {
        var result = ImmutableArray.CreateBuilder<DungeonGroundContent>(MaximumObjects);
        var fragmentTemplate = Items.Get(UnstableTalismanFragmentId);
        result.Add(new(DungeonDropCreation.Create(fragmentTemplate, 1, sample, now)));

        int runeCount;
        int shiningRoughStoneCount = 0;
        switch (Mode)
        {
            case 2:
                runeCount = sample(2) == 0 ? 12 : 13;
                break;
            case 0:
                runeCount = 21;
                break;
            case 1:
                switch (sample(4))
                {
                    case 0:
                        runeCount = 25;
                        break;
                    case 1:
                        runeCount = 26;
                        break;
                    case 2:
                        runeCount = 21;
                        shiningRoughStoneCount = 4;
                        break;
                    default:
                        runeCount = 21;
                        shiningRoughStoneCount = 5;
                        break;
                }
                break;
            default:
                throw new InvalidDataException("无效魔界大战模式");
        }

        if (Items.RuneRandomChoices().TryGet(0, 0, out var choice))
        {
            for (int i = 0; i < runeCount; i++)
            {
                var rune = RuneBoxSelection.Select(Items, Profession.Job, Profession.Grow, Profession.Level, ItemKind.Rune, choice, sample);
                if (rune is not null)
                    result.Add(new(DungeonDropCreation.Create(rune, 1, sample, now)));
            }
        }

        if (shiningRoughStoneCount > 0)
        {
            var roughStoneTemplate = Items.Get(ShiningRuneRoughStoneId);
            result.Add(new(DungeonDropCreation.Create(roughStoneTemplate, shiningRoughStoneCount, sample, now)));
        }

        return result.ToImmutable();
    }
}

internal static class EvildomWarMonsterDropPolicy
{
    public static bool IsThroneBoss(DungeonEntryPlan entry, DungeonPlannedMonster monster) =>
        entry.EvildomWarMode is not null &&
        monster.IsBoss &&
        entry.DungeonId is 5416 or 5422 or 5436 &&
        monster.MonsterId is 109008635 or 109010196;

    public static DungeonDropGenerationPlan PrepareBoss(DungeonEntryPlan entry, DungeonPlannedMonster monster, ItemCatalog items)
    {
        if (!IsThroneBoss(entry, monster) || entry.EvildomWarMode is not { } mode || entry.EvildomWarProfession is not { } profession)
            throw new InvalidDataException("魔界大战领主掉落仅适用螺旋王座黑眼之夏勒·弗兹");
        if (!items.TryGet(EvildomWarSarpozaDropPlan.UnstableTalismanFragmentId, out _) ||
            !items.TryGet(EvildomWarSarpozaDropPlan.ShiningRuneRoughStoneId, out _) ||
            !items.RuneRandomChoices().TryGet(0, 0, out _))
            throw new InvalidDataException("魔界大战领主掉落物品资源缺失");
        return new($"evildom-war:sarpoza:mode={mode}:dungeon={entry.DungeonId}:mob={monster.MonsterId}",
            [], sarpoza: new(mode, profession, items));
    }
}
