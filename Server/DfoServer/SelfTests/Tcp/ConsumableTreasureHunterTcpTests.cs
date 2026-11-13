using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class ConsumableUseTcpTests
{
    public static async Task RunTreasureHunterAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        TreasureHunterPotionTests.RunResources(resources.ItemResources.RecoveryConsumables);
        const int sourceId = 2600018;
        var catalog = resources.ItemResources.RecoveryConsumables;
        if (!catalog.TryGet(sourceId, out var definition) || definition.TreasureHunterPotion is null)
        {
            Console.WriteLine("SKIP 财宝TCP：当前没有可用2600018资源合同；通用格式、身份和概率核心检查另行执行");
            return;
        }
        catalog.TryGet(10099194, out var jobSource);
        catalog.TryGet(490000010, out var dungeonSource);
        var clock = new DailyBoosterTestClock(new(DateTimeOffset.UtcNow.Date, TimeSpan.Zero));
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "treasure-hunter-potions"), resources: resources, clock: clock);
        var repository = new CharacterRepository(fixture.Database);
        ushort roleId;
        ushort? qualificationRoleId = null;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("TreasureDose", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get);
            var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
            var learning = SkillPointLedger.Synchronize(0, 0, 20, candidate.InitialLearning with
                { Experience = growth.GetLevelStartExperience(20) }, resources.Learning);
            var inventory = candidate.Inventory with { Items = candidate.Inventory.Items.Add(new(0, new(InventoryListType.Main, 65),
                InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(sourceId), 3, clock.GetUtcNow()) with
                    { Marker16 = 88, SortLockFlag = 1 })).Add(new(0, new(InventoryListType.Main, 66),
                        InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(2749958), 2, clock.GetUtcNow()))) };
            roleId = repository.Create(owner, candidate with { Level = 20, InitialLearning = learning, Inventory = inventory }).Id;
            repository.Create(owner, candidate with { Name = "NoTreasure", Level = 20, InitialLearning = learning });
            if (jobSource?.TreasureHunterPotion is not null && dungeonSource?.TreasureHunterPotion is not null)
                qualificationRoleId = repository.Create(owner, candidate with
                {
                    Name = "ClientGate", Level = 20, InitialLearning = learning,
                    Inventory = candidate.Inventory with { Items = candidate.Inventory.Items.AddRange(
                        new[] { jobSource.ItemId, dungeonSource.ItemId }.Select((id, index) =>
                            new InventoryItem(0, new(InventoryListType.Main, (short)(65 + index)),
                                InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(id), 1, clock.GetUtcNow())))) }
                }).Id;
        }
        await fixture.StartAsync();
        if (qualificationRoleId is { } sample && jobSource is not null && dungeonSource is not null)
        {
            // 假客户端直接发包验证资格委派，不能据此推断真实ARAD允许在城镇点击职业/副本受限药剂。
            using var peer = Open(fixture);
            peer.Select(2);
            Use(peer, 65, 0, jobSource.ItemId); Accepted(peer, 65, 0, jobSource.ItemId); peer.Barrier();
            That(Read(sample).Inventory.ConsumableStates.Any(row => row.Kind == ConsumableStateKind.Effect && row.ItemId == jobSource.ItemId),
                "限制女枪职业的真实药剂沿普通44事务处理，服务端不重复客户端职业匹配");
            clock.Advance(TimeSpan.FromMilliseconds(Math.Max(jobSource.CoolTime, jobSource.EffectMilliseconds)));
            long until = clock.GetUtcNow().ToUnixTimeMilliseconds() + dungeonSource.EffectMilliseconds;
            Use(peer, 66, 1, dungeonSource.ItemId); Accepted(peer, 66, 1, dungeonSource.ItemId); peer.Barrier();
            var effect = dungeonSource.TreasureHunterPotion!;
            That(Read(sample).Inventory.ConsumableStates.Any(row => row.Kind == ConsumableStateKind.Effect && row.ItemId == dungeonSource.ItemId) &&
                Read(sample).Inventory.Find(new(InventoryListType.Main, 66)) is null &&
                (catalog.AlchemistPotionInfo.TryGetValue(dungeonSource.ItemId, out int configured)
                    ? effect.FromExj && effect.BonusRate == configured
                    : !effect.FromExj && effect.BonusRate == TreasureHunterPotionDefinition.DefaultBonusRate),
                "远古变体共用普通消费及EXJ/兜底规则，不在服务端另筛模板副本名单");
            peer.Send(7); peer.Expect(1, 7, [1]); peer.Expect(0, 2); peer.Select(2);
            CheckRestored(peer, dungeonSource.ItemId, until);
        }
        long deadline;
        string saved;
        using (var peer = Open(fixture))
        {
            peer.Select(inventoryNotices: [(1, 713)]);
            var before = Read();
            var source = before.Inventory.Find(new(InventoryListType.Main, 65))!;
            Use(peer, 65, 0, sourceId); Accepted(peer, 65, 0, sourceId); peer.Barrier();
            var after = Read();
            deadline = clock.GetUtcNow().ToUnixTimeMilliseconds() + definition.EffectMilliseconds;
            That(after.Revision == before.Revision + 1 &&
                after.Inventory.Find(source.Position) == source with { Core = source.Core with { Value = 2 } } &&
                after.Inventory.ConsumableStates.Single(row => row.Kind == ConsumableStateKind.Effect && row.ItemId == sourceId)
                    .UntilMilliseconds == deadline &&
                TreasureHunterPotionPolicy.ActiveBonus(after.Inventory.ConsumableStates, catalog.TreasureHunterPotions,
                    clock.GetUtcNow().ToUnixTimeMilliseconds()) == definition.TreasureHunterPotion.BonusRate,
                "真实44一次提交扣原UID一瓶并持久保存财宝药效；任务概率读取已提交状态和EXJ参数");
            saved = Snapshot();
            Use(peer, 65, 1, sourceId); Refused(peer, 1, sourceId); peer.Barrier();
            That(Snapshot() == saved, "冷却拒绝不扣源、不刷新截止或写审计");
            clock.Advance(TimeSpan.FromSeconds(30));
            peer.Send(7); peer.Expect(1, 7, [1]); peer.Expect(0, 2); peer.Select(inventoryNotices: [(1, 713)]);
            CheckRestored(peer, sourceId, deadline);
            That(Snapshot() == saved, "同连接重选恢复原截止及原UID，不续期或重扣");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(inventoryNotices: [(1, 713)]); CheckRestored(peer, sourceId, deadline);
            That(Snapshot() == saved, "新连接按持久状态恢复真实剩余秒，未创建另一份药效");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(1);
            That(peer.RestoredConsumableStates.Count == 0 && Snapshot() == saved,
                "同账号另一角色不继承财宝药效，也不改原角色的数量和截止");
        }
        clock.Advance(TimeSpan.FromMilliseconds(definition.EffectMilliseconds));
        using (var peer = Open(fixture))
        {
            peer.Select(inventoryNotices: [(1, 713)]);
            That(peer.RestoredConsumableStates.Count == 0 && Read().Inventory.ConsumableStates.All(row => !row.Active(clock.GetUtcNow().ToUnixTimeMilliseconds())) &&
                Read().Inventory.Find(new(InventoryListType.Main, 65))!.Core.Count == 2,
                "到期重新选角清除效果资格，不重新消费或补发药水");
            Use(peer,65,40,sourceId); Accepted(peer,65,40,sourceId); peer.Barrier();
            long realUntil = Read().Inventory.ConsumableStates.Single(row => row.Kind == ConsumableStateKind.Effect).UntilMilliseconds;
            FormalDungeonTcpTests.EntryTown(peer);
            peer.Send(15,new byte[4]); peer.Expect(1,15,[1]);
            peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),1]); peer.Expect(0,27,peer.SelectionNoticeBody());
            peer.Send(16,Convert.FromHexString("030000000000000001FFFF0000000000000000000000000000"));
            That(peer.Expect(0,28).Body[12] == 1,"财宝续时回归走真正练习模式而非普通实例");
            peer.Expect(0,29); FormalDungeonTcpTests.Load(peer); peer.Barrier();
            string persistent = Snapshot();
            FreeUse(65,41,sourceId);
            peer.Expect(0,174,State((sourceId,1800))); peer.Barrier();
            That(Snapshot() == persistent,"普通冷却存在时仍可练习使用，免扣ACK及时间刷新不改真实源和持续账本");
            FreeUse(66,42,2749958);
            peer.Expect(0,174,State((sourceId,0),(2749958,1800))); peer.Barrier();
            Use(peer,66,43,2749958); Refused(peer,43,2749958); peer.Barrier();
            clock.Advance(TimeSpan.FromSeconds(10));
            FreeUse(66,44,2749958);
            peer.Expect(0,174,State((2749958,1800))); peer.Barrier();
            That(Snapshot() == persistent,"练习自身执行十秒冷却，合法重复只刷新临时药效，不保存免费续时");
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),0]); peer.Expect(0,24);
            peer.Expect(0,172,State((sourceId,1790),(2749958,0)));
            peer.Expect(0,174,State((2749958,0),(sourceId,1790)));
            FormalDungeonTcpTests.RefreshedQuests(peer); peer.Barrier();
            That(Read().Inventory.ConsumableStates.Single(row => row.Kind == ConsumableStateKind.Effect).UntilMilliseconds == realUntil,
                "返城172结束练习冷却并恢复真实原剩余；174恢复普通药效原截止，不把练习刷新带出");
            Use(peer,65,45,sourceId); Refused(peer,45,sourceId); peer.Barrier();
            Use(peer,66,46,2749958); Accepted(peer,66,46,2749958);
            peer.Expect(0,174,State((sourceId,0))); peer.Barrier();
            clock.Advance(TimeSpan.FromSeconds(10));
            Use(peer,66,47,2749958); Accepted(peer,66,47,2749958);
            peer.Barrier();
            That(Read().Inventory.Find(new(InventoryListType.Main,66)) is null &&
                Read().Inventory.ConsumableStates.Single(row => row.Kind == ConsumableStateKind.Effect).UntilMilliseconds ==
                    clock.GetUtcNow().ToUnixTimeMilliseconds()+1800000,
                "普通模式真实扣最后一瓶并覆盖截止，刷新包不再次减数量；既有30分钟组冷却仍拒绝");

            void FreeUse(ushort slot,uint token,int id)
            {
                Use(peer,slot,token,id);
                peer.Expect(1,44,Build(w=>{w.Write((byte)0);w.Write((ushort)0);w.Write((byte)0);w.Write(token);w.Write(id);}));
            }
            byte[] State(params (int Id,int Seconds)[] rows) => Build(w=>
            {w.Write((byte)rows.Length);foreach(var row in rows){w.Write(row.Id);w.Write(row.Seconds);}});
        }

        Character Read(ushort? id = null)
        {
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            return new CharacterRepository(fixture.Database).List(owner).Single(row => row.Id == (id ?? roleId));
        }
        string Snapshot() => fixture.Snapshot("SELECT * FROM character_consumable_states WHERE character_id=$p0 ORDER BY kind,item_id", roleId) +
            fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", roleId);
        void CheckRestored(TcpPeer peer, int itemId, long until)
        {
            uint seconds = checked((uint)((until - clock.GetUtcNow().ToUnixTimeMilliseconds()) / 1000));
            That(peer.RestoredConsumableStates.Select(packet => packet.Opcode).SequenceEqual(new ushort[] { 172, 174 }) &&
                peer.RestoredConsumableStates.All(packet => Enumerable.Range(0, packet.Body[0]).Any(index =>
                    U32(packet.Body, 1 + index * 8) == itemId && U32(packet.Body, 5 + index * 8) == seconds)),
                "172/174恢复财宝原物品编号与实际剩余秒数，不用原型编号代替变体");
        }
    }
}
