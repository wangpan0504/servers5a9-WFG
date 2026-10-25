using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class PvpPrivateCharacterFileTests
{
    public static void Run()
    {
        string[] names = ["[growtype 1]", "[growtype 2]", "[growtype 3]",
            "[pvp private create equipment list]", "[/pvp private create equipment list]",
            "[weapon]", "[pvp private recreate penaly period]"];
        string Resolve(int value) => names[value];
        PvfToken[] tokens = [new(3,0),new(3,1),new(3,3),new(6,5),new(0,123),new(0,1),
            new(6,5),new(0,456),new(0,0),new(3,4),new(3,6),new(0,30),
            new(3,2),new(3,3),new(6,5),new(0,789),new(0,7),new(3,4)];
        var rows = PvpPrivateCharacterFile.Parse(tokens, Resolve);
        That(rows.Length == 2 && rows[0].ResourceGrowType == 2 && rows[0].RecreatePenalty == 30 &&
            rows[0].Equipment.Select(row => row.ItemId).SequenceEqual(new[] { 123,456 }), "决斗创建装备保留重复部位与顺序");
        That(rows[1].ResourceGrowType == 3 && rows[1].RecreatePenalty is null && rows[1].Equipment[0].Flag == 7,
            "缺少惩罚不继承上一转职，未知标记保留而非猜作布尔值");
        That(PvpPrivateCharacterFile.Parse([], Resolve).IsEmpty, "未声明决斗创建资源保持空");
        Reject<InvalidDataException>(() => PvpPrivateCharacterFile.Parse(tokens[..^1], Resolve));
        Reject<InvalidDataException>(() => PvpPrivateCharacterFile.Parse(tokens[2..], Resolve));
        Reject<InvalidDataException>(() => PvpPrivateCharacterFile.Parse(tokens.Concat(new PvfToken[] { new(3,2) }).ToArray(), Resolve));
        var malformed = tokens.ToArray(); malformed[5] = new(6,5);
        Reject<InvalidDataException>(() => PvpPrivateCharacterFile.Parse(malformed, Resolve));
        Reject<InvalidDataException>(() => PvpPrivateCharacterFile.Parse([new(3,1),new(3,6),new(0,30),new(0,40)], Resolve));
    }
}
