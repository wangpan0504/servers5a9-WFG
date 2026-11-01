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
        bool CardsChanged() => Guard() && (context.LukeCardsDisplay is not { } displayed || displayed.Instance != view.Instance ||
            !body.AsSpan().SequenceEqual(displayed.Body));
        // 各成员自动退场会重复发布同一快照，已显示的卡牌与命名怪不占用有界消息槽。
        if (context.Owner.Execute(CardsChanged))
            context.Sender.Post(0, (ushort)NotiPacketType.LUKE_RAID_CARD_DISPLAY, body, "luke_cards_snapshot",
                CardsChanged, () => context.LukeCardsDisplay = (view.Instance, body));
        foreach (int monster in battle.LukeNamedTemplates)
        {
            bool killed = snapshot.Killed.Contains(monster);
            bool NamedChanged() => Guard() && (context.LukeNamedInstance != view.Instance ||
                !context.LukeNamedDisplays.TryGetValue(monster, out bool previous) || previous != killed);
            if (!context.Owner.Execute(NamedChanged)) continue;
            context.Sender.Post(0, (ushort)NotiPacketType.LUKE_RAID_KILLED_NAMED_INFO,
                RaidCombatPackets.LukeNamed(monster, killed), "luke_named_state",
                NamedChanged, () =>
                {
                    if (context.LukeNamedInstance != view.Instance)
                    { context.LukeNamedDisplays.Clear(); context.LukeNamedInstance = view.Instance; }
                    context.LukeNamedDisplays[monster] = killed;
                });
        }
    }
}
