using System.Collections.Immutable;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class ConsumableUseSessionTests
{
    static void DisguiseSourceQualifications(TownCatalog towns,CharacterCatalog characters,RecoveryConsumableCatalog consumables)
    {
        const int itemId = 10306815;
        That(consumables.TryGet(itemId,out var gravity),"强者之路引力恶魔COS真实资源用于持有源资格验证");
        using var f = new Fixture(towns,characters,hp:gravity,consumables:consumables,level:95);
        f.Select();
        var source = f.Session.Selected!.Inventory.Find(f.Request.Position)!;
        That(f.Session.ApplyUnlimitedDisguise(2,605,itemId,f.Service) is null,
            "单体不能以相同ID伪装成另一个资源种类");
        SetCore(source.Core with { EquipmentLockId = 1 });
        That(f.Session.ApplyUnlimitedDisguise(3,605,itemId,f.Service) is null,"安全锁源不能授权直接211");
        SetCore(source.Core with { ExpireTime = checked((int)f.Clock.GetUtcNow().ToUnixTimeSeconds()) });
        That(f.Session.ApplyUnlimitedDisguise(3,605,itemId,f.Service) is null,"源在期限等号过期即拒绝");
        SetCore(source.Core);
        var restricted = gravity with { Places = ["[dungeon]"] };
        var restrictedService = new DfoServer.Game.Inventory.Services.InventoryConsumableUseService(new([restricted]),f.Clock);
        That(f.Session.ApplyUnlimitedDisguise(3,605,itemId,restrictedService) is null,
            "无限单体仍校验地点，不能绕过资源使用规则");
        That(f.Session.ApplyUnlimitedDisguise(3,605,itemId,f.Service) is not null,
            "解除源限制后可按真实单体creature声明激活");
        That(f.Session.Selected!.Inventory.Find(f.Request.Position) == source,
            "直接211没有扣除、替换源或改动完整核心");
        long now = f.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        That(DfoServer.Game.Inventory.Services.DisguiseUpgradeLuckPolicy.ActiveBonus(f.Session.Selected!,consumables,now) is
            { Weight:100,UntilMilliseconds:var until } && until == now + 300000 &&
            DfoServer.Game.Inventory.Services.DisguiseUpgradeLuckPolicy.ActiveBonus(f.Session.Selected!,consumables,until).Weight == 0,
            "creature类型的强者之路COS也由共用幸运消费者读取原声明，原截止等号失效");

        void SetCore(DfoServer.Game.Inventory.Models.ItemCore core) => That(f.Session.MutateInventory(c =>
            new(true,0,new(c.Inventory,c.Inventory with { Items = c.Inventory.Items.Select(row => row.Uid == source.Uid
                ? row with { Core = core } : row).ToImmutableArray() },"disguise_source_probe"))).Accepted,"更新真实源资格");
    }
}
