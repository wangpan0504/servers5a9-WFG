using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.Game.TagEvildom;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class TagEvildomTcpTests
{
    static Task VerifyExpiredEquipmentAsync(TcpTestFixture fixture, int port, Character standby)
    {
        fixture.Sql("UPDATE character_inventory_items SET item_core=json_set(item_core,'$.ExpireTime',$p0) WHERE item_uid IN " +
            "(SELECT item_uid FROM character_inventory_slots WHERE character_id=$p1 AND list_type=3)",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1, standby.Id);
        using var peer = Open(fixture, gamePort: port, channelNumber: 46);
        peer.Select(tagEvildom: [1677, 1678, 1709]);
        string before = fixture.Snapshot("SELECT * FROM account_tag_evildom_configuration") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid");
        peer.Send(1662, Setting(0, 2)); peer.Expect(1, 1662, [0, 19, 0]); peer.Expect(0, 1709); peer.Barrier();
        That(before == fixture.Snapshot("SELECT * FROM account_tag_evildom_configuration") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid"),
            "过期备用穿戴在1662成功前明确拒绝，不半写配置或偷偷删资产");
        ReturnToSelection(peer);
        peer.Select(2, tagEvildom: [1677, 1678, 1709]);
        That(fixture.Query("SELECT count(*) FROM character_inventory_slots WHERE character_id=$p0 AND list_type=3", standby.Id) is [[0L]],
            "真正登录备用执行原有Expiry事务清理过期穿戴");
        ReturnToSelection(peer);
        peer.Select(tagEvildom: [1677, 1678, 1393, 1394]); peer.Barrier();
        return Task.CompletedTask;
    }

    static Task VerifyExpiredStandbyAsync(TcpTestFixture fixture, TcpTestResources resources, int port, Character primary, Character standby)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var definition = resources.Learning.Get(0, 5);
        int Cap(int extra)
        {
            int rank = Math.Min(definition.GetMaximum(0), definition.DeclaredMaximumLevels![0]);
            while (rank > 0 && definition.GetOrdinaryNextRequiredCharacterLevel(rank - 1) > 95 + extra) rank--;
            return rank;
        }
        int ordinary = Cap(0), contracted = Cap(resources.Learning.ExpertContractLevels);
        That(contracted > ordinary, "当前PVF95级确有达人额外资格，不能用低等级用例替代TAG备用到期");
        var active = standby.Learning with { ExpertContractExpiresAt = now + 3600 };
        int before = active.Trees[0].Skills.Single(skill => skill.Id == 5).Level;
        var purchase = SkillLearning.Plan(0, 0, 95, active, new(0, 0, [new(5, false, checked((byte)(contracted - before)))]), resources.Learning);
        That(purchase.Success, "95级在有效达人下真实规划付费技能而非捏造超上限等级");
        var stale = purchase.State! with { ExpertContractExpiresAt = now - 1 };
        var normalized = SkillPointLedger.Synchronize(0, 0, 95, stale, resources.Learning);
        That(!TagEvildomLearning.IsCurrent(standby with { Learning = stale }, resources.Learning, now) &&
            TagEvildomLearning.IsCurrent(standby with { Learning = normalized }, resources.Learning, now),
            "到期且需要回退拒绝；相同已到期期限规范化后仍允许，不把所有过期账号永久封锁");
        // 可信测试种子模拟离线备用已经付费、帐号契约随后到期；不通过TAG请求写入完成或奖励事实。
        var tree = stale.Trees[0];
        fixture.Sql("UPDATE character_skill_points SET sp=$p0,tp=$p1 WHERE character_id=$p2 AND context=0", tree.Sp, tree.Tp, standby.Id);
        fixture.Sql("UPDATE character_skill_bindings SET level=$p0 WHERE character_id=$p1 AND context=0 AND skill_id=5", contracted, standby.Id);
        fixture.Sql("INSERT INTO account_wallets(account,revision,premium_contracts) VALUES($p0,1,$p1) " +
            "ON CONFLICT(account) DO UPDATE SET revision=revision+1,premium_contracts=excluded.premium_contracts",
            fixture.Options.DevelopmentAccount, "{\"27\":" + (now - 1) + "}");
        using var peer = Open(fixture, gamePort: port, channelNumber: 46);
        peer.Select(expertContract: true, premiumContracts: true, tagEvildom: [1677, 1678, 1709]);
        string configuration = fixture.Snapshot("SELECT * FROM account_tag_evildom_configuration");
        string learning = fixture.Snapshot("SELECT * FROM character_skill_points WHERE character_id=$p0 ORDER BY context", standby.Id) +
            fixture.Snapshot("SELECT * FROM character_skill_bindings WHERE character_id=$p0 ORDER BY context,slot", standby.Id);
        peer.Send(1662, Setting(0, 2)); peer.Expect(1, 1662, [0, 19, 0]); peer.Expect(0, 1709); peer.Barrier();
        That(configuration == fixture.Snapshot("SELECT * FROM account_tag_evildom_configuration") && learning ==
            fixture.Snapshot("SELECT * FROM character_skill_points WHERE character_id=$p0 ORDER BY context", standby.Id) +
            fixture.Snapshot("SELECT * FROM character_skill_bindings WHERE character_id=$p0 ORDER BY context,slot", standby.Id),
            "1662成功前拒绝未规范化备用，保留配置与资产，不在Builder中伪退款或先ACK再缺1394");
        ReturnToSelection(peer);
        peer.Select(2, expertContract: true, premiumContracts: true, tagEvildom: [1677, 1678, 1709]);
        That(fixture.Query("SELECT level FROM character_skill_bindings WHERE character_id=$p0 AND context=0 AND skill_id=5", standby.Id) is [[long rank]] && rank == ordinary &&
            fixture.Query("SELECT sp FROM character_skill_points WHERE character_id=$p0 AND context=0", standby.Id) is [[long sp]] && sp == normalized.Trees[0].Sp,
            "真正登录备用沿既有权威事务回退等级和返还真实SP，非TAG隐藏修改");
        ReturnToSelection(peer);
        peer.Select(expertContract: true, premiumContracts: true, tagEvildom: [1677, 1678, 1393, 1394]);
        That(peer.Id == primary.Id && peer.TagEvildomInfo[^1].Opcode == 1394, "到期备用规范化后队首重登恢复1393→1394，不永久缺血蓝资料");
        peer.Barrier();
        return Task.CompletedTask;
    }
}
