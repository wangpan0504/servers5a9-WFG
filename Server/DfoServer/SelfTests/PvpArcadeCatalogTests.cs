using PvfLib;
using DfoServer.GameWorld.Pvp;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PvpArcadeCatalogTests
{
    public static void Run()
    {
        int before=Count;
        var modes=PvpArcadeCatalog.ReadModes(Mode());
        That(modes[80] is {Difficulty:0,Rounds:2,Coins:99} && modes[80].RandomOpponents.SequenceEqual([1201,1202]),
            "原模式难度轮数复活数和随机池不硬编码替换");
        That(PvpArcadeCatalog.ReadOpponents(Doc(),modes).Count==0,"原空职业表保留为空，不借其它职业补全");
        var rows=PvpArcadeCatalog.ReadOpponents(Opponents(),modes);
        That(rows[(0,80)].FixedRounds[1].SequenceEqual([1201,1202]) && rows[(0,80)].RandomOpponents.SequenceEqual([1203]),
            "同一轮原始重复声明保留顺序，不把末项覆盖成唯一原数据");
        That(rows[(0,80)].TryGetFixedOpponent(1,_=>true,out int first) && first==1201,"重复轮次首个有效APC生效，不随机抽后项");
        That(rows[(0,80)].TryGetFixedOpponent(1,id=>id==1202,out int next) && next==1202,"原码跳过资源不存在的首项后才插入后项");
        That(!rows[(0,80)].TryGetFixedOpponent(1,_=>false,out int missing) && missing==0 &&
            !rows[(0,80)].TryGetFixedOpponent(2,_=>true,out _),"无有效固定APC不伪造对手");
        var resolved=PvpArcadeCatalog.Resolve(modes[80],rows[(0,80)],_=>true)!;
        That(resolved.Allowed[0].SetEquals([1201]) && resolved.Allowed[1].SetEquals([1203]),
            "固定首项单独约束第一轮，职业随机池替代而非合并全局池");
        var global=PvpArcadeCatalog.Resolve(modes[80],null,_=>true)!;
        That(global.Allowed.Length==2 && global.Allowed.All(round=>round.SetEquals([1201,1202])),
            "缺少职业块按原码使用模式池，不借别的职业配置");
        That(PvpArcadeCatalog.Resolve(modes[80],rows[(0,80)],id=>id!=1203) is null &&
            PvpArcadeCatalog.Resolve(modes[80] with {Rounds=3},null,_=>true) is null,
            "随机池引用缺失或不足实际轮数不补造对手");
        Reject<InvalidDataException>(()=>PvpArcadeCatalog.ReadModes(Mode(difficulty:3)),"未知难度拒绝");
        Reject<InvalidDataException>(()=>PvpArcadeCatalog.ReadModes(Mode(rounds:0)),"零轮数拒绝");
        Reject<InvalidDataException>(()=>PvpArcadeCatalog.ReadModes(Mode(coins:-1)),"负复活次数拒绝");
        Reject<InvalidDataException>(()=>PvpArcadeCatalog.ReadOpponents(Opponents(mode:99),modes),"缺失模式引用拒绝");
        Reject<InvalidDataException>(()=>PvpArcadeCatalog.ReadOpponents(Opponents(round:3),modes),"超范围轮次拒绝");
        Reject<InvalidDataException>(()=>PvpArcadeCatalog.ReadOpponents(Opponents(apc:0),modes),"无效APC编号拒绝");
        Console.WriteLine($"PASS {Count-before} 街机对练原表范围与重复声明生效规则断言");
    }
    public static void RunResources(PvfArchive pvf)
    {
        int before=Count;
        var catalog=new PvpArcadeCatalog(pvf);
        That(catalog.Modes.Keys.Order().SequenceEqual([0,1,2,80,81,82]),"真实街机与教学六个模式均保留");
        var expected=new[]{(0,0,5,99),(1,1,6,6),(2,2,7,3),(80,0,2,99),(81,0,2,99),(82,1,4,99)};
        foreach(var (index,difficulty,rounds,coins) in expected)
            That(catalog.Modes[index] is var m && m.Difficulty==difficulty && m.Rounds==rounds && m.Coins==coins,
                "真实PVF独立常量核对各模式难度/轮数/复活限额");
        That(catalog.DeclaredJobs.SequenceEqual(Enumerable.Range(0,16).Select(i=>(byte)i)) &&
            catalog.Opponents.Keys.Select(key=>key.Job).Distinct().Order().SequenceEqual(Enumerable.Range(0,16).Where(i=>i is not (9 or 10)).Select(i=>(byte)i)),
            "16职业注册但黑暗武士/缔造者原表为空，不擅自补对手");
        That(catalog.Opponents[(0,0,80)].FixedRounds[1].SequenceEqual([1201]) &&
            catalog.Opponents[(0,0,80)].RandomOpponents.SequenceEqual([1202,1203,1204]),"鬼剑士教学原固定/候选池");
        That(catalog.Opponents[(8,0,80)].FixedRounds[1].SequenceEqual([41201,41202,41210,41213,41216]),
            "男法五个同轮对手不丢失");
        That(catalog.Opponents[(13,3,80)].FixedRounds[1].SequenceEqual([65122,65121]) &&
            catalog.Opponents[(12,0,80)].FixedRounds[1].SequenceEqual([61200,61203,61206,61209]),
            "魔枪士原顺序和守护者四条声明不被覆盖");
        Console.WriteLine($"PASS {Count-before} 真实PVF街机模式及{catalog.Opponents.Count}组职业对练配置断言");
    }
    static PvfTokenDocument Mode(int difficulty=0,int rounds=2,int coins=99) => Doc("[Arcade Info]","[index]",80,
        "[difficulty]",difficulty,"[max round]",rounds,"[max UsebleCoin]",coins,"[Random APC Index]",1201,1202,
        "[/random APC Index]","[/Arcade Info]");
    static PvfTokenDocument Opponents(int mode=80,int round=1,int apc=1201) => Doc("[Arcade ApcInfo]","[growType]",0,
        "[Arcade index]",mode,"[APC Index]",round,apc,1,1202,"[/APC Index]","[Random APC Index]",1203,
        "[/Random APC Index]","[/Arcade ApcInfo]");
    static PvfTokenDocument Doc(params object[] values)
    {
        var strings=new List<string>();
        var tokens=values.Select(value=>value is int number ? new PvfToken(0,number) : Tag((string)value)).ToArray();
        return new(tokens,id=>strings[id]);
        PvfToken Tag(string value) { strings.Add(value); return new(3,strings.Count-1); }
    }
}
