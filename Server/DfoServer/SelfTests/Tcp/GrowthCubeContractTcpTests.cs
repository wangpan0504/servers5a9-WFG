using static DfoServer.Testing.CashMileageTcpChecks;
using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class GrowthCubeContractTcpTests
{
    public static async Task RunAsync(ServerOptions options,string root,TcpTestResources resources)
    {
        GrowthCubeContractTests.RunResources(resources.ItemResources,resources.Towns,resources.Characters);
        var definitions=resources.ItemResources.Contracts;
        VerifyCubeEffects(resources);
        var growth=resources.ItemResources.CashShop.Products.Values.First(row=>row.ItemId==2660408 && row.Section=="premium");
        var cube=resources.ItemResources.CashShop.Products.Values.First(row=>row.ItemId==10000387 && row.Section=="premium");
        var clock = new Clock();
        await using var fixture=new TcpTestFixture(options,Path.Combine(root,"growth-cube-contract"),resources:resources,clock:clock);
        var repository=new CharacterRepository(fixture.Database);
        using (var owner=new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,()=>{}))
        {
            var candidate=CharacterCreation.Prepare("CubeTest",resources.Characters.Get(0),resources.Towns.Room,resources.Towns.InitialReturn,null);
            var created = repository.Create(owner,candidate with { Inventory=candidate.Inventory with
            { Items=candidate.Inventory.Items.Add(new(0,new(InventoryListType.Main,65),
                    InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(10000387)))) } });
            repository.CommitInventory(owner, created, new(created.Inventory, created.Inventory with
            { AccountWallet = created.Inventory.AccountWallet.Set(CurrencyKind.Cera,100000).Set(CurrencyKind.ClearCube,5)
                .Set(CurrencyKind.BlueCube,2) }, "test_seed"));
            repository.Create(owner,candidate with { Name="CubePeer" });
        }
        await fixture.StartAsync();
        long growthExpiry,cubeExpiry;
        using (var peer=Open(fixture))
        {
            peer.Select();
            That(Contracts().Count==0,"上号不扫描或消费遗留背包契约");
            peer.Send(535,[1,4]);peer.Expect(1,535,[0,1,0]);peer.Barrier();
            That(fixture.Query("SELECT * FROM cube_premium").Length==0,"未开通契约不能写选择或假装生效");
            var mileage = MileageBefore(fixture, ExpectedCashMileage(growth));
            Buy(peer,growth.CommodityId);peer.Expect(1,64);peer.Expect(0,53);
            MileageAfter(peer, fixture, mileage);
            That(ReadPremium(peer.Expect(0,1760).Body).ContainsKey(84),"购买ACK和余额后1760开通成长84，不发实体14");
            Player(peer); Updated(peer, 84);
            growthExpiry=Contracts()[84];
            mileage = MileageBefore(fixture, ExpectedCashMileage(cube));
            Buy(peer,cube.CommodityId);peer.Expect(1,64);peer.Expect(0,53);
            MileageAfter(peer, fixture, mileage);
            var rights=ReadPremium(peer.Expect(0,1760).Body);
            That(rights.ContainsKey(84) && rights.ContainsKey(97),"统一目录保留成长并激活晶体，不覆盖其他服务");
            peer.Expect(0,901,[0,255]); Updated(peer, 97);peer.Barrier();cubeExpiry=Contracts()[97];
            That(CountLegacy()==1,"获得新契约不新增背包实体，也不自动吞掉遗留实体");
            peer.Send(535,[1,4]);peer.Expect(1,535,[1,1,4]);peer.Expect(0,901,[1,4]);Player(peer);peer.Barrier();
            That(fixture.Query("SELECT selected,cube_type FROM cube_premium") is [[long selected,long type]] && selected==1 && type==4,
                "本版535保存selected/cube_type并901回放，选择不扣晶块");
            mileage = MileageBefore(fixture, ExpectedCashMileage(cube));
            Buy(peer,cube.CommodityId);peer.Expect(1,64);peer.Expect(0,53);
            MileageAfter(peer, fixture, mileage);
            That(ReadPremium(peer.Expect(0,1760).Body).ContainsKey(97), "已有晶块选择的续期仍更新统一资格");
            Updated(peer,97);peer.Barrier();cubeExpiry=Contracts()[97];
            That(fixture.Query("SELECT selected,cube_type FROM cube_premium") is [[1L,4L]],
                "续期不重置颜色、不重装相同效果，不使已配置角色重新进入未选择提示路径");
            string before=fixture.Snapshot("SELECT * FROM cube_premium")+fixture.Snapshot("SELECT revision FROM characters");
            peer.Send(535,[1,4]);peer.Expect(1,535,[1,1,4]);peer.Barrier();
            That(before==fixture.Snapshot("SELECT * FROM cube_premium")+fixture.Snapshot("SELECT revision FROM characters"),"重复选择零变化不写库");
            peer.Send(535,[0,255]);peer.Expect(1,535,[1,0,255]);peer.Expect(0,901,[0,255]);Player(peer);peer.Barrier();
            That(Contracts()[97]==cubeExpiry,"取消不删除账号资格，也不变成黑色索引0");
            peer.Send(535,[1,4]);peer.Expect(1,535,[1,1,4]);peer.Expect(0,901,[1,4]);Player(peer);peer.Barrier();
            await ExerciseContractConsumption(peer);
            peer.Send(7);peer.Expect(1,7,[1]);peer.Expect(0,2);peer.Select(1,premiumContracts:true);
            Bytes(peer.CubeContractInfo,[0,255],"同账号期限共享、不同角色选择独立");
            That(Contracts()[84]==growthExpiry && Contracts()[97]==cubeExpiry,"换角不补发契约或重复续期");
        }
        await fixture.StopAsync();await fixture.StartAsync("restart");
        using (var peer=Open(fixture))
        {
            peer.Select(premiumContracts:true);Bytes(peer.CubeContractInfo,[0,4],"重启按持久选择恢复而非默认黑色");
            That(CountLegacy()==1 && Contracts()[97]==cubeExpiry,"重启不扫描背包或重复激活");
            fixture.Sql("CREATE TRIGGER fail_cube BEFORE UPDATE ON cube_premium BEGIN SELECT RAISE(ABORT,'cube failure'); END");
            peer.Send(535,[1,0]);peer.Closed();
            That(fixture.Query("SELECT selected,cube_type FROM cube_premium") is [[long selected,long type]] && selected==0 && type==4,
                "SQL失败不发送成功，不推进选择或其他账号数据");
            fixture.Sql("DROP TRIGGER fail_cube");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(premiumContracts: true);
            string before = fixture.Snapshot("SELECT id,revision FROM characters") + fixture.Snapshot("SELECT * FROM account_wallets") +
                fixture.Snapshot("SELECT * FROM account_currency_balances") + fixture.Snapshot("SELECT * FROM currency_audit_log");
            fixture.Sql("CREATE TRIGGER fail_equipment_material BEFORE INSERT ON currency_audit_log WHEN NEW.action_name='delete_equipment_material' BEGIN SELECT RAISE(ABORT,'equipment material fault'); END");
            peer.Send(18, Convert.FromHexString("1000000010001A0A080310E50218DC172001200001"));
            peer.Closed();
            That(before == fixture.Snapshot("SELECT id,revision FROM characters") + fixture.Snapshot("SELECT * FROM account_wallets") +
                fixture.Snapshot("SELECT * FROM account_currency_balances") + fixture.Snapshot("SELECT * FROM currency_audit_log"),
                "装备脚本材料晚期SQL失败回滚账号余额、角色revision和审计，不提前发消费成功");
            fixture.Sql("DROP TRIGGER fail_equipment_material");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(premiumContracts: true);
            peer.Send(535, [0,3]); peer.Expect(1,535,[1,0,3]); peer.Expect(0,901,[0,3]); Player(peer); peer.Barrier();
            EnterDungeon(peer);
            await WaitTimer();
            string before = Balances();
            fixture.Sql("CREATE TRIGGER fail_cube_tick BEFORE INSERT ON currency_audit_log WHEN NEW.action_name='cube_contract_consume' BEGIN SELECT RAISE(ABORT,'cube tick fault'); END");
            clock.Advance(TimeSpan.FromSeconds(30)); peer.Closed();
            That(before == Balances(), "周期消费晚期SQL失败整笔回滚、不发成功数量，并关闭故障连接而非反复扣款");
            fixture.Sql("DROP TRIGGER fail_cube_tick");
        }
        await fixture.StopAsync();

        void Buy(TcpPeer peer,int commodity) { byte[] body=new byte[19];body[2]=1;BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(7),commodity);peer.Send(64,body); }
        Dictionary<byte,long> ReadPremium(byte[] body)
        {
            That(body.Length==1+body[0]*9,"1760完整u8/u8/i64宽度");
            return Enumerable.Range(0,body[0]).ToDictionary(i=>body[1+i*9],i=>BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(2+i*9)));
        }
        Dictionary<int,long> Contracts()=>JsonSerializer.Deserialize<Dictionary<int,long>>((string)fixture.Query("SELECT premium_contracts FROM account_wallets").Single()[0])!;
        long CountLegacy()=>Convert.ToInt64(fixture.Query("SELECT count(*) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=10000387").Single()[0]);

        string Balances() => fixture.Snapshot("SELECT * FROM account_wallets") + fixture.Snapshot("SELECT * FROM account_currency_balances") +
            fixture.Snapshot("SELECT id,revision FROM characters") + fixture.Snapshot("SELECT * FROM currency_audit_log");
        void Updated(TcpPeer peer, byte type)
        {
            var body = peer.Expect(0,66).Body;
            That(body.Length == 11 && U16(body,0) == 2 && body[2] == type &&
                BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(3)) > 0, "购买后下发当前契约66，而不是只显示静默期限");
        }
        void Player(TcpPeer peer) => That(peer.Expect(0,2).Body[0] == 1, "效果变化后本人Player重装虚拟装备，不发送Actor");
        async Task WaitTimer()
        {
            for (int attempt = 0; attempt < 200 && clock.CubeTimerCount == 0; attempt++) await Task.Delay(10);
            That(clock.CubeTimerCount == 1 && clock.DailyTimerCount == 2 && clock.TimerCount == 3,
                "已加载副本且有材料时只有一个30秒晶体周期，同时保留礼盒及服务日唤醒，不依赖下一条客户端消耗请求");
        }
        void EnterDungeon(TcpPeer peer)
        {
            peer.Travel(38, 0, 622, 196, 38, 1, bottom: true);
            peer.Travel(38, 2, 1085, 249, 38, 0, kind: 5);
            peer.Send(15, new byte[4]); peer.Expect(1, 15, [1]); peer.Expect(0, 3); peer.Expect(0, 27);
            var selection = Convert.FromHexString("030000000000000000FFFF0000000000490C00000000000000");
            Array.Clear(selection, 16, 4);
            peer.Send(16, selection); peer.Expect(0, 28); peer.Expect(0, 29);
            FormalDungeonTcpTests.Load(peer);
            peer.Expect(0, 36);
            peer.Barrier();
        }
        async Task ExerciseContractConsumption(TcpPeer peer)
        {
            EnterDungeon(peer); await WaitTimer();
            clock.Advance(TimeSpan.FromSeconds(29)); peer.Barrier();
            That(ClearBalance() == 5, "30秒之前不提前扣晶块");
            peer.Send(535,[0,4]);peer.Expect(1,535,[1,0,4]);peer.Expect(0,901,[0,4]);peer.Barrier();
            clock.Advance(TimeSpan.FromSeconds(1)); Consumed(peer,4);
            peer.Send(535,[0,255]);peer.Expect(1,535,[1,0,255]);peer.Expect(0,901,[0,255]);Player(peer);peer.Barrier();
            clock.Advance(TimeSpan.FromSeconds(60)); peer.Barrier();
            That(ClearBalance() == 4 && clock.CubeTimerCount == 0 && clock.DailyTimerCount == 2 && clock.TimerCount == 2, "取消后停止晶体周期，保留礼盒及服务日通知，不补扣后台时间");
            peer.Send(535,[0,4]);peer.Expect(1,535,[1,0,4]);peer.Expect(0,901,[0,4]);Player(peer);peer.Barrier();
            await WaitTimer();
            peer.Send(7);peer.Expect(1,7,[1]);peer.Expect(0,2);peer.Send(63);peer.Expect(0,53);
            clock.Advance(TimeSpan.FromSeconds(60));peer.Send(63);peer.Expect(0,53);
            That(ClearBalance() == 4 && clock.TimerCount == 0, "返回选角取消原实例计时，旧周期不扣账号余额");
            peer.Select(premiumContracts:true);EnterDungeon(peer);await WaitTimer();
            peer.Send(18,Convert.FromHexString("1000000010001A0A080310E50218DC172001200001"));
            peer.Expect(1,18,Convert.FromHexString("010D0000000800120708E502100118031800"));peer.Barrier();
            That(fixture.Query("SELECT balance FROM account_currency_balances WHERE currency_kind=6") is [[1L]],
                "其他装备type3消费仍单独足额扣蓝色晶块，不被晶体契约周期接管");
            for (int remaining = 3; remaining >= 0; remaining--)
            {
                clock.Advance(TimeSpan.FromSeconds(30)); Consumed(peer,remaining);
            }
            string before = Balances();clock.Advance(TimeSpan.FromMinutes(2));peer.Barrier();
            That(before == Balances() && clock.CubeTimerCount == 0 && clock.DailyTimerCount == 2 && clock.TimerCount == 2, "材料耗尽停止晶体周期并保留礼盒及服务日通知，无忙等或补扣，不自动改写角色选择");
            That(fixture.Query("SELECT count(*) FROM currency_audit_log WHERE action_name='cube_contract_consume'") is [[5L]],
                "原生晶体脚本不发周期C2S，服务端只记五次实际扣款");
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]);
            peer.Expect(0,24); FormalDungeonTcpTests.RefreshedQuests(peer); peer.Barrier();
            peer.Send(535,[0,3]); peer.Expect(1,535,[1,0,3]); peer.Expect(0,901,[0,3]); Player(peer); peer.Barrier();
            That(fixture.Query("SELECT selected,cube_type FROM cube_premium") is [[0L,3L]] &&
                clock.CubeTimerCount == 0 && clock.DailyTimerCount == 2 && clock.TimerCount == 2,
                "退出副本恢复城镇state0后可更换契约颜色，不重新选角且不在城镇消费晶块");
            peer.Send(535,[0,4]); peer.Expect(1,535,[1,0,4]); peer.Expect(0,901,[0,4]); Player(peer); peer.Barrier();

            long ClearBalance()
            {
                var rows = fixture.Query("SELECT balance FROM account_currency_balances WHERE currency_kind=7");
                return rows is [[var value]] ? Convert.ToInt64(value) : 0;
            }
            void Consumed(TcpPeer client, int remaining)
            {
                var update = client.Expect(0,1681).Body;
                That(update.Length == 126 && update[0] == 0 && U16(update,1) == 1 && U16(update,3) == 358 &&
                    U32(update,5) == 3037 && U32(update,9) == remaining, "周期用1681同步当前绝对晶块余额，无获得通知或伪造请求ACK");
                client.Barrier();
                That(ClearBalance() == remaining, "每30秒原库存事务准确扣一个晶块");
            }
        }
    }

    static void VerifyCubeEffects(TcpTestResources resources)
    {
        var equipment = PvfLib.PvfScript.List(resources.Archive.ReadText("equipment/equipment.lst"));
        foreach (var option in resources.ItemResources.Contracts.Cubes)
        {
            var script = new PvfLib.PvfDocument(resources.Archive.ReadText("equipment/" + equipment[option.EquipmentId]));
            var conditions = new PvfLib.PvfDocument(script.Block("if"));
            var actions = new PvfLib.PvfDocument(script.Block("then"));
            That(script.TryGetInt32("cool time", out int cooldown) && cooldown == 30000 &&
                conditions.TryGetInt32("cooltime", out int triggerCooldown) && triggerCooldown == 30000 &&
                PvfLib.PvfScript.Integers(conditions.Section("time")).SequenceEqual([30000, 1, 1000]) &&
                conditions.Block("module").Contains("[dungeon type]", StringComparison.Ordinal) &&
                actions.TryGetInt32("duration", out int duration) && duration == 30000 &&
                PvfLib.PvfScript.Integers(actions.Section("consume item")).SequenceEqual([option.ItemId, 1]),
                "六色契约效果使用当前PVF的地下城条件、30秒周期/持续和对应晶块一次1个，不复制其他版本计时器");
        }
    }

    sealed class Clock : TimeProvider
    {
        readonly object sync = new();
        static readonly TimeZoneInfo zone = TimeZoneInfo.CreateCustomTimeZone("cube-contract-test", TimeSpan.FromHours(8), "test", "test");
        // 固定在当前UTC日零点，远离服务日界；商城期限仍按实际UTC生成，保持在本测试时钟之后。
        readonly DateTimeOffset origin = new(DateTimeOffset.UtcNow.Date, TimeSpan.Zero);
        readonly List<ClockTimer> timers = [];
        long milliseconds;
        public override TimeZoneInfo LocalTimeZone => zone;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() { lock(sync) return milliseconds; }
        public override DateTimeOffset GetUtcNow() { lock(sync) return origin.AddMilliseconds(milliseconds); }
        // 契约计数不包含共享公会轮询和活动边界等待；这些计时器仍真实推进、回调和释放。
        public int TimerCount { get { lock(sync) return timers.Count(timer => timer.Period == 0 && !timer.EventBoundary && timer.Due != long.MaxValue); } }
        public int CubeTimerCount { get { lock(sync) return timers.Count(timer => timer.Cube && timer.Due != long.MaxValue); } }
        public int DailyTimerCount { get { lock(sync) return timers.Count(timer => timer.Period == 0 && !timer.Cube && !timer.EventBoundary && timer.Due != long.MaxValue); } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // 周签到轮询与晶体周期都为30秒；按真实调用来源分类，不凭注册顺序冒认晶体计时器。
            bool eventPoll = dueTime == TimeSpan.FromSeconds(30) &&
                new System.Diagnostics.StackTrace().GetFrames().Any(frame =>
                    frame.GetMethod()?.DeclaringType is { } type &&
                    (type == typeof(Network.Projection.ServerEventProjection) ||
                     type.DeclaringType == typeof(Network.Projection.ServerEventProjection)));
            bool cube = dueTime == TimeSpan.FromSeconds(30) && !eventPoll;
            bool eventBoundary = eventPoll || period == Timeout.InfiniteTimeSpan && dueTime == TimeSpan.FromDays(30);
            bool dayBoundary = period == Timeout.InfiniteTimeSpan && dueTime != Timeout.InfiniteTimeSpan &&
                milliseconds + (long)dueTime.TotalMilliseconds == 22 * 60 * 60 * 1000;
            bool onlineEventBoundary;
            bool shared = dueTime == period && (period == TimeSpan.FromSeconds(1) || period == TimeSpan.FromSeconds(5));
            lock(sync)
            {
                // 上游累计在线活动新增一个与服务日同刻的单次唤醒；第三个22:00边界属于该worker。
                onlineEventBoundary = dayBoundary && timers.Count(timer => timer.DayBoundary) >= 2;
                That(shared || eventBoundary || dayBoundary || period == Timeout.InfiniteTimeSpan && cube,
                    "该场景只注册原生30秒晶体等待、服务日/累计在线活动UTC22点通知、活动轮询/边界等待或共享公会轮询，不接纳未知定时器");
            }
            eventBoundary |= onlineEventBoundary;
            var timer = new ClockTimer(this,callback,state,cube,eventBoundary,dayBoundary);
            lock(sync) timers.Add(timer);
            timer.Change(dueTime,period);
            return timer;
        }
        public void Advance(TimeSpan amount)
        {
            ClockTimer[] due;
            lock(sync)
            {
                milliseconds += checked((long)amount.TotalMilliseconds);
                due = timers.Where(timer => timer.Due <= milliseconds).ToArray();
                foreach(var timer in due)
                    timer.Due = timer.Period > 0
                        ? checked(timer.Due + ((milliseconds - timer.Due) / timer.Period + 1) * timer.Period)
                        : long.MaxValue;
            }
            foreach(var timer in due) timer.Callback(timer.State);
        }
        sealed class ClockTimer(Clock clock,TimerCallback callback,object? state,bool cube,bool eventBoundary,bool dayBoundary) : ITimer
        {
            public bool Cube { get; } = cube;
            public bool EventBoundary { get; } = eventBoundary;
            public bool DayBoundary { get; } = dayBoundary;
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public long Due { get; set; } = long.MaxValue;
            public long Period { get; private set; }
            bool disposed;
            public bool Change(TimeSpan dueTime,TimeSpan period)
            {
                if(period != Timeout.InfiniteTimeSpan && period != TimeSpan.FromSeconds(1) && period != TimeSpan.FromSeconds(5))
                    throw new InvalidOperationException("该时钟仅允许契约一次性等待和共享公会轮询");
                lock(clock.sync)
                {
                    if(disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(clock.milliseconds+(long)dueTime.TotalMilliseconds);
                    Period = period > TimeSpan.Zero ? (long)period.TotalMilliseconds : 0;
                    return true;
                }
            }
            public void Dispose() { lock(clock.sync) { disposed=true; Due=long.MaxValue; clock.timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose();return ValueTask.CompletedTask; }
        }
    }
}
