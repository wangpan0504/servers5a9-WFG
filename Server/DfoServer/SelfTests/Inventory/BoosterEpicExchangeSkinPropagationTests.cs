using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class BoosterEpicExchangeSkinPropagationTests
{
    public static void RunResources(TownCatalog towns, CharacterCatalog characters, ItemResources resources)
    {
        var recipe = resources.Skins.Recipes.GetValueOrDefault(2);
        That(recipe is { ResultItemId: 10308800, Materials: [{ ItemId: 490704411, Count: 1 }] } &&
            resources.Skins.Weapons.ContainsKey(32000) && resources.Skins.Token(10308800) is { Type: 4 },
            "外观传播前置沿用已有真实配方、武器和模具身份，不猜ID");
        var weaponTemplate = resources.Items.Get(32000);
        var tokenTemplate = resources.Items.Get(recipe!.ResultItemId);
        That(weaponTemplate is { Kind: ItemKind.Equipment, EquipmentSlot: 12 } &&
            tokenTemplate.Kind == ItemKind.Consumable,
            "合法装备销毁并生成新Consumable资产的真实模板前置");
        var time = new OriginTime(DateTimeOffset.UtcNow);
        var service = new InventorySkinStorageService(resources.Items, resources.Skins, resources.GoldLimits,
            resources.EpicPieces, resources.Contracts, time);
        var now = service.Clock.GetUtcNow();
        var weapon = new InventoryItem(101, new(InventoryListType.Main, 35),
            InventoryRewardGrantService.Create(weaponTemplate, utcNow: now) with { SealFlag = 0 });
        var material = new InventoryItem(102, new(InventoryListType.Main, 121),
            InventoryRewardGrantService.Create(resources.Items.Get(490704411), 1, now, shopExpiration: true));
        var rows = new List<InventoryItem> { weapon, material };
        // 若此真实token允许合堆，保留一个既有普通token，检验来源不会多制造一堆。
        if (tokenTemplate.StackLimit >= 2)
            rows.Add(new(103, new(InventoryListType.Main, 65),
                InventoryRewardGrantService.Create(tokenTemplate, utcNow: now) with { EnchantCardId = 32000 }));
        var ordinary = new Character(1, 0, "OriginSkin", 0, towns.Room, towns.InitialReturn)
        {
            Job = 2, Level = 75, Skills = characters.Get(2).Skills,
            Inventory = new([.. rows], Gold: 54321)
        };
        var markedWeapon = weapon with { Core = weapon.Core with { EpicExchangeOrigin = new(1, 490705251) } };
        var marked = ordinary with { Inventory = ordinary.Inventory with
            { Items = ordinary.Inventory.Items.Replace(weapon, markedWeapon) } };
        string before = JsonSerializer.Serialize(marked);
        var plain = service.Make(ordinary, new(4, 2, weapon.Position));
        var special = service.Make(marked, new(4, 2, weapon.Position));
        That(plain.Accepted && special.Accepted && plain.Plan is not null && special.Plan is not null,
            "带来源装备仍可执行既有合法外观制作，不新增限制");
        var restricted = ordinary with { Inventory = ordinary.Inventory with { Items = ordinary.Inventory.Items.Replace(weapon,
            weapon with { Core = weapon.Core with { DisjointForbidden = true } }) } };
        string restrictedBefore = JsonSerializer.Serialize(restricted);
        var rejected = service.Make(restricted, new(4, 2, weapon.Position));
        That(rejected is { Accepted: false, Error: 93, Plan: null } && JsonSerializer.Serialize(restricted) == restrictedBefore,
            "同模板受限实例不可用幻化消耗，原武器和模具保持");
        var after = special.Plan!.After;
        var tokens = after.Items.Where(item => item.Core.ItemId == tokenTemplate.Id).ToArray();
        That(tokens.Length > 0 && tokens.All(item => item.Core.Kind == ItemKind.Consumable &&
                item.Core.EpicExchangeOrigin is null && item.Core.EnchantCardId == 32000) &&
            tokens.Sum(item => item.Core.Count) == (rows.Any(item => item.Uid == 103) ? 2 : 1) &&
            after.Items.All(item => item.Uid != weapon.Uid && item.Uid != material.Uid) && after.Gold == ordinary.Inventory.Gold &&
            JsonSerializer.Serialize(after) == JsonSerializer.Serialize(plain.Plan!.After) && JsonSerializer.Serialize(marked) == before,
            "新token不继承装备来源，原消费/数量/分堆/来源武器编号规则与普通制作一致且输入不变");
    }

    sealed class OriginTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
