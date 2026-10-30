using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class ChatEmoticonFileTests
{
    public static void Run()
    {
        string[] strings = ["[type]", "COMMON", "LEVEL", "[ani]"];
        string Resolve(int value) => strings[value];
        PvfToken[] common = [new(3, 0), new(6, 1), new(0, 0)];
        PvfToken[] level = [new(3, 0), new(6, 2), new(0, 10), new(3, 3), new(6, 1)];
        That(ChatEmoticonFile.Read(common, Resolve) == new ChatEmoticonDefinition("COMMON", 0), "聊天表情普通类型");
        That(ChatEmoticonFile.Read(level, Resolve) == new ChatEmoticonDefinition("LEVEL", 10), "聊天表情等级类型");
        Reject<InvalidDataException>(() => ChatEmoticonFile.Read(common[..2], Resolve));
        Reject<InvalidDataException>(() => ChatEmoticonFile.Read([.. common, .. common], Resolve));
    }
}
