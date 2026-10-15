using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Session;

internal sealed record ImageCommunicationReceipt(Character Character, QuestLifecycleCommit? Commit,
    long Generation, long SelectionEpoch, long SceneEpoch, bool Accepted, string Reason);

internal sealed partial class GameSession
{
    private readonly record struct CommunicationSource(int NpcId, ushort RequiredQuest, Guid AcceptanceId);
    private sealed record CommunicationScene(ushort CharacterId, long Generation, long SelectionEpoch,
        long SceneEpoch, byte Town, byte Area, string MapPath, ImmutableArray<CommunicationSource> Sources);
    private sealed record PendingCommunication(QuestLifecyclePlan Plan, CommunicationScene Scene);
    private CommunicationScene? communicationScene;
    private PendingCommunication? pendingCommunication;
    private long communicationSceneEpoch;

    // 场景代次独立于持久revision：正常走动及33保存不应使已付款演员失效。
    void InvalidateImageCommunication()
    {
        communicationScene = null;
        pendingCommunication = null;
        communicationSceneEpoch++;
    }

    string? CommunicationMap(Character character) => towns.Areas.TryGetValue((character.Position.Town, character.Position.Area), out var area) &&
        area.Map(QuestEligibility.Completed(character)) is { Error: null } map ? map.Path : null;

    bool SameCommunicationScene(CommunicationScene scene, Character character) => Ready && Phase == SessionPhase.Town &&
        scene.CharacterId == character.Id && scene.Generation == owner.Generation && scene.SelectionEpoch == SelectionEpoch &&
        scene.SceneEpoch == communicationSceneEpoch && scene.Town == character.Position.Town && scene.Area == character.Position.Area &&
        scene.MapPath == CommunicationMap(character);

    ImmutableArray<CommunicationSource> CommunicationSources(Character? character)
    {
        if (character is null || communicationScene is not { } scene || !SameCommunicationScene(scene, character)) return [];
        return scene.Sources.Where(source => character.ActiveMainQuests.Any(active =>
            active.QuestId == source.RequiredQuest && active.AcceptanceId == source.AcceptanceId)).ToImmutableArray();
    }

    public ImageCommunicationReceipt? UseImageCommunication(ImageCommunicationCatalog catalog, QuestCatalog quests)
    {
        var capture = owner.Execute(() => (Character: CanUseImageCommunication() ? Selected : null,
            SelectionEpoch, Scene: communicationSceneEpoch, Pending: pendingCommunication, Sources: CommunicationSources(Selected)));
        if (capture.Character is not { } expected) return null;
        var refusal = new ImageCommunicationReceipt(expected, null, owner.Generation, capture.SelectionEpoch, capture.Scene, false, "通讯场景或活动任务不符");
        if (!towns.Areas.TryGetValue((expected.Position.Town, expected.Position.Area), out var area) ||
            area.Map(QuestEligibility.Completed(expected)) is not { Error: null } map ||
            !map.Npcs.Any(npc => catalog.SourceNpcs.Contains(npc.Id))) return refusal;
        var eligible = catalog.Npcs.SelectMany(npc => expected.ActiveMainQuests.Where(active =>
            active.QuestId == npc.RequiredQuest && active.AcceptanceId != Guid.Empty)
            .Select(active => new CommunicationSource(npc.NpcId, npc.RequiredQuest, active.AcceptanceId))).ToImmutableArray();
        // 客户端只过滤活动槽是否存在，不要求require quest的scalar为1；475不携带选择的NPC。
        if (eligible.Length == 0) return refusal;
        var scene = new CommunicationScene(expected.Id, owner.Generation, capture.SelectionEpoch, capture.Scene,
            expected.Position.Town, expected.Position.Area, map.Path, eligible);
        if (capture.Pending is null && eligible.All(capture.Sources.Contains))
            return refusal with { Accepted = true, Reason = "原场景已付款通讯来源，无重复扣费" };
        QuestLifecyclePlan plan;
        if (capture.Pending is { } pending)
        {
            if (!ReferenceEquals(pending.Plan.Expected, expected) || !SameCommunicationScene(pending.Scene, expected) || !pending.Scene.Sources.SequenceEqual(eligible)) return refusal;
            // 原提交结果丢失时先按原operation查receipt，不能依据旧余额再扣一次。
            plan = pending.Plan; scene = pending.Scene;
        }
        else
        {
            try { plan = QuestLifecyclePlan.PayImageCommunication(expected, eligible[0].RequiredQuest, eligible[0].AcceptanceId,
                catalog.Charge, quests, DateTimeOffset.UtcNow); }
            catch (InvalidDataException error) { return refusal with { Reason = error.Message }; }
        }
        return owner.Execute(() =>
        {
            if (!CanUseImageCommunication() || !ReferenceEquals(pendingCommunication, capture.Pending) ||
                !SameCommunicationScene(scene, expected) || !ReferenceEquals(Selected, expected))
                throw new InvalidOperationException("通讯规划的原选角或场景已失效");
            pendingCommunication = new(plan, scene);
            var commit = repository.CommitQuestLifecycle(owner, plan);
            if (commit.ProjectionCurrent)
            {
                Selected = commit.Character;
                communicationScene = scene;
                pendingCommunication = null;
            }
            return refusal with { Character = commit.Character, Commit = commit, Accepted = true, Reason = "通讯费用与任务库存同事务提交" };
        });
    }

    // 原付款可恢复；这里只排斥其它未决事务，不把自己的pending加入循环门禁。
    bool CanUseImageCommunication() => pendingRental is null && Ready && Phase == SessionPhase.Town && Selected is not null &&
        pendingQuestCompletion is null && pendingQuestLifecycle is null && pendingQuestQuickClear is null && !HasPendingQuestScene &&
        !HasPendingFormalCard && towns.IsPosition(Selected.Position, QuestEligibility.Completed(Selected));

    public bool CanPublishImageCommunication(ImageCommunicationReceipt receipt) => owner.Execute(() => Ready &&
        Phase == SessionPhase.Town && owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch &&
        communicationSceneEpoch == receipt.SceneEpoch && ReferenceEquals(Selected, receipt.Character) &&
        (receipt.Commit?.ProjectionCurrent ?? true) && (!receipt.Accepted ||
            communicationScene is { } scene && SameCommunicationScene(scene, receipt.Character)));
}
