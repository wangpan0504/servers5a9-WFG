using System.Collections.Immutable;
using DfoServer.GameWorld.Pvp;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PvpPracticeComboCatalogTests
{
    public static void Run()
    {
        int before=Count;
        var row=PvpPracticeComboCatalog.Read(Document(),Resolve).Single();
        That(row is {Id:1,Job:0,Grow:0,Variant:7,Difficulty:1,Replay:"Replay/Combo1.rep"} &&
            row.Steps.SequenceEqual([5,0,-1,46,0,-1]),"连招职业取原头字段，保留未解释variant及动作三元组");
        That(PvpPracticeComboCatalog.Read(new([],Resolve),Resolve).IsEmpty,"原空连招文件保持为空");
        Reject<InvalidDataException>(()=>PvpPracticeComboCatalog.Read(Document(job:16),Resolve),"连招越界职业拒绝");
        Reject<InvalidDataException>(()=>PvpPracticeComboCatalog.Read(Document(grow:6),Resolve),"避免job*6+grow索引碰撞");
        Reject<InvalidDataException>(()=>PvpPracticeComboCatalog.Read(Document(difficulty:0),Resolve),"无有效难度拒绝");
        Reject<InvalidDataException>(()=>PvpPracticeComboCatalog.Read(Document(incomplete:true),Resolve),"不完整动作三元组拒绝");
        var original=Document();
        That(original.Sections.Length==1 && original.Sections[0].Children.Length==3,"原块结构不把难度步骤误作顶层声明");
        Console.WriteLine($"PASS {Count-before} 连招目录格式及索引边界断言");
    }
    public static void RunResources(PvfArchive pvf)
    {
        int before=Count;
        var catalog=new PvpPracticeComboCatalog(pvf);
        var sword=catalog.Resolve(0,0).Single(row=>row.Id==1);
        That(sword.Difficulty==1 && sword.Steps.SequenceEqual([5,0,-1,46,0,-1]),"新版鬼剑士原第一组连招独立常量");
        That(catalog.Resolve(10,0).Any(row=>row.Id==1001) && !catalog.Resolve(14,0).Any(row=>row.Id==1001),
            "缔造者目录编号14不是职业14，按原header职业10归类");
        That(catalog.Combos.Keys.Select(key=>key.Job).Distinct().Order().SequenceEqual(Enumerable.Range(0,16).Select(i=>(byte)i)),
            "本版16职业连招原声明全部解析，不丢黑暗武士或缔造者");
        That(catalog.Resolve(16,0).IsEmpty && catalog.Resolve(0,6).IsEmpty,"未知职业转职不借其它索引候选");
        Console.WriteLine($"PASS {Count-before} 真实PVF连招目录断言，{catalog.Combos.Values.Sum(rows=>rows.Length)}组原连招");
    }
    static readonly string[] Strings=["[combo]","连招","[replay]","Replay/Combo1.rep","[tip]","","[difficulty]","[/combo]"];
    static string Resolve(int index)=>Strings[index];
    static PvfTokenDocument Document(int job=0,int grow=0,int difficulty=1,bool incomplete=false)
    {
        ImmutableArray<PvfToken> tokens=[new(3,0),new(0,1),new(6,1),new(0,job),new(0,grow),new(0,7),
            new(3,2),new(6,3),new(3,4),new(6,5),new(3,6),new(0,difficulty),new(0,5),new(0,0),new(0,-1),new(0,46),new(0,0),new(0,-1),new(3,7)];
        return new(incomplete ? tokens.RemoveAt(tokens.Length-2) : tokens,Resolve);
    }
}
