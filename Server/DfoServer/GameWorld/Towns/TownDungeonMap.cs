using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Towns;

internal sealed record TownDungeonOption(int DungeonId, int QuestId, bool InProgress);
internal sealed record TownDungeonMap(int Id, string Path, ImmutableArray<TownDungeonOption> Options, string? Error = null)
{
    // 实验性（未在95客户端实证）：选图区域的深渊派对声明，取自区域主段 [/dungeon] 之后、[extra map] 之前的
    // [hell dungeon] 1 与 [hell quest] ... [/hell quest]（依据参考项目 dungeon_hell_party_entry.go；[hell dungeon legend] 等传奇深渊不在此范围）。
    public bool HellDungeon { get; init; }
    public ImmutableArray<int> HellQuests { get; init; } = [];
    // 形状不符一律当作"无深渊"，不抛错，不影响既有选图解析。
    static TownDungeonMap WithHell(TownDungeonMap map, IReadOnlyList<PvfToken> tokens, int at, Func<int,string> resolve)
    {
        bool hell = false;
        var quests = ImmutableArray.CreateBuilder<int>();
        for (; at < tokens.Count; at++)
        {
            if (tokens[at].Type != 3) continue;
            string tag = resolve(tokens[at].Value).Trim().ToLowerInvariant();
            if (tag == "[extra map]") break;
            if (tag == "[hell dungeon]" && at + 1 < tokens.Count && tokens[at + 1] is { Type: 0, Value: 1 }) hell = true;
            else if (tag == "[hell quest]")
                for (int i = at + 1; i < tokens.Count && tokens[i].Type == 0; i++) quests.Add(tokens[i].Value);
        }
        return map with { HellDungeon = hell, HellQuests = quests.ToImmutable() };
    }
    // 主选择地图与level acc等extra map分别定义；这里只接普通主选择入口。
    internal static TownDungeonMap Read(int id, string path, IReadOnlyList<PvfToken> tokens, Func<int,string> resolve)
    {
        int at = 0;
        while (at < tokens.Count && (tokens[at].Type != 3 || resolve(tokens[at].Value) != "[dungeon]")) at++;
        if (at == tokens.Count) throw new NotSupportedException("选择地图没有普通dungeon块");
        var options = ImmutableArray.CreateBuilder<TownDungeonOption>();
        for (at++; at < tokens.Count;)
        {
            if (tokens[at].Type == 3 && resolve(tokens[at].Value) == "[/dungeon]")
                return options.Count > 0 ? WithHell(new(id,path,options.ToImmutable()),tokens,at + 1,resolve) : throw new InvalidDataException("选择地图地下城列表为空");
            if (tokens[at].Type != 0 || tokens[at].Value <= 0) throw new NotSupportedException("选择地图地下城编号格式未确认");
            int dungeon = tokens[at++].Value;
            string? condition = at < tokens.Count && tokens[at].Type == 3 ? resolve(tokens[at].Value) : null;
            bool progress = condition == "[in progress]";
            bool marked = condition is "[in progress]" or "[is clear quest]";
            // 两种标记都属于前一个地下城；完成条件继续使用持久任务完成记录。
            if (marked) at++;
            if (at >= tokens.Count || tokens[at].Type != 0 || tokens[at].Value is 0 or < -1 || marked && tokens[at].Value == -1)
                throw new NotSupportedException("选择地图任务条件格式未确认");
            options.Add(new(dungeon,tokens[at++].Value,progress));
        }
        throw new InvalidDataException("选择地图dungeon块未闭合");
    }
}
