using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class PremiumContractTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        const int itemId = 35;
        const int unsupportedItemId = 10000387;
        const short slot = 65;
        That(resources.ItemResources.Contracts.TryConsumableContract(itemId, out var definition) && definition.Type == 22,
            "成功回归使用匹配资源中具有完整霸王规则的父物品35");
        long duration = definition.DurationSeconds;
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "premium-contract"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        ushort sourceCharacter;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("OldContract", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get);
            // 明确建立遗留实体，不走获取时即时激活的 grant，才能复验本次消费入口。
            var created = repository.Create(owner, candidate with { Inventory = candidate.Inventory with
            { Items = candidate.Inventory.Items.Add(new(0, new(InventoryListType.Main, slot),
                InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(itemId), 3)))
                .Add(new(0, new(InventoryListType.Main, 66), InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(unsupportedItemId)))) } });
            sourceCharacter = created.Id;
            repository.Create(owner, candidate with { Name = "AccountPerk" });
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            string before = Assets();
            Use(peer, 0); Refused(peer, 0);
            string rejected = Assets();
            // 1347只在Ready回复；未选角以完整ACK44收尾，随后真实选角验证连接继续。
            peer.Select();
            That(rejected == before && CountSource() == 3 && Contracts().Count == 0,
                "未选角契约拒绝完整收尾且可继续选角，不消费、不激活账号权益");
            before = Assets();
            foreach (bool shortRequest in new[] { false, true })
            {
                peer.Send(44, Build(writer =>
                {
                    writer.Write((ushort)66);
                    if (!shortRequest) writer.Write((byte)0);
                    writer.Write(uint.MaxValue); writer.Write(unsupportedItemId);
                    if (!shortRequest) writer.Write(0);
                }));
                peer.Expect(1, 44, Build(writer => { writer.Write((byte)0); writer.Write((ushort)123); writer.Write((byte)0); writer.Write(uint.MaxValue); writer.Write(unsupportedItemId); }));
                peer.Barrier();
                That(Assets() == before, "已识别但晶效果未实现的10/15B使用完整拒绝，不扣源、不续期或写任何资产");
            }
            fixture.Sql("CREATE TRIGGER fail_contract_use_late AFTER UPDATE OF premium_contracts ON account_wallets " +
                "WHEN NEW.premium_contracts<>OLD.premium_contracts BEGIN SELECT RAISE(ABORT,'contract late failure'); END");
            Use(peer, 1); peer.Closed();
            That(Assets() == before, "库存写入后账号期限更新末段失败，实体、钱包、revision及审计整笔回滚，不先发成功");
            fixture.Sql("DROP TRIGGER fail_contract_use_late");
        }
        long firstExpiry;
        long renewedExpiry;
        long finalExpiry;
        using (var peer = Open(fixture))
        {
            peer.Select();
            long started = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Use(peer, uint.MaxValue); Accepted(peer, uint.MaxValue);
            firstExpiry = Contracts().GetValueOrDefault(22);
            AssertPremium(peer.Expect(0, 1760).Body, firstExpiry);
            peer.Barrier();
            That(CountSource() == 2 && Contracts().Count == 1 && firstExpiry >= started + duration && firstExpiry <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + duration,
                "普通44只扣遗留源一件并激活真实霸王契约资源期限；完整u32令牌原样回显，无源14重复扣减或其他假效果");
            Use(peer, uint.MaxValue); Accepted(peer, uint.MaxValue);
            renewedExpiry = Contracts().GetValueOrDefault(22);
            AssertPremium(peer.Expect(0, 1760).Body, renewedExpiry);
            peer.Barrier();
            That(CountSource() == 1 && renewedExpiry == firstExpiry + duration,
                "同正文有剩余源时是下一次合法消费，无伪造冷却或永久token去重，每次精确续期");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(premiumContracts: true);
            AssertPremium(peer.PremiumInfo, renewedExpiry);
            peer.Send(44, Build(writer => { writer.Write((ushort)slot); writer.Write(0u); writer.Write(itemId); }));
            Accepted(peer, 0);
            finalExpiry = Contracts().GetValueOrDefault(22);
            AssertPremium(peer.Expect(0, 1760).Body, finalExpiry);
            peer.Barrier();
            That(CountSource() == 0 && finalExpiry == renewedExpiry + duration,
                "契约10B入口与15B共享单件消费和账号事务，重连后最后一件清槽且精确续期");
            string before = Assets();
            Use(peer, 0); Refused(peer, 0); peer.Barrier();
            That(Assets() == before, "重连按真实耗尽源拒绝，不借重连或不同token激活，失败后连接继续");
            peer.Send(7); peer.Expect(1, 7, [1]); peer.Expect(0, 2);
            peer.Select(1, premiumContracts: true);
            AssertPremium(peer.PremiumInfo, finalExpiry);
            That(Contracts().Count == 1 && CountSource() == 0,
                "同账号换角色使用同一持久期限，不把契约复制为另一个角色实体或再次赠送");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("restart");
        using (var peer = Open(fixture))
        {
            peer.Select(premiumContracts: true);
            AssertPremium(peer.PremiumInfo, finalExpiry);
            That(Contracts().GetValueOrDefault(22) == finalExpiry && CountSource() == 0 && fixture.Query("PRAGMA foreign_key_check").Length == 0,
                "重启恢复原已提交账号期限和耗尽源，不补发、续期或损坏外键");
        }
        await fixture.StopAsync();
        var expert = PremiumContractTests.VerifyExpertAccounts(fixture.Database, fixture.Options.DevelopmentAccount,
            resources.Towns, resources.Characters, resources.Learning, resources.ItemResources);
        await fixture.StartAsync("expert-resume");
        long expertExpiry;
        using (var peer = Open(fixture))
        {
            peer.Select(expert.Slot, premiumContracts: true, expertContract: true);
            That(BinaryPrimitives.ReadInt64LittleEndian(peer.ExpertContract.AsSpan(3)) == 0,
                "已到期账号来源登录清除达人状态，不按持有契约实体误放技能资格");
            peer.Send(29, [0, 1, 5, 0, 0, 3, 0]); peer.Expect(1, 29, [0, 0xD9, 0]); peer.Barrier();
            peer.Send(44, Build(writer => { writer.Write((ushort)65); writer.Write((byte)0); writer.Write(uint.MaxValue); writer.Write(43); writer.Write(0); }));
            peer.Expect(1, 44, Build(writer => { writer.Write((byte)1); writer.Write((ushort)65); writer.Write((byte)0); writer.Write(uint.MaxValue); writer.Write(43); }));
            expertExpiry = Contracts().GetValueOrDefault(27);
            var premiumInfo = peer.Expect(0, 1760).Body;
            That(premiumInfo.Length == 19 && premiumInfo[0] == 2 && premiumInfo[1] == 22 && premiumInfo[10] == 27,
                "达人真实使用ACK后列表包含本账号已提交的27和既有22，不重复发送源14");
            AssertExpertNotice(peer.Expect(0, 66).Body, expertExpiry);
            string beforeAutomatic = fixture.Snapshot("SELECT context,skill_id,level FROM character_skill_bindings WHERE character_id=$p0 AND skill_id<>5 ORDER BY context,skill_id", peer.Id);
            peer.Send(29, [0, 1, 5, 0, 0, 3, 0]);
            That(SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body).Skills.Single() is { Id: 5, Level: 4 },
                "真实TCP同连接消费达人后立即允许超过Lv3普通资格的四级技能，按正常SP费用提交");
            if (beforeAutomatic != fixture.Snapshot("SELECT context,skill_id,level FROM character_skill_bindings WHERE character_id=$p0 AND skill_id<>5 ORDER BY context,skill_id", peer.Id))
                peer.Expect(0, 19);
            peer.Barrier();
            peer.Send(7); peer.Expect(1, 7, [1]); peer.Expect(0, 2);
            peer.Select(expert.PeerSlot, premiumContracts: true, expertContract: true);
            AssertExpertNotice(peer.ExpertContract, expertExpiry);
            That(SkillPacketsTests.DecodeInitialized(peer.SkillInfo)[0].Skills.Single(skill => skill.Id == 5).Level == 1,
                "换角色复用同一账号达人期限，角色已学等级保持独立，不复制付费技能");
        }
        await fixture.StopAsync();
        fixture.Sql("UPDATE account_wallets SET premium_contracts=json_set(premium_contracts,'$.\"27\"',$p0),revision=revision+1",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1);
        await fixture.StartAsync("expert-expired");
        using (var peer = Open(fixture))
        {
            peer.Select(expert.Slot, premiumContracts: true, expertContract: true);
            var tree = SkillPacketsTests.DecodeInitialized(peer.SkillInfo)[0];
            That(tree.Skills.Single(skill => skill.Id == 5).Level == 2 && tree.Sp == expert.NormalSp &&
                BinaryPrimitives.ReadInt64LittleEndian(peer.ExpertContract.AsSpan(3)) == 0,
                "付费达人到期重登两树同步回落并退回超额SP，NOTI66零秒清除资格且额外来源保留");
            peer.Barrier();
        }

        Dictionary<int, long> Contracts() => fixture.Query("SELECT premium_contracts FROM account_wallets") is [var row]
            ? JsonSerializer.Deserialize<Dictionary<int, long>>((string)row[0])! : [];
        int CountSource() => fixture.Query("SELECT item_core FROM character_inventory_items i JOIN character_inventory_slots s USING(item_uid) " +
            "WHERE s.character_id=$p0 AND s.list_type=0 AND s.slot_index=$p1", sourceCharacter, slot) is [var row]
            ? JsonSerializer.Deserialize<ItemCore>((string)row[0])!.Count : 0;
        string Assets() => fixture.Snapshot("SELECT id,revision,gold,revive_coins,win_points FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account") +
            fixture.Snapshot("SELECT * FROM account_currency_balances ORDER BY account,currency_kind") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM currency_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM sqlite_sequence ORDER BY name");
        static void Use(TcpPeer peer, uint token) => peer.Send(44, Build(writer =>
        { writer.Write((ushort)slot); writer.Write((byte)0); writer.Write(token); writer.Write(itemId); writer.Write(0); }));
        static void Accepted(TcpPeer peer, uint token) => peer.Expect(1, 44, Build(writer =>
        { writer.Write((byte)1); writer.Write((ushort)slot); writer.Write((byte)0); writer.Write(token); writer.Write(itemId); }));
        static void Refused(TcpPeer peer, uint token) => peer.Expect(1, 44, Build(writer =>
        { writer.Write((byte)0); writer.Write((ushort)123); writer.Write((byte)0); writer.Write(token); writer.Write(itemId); }));
        static void AssertPremium(byte[] body, long expiry)
        {
            That(body.Length == 1 + body[0] * 9, "1760按已确认数量/类型/8B期限布局独立检查");
            var entries = new Dictionary<byte,long>();
            for (int index = 0; index < body[0]; index++)
                That(entries.TryAdd(body[1 + index * 9], BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(2 + index * 9))),
                    "统一1760目录没有重复服务类型");
            That(entries.ContainsKey(22) && entries.All(pair => pair.Key == 22 || (pair.Key is 56 or 58 && pair.Value == 0)),
                "已提交霸王资格保持完整；附加56/58只清除旧状态，不伪造契约");
            long duration = entries[22];
            long remaining = expiry - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            That(duration >= remaining && duration <= remaining + 5, "1760投影同一账号已提交到期时间的剩余期限");
        }
    }

    static void AssertExpertNotice(byte[] body, long expiry)
    {
        That(body.Length == 11 && U16(body, 0) == 2 && body[2] == 27, "达人沿NOTI66已确认type2/effect27布局");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long remaining = BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(3));
        That(remaining >= Math.Max(0, expiry - now) && remaining <= Math.Max(0, expiry - now) + 5,
            "NOTI66使用同一已提交账号有效期限的剩余秒数");
    }
}
