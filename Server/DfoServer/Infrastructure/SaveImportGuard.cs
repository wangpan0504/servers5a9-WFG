namespace DfoServer.Infrastructure;

/// <summary>持有存档实例锁后、打开数据库前，拒绝接管未完成导入的多文件状态。</summary>
internal static class SaveImportGuard
{
    internal static void RequireNoPending(string dataDirectory)
    {
        string pending = Path.Combine(dataDirectory, ".save-import.pending.json");
        try
        {
            _ = File.GetAttributes(pending);
        }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Cannot verify save import recovery state; database access blocked: {pending}", error);
        }
        // 不解析或删除记录；即使内容损坏或路径被目录占用，也必须先恢复整套存档。
        throw new InvalidDataException($"Incomplete save import; restore the database and sidecars before retrying. Do not delete the recovery record: {pending}");
    }
}
