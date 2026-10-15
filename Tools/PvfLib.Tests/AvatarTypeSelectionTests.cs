namespace PvfLib.Tests;

internal static class AvatarTypeSelectionTests
{
    public static void Run()
    {
        var raw = AvatarTypeSelectionFile.Parse(new("[avatar type select]\n" +
            "7 1 2 200 8 9 0\n30 0 0 440 0 0 0\n0 0 0 1320 0 0 2 `[M socket]` `[A socket]`\n[/avatar type select]"));
        TestCheck.That(raw.Length == 3 && raw[0] is { Days: 7, Gold: 1, WinPoints: 2, Cera: 200, Reserved4: 8, Reserved5: 9 } &&
            raw[2].Sockets.SequenceEqual(new[] { "[M socket]", "[A socket]" }), "七整数与变长孔位完整保留，隐式类型由业务目录补入");
        TestCheck.That(AvatarTypeSelectionFile.Parse(new("[name]\n`[avatar type select]只是说明`" )).IsEmpty,
            "没有类型声明的普通装备不产生装扮期限");
        foreach (string row in new[] { "", "7 0 0 1 0 0", "7 0 0 1.5 0 0 0", "-1 0 0 1 0 0 0",
            "7 0 0 1 0 0 6", "7 0 0 1 0 0 1", "7 0 0 1 0 0 1 nope" })
            TestCheck.Reject<InvalidDataException>(() => AvatarTypeSelectionFile.Parse(new("[avatar type select]\n" + row + "\n[/avatar type select]")));
        TestCheck.Reject<InvalidDataException>(() => AvatarTypeSelectionFile.Parse(new("[avatar type select]\n7 0 0 1 0 0 0")));
        TestCheck.Reject<InvalidDataException>(() => AvatarTypeSelectionFile.Parse(new("[avatar type select]\n7 0 0 1 0 0 0\n" +
            "[/avatar type select]\n[avatar type select]\n0 0 0 1 0 0 0\n[/avatar type select]")));
        for (int type = 1; type <= 9; type++)
            TestCheck.That(AvatarTypeSelectionFile.GiftType(new("[stackable type]\n`[avatar gift]` 0\n[avatar gift type]\n" + type)) == type,
                "兑换券按资源类型辨认，不依赖物品编号");
        TestCheck.That(AvatarTypeSelectionFile.GiftType(new("[stackable type]\n`[etc]` 0\n[avatar gift type]\n6")) is null,
            "普通STK不能仅靠同名整数冒充兑换券");
        foreach (string value in new[] { "", "-1", "1 2", "`6`" })
            TestCheck.Reject<InvalidDataException>(() => AvatarTypeSelectionFile.GiftType(new("[stackable type]\n`[avatar gift]` 0\n[avatar gift type]\n" + value)));
    }
}
