using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Session;

internal sealed record BeastDungeonSelectionReceipt(long Generation, long Epoch, Character Character,
    DungeonSelectionContext Selection, int Day, ImmutableArray<(uint Dungeon, byte Remaining)> Counts);

internal sealed partial class GameSession
{
    // 首版只投影普通频道的原中央公园入口；名单不按剩余次数过滤，耗尽仍保留当日入侵身份。
    public BeastDungeonSelectionReceipt? CaptureBeastDungeonSelection() => owner.Execute(() =>
    {
        if (!CanSelectNativeBeast() || Selected is not { } character || CurrentDungeonSelection is not { } selection)
            return null;
        int day = BeastDungeonRotation.ServiceDay(IncountNow());
        var pair = BeastDungeonRotation.Pair(day);
        var counts = ImmutableArray.CreateBuilder<(uint Dungeon, byte Remaining)>(2);
        foreach (int dungeon in new[] { pair.First, pair.Second })
        {
            int used = repository.LoadBeastDungeonUsed(owner, character.Id, day, dungeon);
            counts.Add(((uint)dungeon, (byte)Math.Clamp(BeastDungeonRotation.DailyLimit - used, 0, BeastDungeonRotation.DailyLimit)));
        }
        return new BeastDungeonSelectionReceipt(owner.Generation, SelectionEpoch, character, selection, day, counts.MoveToImmutable());
    });

    public bool CanPublishBeastDungeonSelection(BeastDungeonSelectionReceipt receipt) => owner.Execute(() =>
        CanSelectNativeBeast() && receipt.Generation == owner.Generation && receipt.Epoch == SelectionEpoch &&
        ReferenceEquals(Selected, receipt.Character) && ReferenceEquals(CurrentDungeonSelection, receipt.Selection) &&
        receipt.Day == BeastDungeonRotation.ServiceDay(IncountNow()));

    bool CanUseNativeBeastLocation() => Ready &&
        ChannelEntry?.Type == ChannelAreaLanding.OrdinaryType && completed.Contains(5927) &&
        Selected is { Position.Town: ChannelAreaLanding.CentralParkTown } character &&
        towns.Areas.TryGetValue((character.Position.Town, character.Position.Area), out var area) && area.DungeonWorldMap == 39;

    bool CanSelectNativeBeast() => CanUseNativeBeastLocation() && Phase == SessionPhase.MapSelect &&
        Selected is { } character &&
        CurrentDungeonSelection is { IsTutorial: false, IsClassExperience: false, Raid: null } selection &&
        selection.SelectionEpoch == SelectionEpoch && selection.CharacterId == character.Id && selection.OriginPosition == character.Position;

    // 独立5335–5339路线的既有次数暂保持；不向原310–314发送550混淆普通与Beast额度。
    public (uint Dungeon, byte Remaining)[] BeastSelectionCounts() => owner.Execute<(uint,byte)[]>(() =>
    {
        // 魔兽世界地图46（worldmap/beast_metrocenter.wdm）的气球编号就是 5335–5339，次数必须按这五个下发。
        // 区域不限定2：频道落点与玩家自行走动都会换区域，取城内的整座城镇51判断。
        if (!Ready || Phase != SessionPhase.MapSelect || Selected is not { Position: { Town:51 } } character ||
            ChannelEntry?.Type != ChannelAreaLanding.BeastType || !completed.Contains(5927)) return [];
        int day = BeastDungeonRotation.ServiceDay(IncountNow());
        return Enumerable.Range(BeastDungeonRotation.FirstGatewayDungeonId,5).Select(dungeon =>
        {
            int used = repository.LoadBeastDungeonUsed(owner,character.Id,day,dungeon);
            return ((uint)dungeon,(byte)(BeastDungeonRotation.Contains(day,dungeon)
                ? Math.Clamp(BeastDungeonRotation.DailyLimit-used,0,BeastDungeonRotation.DailyLimit) : 0));
        }).ToArray();
    });
}
