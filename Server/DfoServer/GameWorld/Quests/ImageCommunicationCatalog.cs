using System.Collections.Frozen;
using System.Collections.Immutable;
using DfoServer.GameWorld.Towns;
using PvfLib;

namespace DfoServer.GameWorld.Quests;

internal readonly record struct ImageCommunicationNpc(int NpcId, ushort RequiredQuest, int X, int Y);

// 目录在Runtime装配时解释一次；请求内不扫描归档，也不按任务编号写死演员。
internal sealed record ImageCommunicationCatalog(uint Charge, int SummonTime,
    ImmutableArray<int> SummonPosition, ImmutableArray<int> EffectPosition,
    ImmutableArray<ImageCommunicationNpc> Npcs, FrozenSet<uint> SourceNpcs)
{
    public const string ResourcePath = "etc/imagecommunication.etc";

    public static ImageCommunicationCatalog Load(PvfArchive pvf, TownCatalog towns)
    {
        var list = PvfScript.List(pvf.ReadText("npc/npc.lst"));
        var sources = towns.Areas.Values.SelectMany(area => area.Phases.Prepend(area.Default))
            .SelectMany(map => map.Npcs).Select(npc => npc.Id).Distinct()
            .Where(id => id <= int.MaxValue && list.TryGetValue((int)id, out var path) &&
                IsSource(pvf.ReadTokens("npc/" + path), pvf.ResolveString)).ToFrozenSet();
        return Parse(pvf.ReadTokens(ResourcePath), pvf.ResolveString, sources);
    }

    internal static bool IsSource(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        // 只读取本能力的role值；其它NPC字段仍由所属目录解释，不让无关声明阻断装配。
        for (int at = 0; at < tokens.Count; at++)
            if (tokens[at].Type == 3 && resolve(tokens[at].Value).Trim().Equals("[role]", StringComparison.OrdinalIgnoreCase))
                for (int value = at + 1; value < tokens.Count && tokens[value].Type != 3; value++)
                    if (tokens[value].Type == 6 && resolve(tokens[value].Value).Trim().Equals("[image communication]", StringComparison.OrdinalIgnoreCase) &&
                        value + 1 < tokens.Count && tokens[value + 1] is { Type: 0, Value: 0 }) return true;
        return false;
    }

    internal static ImageCommunicationCatalog Parse(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve,
        IEnumerable<uint> sourceNpcs)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        if (document.Diagnostics.Length != 0 || document.Sections is not [{ Name: "image communication", Values.Length: 0 } block])
            throw new InvalidDataException("通讯装置必须是完整image communication资源块");
        int[] Integers(ImmutableArray<PvfTokenSection> fields, string name, int count)
        {
            var matches = fields.Where(field => field.Name == name).ToArray();
            if (matches is not [{ Children.Length: 0 } field] || field.Values.Length != count || field.Values.Any(value => value.Type != 0))
                throw new InvalidDataException("通讯装置字段缺失、重复或形状不符: " + name);
            return field.Values.Select(value => value.Value).ToArray();
        }
        if (block.Children.Any(field => field.Name is not ("charge" or "summon time" or "summon pos" or "effect pos" or "npc")))
            throw new NotSupportedException("通讯装置存在未确认字段");
        int charge = Integers(block.Children, "charge", 1)[0], summon = Integers(block.Children, "summon time", 1)[0];
        var position = Integers(block.Children, "summon pos", 3); var effect = Integers(block.Children, "effect pos", 3);
        if (charge < 0) throw new InvalidDataException("通讯装置费用不能为负");
        var npcs = block.Children.Where(field => field.Name == "npc").Select(field =>
        {
            if (field.Values.Length != 0 || field.Children.Any(child => child.Name is not ("npc index" or "npc pos" or "require quest")))
                throw new InvalidDataException("通讯NPC声明形状未确认");
            int id = Integers(field.Children, "npc index", 1)[0], quest = Integers(field.Children, "require quest", 1)[0];
            var point = Integers(field.Children, "npc pos", 2);
            if (id <= 0 || quest is <= 0 or >= 30000)
                throw new InvalidDataException("通讯NPC或任务编号无效");
            return new ImageCommunicationNpc(id, (ushort)quest, point[0], point[1]);
        }).ToImmutableArray();
        if (npcs.Length == 0 || npcs.Select(row => row.NpcId).Distinct().Count() != npcs.Length)
            throw new InvalidDataException("通讯NPC缺失或重复");
        return new((uint)charge, summon, [.. position], [.. effect], npcs, sourceNpcs.ToFrozenSet());
    }
}
