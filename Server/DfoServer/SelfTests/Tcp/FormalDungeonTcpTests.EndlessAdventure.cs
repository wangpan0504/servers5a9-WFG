using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Skills;
using DfoServer.Network.Management;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunEndlessAdventureAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        foreach (bool infinite in new[] { false, true })
        {
            var clock = new DailyBoosterTestClock(DateTimeOffset.Parse("2026-11-03T07:00:00+08:00"));
            await using var fixture = new TcpTestFixture(options, Path.Combine(root, infinite ? "infinite" : "finite"),
                gm: true, resources: resources, sharedResourcePath: true, clock: clock);
            var repository = new CharacterRepository(fixture.Database);
            var candidate = CharacterCreation.Prepare(infinite ? "EndlessInf" : "EndlessBurn", resources.Characters.Get(9),
                resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get);
            Character character;
            using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
                character = repository.Create(owner, candidate with { Level = 4, AcceptedStoryLevel = 1,
                    CompletedQuests = [3145, 4873, 3157],
                    InitialLearning = SkillPointLedger.Synchronize(9, 0, 4,
                        candidate.InitialLearning with { Experience = resources.Growth.GetLevelStartExperience(4) }, resources.Learning) });
            fixture.Sql("INSERT INTO account_wallets(account) VALUES($p0) ON CONFLICT DO NOTHING", fixture.Options.DevelopmentAccount);
            int originalDay = EndlessAdventurePolicy.Day(clock.GetUtcNow());
            fixture.Sql("INSERT INTO account_endless_adventure_days(account,day,earned) VALUES($p0,$p1,199)",
                fixture.Options.DevelopmentAccount, originalDay);
            fixture.Sql("""
                INSERT INTO account_material_balances(account,item_id,balance,raid_week,raid_earned,character_earned)
                VALUES($p0,490011740,500,0,0,'{}')
                """, fixture.Options.DevelopmentAccount);
            await fixture.StartAsync();
            Gm("pc-room on");
            Gm(infinite ? "infinite-fatigue on" : "infinite-fatigue off");
            using var peer = Open(fixture);
            peer.Select(character.Slot, prepared: true, combos: StoredCombos(fixture, character.Id), premiumPcRoom: true);
            Buy(400000023, accepted: false);
            Gm("event on 2257");
            Events(true);
            Buy(2, accepted: false);
            Buy(400000023, accepted: true);
            int remainingCoins = Convert.ToInt32(fixture.Query(
                "SELECT balance FROM account_material_balances WHERE account=$p0 AND item_id=490011740", fixture.Options.DevelopmentAccount)[0][0]);
            That(remainingCoins < 500 && remainingCoins > 0, "真实CMD21消耗原账号硬币，未造普通背包硬币");
            if (infinite)
            {
                clock.Advance(TimeSpan.FromDays(1));
                WirePacket reset;
                int notifications = 0;
                do
                {
                    reset = peer.Read();
                    That(reset.Kind == 0 && ++notifications <= 32, "跨日只有有界的既有服务日通知，持续读取直到实际活动进度到达");
                } while (reset.Opcode != 1103);
                That(reset.Body.Length == 152 && U32(reset.Body, 0) == 2257 && U32(reset.Body, 4) == 0,
                    "在线跨06点由原worker发送真实零进度，无需重登或新通关");
                PartyDungeonTcpTests.Drain(peer);
                // 屏障命令完成后的原网吧日界刷新仍须完整接收，不能误当活动目录或跳过未知包。
                peer.Expect(0, 1517, [0, 0, 0, 0, 0]);
                peer.Expect(0, 1639, [0]);
                peer.Expect(0, 490, [0, 0, 0]);
                That(fixture.Query("SELECT earned FROM account_endless_adventure_days WHERE account=$p0 AND day=$p1",
                    fixture.Options.DevelopmentAccount, originalDay) is [[199L]] &&
                    fixture.Query("SELECT count(*) FROM account_endless_adventure_days WHERE account=$p0 AND day=$p1",
                    fixture.Options.DevelopmentAccount, originalDay + 1) is [[0L]],
                    "跨日只读新额度，保留旧199且不为查询写新日账本");
            }
            Gm("event off 2257");
            Events(false);
            Buy(400000023, accepted: false);
            That(Convert.ToInt32(fixture.Query("SELECT balance FROM account_material_balances WHERE account=$p0 AND item_id=490011740",
                fixture.Options.DevelopmentAccount)[0][0]) == remainingCoins, "关闭和伪造NPC不额外消耗材料或商品限额");

            peer.Send(562, [1]); peer.Expect(1, 562, [1]); peer.Expect(0, 490, [0, 0, 1]); peer.Barrier();
            EntryTown(peer);
            SelectMap(peer);
            var selection = Selection();
            BinaryPrimitives.WriteUInt32LittleEndian(selection.AsSpan(16), 0);
            peer.Send(16, selection);
            peer.Expect(0, 28); peer.Expect(0, 29);
            int beforeFatigue = Remaining();
            Load(peer, pcRoom: true);
            PartyDungeonTcpTests.Drain(peer);
            That(Remaining() == beforeFatigue - (infinite ? 0 : 2),
                "真实普通入场加载：有限燃烧扣2，无限燃烧不扣真实余额");
            int afterFatigue = Remaining();
            peer.Send(37, new byte[8]);
            PartyDungeonTcpTests.Drain(peer);
            That(Remaining() == afterFatigue, "重复房间加载不重复扣燃烧疲劳，连接保持");

            int Remaining() => Convert.ToInt32(fixture.Query("SELECT remaining FROM character_fatigue WHERE character_id=$p0", character.Id)[0][0]);
            void Gm(string command)
            {
                That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, command, out var reply) && reply.StartsWith("OK ", StringComparison.Ordinal),
                    "隔离GM真实命令成功：" + command);
            }
            void Buy(int npc, bool accepted)
            {
                peer.Send(21, Build(writer => { writer.Write(490011735); writer.Write(1); writer.Write(400000008); writer.Write(npc); }));
                byte[] body = peer.Expect(1, 21).Body;
                That(body.Length > 0 && body[0] == (accepted ? 1 : 0), "疲劳商店只有开启且正确NPC可以兑换");
                var updates = PartyDungeonTcpTests.Drain(peer);
                if (accepted)
                {
                    That(updates.Any(packet => packet is { Kind: 0, Opcode: 563 }) &&
                        fixture.Query("SELECT count(*) FROM character_inventory_items WHERE owner_character_id=$p0 AND json_extract(item_core,'$.ItemId')=490011735", character.Id)
                            is [[1L]], "成功ACK后原563材料差量与真实商品物理行均到达");
                    var calendar = updates.Single(packet => packet is {Kind: 0, Opcode: 1681, Body: [45, ..]}).Body;
                    int balance = Convert.ToInt32(fixture.Query("SELECT balance FROM account_material_balances WHERE account=$p0 AND item_id=490011740",
                        fixture.Options.DevelopmentAccount)[0][0]);
                    That(calendar.Length == 126 && U16(calendar, 1) == 1 && U16(calendar, 3) == 0 && U32(calendar, 5) == 490011740 &&
                        U32(calendar, 9) == balance && !updates.Any(packet => packet is {Kind: 0, Opcode: 14, Body: [45, ..]}),
                        "兑换后日历静默同步真实账号剩余余额，不把消费当作新增道具提示");
                }
            }
            void Events(bool active)
            {
                var table = peer.Expect(0, 108);
                using var zipped = new ZLibStream(new MemoryStream(table.Body), CompressionMode.Decompress);
                using var output = new MemoryStream(); zipped.CopyTo(output);
                string decoded = Encoding.UTF8.GetString(output.ToArray());
                That(decoded.Contains("fatigue shop event/0/0", StringComparison.Ordinal) == active,
                    "完整108仅在开启时包含本版原生1400窗口路由");
                That(decoded.Contains("[show_item_index] 1 490011740", StringComparison.Ordinal) == active,
                    "GM开关同步日历特殊道具登记，持有数量仍来自原563真实余额");
                peer.Expect(0, 13, [44, 0, 0]);
                var calendar = peer.Expect(0, 13).Body;
                That(calendar[0] == 45 && BinaryPrimitives.ReadUInt16LittleEndian(calendar.AsSpan(1)) == (active ? 1 : 0) &&
                    (!active || calendar.Length == 126 && U32(calendar, 5) == 490011740 &&
                        U32(calendar, 9) == Convert.ToInt32(fixture.Query("SELECT balance FROM account_material_balances WHERE account=$p0 AND item_id=490011740",
                            fixture.Options.DevelopmentAccount)[0][0])),
                    "活动目录切换同步商店原生查询的list45，list44不重复存币，关闭时清空旧投影");
                var npc = peer.Expect(0, 639).Body;
                That(npc.Length > 5 && npc[0] == (active ? 0 : 2) &&
                    U32(npc, 1) == npc.Length - 5 && Encoding.UTF8.GetString(npc, 5, npc.Length - 5).Contains("400000023 `[right]` 599 240 0", StringComparison.Ordinal),
                    "639按V31单地图格式在赛丽亚房间登记或删除新疲劳商店，不放到负Y画面外");
                That(Encoding.ASCII.GetString(npc, 5, npc.Length - 5) ==
                    "[map] 38 1 [/map] [npc] 400000023 `[right]` 599 240 0 [/npc]",
                    "疲劳商店639不混入其他城镇地图，沿客户端已验证的单地图NPC格式");
                var progress = peer.Expect(0, 1103).Body;
                That(progress.Length == 152 && U32(progress, 0) == 2257 && U32(progress, 4) == (active ? 199 : 0) &&
                    U32(progress, 20) == 128 && progress.AsSpan(24).IndexOfAnyExcept((byte)0) == -1,
                    "1103完整进度正文、CRC与序号符合V31合同");
                peer.Expect(0, 706);
                peer.Barrier();
            }
        }
    }
}
