using DfoServer.Game.MonsterMockBattle;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class MonsterMockBattleProjection(GameConnectionContext context, MonsterMockBattleService service,
    InventoryProjection inventory, TownProjection town, ServerLog log)
{
    long publishedEpoch = -1;
    long infoEpoch = -1;
    byte[]? publishedInfo;
    DfoServer.Game.Inventory.Models.InventorySnapshot? publishedInventory;
    Packet? committedPacket;
    MockBattleView? committedView;
    DateTimeOffset nextRefresh = DateTimeOffset.MinValue;
    public bool Enabled => service.Enabled;
    public IEnumerable<int> AccountMaterialIds => service.Catalog.ItemDraws.Keys.Append(MonsterMockBattleService.CoinItemId);
    public DateTimeOffset NextRefresh => Enabled && context.Session.Phase == SessionPhase.Town ? nextRefresh : DateTimeOffset.MaxValue;
    public bool ContainsDungeon(uint dungeon) => service.ContainsDungeon(dungeon);

    public async Task OpenSelectionAsync(uint dungeon)
    {
        var selected = context.BlocksTownActions ? null : context.Session.BeginMockBattleSelection(service, dungeon);
        if (selected is null)
        {
            await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionUnavailable(), "mock_battle_selection_rejected");
            return;
        }
        if (!await town.DetachForDungeonSelectionAsync(context, selected)) return;
        bool Guard() => context.Session.CanPublishDungeonSelection(selected);
        await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionOpened(), "mock_battle_selection_opened", Guard);
        await context.Notify(NotiPacketType.USER_STATE, DungeonPackets.DungeonPlayerState(selected.Character.Id), "mock_battle_selection_state", Guard);
        await context.Notify(NotiPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionNotice(0), "mock_battle_selection_scene", Guard);
    }

    public FormalDungeonReceipt? Enter(DungeonSelectionRequest request,
        DfoServer.GameWorld.Dungeons.DungeonCatalog dungeons, DfoServer.GameWorld.Dungeons.DungeonRoomCatalog rooms)
    {
        try { return context.Session.EnterMockBattle(service, request.DungeonIndex, request.Parameter, request.Unknown21, dungeons, rooms); }
        catch (MockBattleRejectedException)
        {
            log.Write("monster_mock_battle_entry_rejected", new { Connection = context.Id, Dungeon = request.DungeonIndex, Reason = "admission_rejected" });
            return null;
        }
    }

    public async Task BattleInfoAsync(FormalDungeonReceipt receipt, bool spawn)
    {
        if (receipt.Entry.MockBattle is not { } admission) return;
        bool Guard() => context.Session.CanPublishFormalDungeon(receipt);
        var state = service.Read(context.Owner, receipt.Character);
        await context.Notify(NotiPacketType.MONSTER_MOCK_BATTLE_INFO, MonsterMockBattlePackets.Info(state.Snapshot),
            "monster_mock_battle_battle_info", Guard);
        if (spawn)
            await context.Notify(NotiPacketType.MONSTER_MOCK_BATTLE_UNASSIGN_MONSTER_ADD,
                MonsterMockBattlePackets.Spawns(receipt.Monsters.Monsters.Where(row => row.MockBattleSpawn is not null)
                    .Select(row => row.MockBattleSpawn!).ToArray()), "monster_mock_battle_spawns", Guard);
        publishedEpoch = -1;
        publishedInfo = null;
    }
    public async Task RefreshAsync()
    {
        if (publishedEpoch == context.Session.SelectionEpoch && ReferenceEquals(publishedInventory, context.Session.Selected?.Inventory) &&
            service.Now < nextRefresh) return;
        if (context.Session.ReadMockBattle(service) is not { } view) return;
        if (view.Inventory is { } changed) await inventory.RefreshAsync(context, changed);
        await PublishAsync(view, automatic: true);
        if (context.Session.CanPublishMockBattle(view)) { publishedEpoch = view.Epoch; publishedInventory = view.Character.Inventory; }
    }

    public async Task UseAsync(MonsterMockBattleRequest request, Packet packet)
    {
        MockBattleView? view = null;
        bool accepted = false;
        try
        {
            if (context.BlocksTownActions) throw new MockBattleRejectedException("当前状态不能操作");
            if (request.Operation is 7 or 8)
            {
                view = context.Session.ReadMockBattle(service);
                throw new MockBattleRejectedException("玩家对战暂未开放");
            }
            // 分派器在发布阶段遇忙锁会重放同一Packet对象；复用实际提交回执，不再次扣费或抽卡。
            view = ReferenceEquals(committedPacket, packet) ? committedView :
                context.Session.MutateMockBattle(service, request.Operation, request.Argument0,
                    request.Argument1, request.Payload20.AsSpan());
            accepted = view is not null;
            if (view is not null) { committedPacket = packet; committedView = view; }
        }
        catch (Exception error) when (error is MockBattleRejectedException or System.Text.DecoderFallbackException or OverflowException)
        {
            log.Write("monster_mock_battle_rejected", new { Connection = context.Id, Operation = request.Operation, Reason = "invalid_operation" });
        }
        byte[] slots = [255, 255, 255, 255, 255], stars = new byte[5];
        if (view is { AcquiredSlots.IsDefault: false })
        {
            view.AcquiredSlots.AsSpan().CopyTo(slots);
            // ACK的末五字节用于获得画面的星级，必须取本次提交的对应卡片。
            for (int index = 0; index < view.AcquiredSlots.Length; index++)
                stars[index] = view.Account.Snapshot.Cards[view.AcquiredSlots[index]].Stars;
        }
        bool CanSend() => view is null ? context.Owner.IsCurrent : context.Session.CanPublishMockBattle(view);
        if (view is not null)
        {
            if (view.Inventory is { } changed) await inventory.RefreshAsync(context, changed);
            await PublishAsync(view);
        }
        await context.Reply(CmdPacketType.MONSTER_MOCK_BATTLE,
            MonsterMockBattlePackets.Acknowledge(accepted ? 0u : 7u, request.Operation, slots, stars),
            "monster_mock_battle_ack", CanSend);
        if (view is null) return;
        log.Write("monster_mock_battle_operation", new { Connection = context.Id, request.Operation, Accepted = accepted,
            Revision = view.Account.Revision, Count = view.AcquiredSlots.IsDefault ? 0 : view.AcquiredSlots.Length });
    }

    async Task PublishAsync(MockBattleView view, bool automatic = false)
    {
        var body = MonsterMockBattlePackets.Info(view.Account.Snapshot);
        bool Guard() => context.Session.CanPublishMockBattle(view);
        // 只去重同一选角已实际写出的完整状态；显式操作仍按原协议刷新面板。
        if (context.Sender.IsPublicationQueued)
        {
            publishedInfo = null;
            await context.Notify(NotiPacketType.MONSTER_MOCK_BATTLE_INFO, body, "monster_mock_battle_info", Guard);
        }
        else if (!automatic || infoEpoch != view.Epoch || publishedInfo is null || !body.AsSpan().SequenceEqual(publishedInfo))
        {
            if (!await context.Sender.SendConfirmedAsync(0, (ushort)NotiPacketType.MONSTER_MOCK_BATTLE_INFO,
                body, "monster_mock_battle_info", Guard)) return;
            if (!Guard()) return;
            infoEpoch = view.Epoch;
            publishedInfo = body;
        }
        if (!context.Session.CanPublishMockBattle(view)) return;
        var now = service.Now;
        var local = now.ToOffset(TimeSpan.FromHours(8));
        nextRefresh = new DateTimeOffset(local.Date.AddHours(6).AddDays(local.Hour < 6 ? 0 : 1), local.Offset);
        if (view.Account.Hearts < MockBattleAccount.HeartLimit)
        {
            var recovery = DateTimeOffset.FromUnixTimeSeconds(view.Account.HeartAt).AddSeconds(MockBattleAccount.RecoverySeconds);
            if (recovery < nextRefresh) nextRefresh = recovery > now ? recovery : now.AddSeconds(1);
        }
    }
}
