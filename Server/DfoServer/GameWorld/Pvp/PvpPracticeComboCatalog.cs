using System.Collections.Frozen;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Pvp;

internal sealed record PvpPracticeCombo(int Id,byte Job,byte Grow,int Variant,int Difficulty,
    string Name,string Replay,string Tip,ImmutableArray<int> Steps);

// V31 039D2B30读取新目录；039D264F及039CA43A以job*6+grow查找，目录键不是职业编号。
internal sealed class PvpPracticeComboCatalog
{
    public FrozenDictionary<(byte Job,byte Grow),ImmutableArray<PvpPracticeCombo>> Combos { get; }
    public PvpPracticeComboCatalog(PvfArchive pvf)
    {
        var files=Dungeons.DungeonCatalog.ParseDirectory(pvf.ReadTokens("etc/pvppracticecombo.lst"),pvf.ResolveString,"etc");
        var all=new List<PvpPracticeCombo>();
        foreach(var path in files.Values)
            all.AddRange(Read(new(pvf.ReadTokens(path),pvf.ResolveString),pvf.ResolveString));
        if(all.Select(row=>row.Id).Distinct().Count()!=all.Count) throw Invalid("重复连招编号");
        Combos=all.GroupBy(row=>(row.Job,row.Grow)).ToFrozenDictionary(group=>group.Key,group=>group.ToImmutableArray());
    }
    public ImmutableArray<PvpPracticeCombo> Resolve(byte job,byte grow) => Combos.GetValueOrDefault((job,grow),[]);

    internal static ImmutableArray<PvpPracticeCombo> Read(PvfTokenDocument document,Func<int,string> resolve)
    {
        if(!document.Diagnostics.IsEmpty) throw Invalid("块结构");
        var result=ImmutableArray.CreateBuilder<PvpPracticeCombo>();
        var ids=new HashSet<int>();
        foreach(var block in document.Sections)
        {
            if(block.Name!="combo" || !block.IsBlock || block.Values is not
                [{Type:0} id,{Type:6} name,{Type:0} job,{Type:0} grow,{Type:0} variant] ||
                id.Value<=0 || !ids.Add(id.Value) || job.Value is <0 or >15 || grow.Value is <0 or >5 ||
                block.Children.Length!=3) throw Invalid("连招头或重复编号");
            var replay=Text("replay");var tip=Text("tip");
            var difficulty=block.Children.SingleOrDefault(row=>row.Name=="difficulty") ?? throw Invalid("难度缺失");
            if(difficulty.IsBlock || !difficulty.Children.IsEmpty || difficulty.Values.Length<4 ||
                (difficulty.Values.Length-1)%3!=0 || difficulty.Values.Any(token=>token.Type!=0) || difficulty.Values[0].Value<=0 ||
                string.IsNullOrWhiteSpace(replay)) throw Invalid("难度与动作三元组");
            // variant与动作第三字段保留原声明，不猜成等级、职业门槛或服务器命中事实。
            result.Add(new(id.Value,(byte)job.Value,(byte)grow.Value,variant.Value,difficulty.Values[0].Value,
                resolve(name.Value),replay,tip,difficulty.Values.Skip(1).Select(token=>token.Value).ToImmutableArray()));
            string Text(string field)
            {
                var rows=block.Children.Where(row=>row.Name==field).ToArray();
                return rows is [{IsBlock:false,Children.IsEmpty:true,Values:[{Type:6} value]}]
                    ? resolve(value.Value) : throw Invalid("文本字段: "+field);
            }
        }
        return result.ToImmutable();
    }
    static InvalidDataException Invalid(string field)=>new("练习连招资源无效: "+field);
}
