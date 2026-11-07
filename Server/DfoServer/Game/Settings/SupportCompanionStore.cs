using System.Text.Json;

namespace DfoServer.Game.Settings;

// 仅持久化本服支援兵选择与档次，不写角色、装备或奖励账本。
internal sealed class SupportCompanionStore(string directory)
{
    readonly string path = Path.Combine(directory, "support-companions.json");

    public SupportCompanionConfiguration Read()
    {
        if (!File.Exists(path)) return SupportCompanionConfiguration.Default;
        var value = JsonSerializer.Deserialize<SupportCompanionConfiguration>(File.ReadAllText(path))
            ?? throw new InvalidDataException("支援兵配置不能为空");
        GameDebugSettings.ValidateLegacySupportConfiguration(value);
        var supported = value with { SelectionMask = value.SelectionMask & 3 };
        // 先完成原子替换才返回配置；失败不发布已归一的内存状态，也不反复清空允许项。
        if (supported != value) Write(supported);
        return supported;
    }

    public void Write(SupportCompanionConfiguration value)
    {
        GameDebugSettings.ValidateSupportConfiguration(value);
        Directory.CreateDirectory(directory);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            // 替换已有文件时保留共享删除读句柄的旧快照；首次建立不覆盖竞争创建的目标。
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
