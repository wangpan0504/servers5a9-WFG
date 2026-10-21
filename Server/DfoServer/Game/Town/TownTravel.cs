using DfoServer.Game.Characters;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Town;

internal sealed record TownTravelPlan(Location? Position, Location? Return, string Reason)
{
    public bool Accepted => Reason == "ready";
    public static TownTravelPlan Reject(string reason) => new(null, null, reason);
}

internal sealed record TownTravelReceipt(Character Character, long Generation, long SelectionEpoch);

internal static class TownTravel
{
    public static Location? Walk(Character character, PositionRequest request, TownCatalog towns, IReadOnlySet<int> completed)
    {
        var target = character.Position with { X = request.X, Y = request.Y };
        return towns.IsPosition(target, completed) ? target : null;
    }

    public static TownTravelPlan Plan(Character character, AreaRequest request, TownCatalog towns, IReadOnlySet<int> completed)
    {
        if (request.PreviousTown != character.Position.Town || request.PreviousArea != character.Position.Area)
            return TownTravelPlan.Reject("source-area-mismatch");
        var target = new Location(request.Town, request.Area, request.X, request.Y, character.Position.Direction);
        // 房间返回 0 仍绑定存档返回点；地图传送门对象（实测 PathKind 6）按 PVF warpgate 目标放行，
        // 等级与前置任务由 Admission 的 portal 分支继续校验；菜单 5 同样按传送目标处理。
        bool portal = request.TargetNpc == 0 && towns.IsRoom(character.Position, completed) &&
            (request.PathKind == 5 ||
             request.PathKind != 0 && towns.RoomPortals.Any(p => p.Town == target.Town && p.Area == target.Area));
        string reason = towns.Admission(character.Position, target, character.Level, completed, character.Return, portal, request.TargetNpc);
        if (reason != "ready")
            return TownTravelPlan.Reject(reason);
        // 有坐标切图保留请求落点；返回地区单独解析，不能用门口覆盖客户端坐标。
        var back = towns.IsRoom(target, completed)
            ? towns.Entrance(target, character.Position, completed)
            : towns.ResolveRoomReturn(target, character.Return, character.Level, completed);
        return back is null ? TownTravelPlan.Reject("return-gate-unavailable") : new(target, back, "ready");
    }

    public static TownTravelPlan LeaveRoom(Character character, TownCatalog towns, IReadOnlySet<int> completed)
    {
        if (!towns.IsRoom(character.Position, completed))
            return TownTravelPlan.Reject("not-in-ready-room");
        var back = character.Return ?? towns.DefaultReturn(character.Position, completed);
        var target = back is null ? null : towns.Entrance(character.Position, back, completed);
        if (target is null)
            return TownTravelPlan.Reject("return-gate-unavailable");
        string reason = towns.Admission(character.Position, target, character.Level, completed, back);
        return reason == "ready" ? new(target, target, reason) : TownTravelPlan.Reject(reason);
    }
}
