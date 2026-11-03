using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

// 只记录正式房间原生召唤的身份，不参与静态怪物、奖励、清房或通关集合。
internal readonly record struct FiendwarDynamicSpawn(ushort ParentKey, int MonsterId, ushort ObjectKey);

internal sealed partial class SinglePlayerDungeonRun
{
    internal FiendwarObjectKeyLeases? FiendwarObjectKeys { get; private set; }
    internal void BindFiendwarObjectKeys(FiendwarObjectKeyLeases keys)
    {
        if (!(entry.RaidEntry?.Kind == RaidKind.Fiendwar || IsEvildomWarChallengeDynamic) || Phase != SinglePlayerDungeonPhase.Loading ||
            keys.Run != identity.InstanceId || FiendwarObjectKeys is not null)
            throw new InvalidOperationException("漩涡对象键账本只能绑定原入场准备");
        FiendwarObjectKeys = keys;
    }
    internal bool CanLeaseFiendwarObjectKey(ushort key) => key is > 0 and < ushort.MaxValue &&
        key != entry.CharacterId && !fiendwarDynamicActors.ContainsKey(key) &&
        allRooms.All(room => room.Plan.Monsters.All(monster => monster.ObjectKey != key) &&
            room.Plan.SpecialObjects.All(actor => actor.ObjectKey != key) && room.Plan.HellWaves.All(actor => actor.ObjectKey != key));

    sealed record FiendwarDynamicActor(FiendwarDynamicSpawn Spawn, DungeonRunIdentity Identity, DungeonMap Map, bool Retired = false);
    readonly Dictionary<ushort, FiendwarDynamicActor> fiendwarDynamicActors = [];
    readonly Dictionary<(DungeonRunIdentity Identity, DungeonMap Map, ushort Parent, int Monster, ushort Object), ushort> fiendwarDynamicActive = [];

    // 调用方沿原Run的owner/队伍门禁串行使用；跨成员须先全部检查，再安装各自Run和Ground占位。
    internal DungeonRunOutcome CanRegisterFiendwarDynamic(DungeonRunIdentity expected, FiendwarDynamicSpawn spawn)
    {
        if (!FiendwarDynamicRoomReady(expected) || spawn.ParentKey == 0 || spawn.ObjectKey is 0 or ushort.MaxValue ||
            spawn.ParentKey == spawn.ObjectKey || spawn.ObjectKey == entry.CharacterId ||
            current.Plan.Monsters.SingleOrDefault(monster => monster.ObjectKey == spawn.ParentKey &&
                monster.AiCharacter is null && monster.CanReportDeath && !monster.IsSpecialChild &&
                !current.Dead.Contains(monster.ObjectKey) && !current.Culled.Contains(monster.ObjectKey)) is not { } parent ||
            !AllowedFiendwarChild(parent.MonsterId, spawn.MonsterId, parent.SourceOrdinal))
            return DungeonRunOutcome.Rejected;

        var map = current.Plan.Room.Map;
        if (fiendwarDynamicActors.TryGetValue(spawn.ObjectKey, out var prior))
            return SameFiendwarActorIdentity(prior.Identity, expected) && prior.Map == map && prior.Spawn == spawn
                ? DungeonRunOutcome.Duplicate : DungeonRunOutcome.Rejected;

        // 原客户端key只有16位；本Run保留全部退休键、不复用，因此总留存天然不超过65535。
        // 不限制战斗轮数：核心Groggy_End会重新开启四分体Proc，任意小轮数会截断正常机制。
        if (fiendwarDynamicActors.Count >= ushort.MaxValue ||
            allRooms.Any(room => room.Plan.Monsters.Any(monster => monster.ObjectKey == spawn.ObjectKey) ||
                room.Plan.SpecialObjects.Any(actor => actor.ObjectKey == spawn.ObjectKey) ||
                room.Plan.HellWaves.Any(actor => actor.ObjectKey == spawn.ObjectKey)) ||
            fiendwarDynamicActive.ContainsKey(FiendwarDynamicSlot(expected, map, spawn)))
            return DungeonRunOutcome.Rejected;
        return DungeonRunOutcome.Accepted;
    }

    internal DungeonRunOutcome RegisterFiendwarDynamic(DungeonRunIdentity expected, FiendwarDynamicSpawn spawn)
    {
        var result = CanRegisterFiendwarDynamic(expected, spawn);
        if (result != DungeonRunOutcome.Accepted) return result;
        var map = current.Plan.Room.Map;
        fiendwarDynamicActors.Add(spawn.ObjectKey, new(spawn, expected, map));
        fiendwarDynamicActive.Add(FiendwarDynamicSlot(expected, map, spawn), spawn.ObjectKey);
        return DungeonRunOutcome.Accepted;
    }

    // includeRetired仅供原NativeAuthority对已签发死亡重放核验；不让墓碑重新成为存活对象。
    internal FiendwarDynamicSpawn? FindCurrentFiendwarDynamic(DungeonRunIdentity expected, ushort objectKey,
        bool includeRetired = false) => FiendwarDynamicRoomReady(expected) &&
        fiendwarDynamicActors.TryGetValue(objectKey, out var actor) && SameFiendwarActorIdentity(actor.Identity, expected) &&
        actor.Map == current.Plan.Room.Map &&
        (includeRetired || !actor.Retired) ? actor.Spawn : null;

    internal DungeonRunOutcome CanRetireCurrentFiendwarDynamic(ConfirmedDungeonMonsterDeath death)
    {
        ArgumentNullException.ThrowIfNull(death);
        if (!ReferenceEquals(death.Authority, authority) || !FiendwarDynamicRoomReady(death.Identity) ||
            death.EventId == Guid.Empty || death.CombatSequence <= 0 ||
            !fiendwarDynamicActors.TryGetValue(death.MonsterKey, out var actor) || !SameFiendwarActorIdentity(actor.Identity, death.Identity) ||
            actor.Map != current.Plan.Room.Map)
            return DungeonRunOutcome.Rejected;
        if (actor.Retired) return DungeonRunOutcome.Duplicate;
        var slot = FiendwarDynamicSlot(actor.Identity, actor.Map, actor.Spawn);
        if (!fiendwarDynamicActive.TryGetValue(slot, out ushort active) || active != death.MonsterKey)
            return DungeonRunOutcome.Rejected;
        return DungeonRunOutcome.Accepted;
    }

    internal DungeonRunOutcome RetireCurrentFiendwarDynamic(ConfirmedDungeonMonsterDeath death)
    {
        var outcome = CanRetireCurrentFiendwarDynamic(death);
        if (outcome != DungeonRunOutcome.Accepted) return outcome;
        var actor = fiendwarDynamicActors[death.MonsterKey];
        fiendwarDynamicActors[death.MonsterKey] = actor with { Retired = true };
        fiendwarDynamicActive.Remove(FiendwarDynamicSlot(actor.Identity, actor.Map, actor.Spawn));
        return DungeonRunOutcome.Accepted;
    }

    bool FiendwarDynamicRoomReady(DungeonRunIdentity expected) => Matches(expected) &&
        Phase == SinglePlayerDungeonPhase.Dungeon && current.Loaded && !HasPendingDeath && !HasPendingSettlement &&
        FiendwarObjectKeys?.RecoveryRequired != true &&
        (IsEvildomWarChallengeDynamic || entry.RaidEntry is { Kind: RaidKind.Fiendwar } &&
            (entry.DungeonId is >= 291100339 and <= 291100364 or >= 5350 and <= 5358));

    internal bool IsEvildomWarChallengeDynamic => entry.DungeonId == EvildomWarChallengeEntryPolicy.DungeonId &&
        entry.Difficulty == 2 && entry.IsOrdinary && entry.RaidEntry is null && entry.PartyMemberCount == 4 &&
        entry.IndividualMapMovement && entry.DisableFatigueConsumption && entry.DisableMonsterAndClearExperience &&
        !entry.IsPractice && !entry.IsClassExperience && !entry.IsQuestReplay && entry.HellParty is null;

    bool SameFiendwarActorIdentity(DungeonRunIdentity first, DungeonRunIdentity second) => first == second ||
        IsEvildomWarChallengeDynamic && first == (second with { Epoch = first.Epoch });

    bool AllowedFiendwarChild(int parent, int child, int sourceOrdinal) => entry.DungeonId switch
    {
        >= 291100339 and <= 291100364 =>
            parent == 107013177 && child is >= 107013169 and <= 107013172 ||
            parent == 107013178 && child == 107013188 ||
            parent == 107013154 && child is 107013155 or 107013156 ||
            parent == 107013148 && child is >= 107013173 and <= 107013176,
        >= 5350 and <= 5358 =>
            parent == 109008128 && child is >= 109008120 and <= 109008123 ||
            parent == 109008129 && child == 109008139 ||
            parent == 109008113 && child is 109008114 or 109008115 ||
            // 引导Howling及Normal原ACT明确复用团队幻影，不按编号前缀推定跨模式。
            parent == 109008107 && child is >= 107013173 and <= 107013176,
        EvildomWarChallengeEntryPolicy.DungeonId => IsEvildomWarChallengeDynamic &&
            current.Plan.Room.EvildomWarChallengeSpawn is { } evidence && evidence.ParentId == parent &&
            evidence.ChildId == child && evidence.ParentOrdinal == sourceOrdinal &&
            sourceOrdinal >= 0 && sourceOrdinal < current.Plan.Room.Monsters.Count &&
            current.Plan.Room.Monsters[sourceOrdinal].Path == evidence.ParentPath &&
            evidence.ParentPath == DungeonEvildomWarChallengeWarpRules.Root + (parent == 407000556 ? "sarpoza.mob" : "sarpoza_seal_dummy1.mob") &&
            evidence.ChildPath == DungeonEvildomWarChallengeWarpRules.Root + (parent == 407000556 ? "sarpoza_seal_dummy2.mob" : "sarpoza_seal.mob") &&
            evidence.ActionPath == DungeonEvildomWarChallengeWarpRules.Root + (parent == 407000556 ? "action/seal.act" : "action/proc_0.act") &&
            evidence.SingleActive == (child == 407000557) &&
            current.Plan.Room.Map.Path == DungeonEvildomWarChallengeWarpRules.MapPath(parent == 407000556) &&
            (current.Plan.Room.Map.Id == 400000524 && current.Plan.Room.Map.Position == new DungeonPoint(12, 0) &&
                parent == 407000556 && child == 407000559 ||
             current.Plan.Room.Map.Id == 400000525 && current.Plan.Room.Map.Position == new DungeonPoint(12, 1) &&
                parent == 407000558 && child == 407000557),
        _ => false
    };

    (DungeonRunIdentity Identity, DungeonMap Map, ushort Parent, int Monster, ushort Object) FiendwarDynamicSlot(
        DungeonRunIdentity identity, DungeonMap map, FiendwarDynamicSpawn spawn) =>
        // 团队与引导各自的屏障ACT均先检查全房数量为零，不是每个父对象都能各有一只。
        // 四分体保留每父每色单槽；猫和幻影无已证单模板上限，按真实租约键各自登记和退休。
        // 英雄557保留CHECKED NO0单槽，559逐key；原缓存跨房只改变请求epoch，不清活跃槽或墓碑。
        // 生成时机仍信任当前原生主机报告，模板/活父/房间/租约/非复用键由正式领域核验。
        (IsEvildomWarChallengeDynamic ? identity with { Epoch = 0 } : identity, map,
            spawn.MonsterId is 107013188 or 109008139 ? (ushort)0 : spawn.ParentKey,
            spawn.MonsterId, spawn.MonsterId is >= 107013169 and <= 107013172 or 107013188 or
                >= 109008120 and <= 109008123 or 109008139 or 407000557 ? (ushort)0 : spawn.ObjectKey);
}
