using System.Collections.Immutable;

namespace PvfLib;

// 只辨认已闭合的直接动作声明，不执行ACT、不推导HP条件或客户端动作状态。
public static class RaidObjectEvidence
{
    public static ImmutableArray<string> EtcActions(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        var rows = document.Sections.Where(row => row.Name == "etc action").ToArray();
        if (!document.Diagnostics.IsEmpty || rows.Length != 1 || !rows[0].IsBlock || !rows[0].Children.IsEmpty ||
            rows[0].Values.Length is < 1 or > 16 || rows[0].Values.Any(token => token.Type != 6))
            throw new InvalidDataException("对象ETC动作声明不完整或存在歧义");
        return rows[0].Values.Select(token => resolve(token.Value)).ToImmutableArray();
    }

    // 精确接受 ON SET ACTION → DO BEHAVIOR ME 0 → 单一 RAID SET SYMBOL [+]1。
    // ON DESTROY、条件、重复trigger、额外behavior与其它运算都不是这份证据。
    public static int? SetActionIncrement(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        if (!document.Diagnostics.IsEmpty || document.Sections.Any(row => row.Name is not ("motion" or "trigger" or "behavior"))) return null;
        var triggers = document.Sections.Where(row => row.Name == "trigger").ToArray();
        var behaviors = document.Sections.Where(row => row.Name == "behavior").ToArray();
        if (triggers.Length != 1 || behaviors.Length != 1 || !triggers[0].Values.IsEmpty || !behaviors[0].Values.IsEmpty) return null;
        var trigger = triggers[0].Children;
        var behavior = behaviors[0].Children;
        if (trigger.Length != 3 || trigger.Any(row => !row.Children.IsEmpty) ||
            trigger[0].Name != "on set action" || !trigger[0].Values.IsEmpty ||
            trigger[1].Name != "do behavior" || !trigger[1].Values.IsEmpty ||
            trigger[2].Name != "me" || trigger[2].Values is not [{ Type: 0, Value: 0 }] ||
            behavior.Length != 2 || behavior.Any(row => !row.Children.IsEmpty) ||
            behavior[0].Name != "raid set symbol" || behavior[0].Values is not [{ Type: 0 }] ||
            behavior[1].Name != "+" || behavior[1].Values is not [{ Type: 0, Value: 1 }]) return null;
        return behavior[0].Values[0].Value;
    }

    // 普通地图块必须位于根层，不能把外层条件中的同名行提为无条件出生。
    public static bool UnconditionalPassiveObjects(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        int count = tokens.Count(token => token.Type == 3 && resolve(token.Value).Equals("[passive object]", StringComparison.OrdinalIgnoreCase));
        var document = new PvfTokenDocument(tokens, resolve);
        var roots = document.Sections.Where(row => row.Name == "passive object").ToArray();
        // 只接受当前核验范围的根字段；未知/残缺外层不能被通用声明树降格为相邻字段。
        string[] scalar = ["player number", "pvp start area", "type", "greed", "far sight scroll", "middle sight scroll",
            "near sight scroll", "pathgate pos", "map name", "dungeon start area", "pathgate object", "pathgate recognize range"];
        string[] blocks = ["dungeon", "tile", "extended tile", "background animation", "sound", "animation", "passive object",
            "monster", "monster specific ai", "event monster position", "dungeon movable area"];
        // 匹配client04273E81..3EBD：单字符串[mirror]仅写shadow枚举+798，再取下一顶层tag；不是对象条件块。
        int shadowCount = tokens.Count(token => token.Type == 3 && resolve(token.Value).Equals("[shadow type]", StringComparison.OrdinalIgnoreCase));
        var shadow = document.Sections.Where(row => row.Name == "shadow type").ToArray();
        bool ShadowValue(PvfTokenSection row) => !row.IsBlock && row.Children.IsEmpty &&
            row.Values is [{ Type: 6 } value] && resolve(value.Value).Equals("[mirror]", StringComparison.Ordinal);
        return document.Diagnostics.IsEmpty && shadowCount == shadow.Length && shadow.Length <= 1 && document.Sections.All(row =>
                row.Name == "shadow type" ? ShadowValue(row) : blocks.Contains(row.Name) ? row.IsBlock : scalar.Contains(row.Name) && !row.IsBlock) &&
            count == roots.Length && roots.Length <= 1 &&
            roots.All(row => row.IsBlock && row.Children.IsEmpty && row.Values.Length % 4 == 0 && row.Values.All(token => token.Type == 0));
    }
}
