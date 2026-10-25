using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using static PvfLib.PvfScript;

namespace PvfLib;

public sealed record CustomAbilityGroup(string Key, ImmutableArray<int> Indexes);
public sealed record CustomAbilityMaterial(string Grade, ImmutableArray<(int ItemId, int Count)> Items);
public sealed record CustomAbilityOption(int Index, ImmutableArray<CustomAbilityMaterial> Materials);
public sealed record CustomAbilityFile(string Type, ImmutableArray<CustomAbilityGroup> Groups,
    ImmutableArray<CustomAbilityOption> Options)
{
    // 只还原资源声明；组编号、等级回落和材料合法性由消费玩法解释。
    public static CustomAbilityFile Parse(string text)
    {
        var groups = Blocks(text,"group").Select(body => new CustomAbilityGroup(
            Quoted(body,"key"), Numbers(RequiredBlock(body,"indexes")).ToImmutableArray())).ToImmutableArray();
        var options = Blocks(text,"info").Select(body =>
        {
            if (Regex.Matches(body,@"\[index\]").Count!=1) throw Invalid();
            int[] index = Numbers(Section(body,"index"));
            if (index.Length != 1) throw Invalid();
            var materials = Blocks(body,"material").Select(material =>
            {
                int[] pairs = Numbers(RequiredBlock(material,"list"));
                if (pairs.Length % 2 != 0) throw Invalid();
                return new CustomAbilityMaterial(Quoted(material,"grade"), Enumerable.Range(0,pairs.Length/2)
                    .Select(i => (pairs[i*2],pairs[i*2+1])).ToImmutableArray());
            }).ToImmutableArray();
            return new CustomAbilityOption(index[0],materials);
        }).ToImmutableArray();
        return new(Quoted(text,"custom ability type"),groups,options);
    }

    static IEnumerable<string> Blocks(string text,string tag)
    {
        string open = Regex.Escape("["+tag+"]"), close = Regex.Escape("[/"+tag+"]");
        var matches = Regex.Matches(text,open+"(.*?)"+close,RegexOptions.Singleline);
        if (Regex.Matches(text,open).Count != matches.Count || Regex.Matches(text,close).Count != matches.Count)
            throw Invalid();
        return matches.Select(match => match.Groups[1].Value);
    }
    static int[] Numbers(string text)
    {
        var fields=text.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
        var values=new int[fields.Length];
        for(int i=0;i<fields.Length;i++)
            if(!int.TryParse(fields[i],NumberStyles.Integer,CultureInfo.InvariantCulture,out values[i])) throw Invalid();
        return values;
    }
    static string RequiredBlock(string text,string tag)
    {
        var blocks=Blocks(text,tag).ToArray();
        return blocks is [var block]?block:throw Invalid();
    }
    static string Quoted(string text,string tag)
    {
        if (Regex.Matches(text,Regex.Escape("["+tag+"]")).Count!=1) throw Invalid();
        string value=Section(text,tag).Trim();
        if (value.Length<2 || value[0]!='`' || value[^1]!='`' || value[1..^1].Contains('`')) throw Invalid();
        return value[1..^1];
    }
    static InvalidDataException Invalid()=>new("自定义属性资源声明不完整");
}
