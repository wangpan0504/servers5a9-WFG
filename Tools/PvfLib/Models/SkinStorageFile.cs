using System.Collections.Immutable;
using PvfLib.Scripting;

namespace PvfLib;

public sealed record SkinStorageAction(int SkinId, string Target, string ExpirationMode, int Days,
    string Date, ImmutableArray<string> Places)
{
    public ImmutableArray<int> SkinIds { get; init; } = [SkinId];
}

public static class SkinStorageFile
{
    public static string ReadType(PvfToken[] tokens, Func<int, string> resolve)
    {
        var fields = new PvfTokenFields(tokens, resolve, ["type"]);
        if (!fields.TryGet("type", out var values) || values is not [{ Type: 6 } type])
            throw new InvalidDataException("皮肤资源缺少唯一类型");
        return resolve(type.Value);
    }

    public static SkinStorageAction? ReadAction(int id, string path, PvfToken[] tokens, Func<int, string> resolve)
    {
        var item = StackableFile.Parse(id, path, tokens, resolve);
        return ReadAction(item, tokens, resolve);
    }

    public static SkinStorageAction? ReadAction(StackableFile item, PvfToken[] tokens, Func<int, string> resolve)
    {
        if (item.TypeDeclaration is not { Label: "[waste]" } ||
            item.ActionDeclaration is not { HasIntegerArguments: true, Label: "[add skin storage]", Parameters.Count: > 0 } action ||
            action.Parameters.Any(id => id <= 0))
            return null;
        var fields = new PvfTokenFields(tokens, resolve, ["action target", "action expiration info"]);
        if (!fields.TryGet("action target", out var target) || target is not [{ Type: 6 } targetToken] ||
            !fields.TryGet("action expiration info", out var expiry) || expiry.Length == 0 || expiry[0].Type != 6)
            throw new InvalidDataException("入库动作缺少归属或期限声明");
        string mode = resolve(expiry[0].Value);
        int days = 0;
        string date = "";
        switch (mode)
        {
            case "[unlimit]" when expiry.Length == 1:
                break;
            case "[period]" when expiry is [_, { Type: 0, Value: > 0 } period]:
                days = period.Value;
                break;
            case "[date]" when expiry is [_, { Type: 6 } value]:
                date = resolve(value.Value);
                break;
            default:
                throw new InvalidDataException("入库动作期限结构无效");
        }
        return new(action.Parameters[0], resolve(targetToken.Value), mode, days, date,
            item.DeclaredActionUsablePlaces?.ToImmutableArray() ?? []) { SkinIds = action.Parameters.ToImmutableArray() };
    }
}
