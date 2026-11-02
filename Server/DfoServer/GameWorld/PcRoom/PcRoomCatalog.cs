using System.Collections.Immutable;
using DfoServer.GameWorld.Items;
using PvfLib;

namespace DfoServer.GameWorld.PcRoom;

internal sealed record PcRoomPremiumDefinition(int LoginItemId, int LoginCount, int RentalItemId, uint BuffId)
{
    public int AccelerationExperiencePercent { get; init; }
}
internal sealed record PcRoomWeaponChoice(int ItemId, byte Upgrade, byte Refinement);
internal sealed record PcRoomWeaponTier(byte Job, int MinimumLevel, ImmutableArray<PcRoomWeaponChoice> Choices);

internal sealed class PcRoomCatalog
{
    public const string PremiumPath = "etc/chn_qqpcroompremium.etc";
    public const string RentalPath = "etc/pcbangrentalservice.etc";
    public PcRoomPremiumDefinition Premium { get; }
    public int AccelerationPercent => Premium.AccelerationExperiencePercent;
    readonly ImmutableDictionary<byte, ImmutableArray<PcRoomWeaponTier>> tiers;

    public PcRoomCatalog(PvfArchive archive)
    {
        var premium = PcRoomPremiumFile.Parse(archive.ReadTokens(PremiumPath), archive.ResolveString);
        var rental = PcRoomRentalFile.Parse(archive.ReadTokens(RentalPath), archive.ResolveString);
        var login = premium.LoginRewards.Where(row => row.Grade == 1).ToArray();
        var buffs = premium.Buffs.Where(row => row.Grade == 1).ToArray();
        var rates = premium.AccelerationRates.Where(row => row.Grade == 1).ToArray();
        if (login is not [{ ItemId: > 0, Count: > 0 and <= ushort.MaxValue } reward] ||
            buffs is not [{ BuffId: > 0 } buff] || premium.RentalItemId <= 0 ||
            rates.Length > 1 || rates.Any(rate => rate.Percent is < 0 or > 10000))
            throw new InvalidDataException("网吧128奖励、Buff或燃烧倍率缺失");
        Premium = new(reward.ItemId, reward.Count, premium.RentalItemId, checked((uint)buff.BuffId))
            { AccelerationExperiencePercent = rates.Length == 0 ? 0 : rates[0].Percent };
        if (rental.Tiers.Any(row => row.Index < 0 || row.MinimumLevel is < 1 or > byte.MaxValue) ||
            rental.Tiers.DistinctBy(row => row.Index).Count() != rental.Tiers.Length ||
            rental.Tiers.DistinctBy(row => row.MinimumLevel).Count() != rental.Tiers.Length ||
            rental.Groups.DistinctBy(row => (row.JobTag, row.Tier)).Count() != rental.Groups.Length)
            throw new InvalidDataException("网吧租赁等级或职业档位重复");
        var registeredJobs = PvfScript.List(archive.ReadText("character/character.lst")).Keys.Where(job => job is >= 0 and <= 15).ToArray();
        var definitions = ImmutableArray.CreateBuilder<PcRoomWeaponTier>();
        foreach (int job in registeredJobs)
        foreach (var tier in rental.Tiers)
        {
            var group = rental.Groups.SingleOrDefault(row => row.Tier == tier.Index && row.JobTag == ItemCatalog.JobTag((byte)job));
            if (group is null) continue;
            if (group.Items.Length > byte.MaxValue || group.Items.Any(row => row.ItemId <= 0 || row.Count != 1 ||
                row.Upgrade is < 0 or > 31 || row.Refinement is < 0 or > 127) ||
                group.Items.DistinctBy(row => row.ItemId).Count() != group.Items.Length)
                throw new InvalidDataException("网吧武器候选编号、数量或改造参数无效");
            definitions.Add(new((byte)job, tier.MinimumLevel, group.Items.Select(row =>
                new PcRoomWeaponChoice(row.ItemId, (byte)row.Upgrade, (byte)row.Refinement)).ToImmutableArray()));
        }
        tiers = Index(definitions);
    }

    internal PcRoomCatalog(PcRoomPremiumDefinition premium, IEnumerable<PcRoomWeaponTier> weapons)
    {
        Premium = premium;
        tiers = Index(weapons);
    }

    static ImmutableDictionary<byte, ImmutableArray<PcRoomWeaponTier>> Index(IEnumerable<PcRoomWeaponTier> source) =>
        source.GroupBy(row => row.Job).ToImmutableDictionary(group => group.Key, group => group.OrderBy(row => row.MinimumLevel).ToImmutableArray());

    public ImmutableArray<PcRoomWeaponChoice> Weapons(byte job, byte level) => tiers.TryGetValue(job, out var choices)
        ? choices.LastOrDefault(row => row.MinimumLevel <= level)?.Choices ?? [] : [];

}
