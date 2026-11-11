using System.Collections.Immutable;
using DfoServer.Game.Events;
using DfoServer.Game.Characters;

namespace DfoServer.Game.Session;

internal sealed record AdventurerBenefitBuffReceipt(ImmutableArray<uint> Removed, ImmutableArray<uint> Added, Func<bool> IsCurrent);
internal sealed record ReturningAdventurerReceipt(long Until, Func<bool> IsCurrent);

internal sealed partial class GameSession
{
    public ushort ReturningAdventurerType => debugSettings?.AdventurerBenefits?.ReturningUntil(owner.Account,
        (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds()) > 0 ? (ushort)1 : (ushort)0;

    ImmutableArray<uint> projectedAdventurerBuffs = [];
    long adventurerBuffRevision;
    long adventurerBuffEpoch;
    Guid adventurerBuffInstance;
    AdventurerBenefitConfiguration? projectedAdventurerConfiguration;
    long returningIdentityEpoch = -1;
    long returningIdentityUntil;
    long returningIdentityRevision;

    public ReturningAdventurerReceipt? PlanReturningAdventurerIdentity(bool force = false) => owner.Execute<ReturningAdventurerReceipt?>(() =>
    {
        if (!Ready || Selected is not { } character || debugSettings?.AdventurerBenefits is not { } service) return null;
        long until = service.ReturningUntil(owner.Account, (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds());
        if (!force && returningIdentityEpoch == SelectionEpoch && returningIdentityUntil == until) return null;
        if (until == 0 && returningIdentityUntil == 0) { returningIdentityEpoch = SelectionEpoch; return null; }
        returningIdentityEpoch = SelectionEpoch; returningIdentityUntil = until;
        long revision = ++returningIdentityRevision, epoch = SelectionEpoch;
        bool Current() => owner.Execute(() => owner.IsCurrent && Ready && Selected?.Id == character.Id && SelectionEpoch == epoch &&
            returningIdentityRevision == revision && service.ReturningUntil(owner.Account,
                (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds()) == until);
        return new(until, Current);
    });

    AdventurerBenefitSelection ReadAdventurerBenefits(Character character, long now)
    {
        if (debugSettings?.AdventurerBenefits is not { } service || formalDungeon is null ||
            Phase is not (SessionPhase.Dungeon or SessionPhase.Settlement)) return new([], 0);
        var self = new AdventurerBenefitMember(owner.Account, character.Job, character.GrowType);
        // 实例成员数组为不可变替换；不在本账号锁内访问其他账号的Session或申请其他owner。
        var members = PartyDungeon?.Members.Where(member => member.Actor.Owner.IsCurrent)
            .Select(member => new AdventurerBenefitMember(member.Actor.Owner.Account, member.Actor.Character.Job, member.Actor.Character.GrowType))
            .ToArray() ?? [self];
        if (!members.Contains(self)) return new([], 0);
        return service.Select(self, members, now);
    }

    public AdventurerBenefitBuffReceipt? PlanAdventurerBenefitBuffs(bool clear = false, bool force = false) =>
        owner.Execute<AdventurerBenefitBuffReceipt?>(() =>
        {
            if (debugSettings?.AdventurerBenefits is not { } service) return null;
            var configuration = service.Configuration;
            var desired = !clear && Ready && Selected is { } character
                ? ReadAdventurerBenefits(character, (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds()).Buffs : [];
            Guid instance = formalDungeon?.Run.Snapshot.Identity.InstanceId ?? Guid.Empty;
            if (!force && adventurerBuffEpoch == SelectionEpoch && adventurerBuffInstance == instance &&
                ReferenceEquals(projectedAdventurerConfiguration, configuration) && projectedAdventurerBuffs.SequenceEqual(desired)) return null;
            var removed = projectedAdventurerBuffs;
            projectedAdventurerConfiguration = configuration;
            projectedAdventurerBuffs = desired;
            adventurerBuffEpoch = SelectionEpoch;
            adventurerBuffInstance = instance;
            if (removed.IsEmpty && desired.IsEmpty) return null;
            long revision = ++adventurerBuffRevision, epoch = SelectionEpoch;
            ushort? actor = Selected?.Id;
            bool Current() => owner.Execute(() => owner.IsCurrent && Ready && SelectionEpoch == epoch &&
                Selected?.Id == actor && adventurerBuffRevision == revision &&
                ReferenceEquals(service.Configuration, configuration) &&
                (formalDungeon?.Run.Snapshot.Identity.InstanceId ?? Guid.Empty) == instance &&
                (clear || Selected is { } current && ReadAdventurerBenefits(current,
                    (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds()).Buffs.SequenceEqual(desired)));
            return new(removed, desired, Current);
        });

    ExperienceBonuses ApplyAdventurerExperience(Character character, ExperienceEvent kind, uint basis, long now, ExperienceBonuses reward)
    {
        if (kind != ExperienceEvent.Kill) return reward;
        int percent = ReadAdventurerBenefits(character, now).KillExperiencePercent;
        uint amount = checked((uint)((ulong)basis * (uint)percent / 100));
        if (amount == 0) return reward;
        var parts = reward.Parts;
        int index = -1;
        for (int at = 0; at < parts.Length; at++) if (parts[at].Source == GameWorld.Characters.ExperienceBonusSource.Buff) index = at;
        var part = new GameWorld.Characters.ExperienceBonusPart(GameWorld.Characters.ExperienceBonusSource.Buff,
            checked(amount + (index < 0 ? 0 : parts[index].Amount)));
        var result = reward with { Parts = (index < 0 ? parts.Add(part) : parts.SetItem(index, part)).OrderBy(row => row.Source).ToImmutableArray() };
        _ = result.Total;
        return result;
    }
}
