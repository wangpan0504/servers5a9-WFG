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
        GameDebugSettings.ValidateSupportConfiguration(value);
        return value;
    }

    public void Write(SupportCompanionConfiguration value)
    {
        GameDebugSettings.ValidateSupportConfiguration(value);
        Directory.CreateDirectory(directory);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
