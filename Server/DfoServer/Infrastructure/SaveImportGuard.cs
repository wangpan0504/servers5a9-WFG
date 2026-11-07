namespace DfoServer.Infrastructure;

/// <summary>持有存档实例锁后、打开数据库前，拒绝接管未完成存档导入或整包更新。</summary>
internal static class SaveImportGuard
{
    internal static void RequireNoPending(string dataDirectory)
    {
        // 更新导入首版只支持此一体布局。两条持锁入口共用检查，直接启动也不能绕过恢复。
        var data = new DirectoryInfo(Path.GetFullPath(dataDirectory));
        if (data.Name.Equals("Data", StringComparison.OrdinalIgnoreCase)
            && data.Parent is { } server && server.Name.Equals("ServerS5A9", StringComparison.OrdinalIgnoreCase)
            && server.Parent is { } installation)
        {
            string active = Path.Combine(installation.FullName, ".95jp-update", "active.json");
            RequireAbsent(active, "offline update");
            RequireAbsent(active + ".previous", "offline update");
        }
        string pending = Path.Combine(dataDirectory, ".save-import.pending.json");
        RequireAbsent(pending, "save import");
    }

    private static void RequireAbsent(string pending, string operation)
    {
        try
        {
            _ = File.GetAttributes(pending);
        }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Cannot verify {operation} recovery state; database access blocked: {pending}", error);
        }
        // 不解析或删除记录；即使内容损坏或路径被目录占用，也必须先恢复整套存档。
        throw new InvalidDataException($"Incomplete {operation}; use the recovery tool before retrying. Do not delete the recovery record: {pending}");
    }
}
