using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed partial class InventoryRandomOptionService(ItemCatalog items, RandomOptionCatalog options,
    Func<long, long>? random = null)
{
    readonly Func<long, long> roll = random ?? Random.Shared.NextInt64;

    public InventoryResult Unseal(Character character, UnsealRandomOptionRequest request, DateTimeOffset? utcNow = null)
    {
        var before = character.Inventory;
        var target = before.Find(new(InventoryListType.Main, request.Slot));
        if (target is null || target.Core.Kind != ItemKind.Equipment || target.Core.EquipmentLockId != 0 ||
            target.IsExpired((utcNow ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds()) ||
            target.Core.Random0.Type != 0 || target.Core.Random1.Type != 0 || target.Core.Random2.Type != 0)
            return InventoryResult.Reject();
        var definition = options.Resolve(items.Get(target.Core.ItemId));
        if (definition is null || before.Gold < definition.GoldCost)
            return InventoryResult.Reject();
        var core = Initialize(target.Core, definition, roll);
        return new(true, 0, new(before, before with
        {
            Gold = before.Gold - definition.GoldCost,
            Items = before.Items.Replace(target, target with { Core = core })
        }, "random_option_unseal", new(character)));
    }

    // 纯实例初始化：费用由调用入口决定，奖励不能调用扣金币的背包解封事务。
    internal static ItemCore Initialize(ItemCore core, RandomOptionDefinition definition, Func<long, long> random)
    {
        int poolIndex = Choose(definition.Pools.Select((row, index) => (Value: index, row.Weight)).ToArray(), random);
        var pool = definition.Pools[poolIndex];
        var selected = new RandomOption[3];
        var used = new HashSet<byte>();
        for (int i = 0; i < pool.Count; i++)
        {
            var candidates = pool.Groups[i].Where(row => !used.Contains(row.Type)).ToArray();
            int index = Choose(candidates.Select((row, at) => (Value: at, row.Weight)).ToArray(), random);
            var value = candidates[index];
            selected[i] = new(value.Type, value.Value1, value.Value2);
            used.Add(value.Type);
        }
        return core with
        {
            Random0 = selected[0], Random1 = selected[1], Random2 = selected[2], RandomState = 0,
            RandomChangedIndex = byte.MaxValue, RandomChangeState = 0, RandomChange = default
        };
    }

    static int Choose((int Value, long Weight)[] entries, Func<long, long> random)
    {
        long total = entries.Aggregate(0L, (sum, entry) => checked(sum + entry.Weight));
        if (total <= 0)
            throw new InvalidDataException("魔法封印没有可选权重");
        long value = random(total);
        if (value < 0 || value >= total)
            throw new InvalidDataException("魔法封印随机值越界");
        foreach (var entry in entries)
        {
            if (value < entry.Weight)
                return entry.Value;
            value -= entry.Weight;
        }
        throw new InvalidDataException("魔法封印权重抽取失败");
    }
}
