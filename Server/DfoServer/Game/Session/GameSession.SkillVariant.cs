using DfoServer.Game.Characters;
using DfoServer.Game.Skills;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public Action<PublicCharacterSnapshot, Func<bool>>? SkillVariantChanged { get; set; }

    public void PublishSkillVariant(SkillLearningReceipt receipt, byte previous, bool includeSelf = true)
    {
        if (!CanPublishLearning(receipt) || CharacterSkillVariant.Read(ReadSkillCharacter(receipt.Character)) == previous ||
            PublicCharacter(receipt.Character.Id) is not { } identity) return;
        if (includeSelf) SkillVariantChanged?.Invoke(identity, identity.IsCurrent);
        // 回调在提交锁外执行；排队后按双方当前成员身份复核，旧团/旧连接不收到迟到刷新。
        if (CurrentParty?.Invoke() is { } party)
        {
            long sourceMembership = PartyMembershipVersion;
            foreach (var member in party.Members.Where(member => !ReferenceEquals(member.Actor.Session, this)))
            {
                var recipient = member.Actor.Session;
                long recipientMembership = recipient.PartyMembershipVersion;
                recipient.SkillVariantChanged?.Invoke(identity, () => identity.IsCurrent() && member.IsCurrent() &&
                    PartyMembershipVersion == sourceMembership && recipient.PartyMembershipVersion == recipientMembership &&
                    CurrentParty?.Invoke()?.Id == party.Id && recipient.CurrentParty?.Invoke()?.Id == party.Id);
            }
        }
        if (raidRegistry?.FindCurrent(this, receipt.SelectionEpoch) is { } raid)
            foreach (var member in raid.Members.Where(member => !ReferenceEquals(member.Actor.Session, this)))
            {
                var recipient = member.Actor;
                recipient.Session.SkillVariantChanged?.Invoke(identity, () => identity.IsCurrent() &&
                    raidRegistry.FindCurrent(this, receipt.SelectionEpoch)?.Key == raid.Key && recipient.Session.HasRaidKey(recipient, raid.Key));
            }
    }
}
