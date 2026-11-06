using DfoServer.Development.Dungeons;
using DfoServer.Network.Management;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunClassExperienceAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source with { DevelopmentDungeonEntryProbe = DungeonEntryProbeMode.Disabled },
            Path.Combine(root, "class-experience"), gm: true, resources: resources);
        await fixture.StartAsync();
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "infinite-fatigue off", out _), "体验测试不用GM无限疲劳代替玩法资格");
        byte[] request = Convert.FromHexString("E61B00000000000001FFFF0000000000000000000000000000");
        foreach (var (job, grow, remaining) in new[] { ((byte)0, 0, 0), ((byte)3, 1, 17) })
        {
            ushort slot = checked((ushort)Convert.ToInt32(fixture.Query("SELECT count(*) FROM characters")[0][0]));
            using var peer = Open(fixture);
            peer.Create("Experience" + job, job);
            long originalStamina = grow == 0 ? 0 : DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120;
            fixture.Sql("UPDATE characters SET level=20,grow_type=$p1,stamina_until=$p2 WHERE id=$p0", peer.Id, grow, originalStamina);
            fixture.Sql("UPDATE character_fatigue SET used=maximum-$p1,remaining=$p1 WHERE character_id=$p0", peer.Id, remaining);
            short potion;
            short projectile;
            short placed;
            using (var owner = new DfoServer.Game.Accounts.AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            {
                var repository = new DfoServer.Persistence.CharacterRepository(fixture.Database);
                var character = repository.LoadCharacter(owner, slot);
                var items = resources.ItemResources;
                var inventory = new DfoServer.Game.Inventory.Services.InventoryService(items.Items, items.GoldLimits, items.EpicPieces);
                character = DfoServer.Game.Inventory.Services.InventoryService.Commit(repository, owner, character,
                    inventory.Grant(character, [new(1113, 3), new(6001,1), new(1073,1), new(3037, 20), new(3036, 10)]));
                potion = character.Inventory.Items.Single(item => item.Core.ItemId == 1113).Position.Index;
                projectile = character.Inventory.Items.Single(item => item.Core.ItemId == 6001).Position.Index;
                placed = character.Inventory.Items.Single(item => item.Core.ItemId == 1073).Position.Index;
            }
            peer.Select(slot);
            string original = Stable();
            byte[] skillInfo = peer.SkillInfo;
            string noAttempt = fixture.Snapshot("SELECT * FROM character_dungeon_state WHERE character_id=$p0", peer.Id);
            peer.Send(16, request);
            peer.Expect(1, 16, [0, 9, 0, 0]);
            peer.Barrier();
            That(Stable() == original && fixture.Snapshot("SELECT * FROM character_dungeon_state WHERE character_id=$p0", peer.Id) == noAttempt,
                "直接伪造16缺少本次15体验上下文时拒绝且不预留尝试");
            OpenExperience();
            foreach (int offset in new[] { 4, 5, 7, 8, 9, 11, 15, 16, 20 })
            {
                byte[] invalid = (byte[])request.Clone();
                invalid[offset] ^= 1;
                peer.Send(16, invalid);
                peer.Expect(1, 16, [0, 9, 0, 0]);
                peer.Barrier();
            }
            That(Stable() == original && fixture.Snapshot("SELECT * FROM character_dungeon_state WHERE character_id=$p0", peer.Id) == noAttempt,
                "错误完整模式字段只结束入场等待，不创建普通或回放实例");
            RejectSkillWrites();
            That(Stable() == original, "体验等待阶段也不接受永久技能写入");
            byte[] eventRequest = (byte[])request.Clone();
            eventRequest.AsSpan(21,4).Fill(255);
            peer.Send(16, eventRequest);
            peer.Expect(0, 27, new byte[42]);
            byte[] info = peer.Expect(0, 28).Body;
            byte[] map = peer.Expect(0, 29).Body;
            That(info.Length == 36 && U32(info) == 7142 && info[4] == 0 && info[7] == 0 && info[8] == 0 && info[9] == 0 &&
                map.Length == 45 && U32(map, 14) == 76032 && map[18] == 1 && U32(map, 27) == 75046 && map[31] == 15 && map[32] == 3 && map[40] == 0,
                "体验先27建立场景，再28/29发布真实7142/76032及资源Boss行，不伪造临时职业信息");
            string entered = fixture.Snapshot();
            Load(peer);
            peer.Barrier();
            peer.Send(44, Build(w => { w.Write((ushort)potion); w.Write((byte)0); w.Write(1u); w.Write(1113); w.Write(0); }));
            peer.Expect(1, 44, Build(w => { w.Write((byte)0); w.Write((ushort)0); w.Write((byte)0); w.Write(1u); w.Write(1113); }));
            peer.Barrier();
            foreach (var item in new[] { (Slot: projectile,Id: 6001), (Slot: placed,Id: 1073) })
            {
                peer.Send(44, Build(w => { w.Write((ushort)item.Slot); w.Write((byte)0); w.Write(0u); w.Write(item.Id); w.Write(0); }));
                peer.Expect(1, 44, Build(w => { w.Write((byte)0); w.Write((ushort)0); w.Write((byte)0); w.Write(0u); w.Write(item.Id); }));
                peer.Barrier();
            }
            foreach (var (body, ack) in new[] {
                ("1000000010001A0A080210E60218DD172005200001","010D0000000800120708E602100018021800"),
                ("1000000010001A0A080210E50218DC172005200001","010D0000000800120708E502100018021800") })
            {
                peer.Send(18, Convert.FromHexString(body));
                peer.Expect(1, 18, Convert.FromHexString(ack));
                peer.Barrier();
            }
            That(Stable() == original && fixture.Snapshot() == entered,
                "职业体验1113/throw/set原生0/0免扣且无数量更新，无色及有色技能材料均回删除0，全部角色和账号资产/revision不变");
            RejectSkillWrites();
            peer.Send(37, new byte[8]);
            peer.Barrier();
            That(Stable() == original && fixture.Snapshot() == entered &&
                fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id) is [[1L]],
                "加载、重复加载及永久技能请求均保持职业、技能、金币、库存、疲劳和revision；仅入场预留一次尝试");
            ushort monsterKey = U16(map, 25);
            peer.Send(39, Death(monsterKey, peer.Id, map[31]));
            peer.Expect(0, 38, Build(writer => { writer.Write(monsterKey); writer.Write((ushort)0); writer.Write(new byte[] { 0, 0, 255, 0, 0 }); }));
            peer.Barrier();
            peer.Send(39, Death(monsterKey, peer.Id, map[31]));
            peer.Barrier();
            peer.Send(46, DfoServer.DungeonSettlementProtocolTests.Report(peer.Id, 90));
            peer.Barrier();
            That(Stable() == original && fixture.Snapshot() == entered && EmptyRewardLedger(),
                "体验Boss死亡只应答无掉落38，重放和伪造结算不发经验/任务/翻牌，不写普通奖励账本");
            peer.Send(42);
            peer.Expect(1, 42, [1]);
            peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 0]);
            peer.Expect(0, 24);
            peer.Expect(0,172,Build(w=>
            {w.Write((byte)3);foreach(int id in new[]{1073,1113,6001}){w.Write(id);w.Write(0);}}));
            RefreshedQuests(peer);
            peer.Barrier();
            That(Stable() == original && fixture.Snapshot() == entered, "体验42恢复原入口位置，不改变原职业技能、资产或既有虚弱期限");
            // 同一账号接管后重新加载，确认临时上下文没有持久化并恢复普通技能编辑。
            using var reconnect = Open(fixture);
            // 已有虚弱角色直接恢复房间，覆盖23后、1379前的通知；另一角色保留普通重选路径。
            reconnect.Select(slot, resumeTown: originalStamina > 0);
            Bytes(reconnect.SkillInfo, skillInfo, "重连后的完整技能投影仍是体验前正式技能");
            That(Stable() == original, "重登保留原角色职业与完整技能树");
            reconnect.Send(28, [0, 0, 6, 255, 255, 255, 255, 0]);
            reconnect.Expect(1, 28, [1, 0, 0, 6]);
            reconnect.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_skill_bindings WHERE character_id=$p0 AND context=0 AND slot=6", peer.Id) is [[1L]],
                "结束体验后普通快捷栏调整恢复实际提交，不泄漏体验门禁");

            string Stable() => fixture.Snapshot("SELECT job,grow_type,level,experience,gold,stamina_until FROM characters WHERE id=$p0", peer.Id) +
                string.Concat(new[] { "character_skills", "character_skill_points", "character_skill_bindings", "character_skill_commands",
                    "character_skill_combos", "character_fatigue", "character_active_main_quests", "character_inventory_items", "character_inventory_slots" }
                    .Select(table => fixture.Snapshot($"SELECT * FROM {table} WHERE character_id=$p0 ORDER BY 1,2", peer.Id))) +
                fixture.Snapshot("SELECT * FROM account_currency_balances ORDER BY account,currency_kind");

            bool EmptyRewardLedger() => new[] { "character_dungeon_growth_receipts", "character_dungeon_loot_receipts",
                "character_dungeon_fatigue_receipts", "character_dungeon_settlement_receipts" }.All(table =>
                    fixture.Query($"SELECT count(*) FROM {table} WHERE character_id=$p0", peer.Id) is [[0L]]);

            void OpenExperience()
            {
                peer.Send(15, Convert.FromHexString("E61B0000"));
                peer.Expect(1, 15, [1]);
                peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]);
            }

            void RejectSkillWrites()
            {
                peer.Send(28, [0, 0, 6, 255, 255, 255, 255, 0]);
                peer.Send(29, [0, 1, 5, 0, 0, 1, 0]);
                peer.Send(491, [0, 0]);
                peer.Expect(1, 29, [0, 0xD9, 0]);
                peer.Barrier();
            }
        }
        Console.WriteLine("PASS 职业体验真实请求、零疲劳、临时技能隔离、无奖励、退出及接管重登TCP");
    }
}
