using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class StackableActionTests
{
    public static void Run()
    {
        string[] strings = ["[action type]", "[fixture]", "literal 123", "[/action type]"];
        string Resolve(int id) => strings[id];
        var raw = StackableFile.Parse(1, "stackable/fixture.stk", [new(3, 0), new(6, 1),
            new(0, -7), new(2, BitConverter.SingleToInt32Bits(1.25f)), new(6, 2), new(3, 3)], Resolve);
        That(raw is { HasActionType: true, ActionDeclaration: { Label: "[fixture]", HasIntegerArguments: false } } &&
            raw.ActionDeclaration.Arguments.SequenceEqual(new StackableActionArgument[]
                { new(0, -7), new(2, BitConverter.SingleToInt32Bits(1.25f)), new(6, 0, "literal 123") }),
            "动作原声明保留整数、浮点和字符串类型，不因非整数丢失整个动作或提取字符串内数字");
        var text = StackableActionDeclaration.Read(new PvfDocument("[action type]\n`[fixture]` -7 1.25 `literal 123` // comment\n[/action type]"))!;
        That(text.Arguments.SequenceEqual(raw.ActionDeclaration!.Arguments) && !text.HasIntegerArguments,
            "文本与token入口使用相同动作声明，参数类型及次序一致");
        var numeric = StackableActionDeclaration.Read(new PvfDocument("[action type]\n`[fixture]` -7 0 42\n"))!;
        That(numeric.HasIntegerArguments && numeric.Parameters.SequenceEqual(new[] { -7, 0, 42 }),
            "整数参数兼容视图只对完整整数序列开放");
        string[] boosterNames = ["[booster info]","[etc]","[/etc]","[/booster info]"];
        PvfToken[] booster = [new(3,0),new(3,1),new(0,1),new(0,1),new(0,50000),new(0,1),new(0,1),new(0,50000),new(0,10),new(3,2),new(3,3)];
        var table = StackableFile.Parse(2,"random.stk",booster,id=>boosterNames[id]);
        That(table.HasBoosterInfo && table.DeclaredBoosterEtc!.SequenceEqual([1,1,50000,1,1,50000,10]),
            "etc奖池保留抽取次数和完整三元组，等级及权重含义不在库内解释");
        booster[4] = new(2,BitConverter.SingleToInt32Bits(0.5f));
        That(StackableFile.Parse(2,"random.stk",booster,id=>boosterNames[id]) is { HasBoosterInfo:true, DeclaredBoosterEtc:null },
            "浮点或不完整的etc声明不伪装成原始整数奖池");
        var opaque = StackableFile.Parse(1, "stackable/fixture.stk", [new(3, 0), new(6, 1), new(7, 123), new(3, 3)], Resolve);
        That(opaque.ActionDeclaration is { HasIntegerArguments: false } && opaque.ActionDeclaration.Arguments[0] == new StackableActionArgument(7, 123) &&
            opaque.UnsupportedFields.Contains("action type"), "未知token保留原值并诊断，不伪装成无参动作");
        foreach (string invalid in new[] { "[action type]\n1", "[action type]\n`[fixture]` `broken",
            "[action type]\n`[fixture]` 1junk", "[action type]\n`[fixture]` 2147483648",
            "[action type]\n`[fixture]` 1e100", "[action type]\n`[fixture]`\n[action type]\n`[fixture]`" })
            Reject<InvalidDataException>(() => StackableActionDeclaration.Read(new PvfDocument(invalid)));
        That(StackableActionDeclaration.Read(new PvfDocument("[name]\n`ordinary`")) is null,
            "不存在动作声明与空参数的有效动作保持区别");
    }
}
