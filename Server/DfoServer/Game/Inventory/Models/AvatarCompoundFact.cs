using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Models;

// 保留原两装扮实例、材料及已抽定产物；writer只为新详情实例补共享UID。
internal sealed record AvatarCompoundFact(Guid Id, DateTimeOffset PlannedAt, TimeProvider Clock,
    AvatarCompoundRequest Request, AvatarCompoundReceipt Receipt, InventoryItem Source0,
    InventoryItem Source1, InventoryItem Material, InventorySnapshot Before, InventorySnapshot Paid,
    InventorySnapshot After)
{
    public bool Matches(InventoryPlan plan, InventorySnapshot inventory)
    {
        if (Id == Guid.Empty || plan.Reason != "avatar_compound" ||
            !ReferenceEquals(plan.Before, Before) || !ReferenceEquals(plan.After, After) ||
            Source0.Uid <= 0 || Source1.Uid <= 0 || Source0.Uid == Source1.Uid || Material.Uid <= 0 ||
            Source0.Position != Request.Source0 || Source1.Position != Request.Source1 ||
            Source0.Position == Source1.Position || Material.Position != Request.Material ||
            Source0.Position.List != InventoryListType.Avatar || Source1.Position.List != InventoryListType.Avatar ||
            Source0.Core.Kind != ItemKind.Avatar || Source1.Core.Kind != ItemKind.Avatar ||
            Source0.Avatar is null || Source1.Avatar is null || Source0.Core.ItemId != Request.SourceItemId0 ||
            Source1.Core.ItemId != Request.SourceItemId1 || Material.Position.List != InventoryListType.Main ||
            !Material.Core.IsStackable || Material.Core.Count < 1 ||
            plan.BeforeIndex.Find(Source0.Position) != Source0 || plan.BeforeIndex.Find(Source1.Position) != Source1 ||
            plan.BeforeIndex.Find(Material.Position) != Material || Receipt.MaterialItemId != Material.Core.ItemId ||
            Receipt.MaterialType != Request.MaterialType || Request.TargetItemId <= 1 || Request.TargetIsRare != 0 ||
            Receipt.Part.Length == 0 || Receipt.RareRate is < 0 or > 10000 || Receipt.RareRoll is < 0 or >= 10000 ||
            Receipt.HitRare && Receipt.RareRoll >= Receipt.RareRate || Receipt.Outputs.IsDefaultOrEmpty ||
            Receipt.Outputs.Length != (Receipt.HitRare ? 2 : 1) ||
            Receipt.Outputs[0].ItemId != Request.TargetItemId ||
            Receipt.Outputs.Select(row => row.Position).Distinct().Count() != Receipt.Outputs.Length) return false;
        var paidItems = Before.Items.ToBuilder();
        if (!paidItems.Remove(Source0) || !paidItems.Remove(Source1)) return false;
        int materialIndex = paidItems.IndexOf(Material); if (materialIndex < 0) return false;
        if (Material.Core.Count == 1) paidItems.RemoveAt(materialIndex);
        else paidItems[materialIndex] = Material with { Core = Material.Core with { Value = Material.Core.Value - 1 } };
        if (!Paid.Items.SequenceEqual(paidItems) || (Paid with { Items = Before.Items }) != Before ||
            (After with { Items = Paid.Items }) != Paid || After.Items.Length != Paid.Items.Length + Receipt.Outputs.Length)
            return false;
        var paidIndex = new InventoryIndex(Paid);
        var afterIndex = new InventoryIndex(After);
        if (Paid.Items.Any(item => afterIndex.Find(item.Position) != item)) return false;
        foreach (var output in Receipt.Outputs)
        {
            var item = afterIndex.Find(output.Position);
            if (output.Position.List != InventoryListType.Avatar || paidIndex.Find(output.Position) is not null ||
                item is not { Uid: 0, Avatar: not null, Creature: null } || item.Core.Kind != ItemKind.Avatar ||
                item.Core.ItemId != output.ItemId || item.Core.Durability != Request.Ability) return false;
        }
        if (inventory.Items.Length != After.Items.Length) return false;
        var actual = new InventoryIndex(inventory);
        foreach (var expected in After.Items)
        {
            var current = actual.Find(expected.Position);
            if (current is null || current.Uid <= 0 || expected.Uid > 0 && current.Uid != expected.Uid ||
                expected.Uid == 0 && plan.BeforeIndex.FindUid(current.Uid) is not null) return false;
            var normalized = expected.Uid == 0 && (expected.Avatar is not null || expected.Creature is not null)
                ? expected with { Uid = current.Uid, Core = expected.Core with { Value = checked((int)current.Uid) } }
                : expected;
            if (current != normalized) return false;
        }
        return true;
    }
}
