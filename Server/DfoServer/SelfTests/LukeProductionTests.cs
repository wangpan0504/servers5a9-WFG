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
                    That(all.Where(row => row.ItemId == 10308807).Sum(row => row.Count) == 100,
                        "一场完整团队无论人数固定总100不渝，不把P2箱100叠成125");
                    That(all.All(row => !new[] { 10308804, 10304817, 10161938, 10161939, 10161940, 10161941, 10160380, 10160381, 10160382, 10160384 }.Contains(row.ItemId)),
                        "随机容器全部展开，不发箱和内容双份奖励");
                    That(phases[1].Offers.Single(offer => offer.Kind == 0).Grants.Single(row => row.ItemId == 10159623).Count == 13,
                        "低边界13魔岩及最高外层塔罗箱13魔岩均采用资源内容，不读过时8个文案");
                    foreach (var offer in phases.SelectMany(phase => phase.Offers))
                    {
                        That(offer.LukeResult!.Matches(offer), "每项实际产物与16字节结果字段对应");
                        var replay = JsonSerializer.Deserialize<RaidRewardOffer>(JsonSerializer.Serialize(offer))!;
                        That(AntonRaidBattle.SameLukeOffers([offer], [replay]), "多项奖励及展示持久JSON往返保持一致");
                        var packet = LukeRewardPackets.Result(new(offer.Kind, 7, offer.Card, offer.LukeResult)
                            { AdditionalItems = offer.LukeAdditionalResults });
                        That(packet.Length == 6 + 16 * offer.Grants.Length && packet[5] == offer.Grants.Length,
                            "原Luke结果按实际条数编码，无Anton八字节错位");
                    }
                }
        Console.WriteLine($"PASS {Count - before} Luke production reward resource assertions");
    }
}
