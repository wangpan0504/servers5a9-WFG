using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Raids;
using DfoServer.Network.Builders;
using PvfLib;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class LukeProductionTests
{
    public static void RunResources(PvfArchive archive, RaidDefinition definition)
    {
        int before = Count;
        var resources = new ItemResources(archive);
        var policy = LukeProductionPolicy.Create(definition.Resources!, resources.Items, resources.Boosters);
        var raw = definition.Resources!.Script;
        That(policy.ExecutionScript == raw && raw.Phases.Sum(phase => phase.Rules.Length) == 40,
            "生产策略保留完整40条原规则");
        That(policy.Completion is not null && AntonRaidBattle.ValidLukeCompletion(policy.Completion), "生产资格策略完整可执行");
        var executable = policy.Pacing!.Adapt(raw);
        foreach (var phase in executable.Phases)
            foreach (var rule in phase.Rules)
            {
                var rejection = LukeRaidStart.RuleRejection(rule, executable, raw, phase.Dungeons.Select(row => row.Id).ToHashSet(),
                    definition.Resources.Dungeons.SelectMany(row => row.MapIds).ToHashSet(), policy.Timers is not null,
                    policy.Facilities?.UseResourceBudgets == true, policy.Run?.UseEnergyObjects == true,
                    policy.Pacing is not null && policy.PhaseTimeout is not null);
                That(rejection is null, $"生产派生规则具备完整消费者：{rejection};{string.Join(',', rule.Behavior.Select(row => row.Symbol ?? row.Tag))}");
            }
        foreach (bool pcroom in new[] { false, true })
            foreach (bool privilege in new[] { false, true })
                foreach (bool high in new[] { false, true })
                {
                    var phases = policy.ResourceRewards!.Create(pcroom, privilege, maximum => high ? maximum - 1 : 0);
                    That(phases[0].Offers.Length == 1 && phases[0].Offers.Single().Kind == 1 && phases[1].Offers.Length == 2,
                        "P1仅个人奖励，P2个人及团队奖励类别独立");
                    var all = phases.SelectMany(phase => phase.Offers).SelectMany(offer => offer.Grants).ToArray();
                    That(phases.Select(phase => phase.Offers.Single(offer => offer.Kind == 1).Grants
                            .Single(row => row.ItemId == 10308807).Count).SequenceEqual(new[] { 25, 100 }),
                        "两阶段分别保留匹配资源的25和100不渝，不覆盖为固定总量");
                    That(all.All(row => !new[] { 10308804, 10304817, 10161938, 10161939, 10161940, 10161941, 10160380, 10160381, 10160382, 10160384 }.Contains(row.ItemId)),
                        "随机容器全部展开，不发箱和内容双份奖励");
                    That(phases[1].Offers.Single(offer => offer.Kind == 0).Grants.Single(row => row.ItemId == 10159623).Count == 13,
                        "低边界13魔岩及最高外层塔罗箱13魔岩均采用资源内容，不读过时8个文案");
                    foreach (var offer in phases.SelectMany(phase => phase.Offers))
                    {
                        That(offer.LukeResult!.Matches(offer), "每项实际产物与16字节结果字段对应");
                        byte expectedCode = offer.Kind == 0 && high ? (byte)1 : (byte)0;
                        That(offer.LukeResult.RawCode == expectedCode &&
                            offer.LukeAdditionalResults.All(item => item.RawCode == expectedCode),
                            "个人与低档团队奖励保持code0，高档团队奖励全部展开项携带code1");
                        var replay = JsonSerializer.Deserialize<RaidRewardOffer>(JsonSerializer.Serialize(offer))!;
                        That(AntonRaidBattle.SameLukeOffers([offer], [replay]), "多项奖励及展示持久JSON往返保持一致");
                        var packet = LukeRewardPackets.Result(new(offer.Kind, 7, offer.Card, offer.LukeResult)
                            { AdditionalItems = offer.LukeAdditionalResults });
                        That(packet.Length == 6 + 16 * offer.Grants.Length && packet[5] == offer.Grants.Length &&
                            Enumerable.Range(0, offer.Grants.Length).All(index => packet[6 + 16 * index] == expectedCode),
                            "原Luke结果按实际条数编码且每项16B首字节code一致，无Anton八字节错位");
                    }
                }
        var squadRows = raw.Phases[1].Rewards.Where(row => row.Kind == "squad_item")
            .Select(row => (row.ItemId, row.RawB, row.RawD)).ToArray();
        That(squadRows.SequenceEqual(new[]
        {
            (10161938, 4500, 0), (10161939, 2500, 0), (10161940, 2000, 0), (10161941, 500, 1),
            (10160384, 10, 1), (10160380, 125, 1), (10160381, 125, 1), (10160382, 240, 1)
        }), "卢克二阶段8个团队奖励档位权重与RawD金牌标记精确匹配PVF");
        int darkSourceId = resources.Boosters.Get(10161941).Pools
            .SelectMany(pool => pool.Rewards).Select(row => row.ItemId).Distinct().Single(id => id != 10159623);
        int squadTotal = squadRows.Sum(row => row.RawB);
        int cursor = 0;
        foreach (var (containerId, weight, rawD) in squadRows)
        {
            int squadPick = cursor;
            cursor += weight;
            byte expectedCode = rawD == 0 ? (byte)0 : (byte)1;
            foreach (bool poolHigh in new[] { false, true })
            {
                int rollIndex = 0;
                var phases = policy.ResourceRewards!.Create(false, false,
                    maximum => rollIndex++ == 2 ? squadPick : poolHigh ? maximum - 1 : 0);
                var p1Personal = phases[0].Offers.Single(offer => offer.Kind == 1);
                var p2Personal = phases[1].Offers.Single(offer => offer.Kind == 1);
                var p2Squad = phases[1].Offers.Single(offer => offer.Kind == 0);
                That(p1Personal.LukeResult!.RawCode == 0 && p1Personal.LukeAdditionalResults.All(item => item.RawCode == 0) &&
                    p2Personal.LukeResult!.RawCode == 0 && p2Personal.LukeAdditionalResults.All(item => item.RawCode == 0) &&
                    p2Personal.Grants.Single(row => row.ItemId == 10308807).Count == 100,
                    "二阶段同槽个人100不渝之咏始终保持code0");
                var squadItems = p2Squad.LukeAdditionalResults.Insert(0, p2Squad.LukeResult!);
                var sortedByItemId = squadItems.OrderBy(item => item.ItemId!.Value).ToArray();
                That(squadItems.Length >= 2 && sortedByItemId[0].ItemId > 0 &&
                     sortedByItemId.All(item => item.ItemId > 0 && item.RawCode == expectedCode),
                    $"二阶段团队档位{containerId}展开全部正数道具条目code={expectedCode}，按itemId排序首项一致");
                if (containerId == 10161941)
                    That(p2Squad.Grants.Single(row => row.ItemId == 10159623).Count == 30 &&
                        p2Squad.Grants.Single(row => row.ItemId == darkSourceId).Count == (poolHigh ? 4 : 2) &&
                        expectedCode == 1,
                        "10161941（30魔岩石+2~4暗之根源）展开条目均为金牌code1");
                var teamPacket = LukeRewardPackets.TeamResults([new(0, 7, 2, p2Squad.LukeResult!)
                    { AdditionalItems = p2Squad.LukeAdditionalResults }]);
                That(teamPacket.Length == 6 + 16 * squadItems.Length && teamPacket[5] == squadItems.Length &&
                    Enumerable.Range(0, squadItems.Length).All(index => teamPacket[6 + 16 * index] == expectedCode),
                    "wire3团队页逐项编码与选中档位code一致");
            }
        }
        var invalidSquadRawD = raw with { Phases = raw.Phases.SetItem(1, raw.Phases[1] with
            { Rewards = raw.Phases[1].Rewards.Select(row => row.ItemId == 10161941 ? row with { RawD = 2 } : row).ToImmutableArray() }) };
        Reject<InvalidDataException>(() => new LukeResourceRewards(invalidSquadRawD, resources.Items, resources.Boosters).Create(false),
            "卢克团队奖励RawD越界拒绝");
        var directScript = raw with { Phases = raw.Phases.Select(phase => phase with
            { Rewards = phase.Rewards.Select(row => row.Kind == "party_card" ? row with { ItemId = 10308807 } : row).ToImmutableArray() }).ToImmutableArray() };
        That(new LukeResourceRewards(directScript, resources.Items, resources.Boosters).Create(false, draw: _ => 0)
                .All(phase => phase.Offers.Single(offer => offer.Kind == 1).Grants.Single(row => row.ItemId == 10308807).Count == 1),
            "合法直接材料声明按数量1保留，不用当前25/100替代资源值");
        Console.WriteLine($"PASS {Count - before} Luke production reward resource assertions");
    }
}
