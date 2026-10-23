namespace DfoServer.Game.ExpertJob;

internal static class ExpertJobTradePublication
{
    // 已经提交，只收集发布故障；绝不把 SQLite BUSY 透传给整条命令的重试器。
    public static async Task<Exception?> PublishAsync(Func<Task> customer, Func<Task> owner)
    {
        try { await Task.WhenAll(Run(customer), Run(owner)); return null; }
        catch (Exception error) { return error; }
        static async Task Run(Func<Task> publish) => await publish();
    }
}
