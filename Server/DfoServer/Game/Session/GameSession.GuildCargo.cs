using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Guilds;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    GuildCargoReceipt? guildCargoLoaded;
    (long Epoch, uint GuildId, long Revision) guildCargoObserved;
    public Action<GuildCargoNotice>? GuildCargoNoticeArrived { private get; set; }

    bool InGuildCargoScene(GuildCargoService service) => Ready && Phase == SessionPhase.Town && Selected is { } character &&
        pendingRental is null && !HasPendingTownQuestCommit && towns.Areas.TryGetValue((character.Position.Town, character.Position.Area), out var area) &&
        area.Map(completed) is { Error: null } map && GuildCargoCatalog.IsHall(map.Path) &&
        map.Npcs.Any(npc => service.StorageNpcs.Contains(npc.Id));

    GuildCargoActor? CaptureGuildCargo(GuildCargoService service)
    {
        if (!InGuildCargoScene(service) || Selected is not { } character) return null;
        long epoch = SelectionEpoch;
        bool Current() => owner.IsCurrent && SelectionEpoch == epoch && ReferenceEquals(Selected, character) && InGuildCargoScene(service);
        return new(owner, character, epoch, Current);
    }

    GuildCargoReceipt MakeGuildCargoReceipt(GuildCargoService service, GuildCargoView view)
    {
        var character = Selected!;
        long epoch = SelectionEpoch;
        if (guildCargoObserved.Epoch != epoch || guildCargoObserved.GuildId != view.GuildId || view.Revision > guildCargoObserved.Revision)
            guildCargoObserved = (epoch, view.GuildId, view.Revision);
        bool Current() => owner.IsCurrent && SelectionEpoch == epoch && Selected?.Id == character.Id && InGuildCargoScene(service);
        return new(view, character, Current);
    }

    public (ushort Error, GuildCargoReceipt? Receipt) ReadGuildCargo(GuildCargoService service, bool initialize = true,
        uint expectedGuild = 0, bool requireLoaded = false)
    {
        try
        {
            return owner.Execute<(ushort, GuildCargoReceipt?)>(() =>
            {
                if (CaptureGuildCargo(service) is not { } actor) return (8, null);
                if (requireLoaded && (guildCargoLoaded is not { } loaded || !loaded.IsCurrent() || loaded.View.GuildId != expectedGuild)) return (196, null);
                var read = service.Store.ReadGuildCargo(actor, initialize);
                if (read.Error != 0 || read.View is not { } view) return (read.Error, null);
                if (expectedGuild != 0 && view.GuildId != expectedGuild) return (100, null);
                return (0, MakeGuildCargoReceipt(service, view));
            });
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return (8, null); }
    }

    public bool CanPublishGuildCargo(GuildCargoReceipt receipt) => owner.Execute(() => receipt.IsCurrent() &&
        guildCargoObserved.Epoch == SelectionEpoch && guildCargoObserved.GuildId == receipt.View.GuildId &&
        guildCargoObserved.Revision <= receipt.View.Revision);

    public void MarkGuildCargoLoaded(GuildCargoReceipt receipt) => owner.Execute(() =>
    {
        if (CanPublishGuildCargo(receipt)) guildCargoLoaded = receipt;
        return true;
    });

    public GuildCargoTransferReceipt TransferGuildCargo(GuildCargoService service, GuildCargoRequest request, string requestId)
    {
        try
        {
            var capture = owner.Execute(() =>
            {
                var actor = CaptureGuildCargo(service);
                var view = guildCargoLoaded is { } loaded && loaded.IsCurrent() ? loaded.View : null;
                return (Actor: actor, View: view);
            });
            if (capture.Actor is not { } actor) return new(new(8), null, null);
            // 模板、规则和容器规划在账号锁外；事务只复验同一角色与同一仓库版本。
            var plan = service.Plan(actor.Character, capture.View, request);
            return owner.Execute(() =>
            {
                if (!actor.IsCurrent()) return new GuildCargoTransferReceipt(new(198), null, null);
                var commit = service.Store.CommitGuildCargo(actor, plan, requestId, service.Now);
                if (commit.Character is { } character) Selected = character;
                GuildCargoReceipt? cargo = commit.View is { } view ? MakeGuildCargoReceipt(service, view) : null;
                InventoryResult? inventory = null;
                if (!request.Move && commit.Success && !commit.Replayed && commit.Inventory is { } committedPlan)
                {
                    var receipt = new InventoryCommitReceipt(Selected!, owner.Generation, SelectionEpoch)
                    {
                        Phase = Phase, PreviousQuests = actor.Character.ActiveMainQuests,
                        PreviousSkillSwitchInventory = actor.Character.SkillSwitchInventory
                    };
                    inventory = new(true, 0, committedPlan, request.Count,
                        new(request.List, request.Deposit ? request.InventorySlot : commit.Destination)) { Receipt = receipt };
                }
                return new(commit, cargo, inventory);
            });
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return new(new(8), null, null); }
    }

    public bool TryDeliverGuildCargo(ushort characterId, GuildCargoNotice notice)
    {
        try
        {
            var callback = owner.Execute(() => Ready && Selected?.Id == characterId &&
                guildCargoLoaded is { } loaded && loaded.IsCurrent() && loaded.View.GuildId == notice.GuildId
                    ? GuildCargoNoticeArrived : null);
            if (callback is null) return false;
            callback(notice);
            return true;
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return false; }
    }
}
