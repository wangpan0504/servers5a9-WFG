using PvfLib.Scripting;

namespace PvfLib;

public readonly record struct ChatEmoticonDefinition(string Type, int Requirement);

public static class ChatEmoticonFile
{
    public static ChatEmoticonDefinition Read(PvfToken[] tokens, Func<int, string> resolve)
    {
        var fields = new PvfTokenFields(tokens, resolve, ["type"]);
        if (!fields.TryGet("type", out var values) || values is not [{ Type: 6 } type, { Type: 0 } requirement])
            throw new InvalidDataException("聊天表情类型声明无效");
        return new(resolve(type.Value), requirement.Value);
    }
}
