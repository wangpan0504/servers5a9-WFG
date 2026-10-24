using DfoServer.Game.Session;
using DfoServer.GameWorld.Adventure;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class AdventureProjection(AdventureCatalog catalog)
{
    public async Task OtherInformationAsync(GameConnectionContext context, GameSession other, ushort target)
    {
        long epoch = context.Session.SelectionEpoch;
        var captured = other.PublicAdventure(target, catalog);
        if (captured is not { } identity) return;
        var snapshot = identity.Snapshot;
        // 他人资料只公开展示信息，不把商店余额、购买记录和可领取胶囊发给查询者。
        snapshot = snapshot with { State = snapshot.State with
        {
            Points = [0, 0, 0, 0, 0], CapsuleExperience = 0, BraveExperience = 0,
            Purchases = System.Collections.Immutable.ImmutableDictionary<int, uint>.Empty, Expeditions = []
        } };
        bool Guard() => context.Session.Ready && context.Session.SelectionEpoch == epoch && identity.IsCurrent();
        var body = AdventurePackets.Information(target, snapshot, catalog, snapshot.Representatives(catalog), ack: true);
        await context.Reply(CmdPacketType.REQUEST_ADVENTURE_INFO, body, "other_adventure_information", Guard);
    }

    public void Prepare(GameConnectionContext context) => context.Session.InitializeAdventure(catalog, DateTimeOffset.UtcNow);
    public async Task InformationAsync(GameConnectionContext context, bool ack = false, bool initialization = false)
    {
        var selected = context.Session.Selected;
        if (selected is null && !context.Session.CheckState(UserState.CharacterSelect)) return;
        long epoch = context.Session.SelectionEpoch;
        var snapshot = context.Session.ReadAdventure(catalog, DateTimeOffset.UtcNow);
        bool Guard() => context.Session.SelectionEpoch == epoch && ReferenceEquals(context.Session.Selected, selected) &&
            (selected is null ? context.Session.CheckState(UserState.CharacterSelect) : initialization || context.Session.Ready);
        var body = AdventurePackets.Information(selected?.Id ?? 0, snapshot, catalog, snapshot.Representatives(catalog), ack);
        if (ack) await context.Reply(CmdPacketType.REQUEST_ADVENTURE_INFO, body, "adventure_information", Guard);
        else
        {
            // 1343只更新冒险团信息，1375另行安装客户端账号显示名称；查询不重置名称上下文。
            if (initialization || selected is null)
                await context.Notify(NotiPacketType.REPRESENT_ACCOUNT_NAME,
                    AdventurePackets.AccountName(snapshot.State.NameInitialized ? snapshot.State.Name : ""),
                    "adventure_account_name", Guard);
            await context.Notify(NotiPacketType.ADVENTURE_INFO, body, selected is null ? "adventure_selector_information" : "adventure_information", Guard);
            if (selected is not null)
                await context.Notify(NotiPacketType.ADVENTURE_LV_EXP, AdventurePackets.Refresh(selected.Id, snapshot.State, catalog), "adventure_actor_initialize", Guard);
        }
    }
}
