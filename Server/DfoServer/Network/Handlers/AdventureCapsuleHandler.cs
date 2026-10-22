using DfoServer.Game.Adventure;
using DfoServer.Game.Characters;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
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
            if (CharacterExperienceNotice.ShouldPublish(previous.Level))
                await context.Notify(NotiPacketType.EXP, GrowthPackets.Experience(character.Level, character.Learning, auxiliary,
                    new(0, 0, 0, 0, 0, 0, 0, [], 0, 0, 0, 0, 0, 0, 0)), "adventure_capsule_experience", Guard);
            if (previous.Level == character.Level && SkillPackets.BindingsChanged(previous.Learning, character.Learning))
                await SkillProjection.PublishAsync(context, character, "adventure_capsule_skills", "adventure_capsule_combo", Guard);
            await SkillSwitchInventoryProjection.RefreshStateAsync(
                context,
                previous.SkillSwitchInventory,
                previous.Inventory,
                character,
                Guard,
                "adventure_capsule_skill_switch_inventory");
            await levels.RefreshAsync(context, character, previous.Level, "adventure_capsule_level", Guard,
                previousLearning: previous.Learning);
            await context.Notify(NotiPacketType.ADVENTURE_INFO, AdventurePackets.Information(character.Id, snapshot, service.Catalog,
                snapshot.Representatives(service.Catalog)), "adventure_capsule_information", Guard);
            await context.Notify(NotiPacketType.ADVENTURE_LV_EXP, AdventurePackets.Refresh(character.Id, snapshot.State, service.Catalog),
                "adventure_capsule_actor", Guard);
        }
        return CommandResult.Handled;
    }
}
