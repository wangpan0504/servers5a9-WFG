using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    internal sealed record HelperSafetyReceipt(Character Character, long Epoch, long Generation, long PhaseEpoch,
        HelperSafetyPolicy.Candidate[] Candidates);

    sealed record HelperAssetSafety(ushort CharacterId, long Epoch, long Generation, long PhaseEpoch, long CreatedAt,
        Dictionary<InventorySlot, HelperSafetyPolicy.Candidate> Items)
    {
        public HashSet<long> Consumed { get; } = [];
    }
    HelperAssetSafety? helperAssetSafety;
    long helperAssetPhaseEpoch;
    static readonly TimeSpan HelperAssetSafetyLifetime = TimeSpan.FromMinutes(5);

    void InvalidateHelperAssetSafety()
    {
        helperAssetSafety = null;
        helperAssetPhaseEpoch++;
    }

    internal HelperSafetyReceipt? ReadHelperSafety(ushort characterId, SkillLearningCatalog? learning = null)
    {
        var capture = owner.Execute(() => Ready && Selected is { } character && character.Id == characterId &&
            Phase == SessionPhase.Town && items is not null && !HasPendingTownQuestCommit && pendingRental is null
                ? new HelperSafetyReceipt(character, SelectionEpoch, owner.Generation, helperAssetPhaseEpoch, []) : null);
        if (capture is null || items is null) return null;
        // 目录解析及整包资格计算在owner锁外；发送及安装资格时重新核对原角色快照和阶段。
        var receipt = capture with { Candidates = HelperSafetyPolicy.Read(capture.Character, items, learning is null ? null : learning.Get) };
        var safety = new HelperAssetSafety(capture.Character.Id, capture.Epoch, capture.Generation, capture.PhaseEpoch,
            (timeProvider ?? TimeProvider.System).GetTimestamp(), receipt.Candidates
                .Where(row => row.Operations != 0 && row.Item.Uid > 0)
                .ToDictionary(row => row.Item.Position));
        return owner.Execute(() =>
        {
            if (!HelperSafetyReceiptIsCurrent(receipt) || HasPendingTownQuestCommit || pendingRental is not null)
                return null;
            helperAssetSafety = safety.Items.Count == 0 ? null : safety;
            return receipt;
        });
    }

    internal bool CanPublishHelperSafety(HelperSafetyReceipt receipt) => owner.Execute(() => HelperSafetyReceiptIsCurrent(receipt));

    bool HelperSafetyReceiptIsCurrent(HelperSafetyReceipt receipt) => Ready &&
        Phase == SessionPhase.Town && ReferenceEquals(Selected, receipt.Character) &&
        SelectionEpoch == receipt.Epoch && owner.Generation == receipt.Generation && helperAssetPhaseEpoch == receipt.PhaseEpoch;

    // 仅在owner锁内读取；槽位、UID和完整实例均须保持，换装登记也以当前角色为准。
    HelperAssetSafety? FindHelperAssetSafety(InventorySlot position, byte operation) =>
        helperAssetSafety is { } safety && safety.Items.TryGetValue(position, out var candidate) &&
        (candidate.Operations & operation) != 0 ? safety : null;

    bool CanUseHelperAssetSafety(HelperAssetSafety safety, Character character, InventorySlot position, byte operation)
    {
        if (!ReferenceEquals(helperAssetSafety, safety) || !Ready || Phase != SessionPhase.Town ||
            character.Id != safety.CharacterId || SelectionEpoch != safety.Epoch || owner.Generation != safety.Generation ||
            helperAssetPhaseEpoch != safety.PhaseEpoch ||
            !safety.Items.TryGetValue(position, out var candidate) || (candidate.Operations & operation) == 0)
            return false;
        var expected = candidate.Item;
        if (safety.Consumed.Contains(expected.Uid) ||
            character.Inventory.Find(position) != expected ||
            operation == 2 && character.SkillSwitchInventory.Items.Any(binding => binding.ItemUid == expected.Uid)) return false;
        var elapsed = (timeProvider ?? TimeProvider.System).GetElapsedTime(safety.CreatedAt);
        return elapsed >= TimeSpan.Zero && elapsed < HelperAssetSafetyLifetime;
    }

    static void ConsumeHelperAssetSafety(HelperAssetSafety safety, InventorySlot position) =>
        safety.Consumed.Add(safety.Items[position].Item.Uid);
}
