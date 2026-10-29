namespace DfoServer.Testing;

// 2026-10-29 用户取消魔兽玩法后，正式 PVF 已移除魔兽资源；魔兽相关 TCP 夹具在资源不全时统一暂时屏蔽，
// 资源回归后同一判定自动恢复执行，既不删除原有断言，也不把缺输入误报成功能回归。
internal static class BeastFixtures
{
    // 只读资源形状：入口任务 5927、阶段奖励任务 10000、等待区城镇 51 与 town31 会面 NPC 598 全部在位才执行。
    public static bool Enabled(TcpTestResources resources) =>
        resources.Quests.TryGet(5927, out _) && resources.Quests.TryGet(10000, out _) &&
        resources.Towns.Areas.Keys.Any(key => key.Item1 == 51) &&
        resources.Towns.Areas.Values.Any(area => area.Town == 31 && area.Default.Npcs.Any(npc => npc.Id == 598));

    public static void Skip(string group) =>
        Console.WriteLine($"SKIP {group}：正式PVF未含完整魔兽资源（5927/10000/town51/town31 NPC598），魔兽夹具暂时屏蔽");

    public static Task SkipAsync(string group)
    {
        Skip(group);
        return Task.CompletedTask;
    }
}
