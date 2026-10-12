using DfoServer.Game.Adventure;
using DfoServer.Game.Characters;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class AdventureCapsuleHandler(AdventureCapsuleService service, CharacterLevelProjection levels, TimeProvider? clock = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.ADVENTURE_GROWTHCAPSULE_EXP, Claim, nameof(AdventureCapsuleHandler));
    async Task<CommandResult> Claim(GameConnectionContext context, Packet packet)
    {
        AdventureRequests.Empty(packet.Body);
        var previous = context.Session.Selected;
        var result = context.Session.ClaimAdventureCapsule(service, (clock ?? TimeProvider.System).GetUtcNow());
        bool Guard() => context.Session.CanPublishAdventureCapsule(result);
        await context.Reply(CmdPacketType.ADVENTURE_GROWTHCAPSULE_EXP, AdventurePackets.Ack(result.Error), "adventure_capsule_claim", result.Accepted ? Guard : null);
        if (result.Accepted && previous is not null && result.Commit is { } committed)
        {
            var character = committed.Character; var snapshot = committed.Snapshot;
            var dungeon = character.DungeonState ?? throw new InvalidOperationException("胶囊成长缺少角色附属状态");
            var auxiliary = new CharacterGrowthAuxiliaryState(checked((uint)character.Inventory.WinPoints), dungeon.GrowthCapsuleExperience,
                dungeon.ProficiencyLevel, dungeon.ProficiencyExperience);
            await context.Notify(NotiPacketType.EXP, GrowthPackets.Experience(character.Level, character.Learning, auxiliary,
                new(0, 0, 0, 0, 0, 0, 0, [], 0, 0, 0, 0, 0, 0, 0)), "adventure_capsule_experience", Guard);
            if (SkillPackets.BindingsChanged(previous.Learning, character.Learning))
                await context.Notify(NotiPacketType.SKILLINFO, SkillPackets.Initialized(character.Learning), "adventure_capsule_skills", Guard);
            await levels.RefreshAsync(context, character, previous.Level, "adventure_capsule_level", Guard);
            await context.Notify(NotiPacketType.ADVENTURE_INFO, AdventurePackets.Information(character.Id, snapshot, service.Catalog,
                snapshot.Representatives(service.Catalog), context.Channel.ServerIndex), "adventure_capsule_information", Guard);
            await context.Notify(NotiPacketType.ADVENTURE_LV_EXP, AdventurePackets.Refresh(character.Id, snapshot.State, service.Catalog),
                "adventure_capsule_actor", Guard);
        }
        return CommandResult.Handled;
    }
}
