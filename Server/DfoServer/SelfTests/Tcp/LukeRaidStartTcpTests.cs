using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Raids;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.AntonRaidTcpTests;

namespace DfoServer.Testing;

// 正常Runtime和666入口。明确合成无行为执行策略，只保留当前PVF的阶段/DGN/symbol身份；不代表原服规则已恢复。
internal static class LukeRaidStartTcpTests
{
    public static Task RunAsync(ServerOptions source,string root,TcpTestResources resources) => RunCoreAsync(source,root,resources,false);
    public static Task RunDungeonsAsync(ServerOptions source,string root,TcpTestResources resources) => RunCoreAsync(source,root,resources,true);

    public static Task RunPhasesAsync(ServerOptions source,string root,TcpTestResources resources) => RunCoreAsync(source,root,resources,true,true);

    static async Task RunCoreAsync(ServerOptions source,string root,TcpTestResources resources,bool runDungeons,bool completePhases=false)
    {
        var definition=RaidCatalog.Load(resources.Archive).PrepareResources(resources.Archive,
            SelfTestCatalogs.Dungeons(resources.Archive),resources.ItemResources.Items).Find(1)!;
        var raw=definition.Resources!.Script;
        var execution=new RaidScript("selftest/luke-start-only.etc",raw.WaitingRoom,raw.EntryCost,raw.RetryItem,raw.Symbols,
            raw.Phases.Select(phase=>new RaidPhase(phase.Name,phase.Dungeons,[],[])).ToImmutableArray(),[],[]);
        // 受控Run夹具只启用真实Enter规则：5054正式安装后开放5055/56，不再手工改初态。
        // 未启用其余阶段结束/对象行为，不能称完整原生两阶段策略。
        if(runDungeons) execution=execution with {Phases=execution.Phases.SetItem(0,execution.Phases[0] with
            {Rules=raw.Phases[0].Rules.Where(rule=>rule.Trigger.Any(clause=>clause.Tag=="[on enter dungeon]")).ToImmutableArray()})};
        if(completePhases)
        {
            // 保留P2真实初始化（5311/5313开放）；终结条件为明确合成单目标，不冒充原40条完整机制。
            execution=execution with {Phases=execution.Phases.SetItem(1,execution.Phases[1] with {Rules=[raw.Phases[1].Rules[0]]})};
            execution=execution with {Phases=execution.Phases.Select((phase,index)=>phase with
                {Rules=phase.Rules.Add(new([new("[on change dungeon state]",900,index==0?5054:5311,null,null,null,[]),
                    new("[check dungeon state]",901,index==0?5054:5311,null,RaidDungeonState.Clear,null,[])],
                    [new("[clear phase]",902,null,null,null,null,[])]))}).ToImmutableArray()};
        }
        var catalog=definition.Resources!.EntryCatalog!;
        var policy=new LukeRaidStartPolicy("selftest/no-phase-actions-no-rewards",execution)
        { Run=runDungeons ? new LukeRaidRunPolicy("selftest/explicit-first-maze-no-economy",
            raw.Phases.SelectMany((phase,index)=>phase.Dungeons.Select(dungeon=>new LukeRaidMazeChoice(index,dungeon.Id,
                catalog.Find(dungeon.Id)!.Mazes.First(maze=>maze.QuestConnectionTokens.Count==0).SourceOrdinal))).ToImmutableArray(),
            LukeRaidRunEconomy.ControlledNoRewardsOrRevival) : null };
        if(completePhases) policy=policy with {Completion=new("selftest/explicit-terminal-receipts",2,
            new(DayOfWeek.Sunday,new TimeOnly(6,0),TimeSpan.FromHours(8)),
            Enumerable.Range(0,2).Select(phase=>new LukePhaseCompletionPolicy(0,2,
                new("selftest/explicit-phase-"+phase,[new(0,1,[new(10400,1)],[]),new(1,1,[new(10400,1)],[])],
                    new(0,(byte)phase,1,0,0,0)))).ToImmutableArray(),LukeParticipationCutoff.TerminalCommittedFacts)};
        await using var fixture=new TcpTestFixture(source,Path.Combine(root,completePhases?"luke-raid-phases":runDungeons?"luke-raid-run":"luke-raid-start"),resources:resources,lukeStartPolicy:policy);
        await fixture.StartAsync();
        var endpoints=ChannelTcpTests.Endpoints(fixture); var repository=new CharacterRepository(fixture.Database);
        var owners=new AccountOwners(); List<TcpPeer> peers=[];
        int observedStarts=0;
        async Task<System.Text.Json.JsonElement> NextStartLogAsync()
        {
            var timer=System.Diagnostics.Stopwatch.StartNew();
            while(timer.Elapsed<TimeSpan.FromSeconds(5))
            {
                try
                {
                    var rows=fixture.Events().Where(e=>e.GetProperty("Kind").GetString()=="raid_start_request").ToArray();
                    if(rows.Length>observedStarts)return rows[observedStarts++].GetProperty("Detail");
                }
                catch(System.Text.Json.JsonException) { } // 日志可能正在刷新最后一行；只重读，不猜reason。
                await Task.Delay(10);
            }
            throw new InvalidOperationException("等待本次666结构化日志超时");
        }
        TcpPeer CreatePeer(int index)
        {
                string account="lukestart"+index; var origin=new Location(38,0,629,196);
                using(var owner=owners.Acquire(account,()=>{}))
                {
                    var candidate=CharacterCreation.Prepare("LkSt"+index,resources.Characters.Get(0),origin,origin,resources.Learning.Get);
                    var learning=SkillPointLedger.Synchronize(0,0,90,candidate.InitialLearning with
                        { Experience=resources.Growth.GetLevelStartExperience(90) },resources.Learning);
                    var role=repository.Create(owner,candidate with {Level=90,AcceptedStoryLevel=90,CompletedQuests=[2903,4872],InitialLearning=learning});
                    var inventory=new InventoryService(resources.ItemResources.Items,resources.ItemResources.GoldLimits,resources.ItemResources.EpicPieces);
                    InventoryService.Commit(repository,owner,role,inventory.Grant(role,[new(raw.EntryCost!.ItemId,2*raw.EntryCost.Count)]));
                }
                var peer=Open(fixture,account,gamePort:endpoints[20],channelNumber:20);peer.Select();return peer;
        }
        async Task<List<WirePacket>[]> StartAsync(List<TcpPeer> members,int leaderIndex,bool replay)
        {
            for(int attempt=0;attempt<32;attempt++)
            {
                string before=fixture.Snapshot();
                string assets=fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY character_id,item_uid");
                members[leaderIndex].Send(666);
                var leading=ReadStart(members[leaderIndex],true,replay,allowBusy:true);
                var startLog=await NextStartLogAsync();
                if(leading.Any(p=>p is {Kind:1,Opcode:666}&&p.Body.SequenceEqual(new byte[]{0,19,0})))
                {
                    var detail=startLog;
                    That(detail.GetProperty("Self").GetUInt16()==members[leaderIndex].Id&&
                        detail.GetProperty("Reason").GetString()=="raid-member-command-busy","只重试明确门禁Busy，不吞其它19错误");
                    foreach(var peer in members)Drain(peer,p=>That(p.Opcode is not (9 or 14 or 594 or 587),"Busy不发布名单/费用/启动状态"));
                    That(fixture.Snapshot()==before&&fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY character_id,item_uid")==assets,
                        "每次Busy保持全员位置和资产原样");
                    await Task.Yield();
                    continue;
                }
                var result=new List<WirePacket>[members.Count];result[leaderIndex]=leading;
                for(int i=0;i<members.Count;i++)if(i!=leaderIndex)result[i]=ReadStart(members[i],false,replay);
                return result;
            }
            throw new InvalidOperationException("Luke开团门禁连续32次Busy，保留现场");
        }
        try
        {
            for(int index=0;index<4;index++)peers.Add(CreatePeer(index));
            foreach(var peer in peers)Drain(peer);
            var leader=peers[0];
            leader.Send(664,Build(w=>{w.Write((byte)1);w.Write(Name("LkStart"));w.Write(new byte[4]);}));
            Until(leader,0,590);Until(leader,0,591);var createdRaid=Until(leader,1,664).Body;
            Bytes(createdRaid[..1],[1],"真实Runtime创建Luke团");uint raidKey=U32(createdRaid,1);
            CheckFlag(leader,raidKey,0);
            for(int index=1;index<peers.Count;index++)
            {
                leader.Send(10,Peer(peers[index].Id,0,true));Until(leader,1,10);Until(peers[index],0,7);
                peers[index].Send(11,Peer(leader.Id,0,false));Until(peers[index],1,11);
                foreach(var peer in peers)Drain(peer);
            }
            for(int index=0;index<peers.Count;index++)
            {
                leader.Send(669,Build(w=>{w.Write(0u);w.Write((uint)peers[index].Id);w.Write(index==0?1u:2u);}));
                Bytes(Until(leader,1,669).Body,[1],"允许一人与三人分队，不伪限定满员");
                foreach(var peer in peers)Drain(peer);
            }
            foreach(var peer in peers)CheckFlag(peer,raidKey,1);
            byte[] SingleParty()=>Build(w=>{w.Write((byte)0);w.Write((byte)0);w.Write(Name("Party : 1"));w.Write((byte)4);
                w.Write(uint.MaxValue);w.Write((byte)5);w.Write((ushort)0);w.Write((byte)0);w.Write((byte)0);w.Write((ushort)0);w.Write((byte)7);});
            leader.Send(12,SingleParty());var existing=Until(leader,0,9).Body;Until(leader,0,26);Drain(leader);
            ushort existingId=U16(existing,4);
            var first=await StartAsync(peers,0,false);
            That(U16(first[0].Single(p=>p.Opcode==9).Body,4)==existingId,"真实666复用已存在精确单人Party");
            var created=first.Skip(1).Select(frames=>U16(frames.Single(p=>p.Opcode==9).Body,4)).ToArray();
            That(created.Distinct().Count()==1&&created[0]!=existingId,"缺失三人分队一次安装，不覆盖原单人队");
            foreach(var frames in first)
            {
                CheckInitialSnapshot(frames);
                int prepare=frames.FindIndex(p=>p.Opcode==594),party=frames.FindIndex(p=>p.Opcode==9);
                That(party>=0&&party<prepare&&prepare<frames.FindIndex(p=>p.Opcode==587),"普通完整名单先于594，再发布真实Battle首阶段");
            }
            That(fixture.Query("SELECT sum(json_extract(item_core,'$.Value')) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=$p0",raw.EntryCost!.ItemId)
                is [[long total]]&&total==4*raw.EntryCost.Count,"四人成本各扣一次真实PVF门票");
            That(fixture.Query("SELECT count(*) FROM characters WHERE town=$p0 AND area=$p1",raw.WaitingRoom!.Town,raw.WaitingRoom.Area) is [[4L]],"全部提交到当前PVF候战室");
            CheckFlag(leader,raidKey,0);
            string committed=fixture.Snapshot();
            // 首次发布已写出；重试恢复名单/资产/图状态，但不能重播同阶段动画。
            foreach(var peer in peers)Drain(peer);
            var repeated=await StartAsync(peers,0,true);
            for(int index=0;index<peers.Count;index++)
            {
                CheckInitialSnapshot(repeated[index]);
                That(repeated[index].All(p=>p.Opcode!=587),"重复666恢复完整快照但不重播已发送的同阶段动画");
                Bytes(repeated[index].Single(p=>p.Opcode==583).Body,first[index].Single(p=>p.Opcode==583).Body,"重放完整symbol快照字节相同");
                That(U16(repeated[index].Single(p=>p.Opcode==9).Body,4)==U16(first[index].Single(p=>p.Opcode==9).Body,4),"重放沿原Party编号完整恢复，不重复建队");
                That(repeated[index].Any(p=>p.Opcode==13)&&!repeated[index].Any(p=>p.Opcode==14),"重放完整主背包而非丢失的原扣费差量");
            }
            leader.Send(12,SingleParty());Drain(leader,packet=>That(packet.Opcode is not (9 or 26),"594单人自动CMD12保持同设置无通知幂等"));
            foreach(int index in new[]{2,3})
            {
                peers[index].Send(11,Build(w=>{w.Write(peers[1].Id);w.Write((byte)0);w.Write(0u);}));
                Bytes(Until(peers[index],1,11).Body,Build(w=>{w.Write((byte)1);w.Write(peers[1].Id);w.Write((byte)0);}),"594多人成员同队原CMD11幂等");
            }
            foreach(var peer in peers)Drain(peer);
            That(fixture.Snapshot()==committed,"重放和原生分队握手不改任何持久资产/位置/次数");
            leader.Send(660,[0,0]);Bytes(Until(leader,1,660).Body,[0,0x12,0],"未终结660明确失败并清原生pending，不能断线或发成功");Drain(leader);
            That(fixture.Snapshot()==committed,"start-only Luke不借未类型隔离的Anton领奖表发奖");
            if(runDungeons) LukeRaidRunTcpTests.Single(fixture,leader,raw.Phases[0].Dungeons[0].Id);
            // 同一Runtime继续覆盖满八人跨两队；不另启服务，不手工Attach领域实例。
            foreach(var peer in peers)peer.Dispose();peers.Clear();
            for(int index=4;index<12;index++)peers.Add(CreatePeer(index));
            foreach(var peer in peers)Drain(peer);
            leader=peers[0];
            leader.Send(664,Build(w=>{w.Write((byte)1);w.Write(Name("LkEight"));w.Write(new byte[4]);}));
            Until(leader,0,590);Until(leader,0,591);var opened=Until(leader,1,664);
            Bytes(opened.Body[..1],[1],"同Runtime创建满员团");
            for(int index=1;index<8;index++)Join(leader,peers[index]);
            Assign(leader);
            foreach(var peer in peers)Drain(peer);
            string beforeFailure=fixture.Snapshot();
            string inventoryBefore=fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY character_id,item_uid");
            string auditBefore=fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");
            fixture.Sql($"CREATE TRIGGER luke_start_failure BEFORE UPDATE ON characters WHEN OLD.id={peers[7].Id} BEGIN SELECT RAISE(ABORT,'luke TCP last member rollback'); END");
            // 等待最后一个Barrier后仍可能Busy；仅已证Busy可重试，真正SQL异常沿原连接关闭合同。
            for(int attempt=0;;attempt++)
            {
                That(attempt<32,"SQL故障请求有界取得全团门禁");
                leader.Send(666);
                try
                {
                    var denied=Until(leader,1,666);
                    Bytes(denied.Body,[0,19,0],"SQL前仅允许真实Busy拒绝");
                    var reason=(await NextStartLogAsync()).GetProperty("Reason").GetString();
                    That(reason=="raid-member-command-busy","SQL故障注入不能把其它拒绝当已执行事务");
                    foreach(var peer in peers)Drain(peer,p=>That(p.Opcode is not (9 or 14 or 594 or 587),"失败前不部分建队或通知成功"));
                    await Task.Yield();
                }
                catch(EndOfStreamException){break;}
            }
            That(fixture.Snapshot()==beforeFailure&&fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY character_id,item_uid")==inventoryBefore&&
                fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id")==auditBefore,"末成员SQL失败回滚全部八人角色、门票UID/数量和审计");
            fixture.Sql("DROP TRIGGER luke_start_failure");
            // 关闭旧连接后按正常选角重连，幸存队长沿原邀请恢复八人，不直接改Registry。
            leader.Dispose();peers[0]=Open(fixture,"lukestart4",gamePort:endpoints[20],channelNumber:20);peers[0].Select();
            foreach(var peer in peers)Drain(peer,p=>That(p.Opcode is not (9 or 14 or 594 or 587),"失败后无任何分队安装或启动尾通知"));
            leader=peers[1];Join(leader,peers[0]);Assign(leader);
            foreach(var peer in peers)Drain(peer);
            var full=await StartAsync(peers,1,false);
            var fullIds=full.Select(frames=>U16(frames.Single(p=>p.Opcode==9).Body,4)).ToArray();
            That(fullIds.Take(4).Distinct().Count()==1&&fullIds.Skip(4).Distinct().Count()==1&&fullIds[0]!=fullIds[4],
                "自然666一次提交两支四人普通分队，编号不互相覆盖");
            foreach(var frames in full)That(frames.FindIndex(p=>p.Opcode==9)<frames.FindIndex(p=>p.Opcode==594)&&
                frames.FindIndex(p=>p.Opcode==594)<frames.FindIndex(p=>p.Opcode==587),"八人各自收到完整名单→594→同阶段状态");
            foreach(var frames in full)CheckInitialSnapshot(frames);
            foreach(var peer in peers)
            {
                That(fixture.Query("SELECT town,area FROM characters WHERE id=$p0",peer.Id) is [[long town,long area]]&&
                    town==raw.WaitingRoom!.Town&&area==raw.WaitingRoom.Area,"满团全员同事务到当前资源候战室");
                That(fixture.Query("SELECT json_extract(item_core,'$.Value') FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=$p1",peer.Id,raw.EntryCost!.ItemId)
                    is [[long count]]&&count==raw.EntryCost.Count,"满团成功只扣一次门票，失败未留部分扣费");
            }

            if(completePhases) LukeRaidRunTcpTests.Phases(fixture,peers.ToArray(),raw,
                full[0].Single(p=>p.Opcode==9).Body,catalog);
            else if(runDungeons) LukeRaidRunTcpTests.Parties(fixture,peers.ToArray(),raw.Phases[0].Dungeons.Select(row=>row.Id).ToArray(),
                full[0].Single(p=>p.Opcode==9).Body,full[4].Single(p=>p.Opcode==9).Body,catalog);

            void CheckInitialSnapshot(List<WirePacket> frames)
            {
                var symbols=frames.Single(p=>p.Opcode==583).Body;
                That(symbols.Length==1+raw.Symbols.Count*8&&symbols[0]==raw.Symbols.Count,"初态583完整有界符号表");
                var actual=Enumerable.Range(0,symbols[0]).ToDictionary(i=>checked((int)U32(symbols,1+i*8)),i=>checked((int)U32(symbols,5+i*8)));
                foreach(var symbol in raw.Symbols)
                {
                    int expected=raw.Phases[0].Dungeons.FirstOrDefault(d=>d.HpSymbol==symbol.Key)?.HpRaw??0;
                    That(actual[symbol.Value]==expected,"使用PVF显式symbol ID和原HP初值，不按数组下标");
                }
                var states=frames.Where(p=>p.Opcode==586).ToArray();
                That(states.Length==15&&states.Select(p=>U32(p.Body)).Distinct().Count()==15,"初态覆盖全部15图以清除旧阶段残留");
                foreach(var packet in states)
                {
                    var dungeon=raw.Phases[0].Dungeons.FirstOrDefault(d=>d.Id==U32(packet.Body));
                    That(packet.Body.Length==6&&packet.Body[4]==0&&packet.Body[5]==(byte)(dungeon?.InitialState??RaidDungeonState.Hide),
                        "586中间保留0，非当前phase的已登记图显式Hide");
                }
            }

            void Join(TcpPeer captain,TcpPeer member)
            {
                captain.Send(10,Peer(member.Id,0,true));Until(captain,1,10);Until(member,0,7);
                member.Send(11,Peer(captain.Id,0,false));Until(member,1,11);
                foreach(var peer in peers)Drain(peer);
            }
            void Assign(TcpPeer captain)
            {
                for(int i=0;i<8;i++)
                {
                    captain.Send(669,Build(w=>{w.Write(0u);w.Write((uint)peers[i].Id);w.Write((uint)(i/4+1));}));
                    Bytes(Until(captain,1,669).Body,[1],"满团真实编排两个四人分队");
                    foreach(var peer in peers)Drain(peer);
                }
            }

        }
        finally{foreach(var peer in peers)peer.Dispose();}
        await fixture.StopAsync();
        if(completePhases)
        {
            LukeRaidLifecycleTransactionTests.Run(resources,definition,policy);
            using var deletionOwner=owners.Acquire("lukestart4",()=>{});
            var old=repository.LoadCharacter(deletionOwner,0)!;
            That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE character_id=$p0 AND state=2",old.Id) is [[1L]],
                "删角回归前角色有真实666/两阶段正式清图签发的通关事实");
            repository.Delete(deletionOwner,repository.LoadDeletionTarget(deletionOwner,old.Slot));
            That(repository.Import(deletionOwner,[old])==1&&repository.LoadCharacter(deletionOwner,old.Slot)!.Id==old.Id,
                "沿原Delete/Import确实重用同character ID，不靠假定ID永不重用");
            That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE character_id=$p0",old.Id) is [[0L]]&&
                fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE character_id=$p0",old.Id) is [[0L]],
                "同id新角色不能继承旧实例参与/周clear/阶段资格或奖励receipt");
            That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE state=2") is [[7L]],
                "删除精确角色不清同团其它七人的已提交通关");
        }
        if(runDungeons&&!completePhases)
        {
            // StopAsync已收尾日志writer；不把正在刷新的JSON尾行当作完整事件。
            var clears=fixture.Events().Where(row=>row.GetProperty("Kind").GetString()=="raid_run_clear").Select(row=>row.GetProperty("Detail")).ToArray();
            That(clears.Length==1&&clears[0].GetProperty("Members").GetInt32()==4&&
                clears[0].GetProperty("AfterRevision").GetInt64()==clears[0].GetProperty("BeforeRevision").GetInt64()+1,
                "真实Runtime四人共同Run只推进Battle一次，重复事实仅重同步");
            var entries=fixture.Events().Where(row=>row.GetProperty("Kind").GetString()=="formal_dungeon_entry")
                .Select(row=>row.GetProperty("Detail")).Where(row=>row.TryGetProperty("RaidType",out var type)&&type.GetInt32()==1&&row.GetProperty("Accepted").GetBoolean()).ToArray();
            That(entries.Select(row=>row.GetProperty("Members").GetInt32()).SequenceEqual(new[]{1,1,4,4,4,4,3})&&
                entries.Select(row=>row.GetProperty("Dungeon").GetInt32()).SequenceEqual(new[]{5054,5054,5054,5055,5054,5055,5055})&&
                entries.Take(2).Select(row=>row.GetProperty("RaidInstance").GetGuid()).Distinct().Count()==1&&
                entries.Skip(2).Select(row=>row.GetProperty("RaidInstance").GetGuid()).Distinct().Count()==1&&
                entries[0].GetProperty("RaidInstance").GetGuid()!=entries[2].GetProperty("RaidInstance").GetGuid()&&
                entries.All(row=>row.GetProperty("Runs").GetArrayLength()==1)&&
                entries.Select(row=>row.GetProperty("Runs")[0].GetGuid()).Distinct().Count()==7,
                "七次入场精确覆盖两次单人、四次四人及断线后三人，同步notice不计人数；实例/图/唯一Run均匹配流程");
            LukeRaidRunTransactionTests.Run(resources,definition,policy);
        }
        That(fixture.Events().Any(e=>e.GetProperty("Kind").GetString()=="closed"&&
            e.GetProperty("Detail").GetProperty("Reason").GetString()=="SqliteException"&&
            e.GetProperty("Detail").GetProperty("Message").GetString()!.Contains("luke TCP last member rollback",StringComparison.Ordinal)),
            "故障连接确由末成员SQL注入关闭，不把任意网络错误当事务回滚证据");
        var accepted=fixture.Events().Where(e=>e.GetProperty("Kind").GetString()=="raid_start_request").Select(e=>e.GetProperty("Detail"))
            .Where(e=>e.GetProperty("Accepted").GetBoolean()).ToArray();
        That(accepted.Length==3&&accepted.All(e=>e.GetProperty("Instance").GetGuid()==e.GetProperty("BattleInstance").GetGuid())&&
            accepted[0].GetProperty("Instance").GetGuid()==accepted[1].GetProperty("Instance").GetGuid()&&accepted[1].GetProperty("Replay").GetBoolean()&&accepted[2].GetProperty("Members").GetInt32()==8&&
            accepted[2].GetProperty("Instance").GetGuid()!=accepted[0].GetProperty("Instance").GetGuid(),
            "自然666经Runtime绑定与重放同一个真实Started/Battle实例，未额外Attach");
    }

    static void CheckFlag(TcpPeer peer,uint key,byte expected)
    {
        peer.Send(1361,Build(w=>{w.Write((byte)0);w.Write(key);}));
        AssertStartFlag(Until(peer,1,1361).Body,key,expected);
    }
    internal static void AssertStartFlag(byte[] body,uint key,byte expected)
    {
        That(body.Length>=22&&body[0]==1&&body[1]==0&&U32(body,2)==key&&U32(body,6)==key,"能力检查来自完整1361 mode0原生记录");
        int at=14+checked((int)U32(body,10));
        That(at+16<=body.Length&&body[at]==1&&body[at+7]==expected,"Luke记录+DC与Runtime实际可用启动能力一致");
    }

    static void Validate(WirePacket packet) => That(packet.Kind==0&&packet.Opcode is
        2 or 3 or 4 or 6 or 7 or 8 or 9 or 11 or 13 or 14 or 22 or 23 or 24 or 26 or 153 or 583 or 586 or 590 or 591 or 592 or 593 or 594 or 598,
        $"Luke夹具仅消费已证旁路通知：{packet.Kind}/{packet.Opcode}");

    static WirePacket Until(TcpPeer peer,byte kind,ushort opcode)
    {
        for(int index=0;index<256;index++){var packet=peer.Read();if(packet.Kind==kind&&packet.Opcode==opcode)return packet;Validate(packet);}
        throw new InvalidOperationException("Luke通知超出有界预算");
    }
    static void Drain(TcpPeer peer,Action<WirePacket>? inspect=null)
    {
        peer.Send(1278);
        for(int index=0;index<256;index++)
        {
            var packet=peer.Read();if(packet is {Kind:1,Opcode:1278}){Bytes(packet.Body,[1],"后续连接仍健康");return;}
            Validate(packet);inspect?.Invoke(packet);
        }
        throw new InvalidOperationException("Luke旁路排空超出有界预算");
    }

    static List<WirePacket> ReadStart(TcpPeer peer,bool leader,bool replay,bool allowBusy=false)
    {
        List<WirePacket> frames=[];bool started=false,ack=!leader;
        for(int index=0;index<256&&(!started||!ack);index++)
        {
            var packet=peer.Read();frames.Add(packet);
            if(packet is {Kind:0,Opcode:587}){That(!replay,"已发送的同阶段重放不得重复587动画");Bytes(packet.Body,[2,0],"Luke首次状态严格2与phase0");started=true;}
            else if(packet is {Kind:1,Opcode:666})
            {
                if(allowBusy&&packet.Body.SequenceEqual(new byte[]{0,19,0}))
                {
                    That(frames.All(p=>p.Opcode is not (9 or 14 or 594 or 587)),"Busy之前也没有部分启动通知");
                    return frames;
                }
                Bytes(packet.Body,[1],"666已证单字节成功");ack=true;
            }
            else That(packet.Kind==0&&packet.Opcode is 2 or 3 or 4 or 6 or 9 or 11 or 13 or 14 or 22 or 23 or 24 or 26 or 153 or 583 or 586 or 591 or 594 or 598,
                $"Luke启动只消费已证通知：{packet.Kind}/{packet.Opcode}");
            if(replay&&frames.Count(p=>p.Opcode==586)==15&&frames.Any(p=>p.Opcode==583))started=true;
        }
        That(started&&ack&&frames.Single(p=>p.Opcode==594).Body.Length==0,"各成员完成有界启动/恢复链");
        That(frames.Any(p=>p.Opcode==(replay?13:14)),"首次增量与重放完整背包分别使用已证opcode");
        return frames;
    }
}
