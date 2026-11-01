using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

// 仅由正常Runtime的666夹具调用；不手工Attach、授权或绑定Run。
internal static partial class LukeRaidRunTcpTests
{
    public static void Single(TcpTestFixture fixture,TcpPeer peer,int dungeon)
    {
        var rows=Enter([peer],peer,dungeon);
        That(rows[0].Any(p=>p is {Kind:0,Opcode:29}),"单人成立普通分队也沿真实15/16进入团本Run");
        Load([peer]);
        Return([peer],peer);
        Enter([peer],peer,dungeon);
        Load([peer]);
        Return([peer],peer);
        That(fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0",peer.Id) is [[2L]],
            "单人两次进入沿原尝试事务，不由测试建立Run");
    }

    // 全部九条路线沿真实666/15/16/39/42消费，不手工推进Battle或修改HP。
    internal static void FixedRoute(TcpTestFixture fixture,TcpPeer peer,RaidDungeonCatalog catalog,RaidScript raw)
    {
        foreach(int dungeon in new[]{5054,5055,5056})
        {
            int symbol=raw.Symbols[raw.Phases[0].Dungeons.Single(row=>row.Id==dungeon).HpSymbol];
            for(int step=0;step<3;step++)
            {
                var entry=Enter([peer],peer,dungeon);
                That(entry[0].Single(packet=>packet.Opcode==28).Body[7]==step,"显式组0每个Run冻结对应SourceOrdinal而非始终maze0");
                Load([peer]);
                if(step==1)
                {
                    Return([peer],peer);
                    entry=Enter([peer],peer,dungeon);
                    That(entry[0].Single(packet=>packet.Opcode==28).Body[7]==step,"中途退出真实42不跳ordinal，重进仍同step");
                    Load([peer]);
                }
                Clear(fixture,[peer],[],peer,entry[0],catalog,expectedHp:(symbol,2-step));
                Return([peer],peer);
            }
            Select(peer,dungeon);
            That(!Drain(peer).Any(packet=>packet.Opcode is 28 or 29),"第三次清图后不能继续入图，未绕过Clear状态");
        }
    }

    public static void Parties(TcpTestFixture fixture,TcpPeer[] peers,int[] dungeons,byte[] firstRoster,byte[] secondRoster,RaidDungeonCatalog catalog)
    {
        var first=peers.Take(4).ToArray();var second=peers.Skip(4).ToArray();
        var captain=first.Single(peer=>peer.Id==Leader(firstRoster));
        var otherCaptain=second.Single(peer=>peer.Id==Leader(secondRoster));
        Select(captain,dungeons[2]);
        var hidden=Drain(captain);
        That(hidden.Any(p=>p is {Kind:1,Opcode:15}&&p.Body.SequenceEqual(new byte[]{0,1,0}))&&
            !hidden.Any(p=>p.Opcode is 28 or 29),"尚未正式进入5054前，原始Hide第三图拒绝");
        foreach(var peer in first.Where(peer=>peer!=captain))Drain(peer);
        var entered=Enter(first,captain,dungeons[0]);
        foreach(var frames in entered)
            foreach(int opened in dungeons.Skip(1))
                That(frames.Any(p=>p is {Kind:0,Opcode:586}&&p.Body.Length==6&&U32(p.Body)==opened&&p.Body[4]==0&&p.Body[5]==0),
                    "真实入5054后，本分队收到5055/56开放586，不借手工Open");
        foreach(var peer in second)
        {
            var updates=Drain(peer);
            foreach(int opened in dungeons.Skip(1))
                That(updates.Any(p=>p is {Kind:0,Opcode:586}&&U32(p.Body)==opened&&p.Body[5]==0),"另一分队同步收到真实Enter开图");
            That(updates.Any(p=>p is {Kind:0,Opcode:583}&&p.Body.Length==1+8*p.Body[0]),"另一分队收到同view完整symbol快照");
        }
        ushort partyId=U16(entered[0].Last(p=>p.Opcode==9).Body,4);
        Load(first);
        Select(otherCaptain,dungeons[0]);
        var refused=Drain(otherCaptain);
        That(refused.Any(p=>p is {Kind:1,Opcode:16}&&p.Body[0]==0)&&refused.Count(p=>p is {Kind:0,Opcode:27})==1&&!refused.Any(p=>p.Opcode is 28 or 29),
            "第二分队不能占用第一分队同一阶段地下城");
        foreach(var peer in second.Where(peer=>peer!=otherCaptain))That(!Drain(peer).Any(p=>p.Opcode is 28 or 29),"冲突拒绝不部分安装其它成员Run");
        Enter(second,otherCaptain,dungeons[1],sceneAlreadyOpen:true);Load(second);
        ManualCoins(fixture,first,second,0,8);
        ManualCoins(fixture,second,first,0,1);
        Return(first,captain);
        captain.Send(15,BitConverter.GetBytes(dungeons[0]));
        That(!Drain(captain).Any(p=>p is {Kind:1,Opcode:15,Body:[1]}),"队长先返回而同分队仍在Dungeon时不得重开选择");
        foreach(var peer in first.Where(peer=>peer!=captain))Drain(peer);
        var returning=first.First(peer=>peer!=captain);
        returning.Send(72,[2,2]);
        That(!Drain(returning).Any(packet=>packet.Opcode==72), "未清图不能借72焦点提前获得结果菜单");
        foreach(var peer in first.Where(peer=>peer!=captain))Return(first,peer);
        var repeated=Enter(first,captain,dungeons[0]);
        That(repeated.All(rows=>U16(rows.Last(p=>p.Opcode==9).Body,4)==partyId),"全员回Town后保留原普通分队编号再入图");
        Load(first);
        Clear(fixture,first,second,captain,repeated[0],catalog);
        // 第二轮队长最后退出，覆盖Run队长迁移而不拆除固定Raid分队。
        returning.Send(72,[2,2]);
        That(Drain(returning).Any(packet=>packet is {Kind:1,Opcode:72,Body:[1,2,2]}),
            "卢克已提交共享清图允许原生72结果焦点，不要求普通46翻牌");
        Return(first,returning,resultCommand:true);
        foreach(var peer in first.Where(peer=>peer!=captain&&peer!=returning))Return(first,peer);
        Return(first,captain);
        foreach(var peer in second.Where(peer=>peer!=otherCaptain))Return(second,peer);
        Return(second,otherCaptain);
        // 正常断线是真实移除，不因42保队策略留下幽灵成员；幸存三人继续用原Run回城。
        Enter(second,otherCaptain,dungeons[1]);Load(second);
        var disconnected=second.Last(peer=>peer!=otherCaptain);disconnected.Dispose();
        bool removed=false;
        for(int index=0;index<256&&!removed;index++)
        {
            var packet=otherCaptain.Read();Validate(packet);
            if(packet is {Kind:0,Opcode:9}&&packet.Body[9]==2)
            {
                That(packet.Body[11]==3&&packet.Body.Length==12+3*8+10&&
                    Enumerable.Range(0,3).All(i=>U16(packet.Body,13+i*8)!=disconnected.Id),
                    "断线按原普通分队成员包移除精确角色，幸存三人无幽灵槽");
                removed=true;
            }
        }
        That(removed,"断线清理在有界通知预算内完成");
        var survivors=second.Where(peer=>peer!=disconnected).ToArray();
        foreach(var peer in survivors)Drain(peer);
        foreach(var peer in survivors.Where(peer=>peer!=otherCaptain))Return(survivors,peer);
        Return(survivors,otherCaptain);
        // 原分队现为三人；所有幸存者回Town后仍可新建Run，证明旧断线/旧Run未占住地下城。
        Enter(survivors,otherCaptain,dungeons[1]);Load(survivors);
        foreach(var peer in survivors.Where(peer=>peer!=otherCaptain))Return(survivors,peer);
        Return(survivors,otherCaptain);
        That(fixture.Query("SELECT count(*) FROM characters WHERE town=32 AND area=4") is [[12L]],
            "所有已开团角色最终仍在正式候战位置");
    }

    static void Clear(TcpTestFixture fixture,TcpPeer[] members,TcpPeer[] others,TcpPeer captain,List<WirePacket> entry,RaidDungeonCatalog catalog,byte phase=0,(int Symbol,int Value)? expectedHp=null,int? passiveTarget=null)
    {
        byte[] information=entry.Single(p=>p.Opcode==28).Body;
        byte[] map=entry.Single(p=>p.Opcode==29).Body;
        var maze=catalog.Find((int)U32(information))!.Mazes.Single(row=>row.SourceOrdinal==information[7]);
        var route=QuestStoryRouteTests.Path(maze.StaticTopology,new(map[0],map[1]),new(information[8],information[9]));
        ushort last=0;byte lastLevel=0; byte[]? lastSymbols=null;
        var otherSymbols=new bool[others.Length];var otherClears=new bool[others.Length];
        void ObserveOthers()
        {
            for(int index=0;index<others.Length;index++)
            {
                var updates=Drain(others[index]);
                otherSymbols[index]|=updates.Any(p=>p is {Kind:0,Opcode:583});
                otherClears[index]|=updates.Any(p=>p is {Kind:0,Opcode:586}&&U32(p.Body)==U32(information));
                That(updates.All(p=>p.Opcode!=587),"同Fighting阶段清图只更新583/586，不重播587");
            }
        }
        foreach(var point in route)
        {
            foreach(int offset in Enumerable.Range(0,map[18]).Select(index=>19+index*22)
                .Where(offset=>map[offset+13]!=9&&map[offset+14]==0).OrderBy(offset=>map[offset+13]==3))
            {
                last=U16(map,offset+6);lastLevel=map[offset+12];
                captain.Send(39,FormalDungeonTcpTests.Death(last,captain.Id,lastLevel));
                var own=Drain(captain);
                lastSymbols=own.LastOrDefault(packet=>packet.Opcode==583)?.Body ?? lastSymbols;
                That(!own.Any(p=>p.Opcode is 31 or 115 or 601),"Luke受控清图不借普通结算许可或空团本奖励窗口");
                That(own.Any(p=>p is {Kind:0,Opcode:38}&&U16(p.Body)==last),"队长真实39提交死亡");
                foreach(var member in members.Where(peer=>peer!=captain))
                    That(Drain(member).Any(p=>p is {Kind:0,Opcode:38}&&U16(p.Body)==last),"共同Run各成员正式死亡投影");
                // 另一分队也是活跃客户端，逐事件消费；不能积压整图怪物快照后误报单批预算超限。
                ObserveOthers();
            }
            if(point==route[^1])break;
            var next=route[Array.IndexOf(route,point)+1];byte[] move=new byte[100];move[0]=(byte)next.X;move[1]=(byte)next.Y;
            captain.Send(45,move);map=Drain(captain).Single(p=>p.Opcode==29).Body;
            foreach(var member in members.Where(peer=>peer!=captain))Bytes(Drain(member).Single(p=>p.Opcode==29).Body,map,"共同45仍为同图同对象");
            Load(members);
        }
        if(passiveTarget is { } target)
        {
            string before=fixture.Snapshot();
            captain.Send(39,QuestPassiveClearTcpTests.PassiveDeath((uint)(target+1)));
            foreach(var peer in members)That(!Drain(peer).Any(packet=>packet.Opcode==38),"错误能源模板不能伪造怪物死亡");
            That(fixture.Snapshot()==before,"错误能源模板不写入任何事务或推进团本");
            captain.Send(39,QuestPassiveClearTcpTests.PassiveDeath((uint)target));
            foreach(var peer in members)
            {
                var frames=Drain(peer);
                That(!frames.Any(packet=>packet.Opcode==38),"能源销毁没有伪造普通死亡38");
                lastSymbols=frames.LastOrDefault(packet=>packet.Opcode==583)?.Body??lastSymbols;
            }
            int signal=target==64062?113:114;
            That(lastSymbols is not null&&Enumerable.Range(0,lastSymbols[0]).Any(index=>
                U32(lastSymbols,1+index*8)==signal&&U32(lastSymbols,5+index*8)==1),"真实物件提交后583发布原值能源信号1");
        }
        if(expectedHp is { } hp)
            That(lastSymbols is not null && Enumerable.Range(0,lastSymbols[0]).Any(index=>
                U32(lastSymbols,1+index*8)==hp.Symbol && U32(lastSymbols,5+index*8)==hp.Value),
                "真实死亡事务提交后HP恰减1，与下一次ordinal推进一致");
        ObserveOthers();
        for(int index=0;index<others.Length;index++)
            That(otherSymbols[index]&&otherClears[index],
                "另一分队收到真实清图后的symbol及本图状态，不借重复开场动画同步");
        if(passiveTarget is { } repeatedTarget)captain.Send(39,QuestPassiveClearTcpTests.PassiveDeath((uint)repeatedTarget));
        else captain.Send(39,FormalDungeonTcpTests.Death(last,captain.Id,lastLevel));
        foreach(var peer in members.OrderBy(peer=>peer==captain?0:1))
            That(!Drain(peer).Any(p=>p.Opcode==38),"终图死亡重放不重复成员死亡事务");
        foreach(var peer in others)Drain(peer);
        That(fixture.Query("SELECT count(*),coalesce(sum(amount),0) FROM character_dungeon_growth_receipts") is [[long receipts,0L]]&&receipts>0,
            "禁经验仍提交正式死亡receipt，但全员到账经验严格0");

    }

    static ushort Leader(byte[] roster)
    {
        int start=checked(17+(int)U32(roster,13)+24);
        int count=roster[start++];int tail=start+count*8;
        That(count is >=1 and <=4&&roster.Length==tail+10&&roster[tail]==0,"完整普通分队名册具有受限成员和原生尾部");
        byte leader=roster[tail+2];
        var entries=Enumerable.Range(0,count).Select(index=>start+index*8).Where(at=>roster[at]==leader).ToArray();
        That(entries.Length==1,"从真实服务端名册解析唯一当前分队长，不猜重连后的槽位");
        return U16(roster,entries[0]+1);
    }

    static void Select(TcpPeer captain,int dungeon,bool native=false,bool open=true)
    {
        if(open)captain.Send(15,BitConverter.GetBytes(native?0:dungeon));
        // 实测原生流程先15=0，目标图及FFFFFFFF尾字段在16中提交；同时保留非零15兼容用例。
        captain.Send(16,Build(w=>{w.Write(dungeon);w.Write((byte)2);w.Write((ushort)0);w.Write((byte)0);w.Write((byte)0);
            w.Write(ushort.MaxValue);w.Write(0u);w.Write((byte)0);w.Write(0u);w.Write((byte)0);w.Write(native?uint.MaxValue:0u);}));
    }
    static List<WirePacket>[] Enter(TcpPeer[] members,TcpPeer captain,int dungeon,bool sceneAlreadyOpen=false)
    {
        captain.Send(15,new byte[4]);
        var opened=members.Select(peer=>Drain(peer)).ToArray();
        foreach(var frames in opened)
        {
            That(frames.Count(p=>p is {Kind:0,Opcode:27,Body.Length:42})==(sceneAlreadyOpen?0:1) &&
                !frames.Any(p=>p.Opcode is 28 or 29 or 1536),
                "15零值独立建立全分队scene2，无需先发送16，不提前安装Run或普通目录");
            int ack=frames.FindIndex(p=>p is {Kind:1,Opcode:15,Body:[1]});
            int scene=frames.FindIndex(p=>p is {Kind:0,Opcode:27});
            That(ack>=0&&(sceneAlreadyOpen?scene<0:scene>ack),"新开选图ACK15后发布27，已打开时保留原场景");
            That(!frames.Any(p=>p is {Kind:0,Opcode:6}),"团本打开选图只隐藏城镇实体，保留原分队Actor及P2P引用");
        }
        Select(captain,dungeon,native:true,open:false);
        var leading=Drain(captain);
        var entered=members.Select(peer=>peer==captain?leading:Drain(peer)).ToArray();
        var rows=opened.Select((frames,index)=>frames.Concat(entered[index]).ToList()).ToArray();
        That(leading.Any(p=>p is {Kind:1,Opcode:15,Body:[1]})&&!leading.Any(p=>p is {Kind:1,Opcode:16}),
            "候战室原生15零值成功，连续16沿原28/29通知进入而不伪造ACK");
        byte[] map=rows[0].Single(p=>p is {Kind:0,Opcode:29}).Body;
        foreach(var frames in rows)
        {
            That(frames.Count(p=>p is {Kind:0,Opcode:27})==(sceneAlreadyOpen?0:1) &&
                (sceneAlreadyOpen||frames.FindIndex(p=>p is {Kind:0,Opcode:27})<frames.FindIndex(p=>p is {Kind:0,Opcode:28})),
                "团本进图前恰有一个27场景边界，16确认不能重复打开选图");
            That(frames.Count(p=>p is {Kind:0,Opcode:28})==1,"每成员仅安装一次正式Run");
            Bytes(frames.Single(p=>p is {Kind:0,Opcode:29}).Body,map,"同分队共用同一地图、随机种子与怪物对象键");
        }
        return rows;
    }

    static void SelectionBoundaries(TcpPeer[] members,TcpPeer captain,int dungeon)
    {
        captain.Send(15,new byte[4]);
        foreach(var peer in members)
        {
            var frames=Drain(peer);
            That(frames.Count(p=>p is {Kind:0,Opcode:27,Body.Length:42})==1 && !frames.Any(p=>p.Opcode is 28 or 29),
                "只发15零值即可建立真实选图，尚未确认目标时没有Run");
        }
        captain.Send(15,new byte[4]);
        foreach(var peer in members)That(!Drain(peer).Any(p=>p.Opcode is 27 or 28 or 29),"重发15不重建已打开的原生场景");
        foreach(int repeat in new[]{0,1})
        {
            captain.Send(15,BitConverter.GetBytes(dungeon));
            foreach(var peer in members)
            {
                var selected=Drain(peer);
                That(selected.Any(p=>p is {Kind:1,Opcode:15,Body:[1]})&&!selected.Any(p=>p.Opcode is 27 or 28 or 29),
                    "原生选图内确认或更换目标不重建场景，也不提前进图");
            }
        }
        Select(captain,0,open:false);
        foreach(var peer in members)
        {
            var rejected=Drain(peer);
            That(!rejected.Any(p=>p.Opcode is 27 or 28 or 29),"目标不匹配被拒后仍留在原选图，不部分建立Run");
            if(peer==captain)That(rejected.Any(p=>p is {Kind:1,Opcode:16,Body:[0,9,0,0]}),"目标不匹配16返回明确拒绝");
        }
        captain.Send(132);
        foreach(var peer in members)
        {
            var returned=Drain(peer);
            That(returned.Any(p=>p is {Kind:1,Opcode:132,Body:[1]})&&returned.Any(p=>p is {Kind:0,Opcode:24}),
                "未选图的132可让全分队正常回候战，不遗留虚假Run");
        }
        foreach(bool send15 in new[]{true,false})
        {
            Select(captain,dungeon,open:send15);
            foreach(var peer in members)
            {
                var frames=Drain(peer);
                int scene=frames.FindIndex(p=>p is {Kind:0,Opcode:27,Body.Length:42});
                That(scene>=0&&frames.Count(p=>p is {Kind:0,Opcode:27})==1&&
                    frames.FindIndex(p=>p is {Kind:0,Opcode:28})>scene&&frames.FindIndex(p=>p is {Kind:0,Opcode:29})>scene,
                    "非零15直入与仅16直入均先建立全分队scene2，再安装28/29");
                That(!frames.Any(p=>p is {Kind:0,Opcode:6}),"团本城镇直入也保留原Actor，不能通过销毁重建刷新名册");
            }
            Load(members);
            foreach(var peer in members)Return(members,peer);
        }
    }
    static void Load(TcpPeer[] members)
    {
        foreach(var peer in members.SkipLast(1))
        {
            peer.Send(37,new byte[8]);
            That(!Drain(peer).Any(p=>p.Opcode==30),"其余成员未加载时不提前释放屏障");
        }
        members[^1].Send(37,new byte[8]);
        That(Drain(members[^1]).Any(p=>p is {Kind:1,Opcode:37,Body:[1]}),"末成员完成原加载屏障");
        foreach(var peer in members.SkipLast(1))That(Drain(peer).Any(p=>p is {Kind:1,Opcode:37,Body:[1]}),"全员完成原加载屏障");
        foreach(var peer in members)Drain(peer);
    }
    static List<WirePacket> Return(TcpPeer[] members,TcpPeer target,bool phaseResults=false,bool resultCommand=false)
    {
        if(resultCommand)target.Send(72,[1,2]);else target.Send(42);
        var returned=Drain(target,phaseResults);
        int ack=returned.FindIndex(p=>resultCommand ? p is {Kind:1,Opcode:72,Body:[1,1,2]} : p is {Kind:1,Opcode:42,Body:[1]});
        int state=returned.FindIndex(p=>p is {Kind:0,Opcode:3}&&p.Body.SequenceEqual(new byte[]{1,(byte)target.Id,(byte)(target.Id>>8),0}));
        int town=returned.FindIndex(p=>p is {Kind:0,Opcode:24}&&p.Body.Length==13&&p.Body[0]==32&&p.Body[1]==4&&U16(p.Body,4)==target.Id);
        int quests=returned.FindIndex(p=>p is {Kind:0,Opcode:21});
        int completed=returned.FindIndex(p=>p is {Kind:0,Opcode:356});
        That(ack>=0&&state>ack&&town>state&&quests>town&&completed>quests,
            "原生回城严格ACK→本人Town状态→32/4位置→21/356任务刷新，不以任意旁路包冒充回城");
        foreach(var peer in members.Where(peer=>peer!=target))returned.AddRange(Drain(peer,phaseResults));
        return returned;
    }
    static List<WirePacket> Drain(TcpPeer peer,bool phaseResults=false)
    {
        peer.SendBarrier();List<WirePacket> rows=[];
        for(int index=0;index<256;index++)
        {
            var packet=peer.Read();
            if(packet is {Kind:1,Opcode:1347}){Bytes(packet.Body,[1],"Luke Run连接屏障");return rows;}
            if(packet is {Kind:0,Opcode:778})
                That(packet.Body.Length==1&&packet.Body[0]<=20,"778严格一字节已用数，非phase+remaining");
            else if(packet is {Kind:0,Opcode:32})
                That(packet.Body.Length==4&&packet.Body[2] is 0 or 1&&packet.Body[3]==0,"手动复活仅原生死亡/复活状态通知");
            else if(packet is {Kind:1,Opcode:41})
                That(packet.Body is [1,_,_,0] or [0,1,0],"41仅既有本人复活成功或额度拒绝");
            else if(packet is {Kind:0,Opcode:574})
                That(packet.Body.Length>=4&&U32(packet.Body)<=30&&packet.Body.Length==4+6*U32(packet.Body),
                    "能源场景沿原生命周期提交后同步完整活动任务列表，保留每日自动任务");
            else if(phaseResults&&packet is {Kind:0,Opcode:601})
                That(packet.Body.Length==10&&packet.Body[0] is 0 or 2,"显式两阶段策略只接已证10B成功结果，不接600未知展示");
            else if(phaseResults&&packet is {Kind:0,Opcode:600})
                That(packet.Body.Length>=22&&packet.Body[1]==1&&packet.Body[5] is >=1 and <=32&&packet.Body.Length==6+16*packet.Body[5],"Luke结果按实际物品数保留完整16B布局");
            else if(phaseResults&&packet is {Kind:1,Opcode:660})
                That(packet.Body.SequenceEqual(new byte[]{1})||packet.Body.SequenceEqual(new byte[]{0,0x12,0}),"660仅已证成功或明确清pending失败包");
            else if(packet is {Kind:0,Opcode:592})
                That(packet.Body.Length==2+3*U16(packet.Body),"候战成员列表保留完整数量及条目");
            else Validate(packet);
            rows.Add(packet);
        }
        throw new InvalidOperationException("Luke Run通知超过有界预算");
    }
    static void Validate(WirePacket packet)
    {
        if (packet is { Kind: 0, Opcode: 583 })
        {
            var body = packet.Body;
            That(body.Length >= 1 && body.Length == 1 + 8 * body[0], "583完整消费真实符号快照");
            var ids = new HashSet<uint>();
            for (int index = 0; index < body[0]; index++)
            {
                int at = 1 + 8 * index;
                That(U32(body, at) > 0 && ids.Add(U32(body, at)), "583符号非零且唯一，不发送清表哨兵");
                That(U32(body, at) != 102 || U32(body, at + 4) != 0,
                    "102零值仍会打开原生结果菜单，所有开团/加载/战斗/城镇快照均不得发送");
            }
            return;
        }
        if (packet is { Kind: 0, Opcode: 563 or 1460 or 1461 })
        {
            var body = packet.Body;
            That(body.Length >= 2, "账号材料及周额度保留完整两字节头");
            bool balances = packet.Opcode == 563;
            int count = balances ? body[1] : U16(body);
            int width = balances ? 8 : 12;
            That(body.Length == 2 + count * width && (!balances || body[0] is 0 or 1),
                "563全量/差量与1460/1461周额度各按原生行宽完整消费");
            var ids = new HashSet<uint>();
            for (int index = 0; index < count; index++)
            {
                int at = 2 + index * width;
                That(U32(body, at) is > 0 and <= int.MaxValue && ids.Add(U32(body, at)) &&
                    U32(body, at + 4) <= int.MaxValue &&
                    (balances ? body[0] != 0 || U32(body, at + 4) > 0 : U32(body, at + 8) is > 0 and <= int.MaxValue),
                    "账号材料模板唯一、数值合法，全量不带零余额且周额度有真实上限");
            }
            return;
        }
        if (packet is { Kind: 0, Opcode: 765 }) { RaidPanelTests.ReadMemberStates(packet.Body); return; }
        if (packet is { Kind: 0, Opcode: 1446 }) { RaidPanelTests.ReadWeeklyClears(packet.Body); return; }
        if (packet is { Kind: 1, Opcode: 1361 }) { RaidPanelTests.ReadClears(packet.Body); return; }
        That(packet.Kind==0 && packet.Opcode==597 && packet.Body.Length==5 && packet.Body[0] is 0 or 1 && U32(packet.Body,1)<=86400 || packet.Kind==0&&packet.Opcode is
        2 or 3 or 4 or 6 or 7 or 8 or 9 or 11 or 13 or 14 or 21 or 22 or 23 or 24 or 26 or 27 or 28 or 29 or 30 or 31 or 37 or 38 or 153 or 356 or 583 or 586 or 587 or 590 or 591 or 593 or 594 or 598 ||
        packet.Kind==1&&packet.Opcode is 15 or 16 or 37 or 42 or 45 || packet is {Kind:1,Opcode:662,Body:[1]} ||
        packet is {Kind:1,Opcode:72,Body:[1,1,2] or [1,2,2]} ||
        packet is {Kind:0,Opcode:835,Body.Length:7} || packet is {Kind:1,Opcode:668,Body:[1] or [0,19,0]} ||
        packet is {Kind:1,Opcode:132,Body:[1]},
        $"Luke Run只消费已证通知/应答：{packet.Kind}/{packet.Opcode}");
    }

}
