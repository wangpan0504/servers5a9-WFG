using System.Text.Json;
using DfoServer.Game.Events;

namespace DfoServer.Game.Settings;

internal sealed class AdventurerBenefitStore(string directory)
{
    readonly string path = Path.Combine(directory, "adventurer-benefits.json");
    public AdventurerBenefitConfiguration Read()
    {
        if (File.Exists(path))
            return AdventurerBenefitService.Validate(JsonSerializer.Deserialize<AdventurerBenefitConfiguration>(File.ReadAllText(path))
                ?? throw new InvalidDataException("冒险家活动配置不能为空"));
        if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "adventurer-benefits.json.*.tmp").Any())
            throw new InvalidDataException("冒险家活动配置缺失且存在恢复文件，请检查后重试");
        return AdventurerBenefitConfiguration.Default;
    }
    public void Write(AdventurerBenefitConfiguration value)
    {
        AdventurerBenefitService.Validate(value);
        Directory.CreateDirectory(directory);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
