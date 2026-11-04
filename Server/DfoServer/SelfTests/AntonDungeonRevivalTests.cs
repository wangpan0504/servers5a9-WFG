using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AntonDungeonRevivalTests
{
    internal static void Run()
    {
        var pool = new AntonDungeonRevivalPool();
        Guid run = Guid.NewGuid();
        DungeonRunIdentity Identity(ushort actor) => new(actor, 0, 1, run, 1);
        using (var failed = pool.Reserve(Identity(1), 1, 12))
            That(failed is not null && pool.Used == 0 && pool.Reserve(Identity(1), 1, 12) is null,
                "安徒恩预留不当作消费，同一角色死亡不能并发预留两次");
        That(pool.Used == 0, "库存失败释放预留，不占共享额度");
        Parallel.For(1, 33, actor =>
        {
            using var reserved = pool.Reserve(Identity((ushort)actor), 1, 12);
            reserved?.Commit();
        });
        That(pool.Used == 12 && pool.Reserve(Identity(40), 1, 12) is null,
            "32名并发请求最多提交12次，第13次在钱包操作前拒绝");
        var fresh = new AntonDungeonRevivalPool();
        using var first = fresh.Reserve(Identity(1), 1, 12);
        first!.Commit(); first.Commit();
        That(fresh.Used == 1 && fresh.Reserve(Identity(1), 1, 12) is null &&
            fresh.Reserve(Identity(2) with { InstanceId = Guid.NewGuid() }, 1, 12) is null &&
            fresh.Reserve(Identity(2), 1, 4) is null,
            "重复提交、旧死亡、不同Run或变更额度不能消耗同一池");
        var other = new AntonDungeonRevivalPool();
        using var independent = other.Reserve(Identity(1) with { InstanceId = Guid.NewGuid() }, 1, 12);
        That(independent is not null && other.Used == 0, "新共享Run建立独立额度，不携带旧副本消费");
        var definition = new DungeonDefinition(216, "dungeon/anton/phase1/fleet_fire.dgn", null, null, [],
            [new("coin limit", [new(0, 4)]), new("coin limit", [new(0, 12)])]);
        That(definition.CoinLimit == 12, "舰炮重复coin limit按客户端顺序覆盖，最终共享上限12");
        That((definition with { RawSections = [..definition.RawSections, new("coin limit", [])] }).CoinLimit is null,
            "最后coin limit段畸形不能回退先前上限，保持未声明结果");
    }
}
