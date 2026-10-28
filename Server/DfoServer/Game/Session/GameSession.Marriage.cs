using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Marriage;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Towns;
using DfoServer.GameWorld.Marriage;
using DfoServer.Game.Quests;

namespace DfoServer.Game.Session;

internal sealed record MarriageCapture(Character Character, long Generation, long Epoch, SessionPhase Phase);
internal sealed record MarriageSessionCommit(MarriageRecord Record, InventoryResult Actor, InventoryResult? Peer = null);

internal sealed partial class GameSession
{
    internal AccountOwners.Lease MarriageOwner => owner;
    internal IMarriageStore? MarriageStore => repository as IMarriageStore;

    public TownTravelReceipt? LeaveEndedMarriageHome() => owner.Execute(() =>
    {
        if (!Ready || Phase != SessionPhase.Town || Selected is not { Position.Town: 101 } character ||
            MarriageStore is not { } store || store.FindMarriage(character.Id) is { State: 4 }) return null;
        var target = character.Return ?? towns.InitialReturn;
        if (target.Town == 101 || target is { Town: 39, Area: >= 8 and < 38 } ||
            !towns.IsPosition(target, completed) || towns.DestinationAdmission(target, character.Level, completed) != "ready")
            target = towns.InitialReturn;
        return CommitTravel(new(target, target, "ready"));
    });

    public void PrepareMarriageInitialization(MarriageCatalog catalog, DateTimeOffset now) => owner.Execute(() =>
    {
        if (Ready || Phase != SessionPhase.Unselected || Selected is not { IsPvpPrivate: false } character ||
            MarriageStore is not { } store) return false;
        // 礼堂身份和婚房来源缓存均不跨连接；重登先恢复普通地区，再按正式入口进入。
        if (character.Position is { Town: 39, Area: >= 8 and < 38 } or { Town: 101 })
        {
            var back = character.Return ?? towns.InitialReturn;
            if (back is { Town: 39, Area: >= 8 and < 38 } || back.Town == 101 ||
                !towns.IsPosition(back, completed) || towns.DestinationAdmission(back, character.Level, completed) != "ready")
                back = towns.InitialReturn;
            Selected = character = repository.Save(owner, character, back, back) with { WeddingHall = null };
        }
        if (store.FindMarriage(character.Id) is not { } before) return true;
        var after = MarriageWeddingRules.GrantDailyLove(before, catalog, character.Id, QuestCycle.Day(now));
        if (!ReferenceEquals(before, after))
            Selected = store.CommitMarriageSingle(owner, character, new(character.Inventory, character.Inventory,
                "marriage_daily_love"), before, after).First;
        return true;
    });

    public MarriageCapture? CaptureMarriageInitialization() => owner.Execute(() =>
        !Ready && Phase == SessionPhase.Unselected && Selected is { IsPvpPrivate: false } character
            ? new MarriageCapture(character, owner.Generation, SelectionEpoch, Phase) : CaptureMarriage(false));

    public bool CanPublishMarriageInitialization(MarriageCapture capture) => owner.Execute(() => owner.IsCurrent &&
        owner.Generation == capture.Generation && SelectionEpoch == capture.Epoch && Phase == capture.Phase &&
        ReferenceEquals(Selected, capture.Character));

    public MarriageCapture? CaptureMarriage(bool requireTown = true) => owner.Execute(() =>
        Ready && Selected is { IsPvpPrivate: false } character &&
        (!requireTown || CanUseExpertJob()) && Phase != SessionPhase.PvpRoom
            ? new MarriageCapture(character, owner.Generation, SelectionEpoch, Phase) : null);

    internal bool MatchesMarriage(MarriageCapture capture, bool requireTown) => owner.IsCurrent &&
        owner.Generation == capture.Generation && SelectionEpoch == capture.Epoch && Phase == capture.Phase &&
        ReferenceEquals(Selected, capture.Character) && Ready && (!requireTown || CanUseExpertJob());

    public void RefreshMarriageProjection() => owner.Execute(() =>
    {
        if (Selected is { } character && MarriageStore is { } store)
        {
            var canonical = store.FindMarriage(character.Id);
            if (canonical?.Id == character.Marriage?.Id && canonical?.Revision == character.Marriage?.Revision)
                return true;
            var current = character with { Marriage = canonical };
            if (canonical is null && character.Marriage is not null)
                current = current with
                {
                    ActiveMainQuests = current.ActiveMainQuests.Where(row => row.QuestId is not (20737 or 20738)).ToArray(),
                    CompletedQuests = current.CompletedQuests.Where(id => id is not (20737 or 20738)).ToArray(),
                    QuestDailyClaims = current.QuestDailyClaims.Remove(20737).Remove(20738),
                    QuestAnswers = current.QuestAnswers.Remove(20737).Remove(20738)
                };
            Selected = current;
        }
        return true;
    });

    public TownTravelReceipt? CommitMarriageTravel(MarriageCapture capture, Location destination, MarriageRecord wedding,
        DateTimeOffset now) => owner.Execute(() =>
    {
        if (!MatchesMarriage(capture, true) || !towns.IsPosition(destination, completed) || MarriageStore is not { } store ||
            wedding.Wedding is not { } ceremony || ChannelEntry?.Identity is not { } channel ||
            channel.ServerIndex != ceremony.ServerIndex || channel.Number != ceremony.ChannelNumber) return null;
        Selected = store.CommitMarriageHallTravel(owner, capture.Character, wedding, destination,
            capture.Character.Position.Town is 39 or 101 ? capture.Character.Return : capture.Character.Position,
            now.ToUnixTimeSeconds());
        InvalidateImageCommunication();
        InvalidateExpertJobStore();
        return new TownTravelReceipt(Selected, owner.Generation, SelectionEpoch);
    });

    public MarriageSessionCommit? CommitMarriage(MarriageCapture capture, InventoryPlan plan,
        MarriageRecord before, MarriageRecord after, IReadOnlyList<MailboxDelivery>? mail = null,
        bool requireTown = true) => owner.Execute(() =>
    {
        if (!MatchesMarriage(capture, requireTown) || MarriageStore is not { } store) return null;
        var committed = store.CommitMarriageSingle(owner, capture.Character, plan, before, after, mail);
        Selected = committed.First;
        return new MarriageSessionCommit(committed.Record, MarriageInventoryReceipt(capture, committed.First, plan));
    });

    public static MarriageSessionCommit? CommitMarriageTogether(GameSession actor, MarriageCapture first,
        GameSession peer, MarriageCapture second, InventoryPlan firstPlan, InventoryPlan secondPlan,
        MarriageRecord? before, MarriageRecord after, IReadOnlyList<MailboxDelivery>? mail = null,
        Func<bool>? eligible = null) => AccountOwners.Lease.ExecuteTogether<MarriageSessionCommit?>(
        actor.MarriageOwner, peer.MarriageOwner, () =>
        {
            if (!ReferenceEquals(actor.MarriageStore, peer.MarriageStore) || actor.MarriageStore is not { } store ||
                !actor.MatchesMarriage(first, true) || !peer.MatchesMarriage(second, true) ||
                eligible?.Invoke() == false) return null;
            var committed = store.CommitMarriageTogether(actor.MarriageOwner, peer.MarriageOwner, first.Character,
                second.Character, firstPlan, secondPlan, before, after, mail);
            actor.Selected = committed.First;
            peer.Selected = committed.Second ?? throw new InvalidOperationException("双人婚姻提交缺少另一方快照");
            return new MarriageSessionCommit(committed.Record,
                actor.MarriageInventoryReceipt(first, committed.First, firstPlan),
                peer.MarriageInventoryReceipt(second, committed.Second, secondPlan));
        });

    InventoryResult MarriageInventoryReceipt(MarriageCapture capture, Character current, InventoryPlan plan) =>
        new(true, 0, plan)
        {
            Receipt = new(current, owner.Generation, SelectionEpoch)
            {
                Phase = Phase, PreviousQuests = capture.Character.ActiveMainQuests,
                PreviousSkillSwitchInventory = capture.Character.SkillSwitchInventory
            }
        };
}
