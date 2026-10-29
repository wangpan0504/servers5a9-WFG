using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Testing;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class BeastDungeonSessionTests
{
    static bool SourceUnavailable(DungeonCatalog catalog, int dungeon)
    {
        var source = catalog.Get(BeastDungeonRotation.IsGateway((uint)dungeon)
            ? BeastDungeonRotation.SourceDungeonId(dungeon) : dungeon);
        var broken = source.BeastLayouts.Where(layout=>layout.ResourceError is not null).ToArray();
        foreach (var layout in broken)
            That(layout.Maps.Count==0 && (layout.ResourceError!.Contains("魔兽地图未登记:",StringComparison.Ordinal) ||
                layout.ResourceError.Contains("魔兽地图资源缺失:",StringComparison.Ordinal) ||
                layout.ResourceError.Contains("魔兽平衡表资源缺失",StringComparison.Ordinal)),
                "真实资源拒绝仅适配已证缺依赖，不吞结构错误："+layout.ResourceError);
        return broken.Length!=0;
    }

    static void VerifyMissingGatewayEntrance(TcpTestResources resources, DungeonCatalog catalog, int dungeon)
    {
        var done = new HashSet<int> {5927};
        var landing = ChannelAreaLanding.Decide(resources.Towns,new(new(2,49),40,"[beast]"),95,done);
        That(!resources.Towns.Areas.ContainsKey((51,2)) && landing.Spot is null && landing.Reason=="town-not-in-catalog",
            "真实缺51/2时频道准入精确拒绝，不造魔兽选图落点");
        var character = new Character(1,0,"BeastMissing",0,resources.Towns.InitialReturn,resources.Towns.InitialReturn)
            {Level=95,CompletedQuests=[5927]};
        bool missing = !catalog.ContainsDungeon(dungeon);
        That(missing || SourceUnavailable(catalog,dungeon),"缺入口分支仍须证明网关或源布局实际缺依赖");
        That(!DungeonEntryPlan.TryCreate(catalog,character,(uint)dungeon,0,2,out _,out var reason,beastMode:true) &&
            reason==(missing ? "dungeon-resource-unavailable" : "beast-gateway-source-unavailable"),
            "真实网关资源和频道入口分别精确拒绝："+dungeon+" / "+reason);
        Console.WriteLine("真实魔兽入口"+dungeon+"未执行Session六阶段正向：缺51/2；已验证频道与计划拒绝，合成正向另报");
    }

    // 仅为本测试提供完整输入；不会替换共享资源、真实PVF或任何生产目录。
    sealed class BeastSessionFixture : IDisposable
    {
        const int MapFirst = 900000000, Monster = 900000100;
        readonly PvfArchive archive;
        public DungeonCatalog Dungeons {get;}
        public DungeonRoomCatalog Rooms {get;}
        public DungeonMonsterDropCatalog Drops {get;}
        public TownCatalog Towns {get;}
        public ItemCatalog Items {get;}
        public Func<DungeonEntryPlan,Func<DungeonPlannedMonster,DungeonDropGenerationPlan>> PrepareDrops {get;}

        public BeastSessionFixture(TcpTestResources resources)
        {
            var pvf = new FixtureArchive();
            var sources = Enumerable.Range(310,5).ToArray();
            var gateways = Enumerable.Range(BeastDungeonRotation.FirstGatewayDungeonId,5).ToArray();
            pvf.Script("dungeon/dungeon.lst",sources.Concat(gateways).SelectMany(id=>new[] {I(id),pvf.S("selftest-beast/"+id+".dgn")}).ToArray());
            pvf.Script("map/map.lst",Enumerable.Range(0,15).SelectMany(i=>new[] {I(MapFirst+i),pvf.S("selftest-beast/"+i+".map")}).ToArray());
            pvf.Script("monster/monster.lst",I(Monster),pvf.S("selftest-beast/actor.mob"));
            // 两个目录的解析器要求非空；占位从未被房间引用，不创建额外演员。
            pvf.Script("passiveobject/passiveobject.lst",I(0),pvf.S(""));
            pvf.Script("aicharacter/aicharacter.lst",I(0),pvf.S("selftest-beast/unused.aic"));
            pvf.Script("monster/selftest-beast/actor.mob",pvf.T("no champion"),I(0),pvf.T("item"),pvf.T("/item"));
            for(int i=0;i<15;i++)
                pvf.Script("map/selftest-beast/"+i+".map",pvf.T("monster"),I(Monster),I(1),I(0),I(100),I(100),I(0),I(1),I(1),
                    pvf.S("[fixed]"),pvf.S(i>=9 ? "[boss]" : "[normal]"),pvf.T("/monster"));
            pvf.Text("etc/selftest-beast-balance.tbl","[MAX_HP]\n[NORMAL]\n5.00 5.00 5.00\n[/MAX_HP]\n[ATTACK]\n[NORMAL]\n2.30 0.00 0.00\n[/ATTACK]\n");
            foreach(int id in sources)
            {
                PvfToken[] maps = Enumerable.Range(0,15).SelectMany(i=>new[] {pvf.S(i>=9 ? "Beast" : "map"),I(i%5),I(i/5),I(MapFirst+i)}).ToArray();
                pvf.Script("dungeon/selftest-beast/"+id+".dgn",[..Header(),..Maze(false),pvf.T("Beast dungeon"),
                    pvf.T("BEAST LEVEL"),I(90),I(120),pvf.T("size"),I(5),I(3),
                    pvf.T("greed"),pvf.S("JJNNNNNNMMLLPPPPPPOODDHHHHHHGG"),pvf.T("map specification"),..maps,pvf.T("/map specification"),
                    pvf.T("start map"),I(0),I(0),pvf.T("/start map"),pvf.T("boss map"),I(-1),I(-1),pvf.T("/boss map"),
                    pvf.T("beast dungeon balance table"),pvf.S("etc/selftest-beast-balance.tbl"),
                    pvf.T("cutscene image"),pvf.S("Map/CutScene/Beast.img"),I(0)]);
            }
            foreach(int id in gateways)
                pvf.Script("dungeon/selftest-beast/"+id+".dgn",[..Header(),pvf.T("beast dungeon"),I(1),..Maze(true)]);
            // 正式Session会先查询悲叹目录，即使本次目标不是悲叹；沿用已存在的两表，不另造无关玩法规则。
            foreach(string path in new[] {GriefTowerCatalog.ResourcePath,GriefTowerRewardTable.ResourcePath})
                pvf.Import(path,resources.Archive);
            archive=PvfArchive.OpenBytes(pvf.Encode());
            try
            {
                Dungeons=DungeonCatalog.Load(archive);
                Rooms=DungeonRoomCatalog.Load(archive);
                Drops=DungeonMonsterDropCatalog.Load(archive);
                Items=new(resources.Archive,PvfScript.List(resources.Archive.ReadText("equipment/equipment.lst")),
                    PvfScript.List(resources.Archive.ReadText("stackable/stackable.lst")),0);
                Items.PrefillTemplates(BeastDungeonRotation.StageRewardItems.Select(id=>new KeyValuePair<int,ItemTemplate>(id,
                    new(id,ItemKind.Consumable,0,1,0,99,0,0,0,"",0,0,"显式合成魔兽地下城奖励袖珍罐"))));
                Towns=TownFixture(sources,gateways);
                var empty=new DungeonDropGenerationPlan("显式合成无附加掉落；六阶段奖励走原生产事务",[]);
                PrepareDrops=entry=>monster=>empty;
            }
            catch {archive.Dispose();throw;}

            PvfToken[] Header()=>[pvf.T("minimum required level"),I(90),pvf.T("basis level"),I(90),
                pvf.T("difficulty level"),I(1),I(1),I(1),I(1),I(1)];
            PvfToken[] Maze(bool gateway)=>[pvf.T("maze info"),pvf.T("size"),I(1),I(1),pvf.T("greed"),pvf.S("BB"),
                pvf.T("map specification"),pvf.S(gateway ? "Beast" : "boss"),I(0),I(0),I(MapFirst),pvf.T("/map specification"),
                pvf.T("start map"),I(0),I(0),pvf.T("/start map"),pvf.T("boss map"),I(gateway ? -1 : 0),I(gateway ? -1 : 0),pvf.T("/boss map")];
        }

        static PvfToken I(int value)=>new(0,value);
        static TownCatalog TownFixture(int[] sources,int[] gateways)
        {
            var room=new Location(38,1,100,100);
            var origin=new Location(1,0,100,100);
            var gate=new Location(51,2,100,100);
            var bounds=new WalkRegion(new(0,0,200,200),[],[]);
            var dungeonGate=new WalkRegion(new(80,80,40,40),[],[]);
            var map=new TownMap("map/selftest-beast/town.map",[bounds],[],false,[]) {DungeonGates=[dungeonGate]};
            return new(new()
            {
                [(38,1)]=new(38,1,1,[],[],[],room,map with {IsRoom=true,DungeonGates=[]},[],[]),
                [(1,0)]=new(1,0,1,[],[],[],origin,map with {Links=[new(new(0,0,10,10),38,1,[],[])]},[],[]) {DungeonWorldMap=900},
                [(51,2)]=new(51,2,1,[],[],[],gate,map,[],[]) {DungeonWorldMap=901}
            },room,origin,new(0,[],[]),[],new Dictionary<uint,uint[]>(),new Dictionary<int,TownDungeonMap>
            {
                [900]=new(900,"selftest-beast/source",[..sources.Select(id=>new TownDungeonOption(id,-1,false))]),
                [901]=new(901,"selftest-beast/gateway",[..gateways.Select(id=>new TownDungeonOption(id,-1,false))])
            });
        }
        public void Dispose()=>archive.Dispose();
    }

    // 局部复用Tools/PvfLib.Tests/ArchiveFixture的非variant编码方式；只为本测试打包内存输入。
    sealed class FixtureArchive
    {
        readonly MemoryStream names=new();
        readonly Dictionary<string,int> strings=new(StringComparer.Ordinal);
        readonly List<(string Path,int Type,byte[] Bytes)> files=[];
        public PvfToken S(string value)
        {
            if(!strings.TryGetValue(value,out int offset))
            {
                offset=checked((int)names.Length*2);
                names.Write(Encoding.UTF8.GetBytes(value+'\0'));
                strings.Add(value,offset);
            }
            return new(6,offset);
        }
        public PvfToken T(string name)=>new(3,S("["+name+"]").Value);
        public void Script(string path,params PvfToken[] tokens)
        {
            byte[] bytes=new byte[tokens.Length*5];
            for(int i=0;i<tokens.Length;i++) {bytes[i*5]=tokens[i].Type;Put(bytes,i*5+1,tokens[i].Value);}
            files.Add((path,1,bytes));
        }
        public void Text(string path,string text)=>files.Add((path,3,Encoding.Unicode.GetBytes(text)));
        public void Import(string path,PvfArchive source)
        {
            var tokens=source.ReadTokens(path);
            That(tokens.All(token=>token.Type is 0 or 2 or 3 or 6),"沿用表仅含已支持的标量/字符串，不复制悬空外部引用："+path);
            Script(path,tokens.Select(token=>token.Type is 3 or 6
                ? new PvfToken(token.Type,S(source.ResolveString(token.Value)).Value) : token).ToArray());
        }
        public byte[] Encode()
        {
            using var body=new MemoryStream();
            byte[] table=new byte[files.Count*24],groups=new byte[files.Count*8];
            for(int i=0;i<files.Count;i++)
            {
                Put(table,i*24,S(files[i].Path).Value);Put(table,i*24+4,-1);Put(table,i*24+8,i);
                Put(table,i*24+16,files[i].Bytes.Length);Put(table,i*24+20,files[i].Type);
                byte[] packed=Compress(files[i].Bytes);Crypt(packed,"BodY");body.Write(packed);
                Put(groups,i*8,checked((int)body.Length));Put(groups,i*8+4,files[i].Bytes.Length);
            }
            byte[] pool=[..new byte[8],..Pool(names.ToArray(),false),..Pool(Encoding.Unicode.GetBytes("\0"),true)];
            byte[] header=new byte[48];Put(header,0,0x69706B6E);Put(header,24,files.Count);Put(header,32,checked((int)body.Length));
            Put(header,36,files.Count);Put(header,44,pool.Length);Crypt(header,"HeaD");
            for(int i=24;i<28;i++) header[i]^=0x55;
            Crypt(groups,"GRPI");
            return [..header,..table,..pool,..groups,..body.ToArray()];
        }
        static byte[] Pool(byte[] raw,bool wide)
        {
            byte[] packed=Compress(raw);Crypt(packed,wide ? "sTrW" : "sTrA",0x269EC9);
            byte[] prefix=new byte[8];Put(prefix,0,packed.Length^unchecked((int)(wide ? 0x9A82F037u : 0xAA74472Eu)));
            Put(prefix,4,raw.Length^packed.Length);return [..prefix,..packed];
        }
        static byte[] Compress(byte[] raw)
        {
            if(raw.Length==0) return [0x78,0x9C,0x03,0,0,0,0,1];
            using var output=new MemoryStream();
            using(var zip=new ZLibStream(output,CompressionLevel.Fastest,true)) zip.Write(raw);
            return output.ToArray();
        }
        static void Put(byte[] bytes,int offset,int value)=>BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset),value);
        static void Crypt(Span<byte> bytes,string key,uint magic=0x269EC3)
        {
            unchecked
            {
                uint seed=0x76826701u*key[0]+0x1C1u*(key[3]+0x1C1u*(key[2]+0x1C1u*key[1]));
                for(int pos=0;pos<bytes.Length;pos+=4)
                {
                    uint first=0x343FD*seed+magic;seed=0x343FD*first+magic;
                    uint mask=(first&0xFFFF0000)+(seed>>16);
                    for(int i=0;i<Math.Min(4,bytes.Length-pos);i++) bytes[pos+i]^=(byte)(mask>>(8*i));
                }
            }
        }
    }
}
