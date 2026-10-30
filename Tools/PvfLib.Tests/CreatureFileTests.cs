using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class CreatureFileTests
{
    public static void Run()
    {
        string[] strings = ["[max level]", "[selective grow creature]", "[using exp item]", "[grow Atype item index]",
            "[grow Btype item index]", "[grow Atype creature id]", "[grow Btype creature id]", "[grow level]",
            "[item count for levelup]", "[/selective grow creature]", "[artifact slot]", "red", "blue", "[/artifact slot]"];
        string Resolve(int at) => strings[at];
        PvfToken[] tokens = [new(3, 0), new(0, 50), new(3, 1), new(3, 2), new(0, 1),
            new(3, 3), new(0, 10093073), new(3, 4), new(0, 10093074), new(3, 5), new(0, 345), new(3, 6), new(0, 346),
            new(3, 7), new(0, 2), new(3, 8), new(0, 30), new(3, 9),
            new(3, 10), new(6, 11), new(6, 12), new(3, 13)];
        var result = CreatureFile.Parse(tokens, Resolve);
        That(result.MaximumLevel == 50 && result.ItemsPerLevel == 30 && result.SelectiveGrowth is
            { UsingExperienceItem: 1, ItemA: 10093073, ItemB: 10093074, SpeciesA: 345, SpeciesB: 346, SelectLevel: 2 } &&
            result.ArtifactSlots.SequenceEqual(new[] { "red", "blue" }), "CRE嵌套成长区保留真实物品、种类和等级除数，装备槽独立读取");
        var plain = CreatureFile.Parse([new(3, 0), new(0, 1)], Resolve);
        string NameResolve(int at) => at == 0 ? "[name]" : "波比";
        That(CreatureFile.Parse([new(3, 0), new(6, 1)], NameResolve).Name == "波比" && plain.Name is null,
            "CRE原始名称完整解析，缺字段保持缺失");
        Reject<InvalidDataException>(() => CreatureFile.Parse([new(3, 0), new(0, 1)], NameResolve));
        Reject<InvalidDataException>(() => CreatureFile.Parse([new(3, 0), new(6, 1), new(3, 0), new(6, 1)], NameResolve));
        That(plain.SelectiveGrowth is null && plain.StartLevel is null && plain.ItemsPerLevel is null && plain.MaximumLevel == 1,
            "缺失原始字段保持缺失，不在解析器中生成玩法默认值");
        Reject<InvalidDataException>(() => CreatureFile.Parse(tokens.Concat(new PvfToken[] { new(3, 0), new(0, 100) }).ToArray(), Resolve));
        Reject<InvalidDataException>(() => CreatureFile.Parse([new(3, 0), new(6, 11)], Resolve));
        GroupDeclarations();
    }

    static void GroupDeclarations()
    {
        string[] names = ["[creature growth group]", "[convert type]", "[convert info]", "[/convert info]",
            "[/convert type]", "[/creature growth group]", "bad"];
        string Resolve(int value) => names[value];
        PvfToken[] tokens = [new(3,0),new(0,1),new(3,1),new(0,4),new(0,2683443),
            new(3,2),new(0,1),new(0,100),new(0,200),new(3,3),
            new(3,2),new(0,2),new(0,300),new(0,400),new(3,3),new(3,4),new(3,5)];
        var file = CreatureGroupFile.Parse(tokens, Resolve);
        That(file.Groups is [{ Id: 1, Conversions: [{ Type: 4, ItemId: 2683443 }] }] &&
            file.Groups[0].Conversions[0].Rows[0].SequenceEqual(new[] { 1,100,200 }) &&
            file.Groups[0].Conversions[0].Rows[1].SequenceEqual(new[] { 2,300,400 }),
            "creature group retains repeated conversion rows and their source order");
        That(CreatureGroupFile.Parse([], Resolve).Groups.IsEmpty, "empty optional creature group remains empty");
        Reject<InvalidDataException>(() => CreatureGroupFile.Parse(tokens[..^1], Resolve));
        Reject<InvalidDataException>(() => CreatureGroupFile.Parse([new(3,0),new(6,6),new(3,5)], Resolve));
        var malformed = tokens.ToArray(); malformed[8] = new(6,6);
        Reject<InvalidDataException>(() => CreatureGroupFile.Parse(malformed, Resolve));
    }
}
