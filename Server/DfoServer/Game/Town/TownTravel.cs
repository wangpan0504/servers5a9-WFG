using DfoServer.Game.Characters;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Town;

internal sealed record NpcTownVisit(Location Origin, byte Town, byte Area);

internal sealed record TownTravelPlan(Location? Position, Location? Return, string Reason)
{
    public NpcTownVisit? NpcVisit { get; init; }
    public bool Accepted => Reason == "ready";
    public static TownTravelPlan Reject(string reason) => new(null, null, reason);
}

internal sealed record TownTravelReceipt(Character Character, long Generation, long SelectionEpoch)
{
    public DfoServer.Game.Raids.AntonRaidBattleChange? RaidBattleChange { get; init; }
}

internal static class TownTravel
{
    public static Location? Walk(Character character, PositionRequest request, TownCatalog towns, IReadOnlySet<int> completed)
    {
        var target = character.Position with
        {
            X = request.X,
            Y = request.Y,
            Direction = request.Movement == 0 ? character.Position.Direction : request.Movement
        };
        return towns.IsPosition(target, completed) ? target : null;
    }

    public static TownTravelPlan Plan(Character character, AreaRequest request, TownCatalog towns, IReadOnlySet<int> completed, ChannelEntry? channel = null, NpcTownVisit? npcVisit = null)
    {
        if (character.IsPvpPrivate && request.Town != 10) return TownTravelPlan.Reject("pvp-private-town-only");
        if (request.Town == 39 && request.Area is >= 8 and < 38 &&
            (character.Position.Town != 39 || character.Position.Area != request.Area || character.WeddingHall is null))
            return TownTravelPlan.Reject("wedding-hall-requires-invitation");
        if (request.Town == 101 && (request.Area != 0 || character.Marriage is not { State: 4 } marriage || !marriage.IsMember(character.Id)))
            return TownTravelPlan.Reject("marriage-home-not-eligible");
        var target = new Location(request.Town, request.Area, request.X, request.Y, character.Position.Direction);
        // 婚礼电影结束和大祭司离堂均发送来源0/0、NPC0的普通CMD36；落点按婚礼专用规则核对。
        if (character.Position is { Town: 39, Area: >= 8 and < 38 } && character.WeddingHall is not null &&
            request is { PreviousTown: 0, PreviousArea: 0, TargetNpc: 0, Movement: 5, PathKind: 0 } &&
            towns.IsWeddingMovieReturn(target, completed))
        {
            string admission = towns.DestinationAdmission(target, character.Level, completed);
            if (admission != "ready") return TownTravelPlan.Reject(admission);
            // 原生NPC返回坐标可落在步行矩形外，与NPC导航一样保留已经按资源精确核对的坐标。
            var movieBack = towns.ResolveRoomReturn(target, OrdinaryDistrict(character.Return), character.Level, completed);
            return movieBack is null ? TownTravelPlan.Reject("return-gate-unavailable") : new(target, movieBack, "ready");
        }
        if (request.PreviousTown != character.Position.Town || request.PreviousArea != character.Position.Area)
            return TownTravelPlan.Reject("source-area-mismatch");
        // 公会菜单005B7960直接发送固定落点，不经过街区的PVF有向门。
        // 仅放行本版这一种菜单形状；最终保存仍在同一事务复核真实会籍。
        if (request is { Town: 8, Area: 0, X: 2110, Y: 270, Movement: 5, TargetNpc: 0, PathKind: 0 })
        {
            var hall = new Location(8, 0, 2110, 270, character.Position.Direction);
            if (!towns.IsGuildHall(hall, completed)) return TownTravelPlan.Reject("target-not-guild-hall");
            string admission = towns.DestinationAdmission(hall, character.Level, completed);
            if (admission != "ready") return TownTravelPlan.Reject(admission);
            // 菜单坐标是客户端写死的旧版大地图落点，本版公会大厅步行区止于 x≈1040：
            // 不可站时改用该地图地形首块可走矩形中心，与从外场走门进入的实际落点相同。
            if (!towns.IsPosition(hall, completed))
            {
                if (towns.Spawn(hall, completed) is not { } spawn) return TownTravelPlan.Reject("arrival-unavailable");
                hall = spawn;
            }
            var origin = towns.IsRoom(character.Position, completed) || towns.IsGuildHall(character.Position, completed)
                ? character.Return : character.Position;
            return origin is null ? TownTravelPlan.Reject("return-gate-unavailable") : new(hall, origin, "ready");
        }
        // 婚房-101门返回本次入场缓存的来源地图，原码固定落点300/200，不把-101解释成普通城镇号。
        if (character.Position is { Town: 101, Area: 0 } && npcVisit is { Town: 101, Area: 0 } home &&
            request is { TargetNpc: 0, Movement: 5, PathKind: 0, X: 300, Y: 200 } &&
            request.Town == home.Origin.Town && request.Area == home.Origin.Area &&
            towns.HasSpecialExit(character.Position, -101, 0, completed))
        {
            string admission = towns.DestinationAdmission(target, character.Level, completed);
            if (admission != "ready") return TownTravelPlan.Reject(admission);
            return towns.IsPosition(target, completed) ? new(target, character.Return, "ready") : TownTravelPlan.Reject("arrival-outside-map");
        }
        // 原码 exit town 发送缓存来源的普通 C2S36；只允许本选角成功 NPC 入场留下的精确来源。
        if (npcVisit is { } visit && request.TargetNpc == 0 && request.Movement == 5 && request.PathKind == 0 &&
            character.Position.Town == visit.Town && character.Position.Area == visit.Area &&
            request.Town == visit.Origin.Town && request.Area == visit.Origin.Area &&
            request.X == visit.Origin.X && request.Y == visit.Origin.Y && towns.HasNpcExit(character.Position, completed))
        {
            string admission = towns.DestinationAdmission(visit.Origin, character.Level, completed);
            return admission == "ready" ? new(visit.Origin with { Direction = character.Position.Direction }, character.Return, "ready")
                : TownTravelPlan.Reject(admission);
        }
        // 特殊区域与团队频道的赛丽亚房间按频道落点出门：客户端若以区域请求代替 PREV_VILLAGE 指向频道落点区域，按同一出口处理。
        if (request.TargetNpc == 0 && ChannelAreaLanding.RoomExit(towns, channel, character.Position, character.Level, completed) is { } exit &&
            exit.Town == request.Town && exit.Area == request.Area)
            return ChannelExit(character, towns, completed, exit);
        // 房间返回 0 仍绑定存档返回点；地图传送门对象（实测 PathKind 6）按 PVF warpgate 目标放行，
        // 等级与前置任务由 Admission 的 portal 分支继续校验；菜单 5 同样按传送目标处理。
        bool portal = request.TargetNpc == 0 && towns.IsRoom(character.Position, completed) &&
            (request.PathKind == 5 ||
             request.PathKind != 0 && towns.RoomPortals.Any(p => p.Town == target.Town && p.Area == target.Area));
        string reason = towns.Admission(character.Position, target, character.Level, completed, character.Return, portal, request.TargetNpc);
        if (reason != "ready")
            return TownTravelPlan.Reject(reason);
        // 目标地图没有回连来源的门时客户端发 0,0（实测五陵 117/0 → 1/1）；0,0 不可站时按 PVF 入口解析落点。
        if (request.X == 0 && request.Y == 0 && !towns.IsPosition(target, completed))
        {
            if (towns.Entrance(character.Position, target, completed) is not { } arrival)
                return TownTravelPlan.Reject("arrival-unavailable");
            target = arrival with { Direction = character.Position.Direction };
        }
        // 有坐标切图保留请求落点；返回地区单独解析，不能用门口覆盖客户端坐标。
        // 频道起始城镇（安徒恩战舰 19、只能经频道进入的 67 等）内走动不改返回地区，离开频道后仍回进入前的普通地区。
        var kept = OrdinaryDistrict(character.Return);
        var back = target.Town == 101 ? character.Return : ChannelAreaLanding.PreservesRoomReturn(towns, target.Town) && kept is not null
            ? kept
            : towns.IsRoom(target, completed)
            ? towns.Entrance(target, character.Position, completed)
            : towns.ResolveRoomReturn(target, kept, character.Level, completed);
        // 战舰街区不充当共享房间的返回地区：旧存档的战舰返回点按共享房间初始地区修复。
        if (back is not null && back.Town == ChannelAreaLanding.AntonTown)
            back = kept ?? towns.ResolveRoomReturn(towns.Room, null, character.Level, completed);
        return back is null ? TownTravelPlan.Reject("return-gate-unavailable") : new(target, back, "ready")
        { NpcVisit = request.TargetNpc == 0 && target.Town != 101 ? null : new(character.Position, target.Town, target.Area) };
    }

    public static TownTravelPlan LeaveRoom(Character character, TownCatalog towns, IReadOnlySet<int> completed, ChannelEntry? channel = null)
    {
        if (!towns.IsRoom(character.Position, completed))
            return TownTravelPlan.Reject("not-in-ready-room");
        if (ChannelAreaLanding.RoomExit(towns, channel, character.Position, character.Level, completed) is { } exit)
            return ChannelExit(character, towns, completed, exit);
        var back = character.Return ?? towns.DefaultReturn(character.Position, completed);
        var target = back is null ? null : towns.Entrance(character.Position, back, completed);
        if (target is null)
            return TownTravelPlan.Reject("return-gate-unavailable");
        string reason = towns.Admission(character.Position, target, character.Level, completed, back);
        return reason == "ready" ? new(target, target, reason) : TownTravelPlan.Reject(reason);
    }

    // 频道落点已由 ChannelAreaLanding.Decide 校验目录、等级/任务准入与门口可站。频道专属城镇（115/59/67 等）不覆盖原返回地区，
    // 离开频道后赛丽亚房间仍回进入前的普通地区。
    static TownTravelPlan ChannelExit(Character character, TownCatalog towns, IReadOnlySet<int> completed, Location exit)
    {
        // 返回地区规则与原 CharacterSelection 频道落点一致：专属城镇保留进入前地区，其余按落点解析房间返回地区。
        var back = ChannelAreaLanding.PreservesRoomReturn(towns, exit.Town)
            ? towns.ResolveRoomReturn(character.Position, OrdinaryDistrict(character.Return), character.Level, completed)
            : towns.ResolveRoomReturn(exit, exit, character.Level, completed);
        return new(exit, back, "ready");
    }

    // 共享房间的返回地区只能是普通地区：频道起始城镇（安徒恩战舰 19）的街区不参与，旧存档按初始地区修复。
    static Location? OrdinaryDistrict(Location? saved) => saved is { Town: ChannelAreaLanding.AntonTown } ? null : saved;
}
