using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record OrdinaryHellPassCost(int ItemId, int Count)
{
    // 此历史通行证原文明确每次消耗一张替代邀请函，当前通用表未列出。
    internal const int LegacyInvitationPass = 490009324;
    public static ImmutableArray<OrdinaryHellPassCost> Load(PvfArchive archive) => archive.Contains(CosmoPassCost.Path)
        ? Parse(archive.ReadTokens(CosmoPassCost.Path), archive.ResolveString)
        : [new(LegacyInvitationPass, 1)];

    internal static ImmutableArray<OrdinaryHellPassCost> Parse(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        if (!document.Diagnostics.IsEmpty) throw new InvalidDataException("普通深渊通行证声明不完整");
        var rows = ImmutableArray.CreateBuilder<OrdinaryHellPassCost>();
        var ids = new HashSet<int>();
        var passSections = document.Sections.Where(section => section.Name == "hell freepass item").ToArray();
        if (passSections.Length > 1) throw new InvalidDataException("普通深渊通行证表重复");
        foreach (var section in passSections)
        {
            ValidatePairs(section);
            for (int at = 0; at < section.Values.Length; at += 2)
            {
                int id = section.Values[at].Value, count = section.Values[at + 1].Value;
                if (id <= 0 || count <= 0 || !ids.Add(id)) throw new InvalidDataException("普通深渊通行证编号或费用无效");
                rows.Add(new(id, count));
            }
        }
        var exceptions = document.Sections.Where(section => section.Name == "hell freepass exception item").ToArray();
        if (exceptions.Length > 1) throw new InvalidDataException("普通深渊通行证例外表重复");
        var excluded = new HashSet<int>();
        foreach (var section in exceptions)
        {
            ValidatePairs(section);
            for (int at = 0; at < section.Values.Length; at += 2)
            {
                int id = section.Values[at + 1].Value;
                if (id <= 0) throw new InvalidDataException("普通深渊通行证例外物品编号无效");
                // 首列适用范围尚未确认，暂不启用被列为例外的物品，不推测其区域含义。
                excluded.Add(id);
            }
        }
        if (!ids.Contains(LegacyInvitationPass)) rows.Add(new(LegacyInvitationPass, 1));
        return rows.Where(row => !excluded.Contains(row.ItemId)).ToImmutableArray();
    }

    static void ValidatePairs(PvfTokenSection section)
    {
        if (!section.IsBlock || !section.Children.IsEmpty || section.Values.Length % 2 != 0 || section.Values.Any(value => value.Type != 0))
            throw new InvalidDataException("普通深渊通行证声明须为完整整数二元组表");
    }
}
