using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryDisjointService(ItemCatalog catalog, DisjointCatalog disjoint, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, Random? random = null,
    ExpertJobScriptCatalog? expertJobs = null)
{
    public InventoryResult Disjoint(Character character, DisjointRequest request, out DisjointReceipt? receipt, DateTimeOffset? utcNow = null)
    {
        receipt = null;
        var before = character.Inventory;
        var now = utcNow ?? DateTimeOffset.UtcNow;
        var source = before.Find(request.Position);
        if (request.Position.List != InventoryListType.Main || request.ToolSlot < -1 || source is null)
            return InventoryResult.Reject(17);
        if (source.Core.Kind != ItemKind.Equipment || source.Core.EquipmentLockId != 0 || source.Core.IsExpired(now.ToUnixTimeSeconds()))
            return InventoryResult.Reject(19);
        var template = catalog.Get(source.Core.ItemId);
        if (template.DisjointForbidden || template.AttachType.Contains("[trade delete]", StringComparison.OrdinalIgnoreCase) ||
            (source.Core.AmplifyType & 0x80) != 0)
            return InventoryResult.Reject(19);
        if (disjoint.PieceResults.TryGetValue(template.Id, out int pieceId) &&
            (!pieces.ByPiece.TryGetValue(pieceId, out var piece) || piece.EquipmentId != template.Id))
            return InventoryResult.Reject(19);
        InventoryItem? tool = null;
        if (request.ToolSlot != -1)
        {
            tool = before.Find(new(InventoryListType.Main, request.ToolSlot));
            if (tool is null || !tool.Core.IsStackable || tool.Core.EquipmentLockId != 0 || tool.Core.IsExpired(now.ToUnixTimeSeconds()) ||
                template.MinimumLevel > catalog.Get(tool.Core.ItemId).PortableDisjointLevel)
                return InventoryResult.Reject(19);
        }
        InventoryReward[] rewards;
        try
        {
            rewards = character.ExpertJobType == 3 && expertJobs is not null
                ? DfoServer.Game.ExpertJob.ExpertJobCraft.DisjointProducts(character, expertJobs, template, source.Core)
                : DisjointResultCalculator.Calculate(disjoint, template, random ?? Random.Shared);
        }
        catch (OverflowException) { return InventoryResult.Reject(4); }
        if (rewards.Length == 0)
            return InventoryResult.Reject(19);
        // 先释放输入，再按真实产物规划空间；分解不能沿普通赠送的封顶策略吞掉超限产物。
        var paid = InventoryDeleteService.Plan(before, source.Position, source.Core.Value, 1).Plan!.After;
        if (tool is not null)
            paid = InventoryDeleteService.Plan(paid, tool.Position, tool.Core.Value, 1).Plan!.After;
        foreach (var reward in rewards)
            if (CurrencyWallet.RewardKind(reward.ItemId) is { } kind && (long)CurrencyWallet.Get(paid, kind) + reward.Count > int.MaxValue)
                return InventoryResult.Reject(4);
        InventoryResult grant;
        try { grant = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = paid }, rewards, catalog, goldLimits, pieces, now); }
        catch (InvalidDataException) { return InventoryResult.Reject(19); }
        catch (OverflowException) { return InventoryResult.Reject(4); }
        if (!grant.Accepted)
            return InventoryResult.Reject(grant.Error == 19 ? (ushort)19 : (ushort)4);
        var after = grant.Plan!.After;
        var received = new List<DisjointReward>();
        foreach (var reward in rewards)
        {
            if (pieces.ByPiece.ContainsKey(reward.ItemId))
                received.Add(new(-1, reward.ItemId, reward.Count));
            else if (CurrencyWallet.RewardKind(reward.ItemId) is { } kind)
                received.Add(new(ItemSlotBoundService.CurrencySlot(kind) ?? throw new InvalidDataException("分解回执不支持非Main钱包"), reward.ItemId, reward.Count));
            else
                foreach (var item in after.Items.Where(item => item.Core.ItemId == reward.ItemId && item.Position.List == InventoryListType.Main))
                {
                    int increment = item.Core.Count - (paid.Find(item.Position)?.Core.Count ?? 0);
                    if (increment > 0)
                        received.Add(new(item.Position.Index, reward.ItemId, increment));
                }
        }
        if (received.Count > byte.MaxValue)
            return InventoryResult.Reject(4);
        receipt = new(request, received.ToArray());
        return new(true, 0, new(before, after, "equipment_disjoint"));
    }
}
