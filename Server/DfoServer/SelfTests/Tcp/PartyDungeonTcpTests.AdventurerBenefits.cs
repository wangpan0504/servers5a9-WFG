using DfoServer.Network.Management;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class PartyDungeonTcpTests
{
    public static async Task RunAdventurerBenefitsAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        AdventurerBenefitTests.RunCore();
        AdventurerBenefitTests.RunResources(resources.Archive);
        ReturningAdventurerTests.RunResources(resources.Archive);
        var clock = new FrameClock();
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "adventurer-benefits"), gm: true, resources: resources, clock: clock);
        await fixture.StartAsync();
        using var group = new Coop(fixture, resources, "advent", new(38, 2, 1085, 249), [3145], 50, 2, memberJobs: [0, 1]);
        var hero = group.Peers[0]; var fighter = group.Peers[1];
        string name = Convert.ToString(fixture.Query("SELECT name FROM characters WHERE id=$p0", hero.Id)[0][0])!;
        string account = Convert.ToString(fixture.Query("SELECT account FROM characters WHERE id=$p0", hero.Id)[0][0])!;
        var expectedProfessions = new DfoServer.GameWorld.Events.LuckyCharacterCatalog(resources.Archive).Professions
            .OrderBy(row => row.Key).SelectMany(row => row.Value).ToArray();
        void SetLucky(string targetAccount, string targetName, bool changed = true)
        {
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "account " + targetAccount + " lucky-profession " + targetName,
                out string result) && result.StartsWith("OK "), "真实GM幸运设置成功且先持久化");
            using var receipt = System.Text.Json.JsonDocument.Parse(result[3..]);
            That(receipt.RootElement.GetProperty("scope").GetString() == "all-professions", "GM凭据明确全职业范围，不能用旧单职业成功冒充");
            var first = group.All(); var tail = group.All();
            for (int member = 0; member < first.Length; member++)
            {
                var packets = first[member].Concat(tail[member]).Where(row => row is { Kind: 0, Opcode: 1057 }).ToArray();
                if (!changed) { That(packets.Length == 0, "重复GM开启不替换名单或刷新旧职业为空"); continue; }
                var packet = packets.Single();
                int count = checked((int)U32(packet.Body));
                That(count == expectedProfessions.Length && packet.Body.Length == 4 + count * 8 && Enumerable.Range(0, count).All(at =>
                    U32(packet.Body, 4 + at * 8) == expectedProfessions[at].Job && U32(packet.Body, 8 + at * 8) == expectedProfessions[at].GrowType),
                    "一次GM设置向所有在线会话发送全职业完整合法转职觉醒名单");
            }
        }
        SetLucky(account, name);
        hero.Send(562, [1]); hero.Expect(1, 562, [1]); hero.Expect(0, 490, [0, 0, 1]); group.All(hero);
        That(fixture.Query("SELECT enabled FROM character_fatigue_acceleration WHERE character_id=$p0", hero.Id) is [[1L]],
            "非网吧幸运角色沿真实562保存免费燃烧，不只是显示四叶草");
        string fighterName = Convert.ToString(fixture.Query("SELECT name FROM characters WHERE id=$p0", fighter.Id)[0][0])!;
        string fighterAccount = Convert.ToString(fixture.Query("SELECT account FROM characters WHERE id=$p0", fighter.Id)[0][0])!;
        string allConfiguration = File.ReadAllText(Path.Combine(fixture.Options.DataDirectory, "adventurer-benefits.json"));
        SetLucky(fighterAccount, fighterName, changed: false);
        That(File.ReadAllText(Path.Combine(fixture.Options.DataDirectory, "adventurer-benefits.json")) == allConfiguration,
            "换职业点击同按钮不清其他职业资格、不重写持久化配置");
        hero.Send(562, [0]); hero.Expect(1, 562, [1]); hero.Expect(0, 490, [0, 0, 0]); group.All(hero);
        fighter.Send(562, [1]); fighter.Expect(1, 562, [1]); fighter.Expect(0, 490, [0, 0, 1]); group.All(fighter);
        fighter.Send(562, [0]); fighter.Expect(1, 562, [1]); fighter.Expect(0, 490, [0, 0, 0]); group.All(fighter);
        // 两种职业均可选择幸运燃烧；后续同时核对四叶草和独立女格斗组队活动。
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "account " + account + " returning-adventurer " + name, out string response) && response.StartsWith("OK "),
            "真实GM命令持久化所选账号回归资格");
        That(File.Exists(Path.Combine(fixture.Options.DataDirectory, "adventurer-benefits.json")), "回归GM成功前已落盘");
        group.All();
        var returnRows = group.All()[0];
        var identity = returnRows.Single(packet => packet is { Kind: 0, Opcode: 1761 });
        That(identity.Body.Length == 16 && U32(identity.Body) == 1 && U32(identity.Body, 12) > clock.Now.ToUnixTimeSeconds(),
            "真实账号回归身份按原生16B通知同步到客户端");
        That(U16(identity.Body, 6) == 1 && U16(identity.Body, 8) == 1 &&
            returnRows.Single(packet => packet is { Kind: 0, Opcode: 1762 }).Body.Length == 1200,
            "回归身份同时附带可领奖状态和完整100槽任务列表");
        byte[] welcome = Convert.FromHexString("00C64291605A895F00000000AC0100000000000000");
        byte[] daily = Convert.FromHexString("00C64291605A895F00000000AC0300000000000000");
        byte[] mission = Convert.FromHexString("186B848F00000000C03E75914C0400000002000000");
        string beforeInvalid = RewardSnapshot();
        foreach (byte[] malformed in new byte[][] { welcome[..8], welcome[..20], [.. welcome, 0] })
        {
            hero.Send(1734, malformed);
            That(group.All(hero)[0].All(packet => packet is not { Kind: 1, Opcode: 1734 }) && RewardSnapshot() == beforeInvalid,
                "畸形长度不应答成功、不改背包和账本，后续真实请求仍可继续");
        }
        // 沿用现场栈前缀，业务资格只由真正的action/argument与账号状态决定。
        foreach ((uint action, uint argument) in new (uint, uint)[] { (1, 1), (3, 1), (4, 0), (4, 101), (4, 2), (5, 4) })
        {
            hero.Send(1734, Request(action, argument)); group.All(hero);
            That(RewardSnapshot() == beforeInvalid, "非法参数、未完成任务及观察动作不能发奖或推进任务");
        }
        hero.Send(1734, welcome);
        var claimed = group.All(hero)[0];
        That(claimed.Single(packet => packet is { Kind: 1, Opcode: 1734 }).Body.Length == 12 &&
            U16(claimed.Last(packet => packet is { Kind: 0, Opcode: 1761 }).Body, 6) == 2 &&
            fixture.Query("SELECT item_id,item_count FROM account_returning_reward_claims WHERE account=$p0", account) is [[long welcomeItem, 1L]] &&
            welcomeItem == 490013906,
            "现场21B欢迎请求实际发回归礼包，12B应答后状态变已领取");
        long claimedRevision = Convert.ToInt64(fixture.Query("SELECT revision FROM characters WHERE id=$p0", hero.Id)[0][0]);
        hero.Send(1734, welcome); group.All(hero);
        That(Convert.ToInt64(fixture.Query("SELECT count(*) FROM account_returning_reward_claims WHERE account=$p0", account)[0][0]) == 1 &&
            Convert.ToInt64(fixture.Query("SELECT revision FROM characters WHERE id=$p0", hero.Id)[0][0]) == claimedRevision,
            "TCP重复欢迎请求不重复发物品、不写revision");
        hero.Send(1734, daily);
        var dailyClaimed = group.All(hero)[0];
        That(dailyClaimed.Single(packet => packet is { Kind: 1, Opcode: 1734 }).Body.Length == 12 &&
            U16(dailyClaimed.Last(packet => packet is { Kind: 0, Opcode: 1761 }).Body, 8) == 2 &&
            fixture.Query("SELECT item_id,item_count FROM account_returning_reward_claims WHERE account=$p0 AND action=3", account) is [[490013907L, 1L]],
            "现场21B每日请求真实入账并同步已领取状态");
        string beforeReplay = RewardSnapshot();
        hero.Send(1734, daily); group.All(hero);
        That(RewardSnapshot() == beforeReplay, "每日重放不重复发奖或改revision");
        hero.Send(29, [0, 1, 5, 0, 0, 1, 0]);
        var learned = group.All(hero)[0];
        That(learned.Any(packet => packet is { Kind: 1, Opcode: 29 } && packet.Body[0] == 1) &&
            MissionState(learned.Last(packet => packet is { Kind: 0, Opcode: 1762 }).Body, 2) == 1,
            "通过真实技能保存完成回归任务，不注入伪造进度");
        hero.Send(1734, mission);
        var missionClaimed = group.All(hero)[0];
        That(missionClaimed.Single(packet => packet is { Kind: 1, Opcode: 1734 }).Body.Length == 12 &&
            MissionState(missionClaimed.Last(packet => packet is { Kind: 0, Opcode: 1762 }).Body, 2) == 2 &&
            fixture.Query("SELECT item_id,item_count FROM account_returning_reward_claims WHERE account=$p0 AND action=4 AND argument=2", account)
                is [[490013909L, 1L]], "21B任务请求真实入账并把任务状态从可领取变成已领取");
        beforeReplay = RewardSnapshot();
        hero.Send(1734, Request(4, 2)); group.All(hero);
        That(RewardSnapshot() == beforeReplay, "同任务更换栈前缀重放仍不重复发奖");
        foreach (int itemId in new[] { 490013906, 490013907, 490013909 })
            That(fixture.Query("SELECT sum(json_extract(item_core,'$.Value')) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=$p1",
                hero.Id, itemId) is [[1L]], "三类回归奖励各一份真实入包，与账本一致且重放不增加");
        hero.Send(1734, Request(5, 4)); group.All(hero);
        That(fixture.Query("SELECT count(*) FROM account_returning_mission_progress WHERE account=$p0 AND mission_id=4", account) is [[0L]],
            "客户端不能通过观察事件伪造遗迹任务完成");
        group.Join(); group.Enter(3, 0);
        var entered = group.LastPackets;
        bool ReturningActor(WirePacket packet, ushort id, ushort type) => packet is { Kind: 0, Opcode: 2 } &&
            packet.Body.Length > 158 && packet.Body[0] == 0 && U16(packet.Body, 121) == id &&
            U16(packet.Body, packet.Body.Length - 37) == type;
        That(entered.All(rows => rows.Any(packet => ReturningActor(packet, hero.Id, 1))),
            "组队进图各客户端Actor携带新回归头像身份");
        group.Load();
        var loaded = group.LastPackets;
        bool Buff(WirePacket packet, uint id) => packet is { Kind: 0, Opcode: 487 } &&
            Enumerable.Range(0, packet.Body[0]).Any(index => U32(packet.Body, 1 + index * 16) == id);
        That(loaded[0].Any(packet => Buff(packet, 1087)) && loaded.All(rows => rows.Any(packet => Buff(packet, 1273))),
            "正式加载屏障后职业队友和回归队友收到资源Buff");
        That(!loaded[1].Any(packet => Buff(packet, 1087)), "职业自身空表不授予队友专属Buff");
        ushort key = Enumerable.Range(0, group.Map[18]).Select(index => 19 + index * 22)
            .Where(at => group.Map[at + 21] == 0 && group.Map[at + 13] != 9).Select(at => U16(group.Map, at + 6)).First();
        int monsterAt = Enumerable.Range(0, group.Map[18]).Select(index => 19 + index * 22).Single(at => U16(group.Map, at + 6) == key);
        hero.Send(39, FormalDungeonTcpTests.Death(key, hero.Id, group.Map[monsterAt + 12]));
        var killed = group.All();
        var exp = killed[0].FirstOrDefault(packet => packet is { Kind: 0, Opcode: 37 });
        That(exp is not null, "活动角色沿真实击杀链取得经验通知");
        int buffOffset = 51 + exp!.Body[50] * 8;
        uint displayed = U32(exp.Body, buffOffset);
        long heroExperience = Convert.ToInt64(fixture.Query("SELECT experience FROM characters WHERE id=$p0", hero.Id)[0][0]);
        long fighterExperience = Convert.ToInt64(fixture.Query("SELECT experience FROM characters WHERE id=$p0", fighter.Id)[0][0]);
        That(displayed > 0 && displayed == U32(exp.Body, buffOffset + 4), "原生Buff显示与基础扣除分项一致");
        long levelStart = (long)SelfTestCatalogs.Growth(resources.Archive, resources.Learning).GetLevelStartExperience(50);
        long killBasis = heroExperience - levelStart - displayed;
        That(killBasis > 0 && displayed == killBasis * 20 / 100 + killBasis * 10 / 100 &&
            fighterExperience - levelStart == killBasis + killBasis * 10 / 100 && heroExperience - fighterExperience == killBasis * 20 / 100,
            "全职业双方均真实获得四叶草10%，女格斗队友另给20%，分别取整且没有重复到账");
        // 不把图标当成经验到账：核对持久化角色经验与显示分项使用同一奖励链。
        That(Convert.ToInt64(fixture.Query("SELECT experience FROM characters WHERE id=$p0", hero.Id)[0][0]) >
            (long)SelfTestCatalogs.Growth(resources.Archive, resources.Learning).GetLevelStartExperience(50), "职业活动击杀经验真实落库");
        clock.Now = clock.Now.AddDays(29);
        var expired = group.All();
        // 1347的ACK早于自动刷新；下一请求的ACK才能证明前一次刷新已完成。
        var expiredTail = group.All();
        for (int at = 0; at < expired.Length; at++) expired[at] = [.. expired[at], .. expiredTail[at]];
        That(expired.All(rows => rows.Any(packet => ReturningActor(packet, hero.Id, 0))),
            "到期对本人及队友刷新原生头像身份且不重建战斗Player");
        That(expired[0].Any(packet => packet is { Kind: 0, Opcode: 488 } &&
            Enumerable.Range(0, packet.Body[0]).Any(index => U32(packet.Body, 1 + index * 4) == 1273)) &&
            !expired[0].Any(packet => Buff(packet, 1273)), "到期刷新删除回归Buff且不重新安装");
        fighter.Send(42);
        var left = group.All(fighter);
        That(left[0].Any(packet => packet is { Kind: 0, Opcode: 488 }) && !left[0].Any(packet => Buff(packet, 1087)),
            "活动职业队友离队后幸存角色清除原生职业Buff");
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "lucky-professions off", out string off) &&
            off.Contains("\"enabled\":false"), "真实全局关闭命令返回关闭凭据");
        var disabled = group.All(); var disabledTail = group.All();
        for (int member = 0; member < disabled.Length; member++)
            Bytes(disabled[member].Concat(disabledTail[member]).Single(packet => packet is { Kind: 0, Opcode: 1057 }).Body,
                new byte[4], "关闭向全职业在线客户端同步空名单");
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "lucky-professions status", out string status) &&
            status.Contains("\"enabled\":false"), "关闭后的只读查询不重新开启");

        string RewardSnapshot() => fixture.Snapshot("SELECT * FROM account_returning_reward_claims ORDER BY account,action,argument,day") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT id,revision FROM characters ORDER BY id");
        static byte[] Request(uint action, uint argument) => Build(w =>
        {
            w.Write(Convert.FromHexString("0100000000000000FFFFFFFFFF")); w.Write(action); w.Write(argument);
        });
        static uint MissionState(byte[] body, uint id) => Enumerable.Range(0, 100).Where(index => U32(body, index * 12) == id)
            .Select(index => U32(body, index * 12 + 8)).Single();
    }
}
