using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

// 复用正式资源入口的同一ItemCatalog，不预填或伪造资源模板。
internal static class AvatarEmblemQualificationResourceTests
{
    const int SwordTarget = 42609;
    const int FighterTarget = 46609;
    const int SwordMaterial = 2540007;
    const int FighterMaterial = 2540028;
    const int Now = 1_800_000_000;
    static readonly InventorySlot TargetSlot = new(InventoryListType.Avatar, 7);
    static readonly InventorySlot MaterialSlot = new(InventoryListType.Main, 289);

    public static void Run(ItemCatalog items)
    {
        int checks = Count;
        foreach (var (_, run) in Cases(items)) run();
        Console.WriteLine($"PASS {Count - checks} 装扮徽章真实资源资格与错误码断言");
    }

    static IEnumerable<(string Name, Action Run)> Cases(ItemCatalog items)
    {
        var clock = new FixedClock();
        var service = new InventoryAvatarEmblemService(items, clock);
        var swordTarget = items.Get(SwordTarget);
        var fighterTarget = items.Get(FighterTarget);
        var swordMaterial = items.Get(SwordMaterial);
        var fighterMaterial = items.Get(FighterMaterial);
        // 输入资格绑定已保存身份；这里只确认真实Get没有换资源，不以Jobs代替生产材料L1。
        RequireResource(swordTarget.Kind == ItemKind.Avatar && swordTarget.Jobs == 513 &&
            fighterTarget.Kind == ItemKind.Avatar && fighterTarget.Jobs == 2,
            "saved target identities/flags differ from the matching archive");
        foreach (var template in new[] { swordMaterial, fighterMaterial })
            RequireResource(template.Kind == ItemKind.Emblem && template.EmblemTargetSockets == 16 &&
                template.Jobs == ushort.MaxValue,
                "saved platinum material identity/mask/usable-job differs");

        yield return ("sword target + sword material accepts independently of current job", () =>
            Positive(service, Role(swordTarget, swordMaterial, clock) with { Job = 15 }, SwordMaterial));
        yield return ("fighter target + fighter material accepts independently of current job", () =>
            Positive(service, Role(fighterTarget, fighterMaterial, clock) with { Job = 15 }, FighterMaterial));
        yield return ("sword target + fighter material rejects despite current fighter job", () =>
        {
            var role = Role(swordTarget, fighterMaterial, clock) with { Job = 1 };
            Denied(service, role, Request(role), 1);
        });
        yield return ("fighter target + sword material rejects despite current sword job", () =>
        {
            var role = Role(fighterTarget, swordMaterial, clock) with { Job = 0 };
            Denied(service, role, Request(role), 1);
        });
        yield return ("same-slot aggregate shortage returns17 and no partial plan", () =>
        {
            var role = Role(swordTarget, swordMaterial, clock, 1);
            // 显式实例边界变体：只把第二个物理孔改S以到达真实数量分支。
            // 不声称原42609资源有双S孔，也不改模板或材料mask/资格。
            var target = role.Inventory.Find(TargetSlot)!;
            role = Replace(role, target with { Avatar = target.Avatar! with { Socket1 = new(16, -1) } });
            Denied(service, role, Request(role, 0, 1), 17);
        });
        yield return ("target equipment lock returns214", () =>
        {
            var role = Role(swordTarget, swordMaterial, clock);
            var target = role.Inventory.Find(TargetSlot)!;
            role = Replace(role, target with { Core = target.Core with { EquipmentLockId = 1 } });
            Denied(service, role, Request(role), 214);
        });
        yield return ("material equipment lock returns214", () =>
        {
            var role = Role(swordTarget, swordMaterial, clock);
            var material = role.Inventory.Find(MaterialSlot)!;
            role = Replace(role, material with { Core = material.Core with { EquipmentLockId = 1 } });
            Denied(service, role, Request(role), 214);
        });
        yield return ("all five instance socket types zero returns4", () =>
        {
            var role = Role(swordTarget, swordMaterial, clock);
            var target = role.Inventory.Find(TargetSlot)!;
            // 真实Avatar实例的无孔状态（Get/CreateItem默认即全零）；不以非法容器/假模板冒充。
            role = Replace(role, target with { Avatar = target.Avatar! with
                { Socket0 = default, Socket1 = default, Socket2 = default, Socket3 = default, Socket4 = default } });
            Denied(service, role, Request(role), 4);
        });
        yield return ("target template mismatch uses generic1", () =>
        {
            var role = Role(swordTarget, swordMaterial, clock);
            Denied(service, role, Request(role) with { TargetItemId = FighterTarget }, 1);
        });
        yield return ("material template mismatch uses generic1", () =>
        {
            var role = Role(swordTarget, swordMaterial, clock);
            Denied(service, role, Request(role) with { Emblems = [new(289, FighterMaterial, 0)] }, 1);
        });
        yield return ("duplicate physical socket uses generic1", () =>
        {
            var role = Role(swordTarget, swordMaterial, clock);
            Denied(service, role, Request(role, 0, 0), 1);
        });
        yield return ("S material on existing C socket uses generic1", () =>
        {
            var role = Role(swordTarget, swordMaterial, clock);
            Denied(service, role, Request(role, 1), 1);
        });
        yield return ("unsupported target container uses generic1", () =>
        {
            var role = Role(swordTarget, swordMaterial, clock);
            Denied(service, role, Request(role) with { Target = new(InventoryListType.PersonalCargo, 7) }, 1);
        });
        yield return ("expired target uses generic1", () =>
        {
            var role = Role(swordTarget, swordMaterial, clock);
            var target = role.Inventory.Find(TargetSlot)!;
            role = Replace(role, target with { Avatar = target.Avatar! with { ExpireTime = Now } });
            Denied(service, role, Request(role), 1);
        });
        yield return ("expired material uses generic1", () =>
        {
            var role = Role(swordTarget, swordMaterial, clock);
            var material = role.Inventory.Find(MaterialSlot)!;
            role = Replace(role, material with { Core = material.Core with { ExpireTime = Now } });
            Denied(service, role, Request(role), 1);
        });
    }

    static void Positive(InventoryAvatarEmblemService service, Character role, int materialId)
    {
        var target = role.Inventory.Find(TargetSlot)!;
        var used = UseUnchanged(service, role, Request(role));
        Check(used.Result.Accepted && used.Result.Error == 0 && used.Result.Plan is not null &&
            used.Receipt is not null && used.Rejection is null,
            "legal pair must produce the real inventory plan/receipt; got " + Describe(used));
        var plan = used.Result.Plan!;
        var changed = plan.After.Find(TargetSlot)!;
        Check(ReferenceEquals(plan.Before, role.Inventory) && plan.Reason == "avatar_emblem_use" &&
            changed.Uid == target.Uid && changed.Position == target.Position && changed.Core == target.Core &&
            changed.Avatar == (target.Avatar! with { Socket0 = new(16, materialId) }) &&
            plan.After.Find(MaterialSlot)?.Core.Count == 1 &&
            used.Receipt!.Target == TargetSlot && used.Receipt.Materials.SequenceEqual([new AvatarEmblemRemaining(289, 1)]),
            "only selected S value changes; preserve UID/core/other detail, consume one, return absolute remainder1");
    }

    static void Denied(InventoryAvatarEmblemService service, Character role, AvatarEmblemRequest request, ushort error)
    {
        var used = UseUnchanged(service, role, request);
        Check(!used.Result.Accepted && used.Result.Error == error && used.Result.Plan is null && used.Receipt is null,
            "expected atomic rejection error=" + error + "; got " + Describe(used));
    }

    static (InventoryResult Result, AvatarEmblemReceipt? Receipt, string? Rejection) UseUnchanged(
        InventoryAvatarEmblemService service, Character role, AvatarEmblemRequest request)
    {
        var inventory = role.Inventory;
        var originalItems = inventory.Items.ToArray();
        string before = JsonSerializer.Serialize(inventory);
        long revision = role.Revision;
        try
        {
            var result = service.Use(role, request, out var receipt, out var rejection);
            return (result, receipt, rejection);
        }
        finally
        {
            Check(ReferenceEquals(role.Inventory, inventory) && role.Revision == revision &&
                inventory.Items.SequenceEqual(originalItems) && JsonSerializer.Serialize(inventory) == before,
                "service must preserve every input inventory field/revision even on a rejected pair or exception");
        }
    }

    static Character Role(ItemTemplate target, ItemTemplate material, TimeProvider clock, int count = 2)
    {
        var avatar = InventoryRewardGrantService.CreateItem(target, TargetSlot,
            InventoryRewardGrantService.Create(target, utcNow: clock.GetUtcNow()) with
                { Value = 1001, Attr = 3, Durability = 3 }) with { Uid = 1001 };
        // 两目标保存[avatar type select]的永久条目均为S/C/C。该选择随获得实例保存，
        // 普通coat的Get().AvatarSockets仅解析default声明，所以不能声称它默认生成这三孔。
        avatar = avatar with { Avatar = avatar.Avatar! with
            { Socket0 = new(16, -1), Socket1 = new(4, -1), Socket2 = new(4, -1),
              Socket3 = default, Socket4 = default,
              ClearAvatarId = 123, ClearAvatarSourceUid = 987, Color1 = 1, Color2 = 2 } };
        var stack = InventoryRewardGrantService.CreateItem(material, MaterialSlot,
            InventoryRewardGrantService.Create(material, count, clock.GetUtcNow())) with { Uid = 2001 };
        var role = new Character(1, 0, "C201MR278", 7, new(1, 0, 10, 10), null)
            { Level = 95, Inventory = new([avatar, stack], Gold: 4321) };
        role.Inventory.Validate();
        return role;
    }

    static AvatarEmblemRequest Request(Character role, params byte[] sockets)
    {
        if (sockets.Length == 0) sockets = [0];
        return new(TargetSlot, role.Inventory.Find(TargetSlot)!.Core.ItemId,
            [.. sockets.Select(index => new AvatarEmblemEntry(MaterialSlot.Index,
                role.Inventory.Find(MaterialSlot)!.Core.ItemId, index))]);
    }

    static Character Replace(Character role, InventoryItem changed) => role with
        { Inventory = role.Inventory with { Items = role.Inventory.Items.Replace(role.Inventory.Find(changed.Position)!, changed) } };

    static string Describe((InventoryResult Result, AvatarEmblemReceipt? Receipt, string? Rejection) used) =>
        $"accepted={used.Result.Accepted}, error={used.Result.Error}, plan={used.Result.Plan is not null}, " +
        $"receipt={used.Receipt is not null}, reason={used.Rejection}";

    static void Check(bool condition, string message)
    {
        That(condition, message);
    }

    static void RequireResource(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException("C201_MR278_INPUT_MISMATCH " + message);
    }

    sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Now);
    }
}
