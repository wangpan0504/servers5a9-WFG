using System.Collections.Immutable;

namespace PvfLib;

// 只保留原始剧情城镇块；账号隔离、传送和任务资格由服务端资源层解释。
public sealed record TownEpisodeFile(int? ReturnNpc,ImmutableArray<int> GuideQuests,ImmutableArray<PvfTokenSection> Fields)
{
    public static TownEpisodeFile? Parse(IReadOnlyList<PvfToken> tokens,Func<int,string> resolve)
    {
        var tags=tokens.Select((token,index)=>(token,index)).Where(row=>row.token.Type==3)
            .Select(row=>(Name:resolve(row.token.Value).Trim().ToLowerInvariant(),row.index)).ToArray();
        var starts=tags.Where(row=>row.Name=="[episode town]").ToArray();
        var ends=tags.Where(row=>row.Name=="[/episode town]").ToArray();
        if(starts.Length==0 && ends.Length==0) return null;
        if(starts.Length!=1 || ends.Length!=1 || ends[0].index<=starts[0].index)
            throw new InvalidDataException("剧情城镇块重复、缺失闭合或顺序非法");
        var paired=tags.Where(row=>row.Name.StartsWith("[/",StringComparison.Ordinal)).Select(row=>"["+row.Name[2..]).ToHashSet();
        var stack=new Stack<string>();
        foreach(var tag in tags.TakeWhile(row=>row.index<starts[0].index))
        {
            if(tag.Name.StartsWith("[/",StringComparison.Ordinal))
            {
                if(stack.Count==0 || stack.Pop()!="["+tag.Name[2..]) throw new InvalidDataException("剧情城镇前的块结构不匹配");
            }
            else if(paired.Contains(tag.Name)) stack.Push(tag.Name);
        }
        if(stack.Count!=0) throw new InvalidDataException("剧情城镇只能声明在根层");
        var document=new PvfTokenDocument(tokens.Skip(starts[0].index).Take(ends[0].index-starts[0].index+1).ToArray(),resolve);
        if(!document.Diagnostics.IsEmpty || document.Sections is not [{Name:"episode town",IsBlock:true,Values.Length:0} block])
            throw new InvalidDataException("剧情城镇必须为无值的闭合块");
        int? npc=null; ImmutableArray<int> guide=[];
        var returns=block.Children.Where(row=>row.Name=="return npc").ToArray();
        if(returns.Length!=0)
        {
            if(returns is not [{Children.Length:0,Values:[{Type:0} value]}]) throw new InvalidDataException("剧情城镇返回NPC必须为单个整数");
            npc=value.Value;
        }
        var guides=block.Children.Where(row=>row.Name=="town go guide quest").ToArray();
        if(guides.Length!=0)
        {
            if(guides is not [{IsBlock:true,Children.Length:0} row] || row.Values.Any(value=>value.Type!=0))
                throw new InvalidDataException("剧情城镇引导任务必须为闭合整数列表");
            guide=row.Values.Select(value=>value.Value).ToImmutableArray();
        }
        return new(npc,guide,block.Children);
    }
}
