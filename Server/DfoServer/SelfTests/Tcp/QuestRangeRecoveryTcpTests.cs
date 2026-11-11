using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.Network.Protocol;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

// 真实 TCP：父33 / 子33 / 父34。进程内断言仍由 quest-range-recovery 先跑。
internal static class QuestRangeRecoveryTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        int checks = Count;
        var points = Locate(resources.Towns, resources.Quests);
        await RunLiveAsync(options, root, resources, points);
        await RunRejectedRewardAsync(options, root, resources, points);
        await RunParentFaultAsync(options, root, resources, points);
        Console.WriteLine($"PASS {Count - checks} 斯曼工业基地3698真实TCP范围补归档与父提交重试");
    }

    static async Task RunLiveAsync(ServerOptions options, string root, TcpTestResources resources, ScenePoints points)
    {
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "quest-range-recovery"), resources: resources);
        ushort live = Seed(fixture, resources, points, "range-live", "RangeLive", points.Far, zeroRange: false);
        ushort parked = Seed(fixture, resources, points, "range-call", "RangeCall", points.Near, zeroRange: false);
        SqliteConnection.ClearAllPools();
        await fixture.StartAsync();
        using (var peer = Open(fixture, "range-live"))
        {
            peer.Select(0, prepared: true, resumeTown: true, room: false);
            That(State(fixture, live, 3759) == 1 && Receipts(fixture, live, 3759) == 0, "远点登录时3759仍是未达成且未归档");
            peer.Send(33, Trigger(3759));
            peer.Expect(1, 33, QuestPackets.Rejected(19));
            peer.Barrier();
            That(State(fixture, live, 3759) == 1 && Receipts(fixture, live, 3759) == 0 && Receipts(fixture, live, 3698) == 0,
                "范围外子33拒绝，不写0也不归档");
            peer.Send(34, FinishBody(3698));
            peer.Expect(1, 34, QuestPackets.Rejected(19));
            peer.Barrier();
            That(Receipts(fixture, live, 3698) == 0, "3759未达成时父34拒绝");
            peer.Send(33, Trigger(3698));
            Bytes(peer.Expect(1, 33).Body, QuestPackets.ClearTrigger(3698, 1), "子任务未达成时父33保持scalar 1");
            var idle = Drain(peer);
            That(!idle.Any(row => row.Opcode is 574 or 356), "未归档的父33不发574/356");
            peer.Send(35, Move(points.Near.X, points.Near.Y));
            peer.Barrier();
            That(fixture.Query("SELECT x,y FROM characters WHERE id=$p0", live) is [[long x, long y]] && x == points.Near.X && y == points.Near.Y,
                "原生35走到米娅半径内");
            peer.Send(33, Trigger(3759));
            Bytes(peer.Expect(1, 33).Body, QuestPackets.ClearTrigger(3759, 0), "半径内子33先确认scalar 0");
            Lists(fixture, resources, "range-live", live, Drain(peer), "子33自动归档", requireActive: true);
            That(State(fixture, live, 3759) is null && State(fixture, live, 3698) == 0 && Receipts(fixture, live, 3759) == 1 && Receipts(fixture, live, 3698) == 0,
                "范围内子33按原处理器归档3759，父任务scalar归零且不发奖");
            peer.Send(33, Trigger(3759));
            peer.Expect(1, 33, QuestPackets.Rejected(19));
            peer.Barrier();
            That(Receipts(fixture, live, 3759) == 1, "重复子33不再归档");
            peer.Send(33, Trigger(3698));
            Bytes(peer.Expect(1, 33).Body, QuestPackets.ClearTrigger(3698, 0), "子任务已归档后父33保持scalar 0");
            That(!Drain(peer).Any(row => row.Opcode is 574 or 356) && Receipts(fixture, live, 3759) == 1, "重复父33不补发574/356，也不再写回执");
            ulong before = Experience(fixture, live);
            peer.Send(34, FinishBody(3698));
            var ack = peer.Expect(1, 34).Body;
            var rewarded = Drain(peer);
            Lists(fixture, resources, "range-live", live, rewarded, "父34交付", requireActive: false);
            That(ack[0] == 1 && U16(ack, 1) == 3698 && Experience(fixture, live) == before + U32(ack, 4) &&
                State(fixture, live, 3698) is null && Receipts(fixture, live, 3698) == 1 && Receipts(fixture, live, 3759) == 1,
                "父34只发一次奖励");
            peer.Send(34, FinishBody(3698));
            peer.Expect(1, 34, QuestPackets.Rejected(19));
            peer.Barrier();
            That(Receipts(fixture, live, 3698) == 1 && Experience(fixture, live) == before + U32(ack, 4), "重复父34拒绝且不重复发奖");
        }
        using (var peer = Open(fixture, "range-live"))
        {
            var packets = peer.Select(0, prepared: true, resumeTown: true, room: false);
            byte[] entry = packets.Single(row => row.Kind == 1 && row.Opcode == 4).Body;
            That(Enumerable.Range(0, 30).All(slot => { ushort quest = U16(entry, 22 + 6 * slot); return quest != 3698 && quest != 3759; }),
                "重连ACK4不再把3698/3759放进活动槽");
            ListsMatch(fixture, resources, "range-live", live, peer.CompletedQuestHistory, null, "重连356");
        }
        using (var peer = Open(fixture, "range-call"))
        {
            // 入城核对会归档播种时已经是 0 的零奖励子任务。这里保持 scalar 1 进镇，再在线只写条件、不调用完成，才能让随后的父33自己补归档。
            ulong before = Experience(fixture, parked);
            peer.Select(0, prepared: true, resumeTown: true, room: false);
            That(State(fixture, parked, 3759) == 1 && Receipts(fixture, parked, 3759) == 0 && Receipts(fixture, parked, 3698) == 0,
                "未达成的3759入城后仍在活动表");
            Satisfy(fixture, resources, "range-call", resources.Quests);
            peer.Send(33, Trigger(3698));
            Bytes(peer.Expect(1, 33).Body, QuestPackets.ClearTrigger(3698, 0), "父33补归档后scalar归零");
            Lists(fixture, resources, "range-call", parked, Drain(peer), "父33补归档", requireActive: true);
            That(State(fixture, parked, 3759) is null && State(fixture, parked, 3698) == 0 && Experience(fixture, parked) == before &&
                Receipts(fixture, parked, 3759) == 1 && Receipts(fixture, parked, 3698) == 0,
                "父33补归档3759，父scalar归零，不发父奖励");
            peer.Send(33, Trigger(3698));
            Bytes(peer.Expect(1, 33).Body, QuestPackets.ClearTrigger(3698, 0), "重复父33仍是scalar 0");
            That(!Drain(peer).Any(row => row.Opcode is 574 or 356) && Receipts(fixture, parked, 3759) == 1 && Receipts(fixture, parked, 3698) == 0,
                "重复父33不再补发574/356，也不发父奖励");
        }
    }

    static async Task RunRejectedRewardAsync(ServerOptions options, string root, TcpTestResources resources, ScenePoints points)
    {
        var parent = resources.Quests.Get(3698) with { Rewards = [new(0, 2058, null), new(0, 1, null)] };
        var catalog = new QuestCatalog(resources.Quests.All.Select(row => row.Id == 3698 ? parent : row).ToDictionary(row => row.Id));
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "quest-range-full"), resources: resources, questCatalog: catalog);
        ushort id = Seed(fixture, resources, points, "range-full", "RangeFull", points.Near, zeroRange: false, catalog: catalog, fill: true);
        SqliteConnection.ClearAllPools();
        await fixture.StartAsync();
        using (var peer = Open(fixture, "range-full"))
        {
            peer.Select(0, prepared: true, resumeTown: true, room: false);
            That(State(fixture, id, 3759) == 1 && Receipts(fixture, id, 3759) == 0, "满包角色入城时3759尚未达成，核对不会先归档");
            Satisfy(fixture, resources, "range-full", catalog);
            peer.Send(34, FinishBody(3698));
            peer.Expect(1, 34, QuestPackets.Rejected(4));
            peer.Barrier();
            That(State(fixture, id, 3759) == 0 && Receipts(fixture, id, 3759) == 0 && Receipts(fixture, id, 3698) == 0,
                "父34背包不足时不归档3759，也不发574/356");
            ReleaseOne(fixture, resources, "range-full");
            peer.Send(34, FinishBody(3698));
            var ack = peer.Expect(1, 34).Body;
            var notices = Drain(peer);
            Lists(fixture, resources, "range-full", id, notices, "腾出空间后的父34", requireActive: true);
            That(ack[0] == 1 && U16(ack, 1) == 3698 && State(fixture, id, 3759) is null && State(fixture, id, 3698) is null &&
                Receipts(fixture, id, 3759) == 1 && Receipts(fixture, id, 3698) == 1,
                "腾出空间后同一连接再交父任务，子任务和父奖励各一次");
            peer.Send(34, FinishBody(3698));
            peer.Expect(1, 34, QuestPackets.Rejected(19));
            peer.Barrier();
            That(Receipts(fixture, id, 3698) == 1, "恢复后的重复父34拒绝");
        }
        using (var peer = Open(fixture, "range-full"))
        {
            var packets = peer.Select(0, prepared: true, resumeTown: true, room: false);
            byte[] entry = packets.Single(row => row.Kind == 1 && row.Opcode == 4).Body;
            That(Enumerable.Range(0, 30).All(slot => { ushort quest = U16(entry, 22 + 6 * slot); return quest != 3698 && quest != 3759; }),
                "满包恢复后重连ACK4不再把3698/3759放进活动槽");
            ListsMatch(fixture, resources, "range-full", id, peer.CompletedQuestHistory, null, "满包恢复后重连356");
        }
    }

    // 子任务提交后，父提交前的 SQLITE_BUSY 与父提交成功后的 SQLITE_BUSY 都由同连接忙锁重试。
    // 返回丢失的 IOException 关掉连接。三种都要 574 先于 356，奖励和回执只一次，重连后活动槽与完成表一致。
    static async Task RunParentFaultAsync(ServerOptions options, string root, TcpTestResources resources, ScenePoints points)
    {
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "quest-range-fault"), resources: resources);
        ushort beforeId = Seed(fixture, resources, points, "range-busy-before", "BusyBefore", points.Near, zeroRange: false);
        ushort afterId = Seed(fixture, resources, points, "range-busy-after", "BusyAfter", points.Near, zeroRange: false);
        ushort dropId = Seed(fixture, resources, points, "range-drop-after", "DropAfter", points.Near, zeroRange: false);
        SqliteConnection.ClearAllPools();
        await fixture.StartAsync();
        Busy(fixture, resources, "range-busy-before", beforeId, afterCommit: false);
        Busy(fixture, resources, "range-busy-after", afterId, afterCommit: true);
        Drop(fixture, resources, "range-drop-after", dropId);
    }

    static void Busy(TcpTestFixture fixture, TcpTestResources resources, string account, ushort id, bool afterCommit)
    {
        string label = afterCommit ? "父提交成功后忙锁" : "父提交前忙锁";
        QuestCommitFaultProxy proxy;
        ulong before;
        uint gained;
        using (var peer = Open(fixture, account))
        {
            peer.Select(0, prepared: true, resumeTown: true, room: false);
            That(State(fixture, id, 3759) == 1 && Receipts(fixture, id, 3759) == 0 && Receipts(fixture, id, 3698) == 0,
                label + "入城时3759未达成且两笔回执都没有");
            Satisfy(fixture, resources, account, resources.Quests);
            proxy = QuestCommitFaultProxy.Arm(Live(fixture, account), 3698, afterCommit, busy: true);
            before = Experience(fixture, id);
            peer.Send(34, FinishBody(3698));
            var ack = peer.Expect(1, 34).Body;
            var notices = Drain(peer);
            Lists(fixture, resources, account, id, notices, label, requireActive: true);
            gained = U32(ack, 4);
            That(ack[0] == 1 && U16(ack, 1) == 3698 && gained > 0 && Experience(fixture, id) == before + gained &&
                State(fixture, id, 3759) is null && State(fixture, id, 3698) is null &&
                Receipts(fixture, id, 3759) == 1 && Receipts(fixture, id, 3698) == 1,
                label + "只成功一次，574已在356之前");
            That(proxy.ParentCalls == 2 && proxy.SamePlan && proxy.FirstDuplicate == false && proxy.LastDuplicate == afterCommit,
                label + "第二次沿同一冻结计划，" + (afterCommit ? "读到原回执" : "才真正写入父回执"));
            peer.Send(34, FinishBody(3698));
            peer.Expect(1, 34, QuestPackets.Rejected(19));
            peer.Barrier();
            That(Receipts(fixture, id, 3698) == 1 && Receipts(fixture, id, 3759) == 1 && Experience(fixture, id) == before + gained,
                label + "重复34拒绝且不第二次发奖");
        }
        Reconnect(fixture, resources, account, id, before + gained, label);
    }

    static void Drop(TcpTestFixture fixture, TcpTestResources resources, string account, ushort id)
    {
        ulong before;
        ulong persisted;
        using (var peer = Open(fixture, account))
        {
            peer.Select(0, prepared: true, resumeTown: true, room: false);
            That(State(fixture, id, 3759) == 1 && Receipts(fixture, id, 3759) == 0, "返回丢失入城时3759未达成");
            Satisfy(fixture, resources, account, resources.Quests);
            var proxy = QuestCommitFaultProxy.Arm(Live(fixture, account), 3698, afterCommit: true, busy: false);
            before = Experience(fixture, id);
            peer.Send(34, FinishBody(3698));
            bool dropped = false;
            try { peer.Expect(1, 34); }
            catch (IOException) { dropped = true; }
            persisted = Experience(fixture, id);
            long amount = Convert.ToInt64(fixture.Query(
                "SELECT experience_amount FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=$p1", id, 3698)[0][0]);
            That(dropped && proxy.ParentCalls == 1 && proxy.FirstDuplicate == false && !proxy.LastDuplicate &&
                Receipts(fixture, id, 3759) == 1 && Receipts(fixture, id, 3698) == 1 &&
                amount > 0 && persisted == before + (ulong)amount,
                "父提交成功后返回丢失关掉连接，客户端看不到成功34，回执和经验只记一次");
        }
        Reconnect(fixture, resources, account, id, persisted, "返回丢失");
        using var again = Open(fixture, account);
        again.Select(0, prepared: true, resumeTown: true, room: false);
        again.Send(34, FinishBody(3698));
        again.Expect(1, 34, QuestPackets.Rejected(19));
        again.Barrier();
        That(Receipts(fixture, id, 3698) == 1 && Receipts(fixture, id, 3759) == 1 && Experience(fixture, id) == persisted,
            "返回丢失重连后再交父任务被拒绝，不第二次发奖");
    }

    static void Reconnect(TcpTestFixture fixture, TcpTestResources resources, string account, ushort id, ulong experience, string label)
    {
        using var peer = Open(fixture, account);
        var packets = peer.Select(0, prepared: true, resumeTown: true, room: false);
        byte[] entry = packets.Single(row => row.Kind == 1 && row.Opcode == 4).Body;
        That(Enumerable.Range(0, 30).All(slot => { ushort quest = U16(entry, 22 + 6 * slot); return quest != 3698 && quest != 3759; }),
            label + "重连ACK4不再把3698/3759放进活动槽");
        ListsMatch(fixture, resources, account, id, peer.CompletedQuestHistory, null, label + "重连356");
        That(Experience(fixture, id) == experience && Receipts(fixture, id, 3698) == 1 && Receipts(fixture, id, 3759) == 1,
            label + "重连后经验与两笔回执仍各一次");
    }

    readonly record struct ScenePoints(Location Melvin, Location Near, Location Far, int[] Done);

    static ScenePoints Locate(TownCatalog towns, QuestCatalog quests)
    {
        var parent = quests.Get(3698);
        var slough = towns.Areas.Values.Single(area => area.Phases.Prepend(area.Default).Any(map =>
            map.Path.Contains("Slough_up_street.map", StringComparison.OrdinalIgnoreCase) && map.Npcs.Any(npc => npc.Id == 78 && npc.X == 1397 && npc.Y == 210)));
        var gent = towns.Areas.Values.Single(area => area.Phases.Prepend(area.Default).Any(map => map.Npcs.Any(npc => npc.Id == 59 && npc.X == 2004 && npc.Y == 187)));
        var blocked = parent.Definition!.Collisions.ToHashSet();
        var done = parent.Definition.PrerequisiteGroups.First(group => group.Length > 0).ToHashSet();
        Location? melvin = null, near = null, far = null;
        for (int attempt = 0; attempt < 2 && (melvin is null || near is null || far is null); attempt++)
        {
            melvin = Walk(towns, gent.Town, gent.Area, done, _ => true);
            near = Walk(towns, slough.Town, slough.Area, done, point => Math.Abs(point.X - 1397) <= 90 && Math.Abs(point.Y - 210) <= 90);
            far = Walk(towns, slough.Town, slough.Area, done, point => Math.Abs((long)point.X - 1397) > 90 || Math.Abs((long)point.Y - 210) > 90);
            if (attempt == 0 && (melvin is null || near is null || far is null))
                foreach (int id in slough.Quests.Concat(slough.AnyQuests.Take(1)).Concat(gent.Quests).Concat(gent.AnyQuests.Take(1)))
                    if (id > 0 && !blocked.Contains(id)) done.Add(id);
        }
        That(melvin is not null && near is not null && far is not null, "TCP落点沿用阶段地图上的可行走坐标");
        return new(melvin!, near!, far!, done.ToArray());
    }

    static ushort Seed(TcpTestFixture fixture, TcpTestResources resources, ScenePoints points, string account, string name, Location position, bool zeroRange,
        QuestCatalog? catalog = null, bool fill = false)
    {
        var quests = catalog ?? resources.Quests;
        var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
        var parameters = SelfTestCatalogs.QuestRewardParameters(resources.Archive);
        var owners = new AccountOwners();
        using var owner = owners.Acquire(account, () => { });
        var repository = new CharacterRepository(fixture.Database, quests);
        var skills = resources.Learning;
        var candidate = CharacterCreation.Prepare(name, resources.Characters.Get(0), position, position, skills.Get);
        candidate = candidate with
        {
            GrowType = 5,
            Skills = skills.FreeSkills(0, 5).ToArray(),
            InitialLearning = SkillLearning.Initialize(0, skills.FreeSkills(0, 5), skills).State!
        };
        var learning = SkillPointLedger.Synchronize(0, 5, 81, candidate.InitialLearning with { Experience = growth.GetLevelStartExperience(81) }, skills);
        var created = repository.Create(owner, candidate with
        {
            Level = 81, CompletedQuests = points.Done, InitialLearning = learning, AcceptedStoryLevel = 81
        });
        var session = new GameSession(repository, resources.Towns, owner, questCatalog: quests);
        session.Select(0, resumeTown: true);
        session.MarkReady();
        Place(session, repository, owner, points.Melvin);
        That(session.ChangeQuest(3698, QuestLifecycleAction.Accept, quests, resources.ItemResources.Items, resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces)?.Commit is not null &&
            session.ChangeQuest(3758, QuestLifecycleAction.Accept, quests, resources.ItemResources.Items, resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces)?.Commit is not null &&
            session.ChangeQuest(3758, QuestLifecycleAction.Condition, quests, resources.ItemResources.Items, resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces, new(null, 0, 0))?.Commit is not null &&
            session.FinishQuest(3758, ushort.MaxValue, quests, parameters, resources.ItemResources.Items, resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces, growth).Outcome == QuestFinishOutcome.Committed &&
            session.ChangeQuest(3759, QuestLifecycleAction.Accept, quests, resources.ItemResources.Items, resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces)?.Commit is not null,
            "TCP角色先完成梅尔文3758再接3759：" + name);
        Place(session, repository, owner, position);
        if (zeroRange)
            That(session.ChangeQuest(3759, QuestLifecycleAction.Condition, quests, resources.ItemResources.Items, resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces, new(null, 0, 0))?.Commit is not null,
                "播种时只把范围内3759归零，不走TCP自动归档：" + name);
        if (fill)
        {
            int granted = 0;
            ushort error = 0;
            int chunk = resources.ItemResources.Items.Get(2058).StackLimit;
            while (granted < 256)
            {
                var grant = session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character, [new(2058, chunk)],
                    resources.ItemResources.Items, resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces));
                if (!grant.Accepted) { error = grant.Error; break; }
                granted++;
            }
            That(granted > 0 && error == 17, "TCP满包角色被2058填到错误17");
        }
        session.End();
        return created.Id;
    }

    static void Satisfy(TcpTestFixture fixture, TcpTestResources resources, string account, QuestCatalog quests)
    {
        // 子33在半径内会当场归档。父33/父34要补的是“条件已写入、自动归档没发生”，所以只走会话里的条件合同。
        var session = Live(fixture, account);
        var receipt = session.ChangeQuest(3759, QuestLifecycleAction.Condition, quests, resources.ItemResources.Items,
            resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces, new(null, 0, 0));
        ushort id = session.Selected!.Id;
        That(receipt?.Commit is not null && State(fixture, id, 3759) == 0 && Receipts(fixture, id, 3759) == 0,
            account + "在线只把范围内3759归零，不归档");
    }

    static void ReleaseOne(TcpTestFixture fixture, TcpTestResources resources, string account)
    {
        var session = Live(fixture, account);
        That(session.MutateInventory(character =>
        {
            var item = character.Inventory.Items.First(row => row.Core.ItemId == 2058 && row.Position.List == InventoryListType.Main);
            return InventoryDeleteService.Plan(character.Inventory, item.Position, item.Core.Value, item.Core.IsStackable ? 1 : item.Core.Count);
        }).Accepted, "在线腾出一个2058名额");
    }

    static GameSession Live(TcpTestFixture fixture, string account)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var host = typeof(TcpTestFixture).GetField("runtimeHost", flags)!.GetValue(fixture)!;
        var protocol = host.GetType().GetFields(flags).Select(field => field.GetValue(host)).OfType<GameProtocolHandler>().Single();
        var directory = typeof(GameProtocolHandler).GetFields(flags).Select(field => field.GetValue(protocol)).OfType<OnlineSessionDirectory>().Single();
        return directory.Find(account) ?? throw new InvalidOperationException("在线会话不存在：" + account);
    }

    static void Place(GameSession session, CharacterRepository repository, AccountOwners.Lease owner, Location point)
    {
        var current = repository.LoadCharacter(owner, 0);
        repository.Save(owner, current, point, current.Return);
        session.ReturnToRoster();
        session.Select(0, resumeTown: true);
        session.MarkReady();
    }

    static void Lists(TcpTestFixture fixture, TcpTestResources resources, string account, ushort id, WirePacket[] notices, string reason, bool requireActive)
    {
        // 单独交付的父任务由 ACK34 删槽，ProgressAsync 会把该任务从对照表去掉，因此不再另发 574。
        // 同一提交里还删了别的槽（范围子任务）或父 33 补归档时，574 必须在最后一条 356 之前。
        int completedAt = Array.FindLastIndex(notices, row => row.Kind == 0 && row.Opcode == 356);
        int lastActive = Array.FindLastIndex(notices, row => row.Kind == 0 && row.Opcode == 574);
        string seen = string.Join(",", notices.Select(row => row.Kind + ":" + row.Opcode));
        That(completedAt >= 0 && (lastActive < 0 || lastActive < completedAt) && (!requireActive || lastActive >= 0 && lastActive < completedAt),
            reason + "：356 存在，且 574 若出现必须在最后一条 356 之前 [" + seen + "]");
        byte[]? active = lastActive >= 0 && lastActive < completedAt ? notices[lastActive].Body : null;
        ListsMatch(fixture, resources, account, id, notices[completedAt].Body, active, reason);
    }

    static void ListsMatch(TcpTestFixture fixture, TcpTestResources resources, string account, ushort id, byte[] completed, byte[]? active, string reason)
    {
        var owners = new AccountOwners();
        using var owner = owners.Acquire(account, () => { });
        var loaded = new CharacterRepository(fixture.Database, resources.Quests).LoadCharacter(owner, 0);
        That(loaded.Id == id, reason + "读到原角色");
        Bytes(completed, TownPackets.CompletedQuests(loaded), reason + "的356与完成表一致");
        if (active is not null)
            Bytes(active, QuestPackets.ActiveList(loaded.ActiveMainQuests), reason + "的574与活动表一致");
    }

    static WirePacket[] Drain(TcpPeer peer)
    {
        var notices = new List<WirePacket>();
        QuestLifecycleTcpTests.DrainNotifications(peer, notices.Add);
        return notices.ToArray();
    }

    static uint? State(TcpTestFixture fixture, ushort id, int quest)
    {
        var rows = fixture.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1", id, quest);
        return rows.Length == 0 ? null : Convert.ToUInt32(rows[0][0]);
    }

    static long Receipts(TcpTestFixture fixture, ushort id, int quest) =>
        Convert.ToInt64(fixture.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=$p1", id, quest)[0][0]);

    static ulong Experience(TcpTestFixture fixture, ushort id) =>
        Convert.ToUInt64(fixture.Query("SELECT experience FROM characters WHERE id=$p0", id)[0][0]);

    static byte[] Trigger(ushort id) => [33, 0, (byte)id, (byte)(id >> 8), 0, 0];
    static byte[] FinishBody(ushort id) => [34, 0, (byte)id, (byte)(id >> 8), 255, 255, 1, 0, 255, 255];
    static uint U32(byte[] body, int at) => BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at));

    static Location? Walk(TownCatalog towns, byte town, byte area, IReadOnlySet<int> done, Func<Location, bool> match)
    {
        var map = towns.Areas[(town, area)].Map(done);
        foreach (var region in map.Bounds)
        {
            var box = region.Bounds;
            for (int y = box.Y; y <= box.Y + box.Height; y += 15)
            for (int x = box.X; x <= box.X + box.Width; x += 15)
            {
                if (x is < 0 or > ushort.MaxValue || y is < 0 or > ushort.MaxValue) continue;
                var point = new Location(town, area, (ushort)x, (ushort)y);
                if (towns.IsPosition(point, done) && match(point)) return point;
            }
        }
        return null;
    }
}
