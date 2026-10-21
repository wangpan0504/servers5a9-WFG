using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed partial class SinglePlayerDungeonRun
{
    private sealed record RidableBinding(DungeonRunIdentity Identity, ushort ObjectKey, uint ResourceId);
    private RidableBinding? activeRidable;

    internal DungeonRunResult OperateRidable(DungeonRunIdentity expected, byte operation, uint resourceId, DungeonPoint source)
    {
        if (!Matches(expected) || Phase != SinglePlayerDungeonPhase.Dungeon || !CanAdvance() || HasPendingDeath ||
            pendingSettlement is not null || scriptedReturn is not null || source != current.Plan.Room.Map.Position ||
            !IsHarlemRidableRoom() || operation is < 1 or > 3)
            return Result(DungeonRunOutcome.Rejected);

        // 本版四个巴古尔模板共用原生骑乘类。316上报资源编号，不是服务端分配的ObjectKey。
        var candidates = current.Plan.Monsters.Where(monster => monster.MonsterId == resourceId &&
            monster.MonsterId is 107010392 or 107012115 or 107012117 or 107012119 &&
            monster.IsFriendly && !monster.IsBlocking && monster.AiCharacter is null &&
            !current.Dead.Contains(monster.ObjectKey) && !current.Culled.Contains(monster.ObjectKey)).ToArray();
        if (candidates.Length != 1) return Result(DungeonRunOutcome.Rejected);
        var binding = new RidableBinding(identity, candidates[0].ObjectKey, resourceId);
        if (operation == 2)
        {
            if (activeRidable == binding) return Result(DungeonRunOutcome.Duplicate);
            if (activeRidable is not null) return Result(DungeonRunOutcome.Rejected);
            Advance();
            activeRidable = binding with { Identity = identity };
        }
        else
        {
            if (activeRidable != binding) return Result(DungeonRunOutcome.Rejected);
            Advance();
        }
        return Result(DungeonRunOutcome.Accepted);
    }

    internal void CancelRidable() => activeRidable = null;

    private bool IsHarlemRidableRoom() => entry.DungeonId == 291100318 && entry.RouteQuestId == 5934 &&
        current.Plan.Room.Map is { Id: 292104867, Kind: "map" };

    private bool CanRidableWarp(DungeonPoint target) => IsHarlemRidableRoom() && activeRidable is { } binding &&
        binding.Identity == identity && current.Plan.Monsters.Any(monster => monster.ObjectKey == binding.ObjectKey &&
            monster.MonsterId == binding.ResourceId && !current.Dead.Contains(monster.ObjectKey) && !current.Culled.Contains(monster.ObjectKey)) &&
        entry.Maze.WarpMaps.Any(warp => warp.Source == current.Plan.Room.Map.Position && warp.Destination == target);
}
