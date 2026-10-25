using System.Collections.Frozen;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Pvp;

internal sealed record PvpArcadeRoundPlan(PvpArcadeMode Mode, ImmutableArray<ImmutableHashSet<int>> Allowed)
{
    public ImmutableHashSet<int> RandomRounds { get; init; }=[];
}
internal sealed record PvpArcadeMode(int Index,int Difficulty,int Rounds,int Coins,ImmutableArray<int> RandomOpponents);
internal sealed record PvpArcadeOpponents(FrozenDictionary<int,ImmutableArray<int>> FixedRounds,ImmutableArray<int> RandomOpponents)
{
    // V31 015DC8E8 先检查APC资源，017FCBA0 唯一键插入保留首项；不是同轮随机池。
    public bool TryGetFixedOpponent(int round, Func<int,bool> exists, out int opponent)
    {
        opponent=0;
        if(!FixedRounds.TryGetValue(round,out var declarations)) return false;
        foreach(int id in declarations)
            if(exists(id)) { opponent=id; return true; }
        return false;
    }
}

// 读取本版街机对练的模式和职业原表，不由任务ID猜对手、轮数或难度。
internal sealed class PvpArcadeCatalog
{
    readonly FrozenSet<int> availableActors;
    public FrozenDictionary<int,PvpArcadeMode> Modes { get; }
    public ImmutableArray<byte> DeclaredJobs { get; }
    public FrozenDictionary<(byte Job,byte Grow,int Mode),PvpArcadeOpponents> Opponents { get; }

    public PvpArcadeCatalog(PvfArchive pvf)
    {
        availableActors=Dungeons.DungeonCatalog.ParseDirectory(pvf.ReadTokens("aicharacter/aicharacter.lst"),
            pvf.ResolveString,"aicharacter").Where(row=>pvf.Contains(row.Value)).Select(row=>row.Key).ToFrozenSet();
        Modes=ReadModes(Read("etc/pvparcade/arcadeinfo.etc"));
        var all=new Dictionary<(byte,byte,int),PvpArcadeOpponents>();
        var jobs=pvf.ReadTokens("etc/pvparcade/arcadeapcinfolist.etc");
        if(jobs.Length==0 || jobs.Length%2!=0) throw Invalid("职业索引");
        var seen=new HashSet<int>();
        for(int at=0;at<jobs.Length;at+=2)
        {
            if(jobs[at].Type!=0 || jobs[at].Value is <0 or >15 || jobs[at+1].Type!=6 || !seen.Add(jobs[at].Value))
                throw Invalid("职业索引类型或重复");
            string path=pvf.ResolveString(jobs[at+1].Value);
            if(string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['/', '\\', ':'])>=0 || path is "." or "..")
                throw Invalid("职业索引路径");
            foreach(var pair in ReadOpponents(Read("etc/pvparcade/arcadeapclist/"+path),Modes))
                if(!all.TryAdd(((byte)jobs[at].Value,pair.Key.Grow,pair.Key.Mode),pair.Value)) throw Invalid("重复职业配置");
        }
        DeclaredJobs=seen.Order().Select(job=>(byte)job).ToImmutableArray();
        Opponents=all.ToFrozenDictionary();
        PvfTokenDocument Read(string path) => new(pvf.ReadTokens(path),pvf.ResolveString);
    }

    public PvpArcadeRoundPlan? Resolve(byte job,byte grow,int mode)
    {
        if(!Modes.TryGetValue(mode,out var definition)) return null;
        Opponents.TryGetValue((job,grow,mode),out var opponents);
        return Resolve(definition,opponents,availableActors.Contains);
    }

    // 015D8FF1..015D903C：职业随机池非空才替代模式池；无职业块仍使用模式原池。
    // 015D9090..015D9154：固定轮次优先，未命中才从客户端已打乱的随机池弹出。
    // 此处是报告的资源边界，不重抽客户端次序，也不宣称仅凭成员资格能证实击杀。
    internal static PvpArcadeRoundPlan? Resolve(PvpArcadeMode mode,PvpArcadeOpponents? opponents,Func<int,bool> exists)
    {
        if(mode.Rounds is <1 or >255) return null;
        var random=opponents is {RandomOpponents.IsEmpty:false} ? opponents.RandomOpponents : mode.RandomOpponents;
        if(random.Any(id=>!exists(id))) return null;
        var pool=random.ToImmutableHashSet();
        var rounds=ImmutableArray.CreateBuilder<ImmutableHashSet<int>>(mode.Rounds);
        var randomRounds=ImmutableHashSet.CreateBuilder<int>();
        for(int round=1;round<=mode.Rounds;round++)
        {
            if(opponents is not null && opponents.TryGetFixedOpponent(round,exists,out int id)) rounds.Add(ImmutableHashSet.Create(id));
            else { rounds.Add(pool); randomRounds.Add(round); }
        }
        // 不能用轮次空集合或重复扩充资源池，掩盖不完整的原声明。
        return randomRounds.Count>pool.Count || rounds.Any(row=>row.Count==0) ? null :
            new(mode,rounds.MoveToImmutable()) {RandomRounds=randomRounds.ToImmutable()};
    }

    internal static FrozenDictionary<int,PvpArcadeMode> ReadModes(PvfTokenDocument document)
    {
        Valid(document);
        var result=new Dictionary<int,PvpArcadeMode>();
        foreach(var section in document.Sections)
        {
            if(section.Name!="arcade info" || !section.IsBlock || !section.Values.IsEmpty ||
                section.Children.Length!=5) throw Invalid("模式块");
            var rows=section.Children;
            int index=Scalar(rows,"index"),difficulty=Scalar(rows,"difficulty"),rounds=Scalar(rows,"max round"),coins=Scalar(rows,"max useblecoin");
            var random=Numbers(Single(rows,"random apc index"));
            if(index<0 || difficulty is <0 or >2 || rounds is <1 or >255 || coins is <0 or >255 || random.IsEmpty ||
                random.Any(id=>id<=0) || random.Distinct().Count()!=random.Length ||
                !result.TryAdd(index,new(index,difficulty,rounds,coins,random))) throw Invalid("模式值或重复");
        }
        if(result.Count==0) throw Invalid("空模式表");
        return result.ToFrozenDictionary();
    }

    internal static FrozenDictionary<(byte Grow,int Mode),PvpArcadeOpponents> ReadOpponents(PvfTokenDocument document,
        IReadOnlyDictionary<int,PvpArcadeMode> modes)
    {
        Valid(document);
        var result=new Dictionary<(byte,int),PvpArcadeOpponents>();
        var grows=new HashSet<int>();
        foreach(var section in document.Sections)
        {
            if(section.Name!="arcade apcinfo" || !section.IsBlock || !section.Values.IsEmpty || section.Children.Length<3 ||
                section.Children[0].Name!="growtype") throw Invalid("职业对练块");
            int grow=One(section.Children[0]);
            if(grow is <0 or >15 || !grows.Add(grow)) throw Invalid("职业转职值或重复");
            var rows=section.Children;
            for(int at=1;at<rows.Length;)
            {
                if(rows[at].Name!="arcade index") throw Invalid("模式顺序");
                int mode=One(rows[at++]);
                if(!modes.TryGetValue(mode,out var definition) || at>=rows.Length || rows[at].Name!="apc index")
                    throw Invalid("对练模式缺失或固定对手块");
                var fixedValues=Numbers(rows[at++]);
                if(fixedValues.Length%2!=0) throw Invalid("固定轮次与APC不成对");
                var fixedRounds=new Dictionary<int,List<int>>();
                for(int pair=0;pair<fixedValues.Length;pair+=2)
                {
                    if(fixedValues[pair]<1 || fixedValues[pair]>definition.Rounds || fixedValues[pair+1]<=0)
                        throw Invalid("固定轮次范围");
                    // 原表存在同轮多条声明；保留原始顺序，运行时取首个存在的资源，不作随机池。
                    if(!fixedRounds.TryGetValue(fixedValues[pair],out var candidates))
                        fixedRounds.Add(fixedValues[pair],candidates=[]);
                    candidates.Add(fixedValues[pair+1]);
                }
                ImmutableArray<int> random=[];
                if(at<rows.Length && rows[at].Name=="random apc index") random=Numbers(rows[at++]);
                if(random.Any(id=>id<=0) || random.Distinct().Count()!=random.Length ||
                    !result.TryAdd(((byte)grow,mode),new(fixedRounds.ToFrozenDictionary(pair=>pair.Key,pair=>pair.Value.ToImmutableArray()),random))) throw Invalid("随机对手或重复模式");
            }
        }
        // 黑暗武士/缔造者原文件为空，只保留注册，不能据此编造对练候选。
        return result.ToFrozenDictionary();
    }
    static void Valid(PvfTokenDocument document)
    {
        if(!document.Diagnostics.IsEmpty) throw Invalid("块结构诊断");
    }
    static PvfTokenSection Single(ImmutableArray<PvfTokenSection> rows,string name) =>
        rows.Where(row=>row.Name==name).ToArray() is [var found] ? found : throw Invalid("字段缺失或重复: "+name);
    static int Scalar(ImmutableArray<PvfTokenSection> rows,string name) => One(Single(rows,name));
    static int One(PvfTokenSection row) => !row.IsBlock && row.Values is [{Type:0} value] && row.Children.IsEmpty
        ? value.Value : throw Invalid("整数: "+row.Name);
    static ImmutableArray<int> Numbers(PvfTokenSection row) => row.IsBlock && row.Children.IsEmpty && row.Values.All(value=>value.Type==0)
        ? row.Values.Select(value=>value.Value).ToImmutableArray() : throw Invalid("整数块: "+row.Name);
    static InvalidDataException Invalid(string field) => new("街机对练资源无效: "+field);
}
