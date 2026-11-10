using DfoServer.GameWorld.Dungeons;
using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Management;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunOtherverseAncientAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "otherverse-ancient"), gm: true, resources: resources);
        var repository = new CharacterRepository(fixture.Database, resources.Quests);
        var items = resources.ItemResources;
        var grants = new InventoryService(items.Items, items.GoldLimits, items.EpicPieces);
        // 远古七图与镇17 Area5是独立资源增量；官方PVF未安装时服务器按设计拒绝落点，TCP只覆盖现代异界。
        bool ancientAvailable = resources.Towns.Areas.ContainsKey((17, 5)) &&
            SelfTestCatalogs.Dungeons(resources.Archive).ContainsDungeon(7101);
        Character modern, ancient = null!;
        short ticket;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            modern = Seed("OVerseTcp", 2);
            modern = InventoryService.Commit(repository, owner, modern, grants.Grant(modern, [new(10161066, 2)]));
            ticket = modern.Inventory.Items.Single(item => item.Core.ItemId == 10161066).Position.Index;
            if (ancientAvailable) ancient = Seed("AncientTcp", 5);

            Character Seed(string name, byte areaId)
            {
                var area = resources.Towns.Areas[(17, areaId)];
                var done = area.Quests.Concat(area.AnyQuests).Concat(area.Default.DungeonGates.SelectMany(gate => gate.Quests)).Distinct().ToArray();
                var bounds = area.Map(done.ToHashSet()).DungeonGates.First(gate => gate.Conditions.Length == 0 && gate.Quests.All(done.Contains)).Bounds;
                var position = new Location(17, areaId, checked((ushort)(bounds.X + bounds.Width / 2)), checked((ushort)(bounds.Y + bounds.Height / 2)));
                var seed = CharacterCreation.Prepare(name, resources.Characters.Get(0), position, position, resources.Learning.Get);
                var actor = repository.Create(owner, seed with { GrowType = 1, Level = 95, AcceptedStoryLevel = 95,
                    CompletedQuests = done, InitialLearning = seed.InitialLearning with { Experience = resources.Growth.GetLevelStartExperience(95) } });
                return InventoryService.Commit(repository, owner, actor, grants.Grant(actor,
                    areaId == 5 ? [new(3037, 1000), new(3332, 100)] : [new(3332, 100)]));
            }
        }
        await fixture.StartAsync();
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "infinite-fatigue off", out var mode) &&
            mode == "OK {\"infiniteFatigue\":false}", "异界远古TCP显式验证实际疲劳扣除");
        using (var peer = Open(fixture))
        {
            peer.Select(modern.Slot, prepared: true, resumeTown: true, room: false);
            Bytes(peer.DimensionEntranceState!, [0,0,0,0,0,0,0,0,2], "异界入口815初始化完整零状态与隐藏奖励剩余2");
            UseTicket(peer);
            peer.Expect(1, 515, Build(writer => { writer.Write((byte)1); writer.Write(ticket); writer.Write((byte)0); writer.Write(159u); }));
            Drain(peer);
            Bytes(peer.DimensionEntranceState!, [0,0,0,0,5,0,0,0,2], "515成功后815刷新真实额外次数");
            string capped = State(modern);
            UseTicket(peer);
            peer.Expect(1, 515, Build(writer => { writer.Write((byte)0); writer.Write((ushort)0); writer.Write(ticket); writer.Write((byte)0); writer.Write((byte)159); }));
            Drain(peer);
            Bytes(peer.DimensionEntranceState!, [0,0,0,0,5,0,0,0,2], "第二张五次券被拒，已提交额度保持五次");
            That(State(modern) == capped, "超限重复请求不扣券、不改revision或额度");
            string stable = State(modern);
            SelectEntrance(peer);
            var entry = Entry(7165, 0);
            fixture.Sql("CREATE TRIGGER otherverse_tcp_fail AFTER UPDATE ON character_dungeon_state BEGIN SELECT RAISE(ABORT,'entry rollback'); END");
            peer.Send(16, entry); peer.Closed();
            That(State(modern) == stable, "真实16晚期SQL失败不扣次数、门票、疲劳或尝试号");
            fixture.Sql("DROP TRIGGER otherverse_tcp_fail");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(modern.Slot, prepared: true, resumeTown: true, room: false);
            Bytes(peer.DimensionEntranceState!, [0,0,0,0,5,0,0,0,2], "失败重连回放原815额外次数");
            SelectEntrance(peer);
            peer.Send(16, Entry(7167, 2)); var info = peer.Expect(0, 28).Body; var map = peer.Expect(0, 29).Body;
            Drain(peer);
            That(peer.DimensionEntranceState is { Length: 9 } state && U32(state, 0) == 1 && U32(state, 4) == 5 && state[8] is 1 or 2,
                "现代实际副本入场计一次基本次数，不发送虚构550");
            var maze = SelfTestCatalogs.Dungeons(resources.Archive).Get(7167).Mazes.Single(m => m.SourceOrdinal == info[7]);
            var route = QuestStoryRouteTests.Path(maze.StaticTopology, new(map[0], map[1]), new(info[8], info[9]));
            var own = new DimensionRewardCatalog(resources.Archive, items.Items).Professions[(0, 1)];
            bool bossSeen = false;
            for (int room = 0; room < route.Length; room++)
            {
                Load(peer); Drain(peer);
                foreach (int at in Enumerable.Range(0, map[18]).Select(i => 19 + 22 * i).Where(at => map[at + 21] == 0 && map[at + 13] != 9))
                {
                    ushort key = U16(map, at + 6); peer.Send(39, Death(key, peer.Id, map[at + 12]));
                    var notices = new List<WirePacket>(); QuestLifecycleTcpTests.DrainNotifications(peer, notices.Add);
                    byte[] loot = notices.Single(p => p is { Kind: 0, Opcode: 38 } && U16(p.Body) == key).Body;
                    if (map[at + 13] != 3 || room != route.Length - 1) continue;
                    That(Enumerable.Range(0, U16(loot, 2)).Count(i => own.Normal.Contains((int)U32(loot, 10 + i * 131)) ||
                        own.Set.Contains((int)U32(loot, 10 + i * 131))) >= 2, "TCP单人勇士终点Boss至少两件本人职业异界装");
                    bossSeen = true;
                }
                if (room + 1 < route.Length)
                {
                    byte[] move = new byte[100]; move[0] = (byte)route[room + 1].X; move[1] = (byte)route[room + 1].Y;
                    peer.Send(45, move); peer.Expect(1, 45, [1]); map = peer.Expect(0, 29).Body; Drain(peer);
                }
            }
            That(bossSeen, "TCP单人实际完成终点Boss死亡");
            Return(peer);
        }
        if (!ancientAvailable)
            TestCheck.Skip("ancient.resource-missing", "远古7101：官方PVF未安装镇17 Area5与七远古DGN资源增量，仅覆盖现代异界；缺资源时的落点拒绝见 ChannelAreaLandingTests");
        else
        {
            using (var peer = Open(fixture))
            {
                peer.Select(ancient.Slot, prepared: true, resumeTown: true, room: false);
                That(peer.AncientGateEnergy == 0, "远古新增大厅初始化四字节772能量");
                long fatigue = (long)fixture.Query("SELECT remaining FROM character_fatigue WHERE character_id=$p0", ancient.Id)[0][0];
                SelectEntrance(peer);
                peer.Send(16, Entry(7101, 2)); peer.Expect(0, 28); peer.Expect(0, 29);
                bool cube = false, material = false;
                Drain(peer, packet =>
                {
                    if (packet.Opcode != 14) return;
                    That(packet.Body.Length == 3 + U16(packet.Body, 1) * 123, "入场物品差量保持完整123字节行");
                    for (int at = 3; at < packet.Body.Length; at += 123)
                    {
                        if (U16(packet.Body, at) == 358)
                        {
                            That(U32(packet.Body, at + 2) == 3037 && U32(packet.Body, at + 6) == 880, "远古入场14同步实际账号无色余额");
                            cube = true;
                        }
                        if (U32(packet.Body, at + 2) == 3332)
                        {
                            That(U32(packet.Body, at + 6) == 90, "远古入场14同步十个入场材料消耗");
                            material = true;
                        }
                    }
                });
                That(cube && material && (long)fixture.Query("SELECT remaining FROM character_fatigue WHERE character_id=$p0", ancient.Id)[0][0] == fatigue - 6,
                    "远古入场双材料和六点疲劳均真实提交与投影");
                Load(peer); Drain(peer); Return(peer);
            }
            using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
                That(CurrencyWallet.Get(repository.LoadCharacter(owner, ancient.Slot).Inventory, CurrencyKind.ClearCube) == 880,
                    "远古账号晶体扣款重读保持一次");
        }

        void UseTicket(TcpPeer peer) => peer.Send(515, Build(writer =>
        { writer.Write(ticket); writer.Write((byte)0); writer.Write(0u); writer.Write(159u); writer.Write(0u); writer.Write(0u); }));
        static byte[] Entry(uint id, byte difficulty)
        {
            byte[] body = new byte[25]; BinaryPrimitives.WriteUInt32LittleEndian(body, id);
            body[4] = difficulty; body[9] = body[10] = 255; return body;
        }
        static void Drain(TcpPeer peer, Action<WirePacket>? observe = null) => QuestLifecycleTcpTests.DrainNotifications(peer, packet =>
        {
            if (packet.Opcode == 815) peer.ReadDimensionEntrance(packet);
            if (packet.Opcode == 772) peer.ReadAncientGate(packet);
            That(packet.Opcode != 550 || U32(packet.Body, 0) is < 7165 or > 7179, "现代异界不发布通用550虚构状态");
            observe?.Invoke(packet);
        });
        static void Return(TcpPeer peer)
        {
            peer.Send(42); peer.Expect(1, 42, [1]); peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 0]);
            peer.Expect(0, 24); RefreshedQuests(peer); Drain(peer);
        }
        static void SelectEntrance(TcpPeer peer)
        {
            peer.Send(15, new byte[4]); peer.Expect(1, 15, [1]);
            peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]);
            int notices = 0;
            Drain(peer, packet =>
            {
                if (packet.Opcode != 27) return;
                Bytes(packet.Body, peer.SelectionNoticeBody(), "异界远古保持原选图完整正文");
                notices++;
            });
            That(notices == 1, "真实15只打开一次副本选择");
        }
        string State(Character actor) => fixture.Snapshot("SELECT revision,daily_booster_usage FROM characters WHERE id=$p0", actor.Id) +
            fixture.Snapshot("SELECT * FROM character_dungeon_state WHERE character_id=$p0", actor.Id) +
            fixture.Snapshot("SELECT * FROM character_dungeon_incount WHERE character_id=$p0 ORDER BY dungeon", actor.Id) +
            fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", actor.Id) +
            fixture.Snapshot("SELECT * FROM character_fatigue WHERE character_id=$p0", actor.Id);
    }
}
