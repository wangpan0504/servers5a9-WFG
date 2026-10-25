using System.Buffers.Binary;
using System.Diagnostics;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Skills;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunAltarsAsync(ServerOptions source,string root,TcpTestResources resources)
    {
        DungeonAltarSessionTests.Run(resources);
        var clock = new AltarTimers(20);
        await using var fixture = new TcpTestFixture(source,Path.Combine(root,"dungeon-altars"),resources:resources,clock:clock);
        await fixture.StartAsync();
        var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 11006));
        var done = area.Quests.Concat(area.AnyQuests.Take(1)).Concat(area.Default.Bounds.SelectMany(row=>row.Quests))
            .Concat(area.Default.DungeonGates.SelectMany(row=>row.Quests)).Distinct().ToArray();
        var bounds = area.Map(done.ToHashSet()).Bounds.First(row=>row.Conditions.Length==0 && row.Quests.All(done.Contains)).Bounds;
        var position = new DfoServer.GameWorld.Towns.Location(area.Town,area.Area,
            checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        var growth = SelfTestCatalogs.Growth(resources.Archive);
        foreach (uint id in new uint[] {11006,11007})
        {
            Character created;
            using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,()=>{}))
            {
                var repo = new CharacterRepository(fixture.Database);
                var candidate = CharacterCreation.Prepare(id==11006?"Blood466":"Ultimate466",resources.Characters.Get(0),
                    position,position,resources.Learning.Get);
                created = repo.Create(owner,candidate with { Level=70,AcceptedStoryLevel=70,CompletedQuests=done,
                    InitialLearning=SkillPointLedger.Synchronize(0,0,70,candidate.InitialLearning with
                        { Experience=growth.GetLevelStartExperience(70) },resources.Learning) });
                created = repo.CommitInventory(owner,created,new(created.Inventory,created.Inventory with
                    { Gold=500000,ReviveCoins=3 },"altar_tcp_seed"));
            }
            using var peer = Open(fixture);
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
            // 新角色悲叹为第1层；塔类入口严格核对1536→1303→两包550→27。
            SelectTowerMap(peer,griefLayer:1);
            var request = Selection();
            BinaryPrimitives.WriteUInt32LittleEndian(request,id);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16),0);
            peer.Send(16,request);
            var info = peer.Expect(0,212).Body;
            var map = peer.Expect(0,211).Body;
            int fee = id==11006?22000:0;
            if (fee>0) peer.Expect(0,14);
            That(info.Length==12 && U32(info,0)==id && map.Length==15 && map[7]==1 &&
                fixture.Query("SELECT gold FROM characters WHERE id=$p0",peer.Id) is [[long gold]] && gold==500000-fee,
                "实际CMD16按212→211接入祭坛，金币只扣本次资源费用");
            peer.Barrier();
            Load(peer);
            // 未准备UI前无波次；伪造普通255对象和空报文均不能越过此门禁。
            peer.Send(255,[1,255,255]); peer.Barrier();
            peer.Send(258);
            peer.Expect(0,215);
            var next = peer.Read();
            while (next is {Kind:0,Opcode:213}) next=peer.Read();
            That(next is {Kind:0,Opcode:210} && next.Body.Length==4+U16(next.Body,0)*17 && U16(next.Body,0)>0,
                "UI准备后实际计时发布210，正文数量和17字节对象记录相符");
            // 只合成已发布对象的死亡报告，未使用普通39经验/掉落。
            int count=U16(next.Body,0);
            byte[] killed=new byte[1+count*2]; killed[0]=checked((byte)count);
            for(int at=0;at<count;at++) next.Body.AsSpan(3+at*17,2).CopyTo(killed.AsSpan(1+at*2));
            peer.Send(255,killed);
            peer.Send(40,[0x74,0,4,1,0,0]);
            ReadUntil(32);
            peer.Send(41,[(byte)peer.Id,(byte)(peer.Id>>8)]);
            peer.Expect(1,41,[1,(byte)peer.Id,(byte)(peer.Id>>8),0]);
            peer.Expect(0,32,[(byte)peer.Id,(byte)(peer.Id>>8),1,0]); peer.Expect(0,14,CoinBalance(2));
            peer.Send(40,[0x74,0,4,1,0,0]); ReadUntil(32);
            peer.Send(255,killed);
            var ranking=ReadUntil(208).Body;
            That(ranking.Length==24 && U32(ranking,16)==(id==11006?15:7) &&
                fixture.Query("SELECT count(*) FROM dungeon_altar_clear_receipts WHERE character_id=$p0",peer.Id) is [[1L]] &&
                fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0",peer.Id) is [[2L]],
                "第二次死亡截止后仅提交一次专用失败奖励，第一轮死亡回调与复活不重复结算");
            var rewards=peer.Expect(0,209).Body;
            That(rewards.Length>=6 && rewards[1]==(id==11006?15:7) && rewards[^3..].All(value=>value==0),
                "失败209保留四队员槽与真实完成回合，不替换成普通翻牌");
            var state=ReadUntil(3);
            That(state.Body is [1,_,_,0],"祭坛专用演出结束恢复本人城镇state0");
            peer.Expect(0,24); RefreshedQuests(peer); peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM dungeon_special_entry_receipts WHERE character_id=$p0",peer.Id) is [[1L]] &&
                fixture.Query("SELECT count(*) FROM dungeon_altar_clear_receipts WHERE character_id=$p0",peer.Id) is [[1L]],
                "一次准入和一次失败持久事实保持独立，计时不重复收费或发奖");

            WirePacket ReadUntil(ushort opcode)
            {
                for(int messages=0;messages<200;messages++)
                {
                    var packet=peer.Read();
                    if(packet.Kind==0 && packet.Opcode==opcode) return packet;
                    That(packet.Kind==0 && packet.Opcode is 2 or 13 or 14 or 19 or 21 or 36 or 37 or 210 or 213 or 215 or 297,
                        $"祭坛计时只允许专用波次、到账与成长投影，不混入普通结算；收到{packet.Kind}/{packet.Opcode}");
                }
                throw new InvalidOperationException("祭坛预期通知没有到达："+opcode);
            }
        }
        Console.WriteLine("PASS 两祭坛实际TCP准入、UI门禁、计时刷怪、255、复活与失败专用结算（非客户端验收）");
    }

    sealed class AltarTimers(int factor) : TimeProvider
    {
        readonly long origin=Stopwatch.GetTimestamp();
        public override long GetTimestamp() => checked((Stopwatch.GetTimestamp()-origin)*factor);
        public override long TimestampFrequency => Stopwatch.Frequency;
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period) =>
            base.CreateTimer(callback,state,Scale(dueTime),Scale(period));
        TimeSpan Scale(TimeSpan value) => value==Timeout.InfiniteTimeSpan || value<=TimeSpan.Zero?value:
            TimeSpan.FromMilliseconds(Math.Max(1,Math.Ceiling(value.TotalMilliseconds/factor)));
    }
}
