using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class EnchantmentTcpTests
{
    static async Task RunMultipleCardsAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        // 原五珠和资格代表继续覆盖；追加总权重100/500/99/600的真实普通卡池。
        int[] sources = [490701137, 490701256, 490702314, 490702319, 490702378, 2683770, 10099668, 10148273,
            10304051, 10310134, 10311584, 10311590];
        int[] unknown = [2600657, 10158322]; // foil与独立过期使用标记仍未闭合。
        var items = resources.ItemResources.Items;
        var catalog = resources.ItemResources.Enchantments;
        var definitions = sources.Select(id => catalog.Get(id)).ToArray();
        for (int i = 0; i < sources.Length; i++)
            That(definitions[i]?.RandomCards is { Positions.IsEmpty: false }, $"真实多卡珠{sources[i]}有完整奖池");
        int[] targets = definitions.Select(definition => definition!.LimitedItems.IsEmpty ?
            new[] { 27308, 10400, 22004, 24002, 400330097, 400330118 }
                .First(id => definition.Targets.Contains((items.Get(id).Kind, items.Get(id).EquipmentSlot))) :
            definition.LimitedItems.Order().First()).ToArray();
        ItemCore[] originals = targets.Select(id => InventoryRewardGrantService.Create(items.Get(id)) with
        {
            Attr = 7, GenuineUpgrade = 4, AmplifyType = 2, AmplifyValue = 21, Rune = 8,
            EnchantCardId = 3175, EnchantUpgrade = 2, Extension97 = 0x00012711,
            SortLockFlag = 1, Random0 = new(1, 2, 3)
        }).ToArray();
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "multiple-card-beads"), resources: resources);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            new CharacterRepository(fixture.Database).Create(owner, new("MultiBeads", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([
                    .. sources.Select((id, index) => new InventoryItem(0, Main((short)(82 + index)),
                        InventoryRewardGrantService.Create(items.Get(id), 2))),
                    .. originals.Select((core, index) => new InventoryItem(0, Main((short)(9 + index)), core)),
                    .. unknown.Select((id, index) => new InventoryItem(0, Main((short)(110 + index)),
                        InventoryRewardGrantService.Create(items.Get(id), 2)))], Gold: 1234)
            });
        fixture.Sql("INSERT INTO character_expert_job(character_id,account,job_type,expertness) SELECT id,account,1,0 FROM characters");
        string originalUids = Uids();
        var saved = new byte[sources.Length][];
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(inventoryNotices: Notices(), expertJobInfo: ExpertInfo());
            string before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_weighted_bead BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='enchant_by_bead' AND NEW.slot_index=90 BEGIN SELECT RAISE(ABORT,'weighted bead rollback'); END");
            Send(peer, 90, 17); peer.Closed();
            That(Snapshot() == before, "真实权重珠晚SQL失败，已规划卡与源/完整目标/金币/版本全部回滚，无提前成功");
            fixture.Sql("DROP TRIGGER fail_weighted_bead");
        }
        using (var peer = Open(fixture))
        {
            var entry = peer.Select(inventoryNotices: Notices(), expertJobInfo: ExpertInfo());
            byte[] full = entry.First(packet => packet is { Kind: 0, Opcode: 13 } && packet.Body[0] == 0).Body;
            int success = 0;
            foreach (short source in new short[] { 110, 111 })
            {
                string before = Snapshot();
                Send(peer, source, 9); peer.Expect(1, 272, [0, 23, 0]); peer.Barrier();
                That(Snapshot() == before, "未知foil/过期使用声明272正常拒绝，不扣源或提交任何资产");
                Accept(peer, success++, full);
                before = Snapshot();
                // 本人601复用正式珠服务回退入口，不伪装跨玩家配方卡片请求。
                SendStore(peer, source, 9);
                peer.Expect(1, 601, [0, 23, 0]); peer.Barrier();
                That(Snapshot() == before, "同一未知声明本人601也正常拒绝，金币/熟练度/耐久与库存不变");
                Accept(peer, success++, full);
            }
            Accept(peer, success, full);

            // 已提交移动后，旧槽位请求必须重新取得真实源，不能继续使用之前的源引用。
            peer.Send(19, Build(writer =>
            {
                writer.Write((byte)0); writer.Write((short)87); writer.Write(sources[5]); writer.Write(0);
                writer.Write((byte)0); writer.Write((short)96); writer.Write(0); writer.Write(0); writer.Write(-1); writer.Write((ushort)0);
            }));
            peer.Expect(1, 19, Build(writer =>
            {
                writer.Write((byte)1); writer.Write((byte)0); writer.Write((short)87); writer.Write(2);
                writer.Write((byte)0); writer.Write((short)96);
            }));
            peer.Barrier();
            string moved = Snapshot();
            Send(peer, 87, 14); peer.Expect(1, 272, [0, 23, 0]); peer.Barrier();
            That(Snapshot() == moved, "源移位后旧槽请求不重复抽取或消费，新槽合法请求仍可继续");
            for (int i = 5; i < sources.Length; i++) Accept(peer, i, full);

            peer.Send(15, new byte[4]); peer.Expect(1, 15, [1]);
            peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]);
            peer.Expect(0, 27, new byte[42]);
            string beforeMapUse = Snapshot();
            for (int i = 5; i < sources.Length; i++)
            {
                Send(peer, SourceSlot(i), (short)(9 + i));
                peer.Expect(1, 272, [0, 23, 0]);
                SendStore(peer, SourceSlot(i), (short)(9 + i));
                peer.Expect(1, 601, [0, 19, 0]);
            }
            peer.Barrier();
            That(Snapshot() == beforeMapUse, "三种已证village声明在选图阶段272/本人601均拒绝，不消费或更换附魔");
        }
        using (var peer = Open(fixture))
        {
            var entry = peer.Select(inventoryNotices: Notices(), expertJobInfo: ExpertInfo());
            byte[] full = entry.First(packet => packet is { Kind: 0, Opcode: 13 } && packet.Body[0] == 0).Body;
            for (int i = 0; i < sources.Length; i++)
                Bytes(Row(full, (short)(9 + i), true), saved[i], "重连恢复每颗实际珠完整123B目标行及97扩展");
            peer.Barrier();
        }
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var character = new CharacterRepository(fixture.Database).List(owner).Single();
            for (int i = 0; i < sources.Length; i++)
            {
                var target = character.Inventory.Find(Main((short)(9 + i)))!;
                That(target.Core == originals[i] with { EnchantCardId = (int)U32(saved[i], 14), EnchantUpgrade = saved[i][18] } &&
                    character.Inventory.Find(Main(SourceSlot(i)))!.Core.Count == 1,
                    "实际普通多卡珠只改Card/Upgrade并各扣一源，完整核心在新Repository恢复");
            }
            That(character.Inventory.Gold == 1234 && character.ExpertJobExperience == 0 &&
                character.ExpertMachineEndurance == 300 && Uids() == originalUids &&
                unknown.Select((id, index) => character.Inventory.Find(Main((short)(110 + index)))!.Core.Count).All(count => count == 2),
                "全部目标和源UID保留，未知源/钱包/本人副职业账本没有附带变更");
        }

        void Accept(TcpPeer peer, int index, byte[] full)
        {
            short target = (short)(9 + index), source = SourceSlot(index);
            Send(peer, source, target);
            byte[] delta = peer.Expect(0, 14).Body;
            byte[] row = Row(delta, target), original = Row(full, target, true);
            int[] expected = index < 2 ? [490701142, 490701143, 490701144, 490701145, 490701146, 490701147] :
                index < 5 ? [490702344, 490702345, 490702346, 490702347, 490702348, 490702349,
                    490702350, 490702351, 490702352, 490702353, 490702354] :
                definitions[index]!.RandomCards!.Positions.ToArray();
            That(expected.Contains((int)U32(row, 14)) && row[18] == definitions[index]!.Upgrade &&
                row.AsSpan(0, 14).SequenceEqual(original.AsSpan(0, 14)) && row.AsSpan(19).SequenceEqual(original.AsSpan(19)),
                "实际五珠和资格代表只安装真实池成员及Upgrade，97/期限/强化/增幅等字节完整保留");
            That(U32(Row(delta, source), 2) == sources[index] && U32(Row(delta, source), 6) == 1,
                "合法Town请求在成功ACK前同步源剩余一颗，不把购买材料当使用材料");
            saved[index] = row;
            peer.Expect(1, 713, [1, 0, (byte)target, 0]);
            peer.Expect(1, 272, [1, 0, (byte)target, 0]); peer.Barrier();
        }
        static InventorySlot Main(short slot) => new(InventoryListType.Main, slot);
        static short SourceSlot(int index) => index == 5 ? (short)96 : (short)(82 + index);
        static void Send(TcpPeer peer, short source, short target) => peer.Send(272, Build(writer =>
        { writer.Write((byte)0); writer.Write(source); writer.Write((byte)0); writer.Write(target); writer.Write(uint.MaxValue); }));
        static void SendStore(TcpPeer peer, short source, short target) => peer.Send(601, Build(writer =>
        {
            writer.Write(peer.Id); writer.Write(0u); writer.Write((byte)2);
            writer.Write((byte)0); writer.Write((ushort)target); writer.Write((byte)0); writer.Write((ushort)source);
        }));
        static byte[] Row(byte[] body, short slot, bool full = false)
        {
            int head = full ? 5 : 3, count = U16(body, full ? 3 : 1);
            That(body.Length == head + count * 123, "普通多卡珠复用完整123B物品行");
            int at = Enumerable.Range(0, count).Select(index => head + index * 123).Single(offset => U16(body, offset) == slot);
            return body.AsSpan(at, 123).ToArray();
        }
        (byte Kind, ushort Opcode)[] Notices() => Enumerable.Repeat(((byte)1, (ushort)713), sources.Length).ToArray();
        static byte[] ExpertInfo() => Build(writer =>
        { writer.Write((byte)0); writer.Write((byte)1); writer.Write((byte)0); writer.Write((byte)0); writer.Write(1u); writer.Write(300u); });
        string Uids() => fixture.Snapshot("SELECT item_uid FROM character_inventory_items ORDER BY item_uid");
        string Snapshot() => fixture.Snapshot("SELECT id,revision,gold FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM character_expert_job ORDER BY character_id") +
            fixture.Snapshot("SELECT * FROM character_expert_meta ORDER BY character_id") +
            fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account") +
            fixture.Snapshot("SELECT * FROM sqlite_sequence ORDER BY name");
    }
}
