using DfoServer.Game.Characters;
using DfoServer.Game.Guild;

namespace DfoServer.Game.Session;

internal sealed record GuildManagementReceipt(Character Character, GuildManagementState? State, bool Joined,
    Func<bool> IsCurrent)
{
    public bool EvacuatedHall { get; init; }
    public bool GradeChanged { get; init; }
    public bool ActorChanged { get; init; }
}

internal sealed partial class GameSession
{
    long guildManagementVersion;
    public Action<GuildManagementNotice>? GuildManagementNoticeArrived { private get; set; }

    public GuildManagementIdentity? CaptureGuildManagement(bool requireTown = true)
    {
        try
        {
            return owner.Execute(() =>
            {
                if (!Ready || Selected is not { } character || requireTown && Phase != SessionPhase.Town) return null;
                long epoch = SelectionEpoch;
                bool Current() => owner.IsCurrent && Ready && SelectionEpoch == epoch && Selected?.Id == character.Id &&
                    (!requireTown || Phase == SessionPhase.Town);
                return new GuildManagementIdentity(this, owner, character.Id, character.Name, epoch,
                    ChannelEntry?.Identity ?? default, Current) { Delivery = GuildManagementNoticeArrived };
            });
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return null; }
    }

    public bool TryDeliverGuildManagement(ushort characterId, GuildManagementNotice notice)
    {
        try
        {
            var delivery = owner.Execute(() => Ready && Selected?.Id == characterId &&
                (notice.Kind != GuildManagementNoticeKind.Invite || Phase == SessionPhase.Town)
                    ? GuildManagementNoticeArrived : null);
            if (delivery is null) return false;
            delivery(notice);
            return true;
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return false; }
    }

    public GuildManagementReceipt? RefreshGuildManagement(IGuildManagementStore store, uint expectedGuild)
    {
        try
        {
            return owner.Execute(() =>
            {
                if (!Ready || Selected is not { } character) return null;
                var state = store.ReadGuildManagement(owner, character.Id);
                // 旧退会或编辑通知不能覆盖后来加入的另一个公会。
                if (state is not null && state.Guild.Id != expectedGuild) return null;
                bool joined = state is not null && character.Guild?.Id != state.Guild.Id;
                bool gradeChanged = character.Guild?.Grade != state?.Grade;
                bool actorChanged = character.Guild?.Id != state?.Guild.Id || character.Guild?.Name != state?.Guild.Name ||
                    character.Guild?.Appearance != state?.Guild.Appearance;
                Selected = character with { Guild = state?.Guild };
                bool evacuated = state is null && Phase == SessionPhase.Town && towns.IsGuildHall(character.Position, completed);
                if (evacuated)
                {
                    var back = character.Return is { } saved && !towns.IsGuildHall(saved, completed)
                        ? saved : towns.InitialReturn;
                    Selected = repository.Save(owner, Selected, back, back);
                }
                var installed = Selected;
                long epoch = SelectionEpoch;
                long version = ++guildManagementVersion;
                bool Current() => owner.IsCurrent && Ready && SelectionEpoch == epoch &&
                    guildManagementVersion == version && ReferenceEquals(Selected, installed);
                return new GuildManagementReceipt(installed, state, joined, Current)
                    { EvacuatedHall = evacuated, GradeChanged = gradeChanged, ActorChanged = actorChanged };
            });
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return null; }
    }
}
