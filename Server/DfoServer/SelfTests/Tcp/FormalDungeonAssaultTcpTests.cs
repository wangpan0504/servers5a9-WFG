using System.Buffers.Binary;
using DfoServer.Development.Dungeons;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    // 实机 2026-10-21 00:57～00:58：强袭隐藏任务7323通关后点「选择其它地下城」，服务端回 ACK72 010101 + 成功ACK15，
    // 客户端成功ACK15为空操作、ACK72 state1 已锁住结算页按钮，没有 NOTI24 就不会离开结算页，菜单再无响应。
    // 主线 #632 起普通结算页「选择其它地下城」改为提交退场后直接发布完整选图（客户端不会续发15）。
    // 本用例走真实 1547→15→16 强袭入场、逐房清图与结算，锁定强袭 72{1,1} 同样走 ACK72{1,1}→ACK15→NOTI3 state1→完整NOTI27。
    public static async Task RunAssaultResultMenuAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await RunAssaultResultMenuAsync(source, root, resources, 3, 7323, 0, true);
        await RunAssaultResultMenuAsync(source, root, resources, 3, 7323, 0);
        await RunAssaultResultMenuAsync(source, root, resources, 2, 7321, 6084);
    }

    static async Task RunAssaultResultMenuAsync(ServerOptions source, string root, TcpTestResources resources,
        int day, uint mission, ushort questId, bool missingMaterials = false)
    {
        string? previousDay = Environment.GetEnvironmentVariable(AssaultModePackets.DayIndexOverrideVariable);
        // 固定覆盖原菜单现场与接取6084后的周二隐藏关，不依赖运行当天的星期。
        Environment.SetEnvironmentVariable(AssaultModePackets.DayIndexOverrideVariable, day.ToString());
        try
        {
            await using var fixture = new TcpTestFixture(source with { DevelopmentDungeonEntryProbe = DungeonEntryProbeMode.Disabled },
                Path.Combine(root, "assault-result-menu-" + day + (missingMaterials ? "-missing" : "")), gm: true, resources: resources);
            await fixture.StartAsync();
            using var peer = Open(fixture);
            peer.Create("AssaultMenu");
            var growth = SelfTestCatalogs.Growth(resources.Archive);
            fixture.Sql("UPDATE characters SET level=95,experience=$p0 WHERE id=$p1", growth.GetLevelStartExperience(95), peer.Id);
            // 前置6119，以及当前PVF中 54/2 地区、强袭入口门和7327选图项声明的任务（只读资源推导，不手写任务号）。
            var towns = resources.Towns;
            var area = towns.Areas[(54, 2)];
            var done = new HashSet<int> { 6119 };
            if (questId != 0)
                done.UnionWith(resources.Quests.Get(questId).Definition!.PrerequisiteGroups.SelectMany(group => group));
            done.UnionWith(area.Quests);
            if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
            done.UnionWith(area.Map(done).DungeonGates.First(gate => gate.Conditions.Length == 0).Quests);
            foreach (var option in towns.DungeonMaps[area.DungeonWorldMap!.Value].Options.Where(option => option.DungeonId == 7327 && option.QuestId != -1 && !option.InProgress))
                done.Add(option.QuestId);
            foreach (int quest in done)
                fixture.Sql("INSERT INTO character_completed_quests(character_id,quest_id) VALUES($p0,$p1)", peer.Id, quest);
            int dayKey = DfoServer.Game.Dungeons.AssaultModeEntryPolicy.DayKey(TimeProvider.System.GetLocalNow());
            fixture.Sql("INSERT INTO character_assault_daily(character_id,account,day,clear_count,hidden_count) " +
                "SELECT id,account,$p1,3,0 FROM characters WHERE id=$p0", peer.Id, dayKey);
            short potion;
            using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            {
                var repository = new CharacterRepository(fixture.Database);
                var character = repository.LoadCharacter(owner, 0);
                var items = resources.ItemResources;
                var inventory = new InventoryService(items.Items, items.GoldLimits, items.EpicPieces);
                character = InventoryService.Commit(repository, owner, character, inventory.Grant(character,
                    missingMaterials ? [new(2600014, 1)] : [new(10161610, 10), new(2600014, 1)]));
                potion = character.Inventory.Items.Single(item => item.Core.ItemId == 2600014).Position.Index;
            }
            peer.Select(prepared: true);
            // 实机 1547 前角色位于 54/2（1081,286）；选角落在房间，用传送药走正式准入到同一地区，强袭入口门校验与实机一致。
            peer.Send(237, Build(w => { w.Write(potion); w.Write(2600014); w.Write((byte)0);
                w.Write((byte)54); w.Write((byte)2); w.Write((ushort)1081); w.Write((ushort)286); w.Write((byte)5); }));
            That(peer.Expect(1, 237).Body[0] == 1, "传送药进入54/2");
            for (int read = 0; read < 16 && peer.Read() is not { Kind: 0, Opcode: 24 }; read++) { }
            peer.Barrier();
            That(fixture.Query("SELECT town,area FROM characters WHERE id=$p0", peer.Id) is [[54L, 2L]], "fixture 角色位于 54/2");
            if (questId != 0)
            {
                peer.Send(31, [31, 0, (byte)questId, (byte)(questId >> 8)]);
                That(peer.Expect(1, 31).Body[0] == 1, "真实接取强袭特殊补给6084");
                QuestLifecycleTcpTests.DrainNotifications(peer);
            }
            SelectMap(peer, incompleteHellQuest: true);
            byte[] selection = Convert.FromHexString("9B1C00000000000000FFFF0000000000000000000000000000");
            BinaryPrimitives.WriteUInt32LittleEndian(selection, mission);
            BinaryPrimitives.WriteUInt32LittleEndian(selection.AsSpan(16), questId);
            if (questId != 0)
            {
                string before = fixture.Snapshot();
                peer.Send(16, selection);
                peer.Expect(1, 16, [0, 9, 0, 0]);
                peer.Barrier();
                That(fixture.Snapshot() == before, "已接任务但没有1547授权不能直接进入隐藏mission或扣费");
            }
            peer.Send(1547, [0x9F, 0x1C, 0, 0]);
            if (missingMaterials)
            {
                string before = fixture.Snapshot();
                peer.Expect(1, 1547, [0, 1, 0]);
                peer.Expect(1, 16, [0, 0x11, 0, 0]);
                peer.Barrier();
                That(fixture.Snapshot() == before, "强袭材料不足不改变角色资产");
                return;
            }
            peer.Expect(1, 1547, Build(w => { w.Write((byte)1); w.Write(mission); w.Write(0u); }));
            if (questId != 0)
            {
                string before = fixture.Snapshot();
                foreach (var (dungeon, quest) in new (uint, uint)[] { (7320, questId), (3, 0), (mission, 65535) })
                {
                    byte[] invalid = (byte[])selection.Clone();
                    BinaryPrimitives.WriteUInt32LittleEndian(invalid, dungeon);
                    BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(16), quest);
                    peer.Send(16, invalid);
                    peer.Expect(1, 16, [0, 9, 0, 0]);
                    peer.Barrier();
                    That(fixture.Snapshot() == before, "强袭授权不能用于其它mission、非强袭越区图或伪造任务号");
                }
            }
            peer.Send(15, Build(w => w.Write(mission)));
            peer.Expect(1, 15, [1]);
            peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]);
            peer.Barrier();
            peer.Send(16, selection);
            var information = peer.Read();
            That(information is { Kind: 0, Opcode: 28 } && U32(information.Body) == mission,
                $"普通3次后强袭隐藏关{mission}携带任务{questId}应正式进图，实际kind={information.Kind} opcode={information.Opcode} body={Convert.ToHexString(information.Body)}");
            byte[] map = peer.Expect(0, 29).Body;
            // 入场提交扣除10个诞生之芽后的库存差量通知。
            QuestLifecycleTcpTests.DrainNotifications(peer);
            var path = new (byte X, byte Y)[] { (1, 0), (2, 0), (3, 0), (4, 0), (5, 0), (6, 0) };
            for (int room = 0; room <= path.Length; room++)
            {
                Load(peer); QuestLifecycleTcpTests.DrainNotifications(peer);
                var enemies = Enumerable.Range(0, map[18]).Select(at => 19 + 22 * at)
                    .Where(at => map[at + 13] != 9 && map[at + 14] == 0).OrderBy(at => map[at + 13] == 3);
                foreach (int at in enemies)
                {
                    var death = Death(U16(map, at + 6), peer.Id, map[at + 12]);
                    if (map[at + 13] == 3)
                    {
                        death[55] = 1;
                        BinaryPrimitives.WriteUInt64LittleEndian(death.AsSpan(18), 9000);
                        BinaryPrimitives.WriteUInt32LittleEndian(death.AsSpan(26), 30);
                        SealDeath(death);
                    }
                    peer.Send(39, death); QuestLifecycleTcpTests.DrainNotifications(peer);
                }
                if (room == path.Length) break;
                var move = new byte[100]; move[0] = path[room].X; move[1] = path[room].Y;
                peer.Send(45, move); peer.Expect(1, 45, [1]); map = peer.Expect(0, 29).Body;
            }
            peer.Send(46, DfoServer.DungeonSettlementProtocolTests.Report(peer.Id, 99)); QuestLifecycleTcpTests.DrainNotifications(peer);
            That(fixture.Query("SELECT clear_count,hidden_count FROM character_assault_daily WHERE character_id=$p0", peer.Id) is [[3L, 1L]],
                "强袭隐藏任务通关提交当日隐藏计数");
            if (questId != 0)
            {
                That(fixture.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1", peer.Id, questId) is [[2L]],
                    "隐藏关真实暗影格鲁姆死亡推进6084，保留任务接取身份");
                peer.Send(46, DfoServer.DungeonSettlementProtocolTests.Report(peer.Id, 99));
                QuestLifecycleTcpTests.DrainNotifications(peer);
                That(fixture.Query("SELECT clear_count,hidden_count FROM character_assault_daily WHERE character_id=$p0", peer.Id) is [[3L, 1L]],
                    "重复结算不再次增加隐藏或普通强袭计数");
            }
            long attempt = Attempt();
            // 实机先发两次焦点 72{2,x}，再点 F11 选其它地下城。
            peer.Send(72, [2, 2]); peer.Expect(1, 72, [1, 2, 2]);
            peer.Send(72, [2, 1]); peer.Expect(1, 72, [1, 2, 1]);
            peer.Barrier();
            peer.Send(72, [1, 1]);
            peer.Expect(1, 72, [1, 1, 1]);
            peer.Expect(1, 15, [1]);
            peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]);
            byte[] selectNotice = new byte[44];
            selectNotice[8] = 1;
            peer.Expect(0, 27, selectNotice);
            peer.Barrier();
            if (questId != 0)
            {
                foreach (uint parent in new uint[] { 7300, 7327 })
                {
                    peer.Send(1547, Build(w => w.Write(parent)));
                    peer.Expect(1, 1547, [0, 1, 0]);
                }
                peer.Barrier();
                That(fixture.Query("SELECT clear_count,hidden_count FROM character_assault_daily WHERE character_id=$p0", peer.Id) is [[3L, 1L]],
                    "通关后普通3次和隐藏1次上限继续拒绝1547");
            }
            string returned = fixture.Snapshot();
            peer.Send(72, [1, 1]); peer.Send(72, [1, 3]); peer.Send(72, [1, 2]);
            peer.Barrier();
            That(fixture.Snapshot() == returned && Attempt() == attempt,
                "退场后迟到的结果页命令不再回包、不重复回城或预留副本尝试");
            That(fixture.Query("SELECT town,area FROM characters WHERE id=$p0", peer.Id) is [[54L, 2L]],
                "选其它地下城后角色回到入场城镇安全点，选图已完整发布");
            Console.WriteLine("PASS 强袭通关选其它地下城 ACK72{1,1}→ACK15→NOTI3 state1→完整NOTI27");
            await fixture.StopAsync();

            long Attempt() => Convert.ToInt64(fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id)[0][0]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AssaultModePackets.DayIndexOverrideVariable, previousDay);
        }
    }
}
