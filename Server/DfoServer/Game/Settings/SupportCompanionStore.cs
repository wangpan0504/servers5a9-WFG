using System.Text.Json;

namespace DfoServer.Game.Settings;

// 仅持久化本服支援兵选择与档次，不写角色、装备或奖励账本。
internal sealed class SupportCompanionStore(string directory)
{
    readonly string path = Path.Combine(directory, "support-companions.json");
    readonly Action<string, string> replaceFile = (source, target) => File.Replace(source, target, null);

    internal SupportCompanionStore(string directory, Action<string, string> replace) : this(directory)
    { replaceFile = replace; }

    public SupportCompanionConfiguration Read()
    {
        if (!File.Exists(path))
        {
            // 缺目标但有遗留源不等于首次安装；仅取一个提示，不猜测哪个快照已提交。
            if (Directory.Exists(directory))
                foreach (string source in Directory.EnumerateFiles(directory, Path.GetFileName(path) + ".*.tmp"))
                    throw new RecoveryRequiredException(
                        $"Support configuration target is missing; inspect retained recovery source '{source}'. Default configuration was not loaded; no automatic recovery was performed.", source);
            return SupportCompanionConfiguration.Default;
        }
        var value = JsonSerializer.Deserialize<SupportCompanionConfiguration>(File.ReadAllText(path))
            ?? throw new InvalidDataException("支援兵配置不能为空");
        GameDebugSettings.ValidateLegacySupportConfiguration(value);
        return value;
    }

    public void Write(SupportCompanionConfiguration value)
    {
        GameDebugSettings.ValidateSupportConfiguration(value);
        Directory.CreateDirectory(directory);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Exception? failure = null;
        bool preserveTemporary = false;
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            // 替换已有文件时保留共享删除读句柄的旧快照；首次建立不覆盖竞争创建的目标。
            if (File.Exists(path))
            {
                for (int attempt = 0; ; attempt++)
                {
                    try { replaceFile(temporary, path); break; }
                    // 1175保留源和目标原名；只重试这次替换，主动退避合计70ms，不重新生成快照。
                    catch (IOException error) when (OperatingSystem.IsWindows() && error.HResult == unchecked((int)0x80070497) && attempt < 3)
                    { Thread.Sleep(10 << attempt); }
                    catch (IOException error) when (OperatingSystem.IsWindows() &&
                        error.HResult is unchecked((int)0x80070498) or unchecked((int)0x80070499))
                    {
                        // 1176/1177可能已经部分完成；不盲目重试或回滚，保留可能唯一的恢复源。
                        preserveTemporary = true;
                        error.Data["SupportCompanionRecoveryPath"] = temporary;
                        throw new RecoveryRequiredException(
                            $"Support configuration partial replacement (0x{unchecked((uint)error.HResult):X8}); save failed. Inspect recovery source '{temporary}' before continuing; no automatic rollback was performed.", temporary, error);
                    }
                }
            }
            else File.Move(temporary, path);
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            if (!preserveTemporary)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception cleanupError) when (failure is not null && cleanupError is IOException or UnauthorizedAccessException)
                { failure.Data["SupportCompanionCleanupError"] = cleanupError; }
            }
        }
    }

    sealed class RecoveryRequiredException : IOException
    {
        public RecoveryRequiredException(string message, string recoveryPath, IOException? original = null) : base(message, original)
        {
            if (original is not null) HResult = original.HResult;
            Data["SupportCompanionRecoveryPath"] = recoveryPath;
        }
    }
}
