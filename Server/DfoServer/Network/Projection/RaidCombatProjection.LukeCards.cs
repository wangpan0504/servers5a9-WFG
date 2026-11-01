using DfoServer.Game.Raids;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static partial class RaidCombatProjection
{
    static void PostLukeCards(GameConnectionContext context, AntonRaidBattle battle, AntonRaidBattleView view,
        Func<bool> current)
    {
        if (battle.LukeCardsSnapshot(view) is not { } snapshot) return;
        bool Guard() => current() && battle.IsCurrent(view);
        byte[] body = RaidCombatPackets.LukeCards(snapshot);
        context.Sender.Post(0, (ushort)NotiPacketType.LUKE_RAID_CARD_DISPLAY, body, "luke_cards_snapshot",
            () => Guard() && (context.LukeCardsDisplay is not { } displayed || displayed.Instance != view.Instance ||
                !body.AsSpan().SequenceEqual(displayed.Body)),
            () => context.LukeCardsDisplay = (view.Instance, body));
        foreach (int monster in battle.LukeNamedTemplates)
        {
            bool killed = snapshot.Killed.Contains(monster);
            context.Sender.Post(0, (ushort)NotiPacketType.LUKE_RAID_KILLED_NAMED_INFO,
                RaidCombatPackets.LukeNamed(monster, killed), "luke_named_state",
                () => Guard() && (context.LukeNamedInstance != view.Instance ||
                    !context.LukeNamedDisplays.TryGetValue(monster, out bool previous) || previous != killed), () =>
                {
                    if (context.LukeNamedInstance != view.Instance)
                    { context.LukeNamedDisplays.Clear(); context.LukeNamedInstance = view.Instance; }
                    context.LukeNamedDisplays[monster] = killed;
                });
        }
    }
}
