using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Characters;

internal readonly record struct ChatEmoticonEntry(ushort Id, int Requirement, string Type, int PremiumType = 0);

internal readonly record struct ChatEmoticonEligibility(byte Level, bool Married, int Love, ulong PremiumLow, ulong PremiumHigh, byte PremiumTail)
{
    public bool Premium(int type) => type switch
    {
        > 0 and < 64 => (PremiumLow & (1UL << type)) != 0,
        >= 64 and < 128 => (PremiumHigh & (1UL << (type - 64))) != 0,
        >= 128 and < 132 => (PremiumTail & (1 << (type - 128))) != 0,
        _ => false
    };
}

internal sealed class ChatEmoticonCatalog(ImmutableArray<ChatEmoticonEntry> entries)
{
    public ImmutableArray<ChatEmoticonEntry> Eligible(byte level) => Eligible(new ChatEmoticonEligibility(level, false, 0, 0, 0, 0));
    public ImmutableArray<ChatEmoticonEntry> Eligible(ChatEmoticonEligibility user) => entries
        .Where(row => row.Type == "COMMON" || row.Type == "LEVEL" && row.Requirement <= user.Level ||
            row.Type == "WEDDING" && user.Married && row.Requirement <= user.Love ||
            row.Type is "CASH" or "EVENT" && user.Premium(row.PremiumType))
        .ToImmutableArray();

    public bool Contains(ushort id) => entries.Any(row => row.Id == id);

    public static ChatEmoticonCatalog Load(PvfArchive archive, DfoServer.GameWorld.Items.PremiumContractCatalog? premiums = null)
    {
        const string list = "chatemoticon/chatemoticon.lst";
        const string order = "chatemoticon/emoticonorderlist.etc";
        if (!archive.Contains(list)) return new([]);
        var ranks = archive.Contains(order)
            ? PvfScript.Integers(PvfScript.Block(archive.ReadText(order), "order"))
                .Select((id, rank) => (id, rank)).Where(row => row.id > 0)
                .GroupBy(row => row.id).ToDictionary(group => group.Key, group => group.First().rank)
            : [];
        var result = new List<(ChatEmoticonEntry Entry, int Rank)>();
        foreach (var (id, path) in PvfScript.List(archive.ReadText(list)))
        {
            if (id is <= 0 or > short.MaxValue) throw new InvalidDataException("聊天表情编号越界");
            string full = path.StartsWith("chatemoticon/", StringComparison.OrdinalIgnoreCase) ? path : "chatemoticon/" + path;
            if (!archive.Contains(full)) throw new InvalidDataException("聊天表情资源缺失: " + full);
            var value = ChatEmoticonFile.Read(archive.ReadTokens(full), archive.ResolveString);
            string type = value.Type.Trim().ToUpperInvariant();
            if (type is not ("COMMON" or "LEVEL" or "WEDDING" or "CASH" or "EVENT")) continue;
            if (value.Requirement < 0) throw new InvalidDataException("聊天表情资格参数无效");
            int premium = type is "CASH" or "EVENT" ? premiums?.Get(value.Requirement)?.Type ?? 0 : 0;
            result.Add((new((ushort)id, value.Requirement, type, premium), ranks.GetValueOrDefault(id, int.MaxValue)));
        }
        if (result.Count > 256) throw new InvalidDataException("聊天表情目录数量越界");
        return new(result.OrderBy(row => row.Rank).ThenBy(row => row.Entry.Id).Select(row => row.Entry).ToImmutableArray());
    }
}
