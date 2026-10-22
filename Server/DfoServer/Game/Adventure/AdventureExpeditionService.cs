using System.Collections.Immutable;
using DfoServer.GameWorld.Adventure;

namespace DfoServer.Game.Adventure;

internal sealed class AdventureExpeditionService(AdventureCatalog catalog, AdventureExpeditionCatalog expeditions)
{
    public AdventureCatalog Catalog => catalog;
    public AdventureExpeditionCatalog Expeditions => expeditions;
    public ImmutableArray<byte> AreaAttributes(byte index, DateTimeOffset now)
    {
        var area = expeditions.Areas[index];
        // 公告：地下城属性每天06:00随机变更；06:00前仍沿用前一周期。
        long day = (AdventurePeriod.ChinaDay(now).Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerDay / expeditions.RotationDays;
        var local = new HashSet<string>();
        var global = new HashSet<string>();
        var result = ImmutableArray.CreateBuilder<byte>();
        foreach (var current in expeditions.Areas.Values.OrderBy(row => row.Index))
        {
            local.Clear();
            foreach (string group in current.Groups)
            {
                string selected;
                if (expeditions.Attributes.TryGetValue(group, out var attribute) && attribute.Id != byte.MaxValue)
                    selected = group;
                else
                {
                    // 组别名（如[GROUP C ALLOW]）先落到目标组，再按日选具体属性；255不能直接下发。
                    string groupName = attribute.Group is { Length: > 0 } alias ? alias : group;
                    var candidates = expeditions.AttributeGroups[groupName];
                    string? unique = current.Uniqueness.GetValueOrDefault(group);
                    var used = unique == "global" ? global : unique == "local" ? local : null;
                    selected = Enumerable.Range(0, candidates.Length).Select(offset => candidates[(int)((day + current.Index + offset) % candidates.Length)])
                        .FirstOrDefault(value => used?.Contains(value) != true) ?? throw new InvalidDataException("远征指定属性无可用候选");
                    used?.Add(selected);
                }
                local.Add(selected);
                if (current.Index == area.Index) result.Add(expeditions.Attributes[selected].Id);
            }
            if (current.Index == area.Index) break;
        }
        return result.ToImmutable();
    }

    public (AdventureExpeditionPlan? Plan, ushort Error) Start(AdventureSnapshot snapshot, byte area, uint duration,
        ImmutableArray<(byte Selector, byte Job, byte Grow)> request, DateTimeOffset now)
    {
        if (!expeditions.Areas.TryGetValue(area, out var definition) || snapshot.State.Level < definition.RequiredLevel) return (null, 7);
        if (snapshot.State.Expeditions.Any(run => run.Area == area)) return (null, 14);
        if (!definition.Rewards.TryGetValue(duration, out float rate)) return (null, 18);
        var representatives = snapshot.Representatives(catalog);
        var members = ImmutableArray.CreateBuilder<AdventureMember>();
        foreach (var value in request)
        {
            // Selector属于远征名册，不是characters.slot；最终绑定UID。
            var member = representatives.FirstOrDefault(row => row.Job == value.Job && row.ProfessionGrow == (value.Grow & 15));
            if (member is null || value.Selector >= representatives.Length || representatives[value.Selector].Id != member.Id ||
                members.Any(row => row.Id == member.Id) || snapshot.State.Expeditions.Any(run => run.Members.Contains(member.Id))) return (null, 22);
            members.Add(member);
        }
        if (members.Count is < 1 or > 4) return (null, 22);
        var attributes = AreaAttributes(area, now);
        var supplied = members.SelectMany(row => expeditions.CharacterAttributes.GetValueOrDefault((row.Job, row.ProfessionGrow), []))
            .Select(name => expeditions.Attributes[name].Id).ToHashSet();
        // 同一属性在多名成员上重复出现不重复计数；需求属性已展开为具体ID。
        int matches = attributes.Count(supplied.Contains);
        float product = rate * expeditions.PartyRates[members.Count] * expeditions.AttributeRates[Math.Min(matches, expeditions.AttributeRates.Length - 1)];
        // ARAD 0421F0F0先按float32四舍五入，再乘extra rate并截断；该倍率不是额外再加一次的奖励。
        uint reward = checked((uint)((int)(product + 0.5f) * expeditions.ExtraRate));
        var run = new AdventureExpedition(area, Guid.NewGuid(), now.ToUnixTimeSeconds(), checked(now.ToUnixTimeSeconds() + duration),
            reward, 0, attributes, members.Select(row => row.Id).ToImmutableArray())
        {
            FrozenMembers = members.Select(row => new ExpeditionMember(row.Id, checked((byte)representatives.IndexOf(row)),
                row.Name, row.Job, row.ProfessionGrow, row.Level)).ToImmutableArray()
        };
        return (new(snapshot, snapshot.State with { Expeditions = snapshot.State.Expeditions.Add(run).OrderBy(row => row.Area).ToImmutableArray() }, "expedition_start"), 0);
    }

    public (AdventureExpeditionPlan? Plan, ushort Error, uint Reward) Finish(AdventureSnapshot snapshot, byte area, bool claim, DateTimeOffset now)
    {
        var run = snapshot.State.Expeditions.SingleOrDefault(row => row.Area == area);
        if (run is null || claim && run.Ends > now.ToUnixTimeSeconds()) return (null, 3, 0);
        if (run.Members.Any(id => !snapshot.Members.Any(row => row.Id == id))) return (null, 3, 0);
        var after = snapshot.State with { Expeditions = snapshot.State.Expeditions.Remove(run) };
        uint reward = claim ? checked(run.Reward + run.ExtraReward) : 0;
        if (claim)
        {
            var shop = catalog.Shops[1];
            after = after with { Points = after.Points.SetItem(1, checked((uint)Math.Min(shop.Maximum, (ulong)after.Points[1] + reward))) };
        }
        return (new(snapshot, after, claim ? "expedition_claim" : "expedition_cancel"), 0, reward);
    }
}
