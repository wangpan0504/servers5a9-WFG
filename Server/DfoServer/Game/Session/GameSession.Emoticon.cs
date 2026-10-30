using DfoServer.Game.Chat;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Pvp;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Session;

internal sealed record EmoticonScene(ChatScene Location, SessionPhase Phase, int MapId = 0,
    DungeonPoint MapPosition = default, Guid Match = default, PvpArenaMatchPhase? MatchPhase = null);
internal sealed record EmoticonNotice(ushort MemberId, int SkinId, EmoticonScene Scene, Func<bool> Guard);

internal sealed partial class GameSession
{
    public Action<EmoticonNotice>? EmoticonArrived { private get; set; }

    EmoticonScene? CurrentEmoticonScene()
    {
        if (!Ready || Selected is not { } character) return null;
        if (Phase == SessionPhase.Town) return new(ChatSceneOf(character), Phase);
        if (Phase is SessionPhase.Dungeon or SessionPhase.Settlement && formalDungeon is { } dungeon)
        {
            var run = dungeon.Run.Scene;
            // 组队成员的回城位置可能不同；战斗可见性只取共同副本实例与当前房间。
            return new(new(ChannelEntry?.Identity ?? default, 0, 0, "", null, run.Identity.InstanceId),
                Phase, run.Map.Id, run.Map.Position);
        }
        if (Phase == SessionPhase.PvpRoom && pvpArena is { } arena)
            return new(new(ChannelEntry?.Identity ?? default, 0, 0, "", null, arena.Room.Instance),
                Phase, arena.Room.Map, Match: arena.Room.Match?.Id ?? Guid.Empty, MatchPhase: arena.Room.Match?.Phase);
        return null;
    }

    public EmoticonNotice? CaptureEmoticon(InventorySkinStorageService skins, int skinId)
    {
        try
        {
            return owner.Execute(() =>
            {
                if (Selected is not { } character || CurrentEmoticonScene() is not { } scene ||
                    !skins.CanSendEmoticon(character, skinId)) return null;
                long epoch = SelectionEpoch;
                bool Current() => owner.IsCurrent && SelectionEpoch == epoch && Selected is { } current &&
                    current.Id == character.Id && CurrentEmoticonScene() == scene && skins.CanSendEmoticon(current, skinId);
                return new EmoticonNotice(character.Id, skinId, scene, Current);
            });
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return null; }
    }

    internal bool TryDeliverEmoticon(EmoticonNotice notice)
    {
        Action<EmoticonNotice>? delivery;
        EmoticonNotice? guarded = null;
        try
        {
            delivery = owner.Execute(() =>
            {
                if (!notice.Guard() || Selected is not { } character || CurrentEmoticonScene() != notice.Scene)
                    return null;
                long epoch = SelectionEpoch;
                bool Current() => owner.IsCurrent && SelectionEpoch == epoch && Selected?.Id == character.Id &&
                    CurrentEmoticonScene() == notice.Scene && notice.Guard();
                guarded = notice with { Guard = Current };
                return EmoticonArrived;
            });
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return false; }
        if (delivery is null || guarded is null) return false;
        // 回调只入本连接发送队列，不持账号锁等待网络，也不获取源账号锁。
        delivery(guarded);
        return true;
    }
}
