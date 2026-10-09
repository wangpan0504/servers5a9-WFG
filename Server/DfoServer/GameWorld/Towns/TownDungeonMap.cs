using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Towns;

internal sealed record TownDungeonOption(int DungeonId, int QuestId, bool InProgress);
internal sealed record TownDungeonMap(int Id, string Path, ImmutableArray<TownDungeonOption> Options, string? Error = null)
{
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
                return options.Count > 0 ? new(id,path,options.ToImmutable()) : throw new InvalidDataException("选择地图地下城列表为空");
            if (tokens[at].Type != 0 || tokens[at].Value <= 0) throw new NotSupportedException("选择地图地下城编号格式未确认");
            int dungeon = tokens[at++].Value;
            bool progress = at < tokens.Count && tokens[at].Type == 3 && resolve(tokens[at].Value) == "[in progress]";
            if (progress) at++;
            if (at >= tokens.Count || tokens[at].Type != 0 || tokens[at].Value is 0 or < -1 || progress && tokens[at].Value == -1)
                throw new NotSupportedException("选择地图任务条件格式未确认");
            options.Add(new(dungeon,tokens[at++].Value,progress));
        }
        throw new InvalidDataException("选择地图dungeon块未闭合");
    }
}
