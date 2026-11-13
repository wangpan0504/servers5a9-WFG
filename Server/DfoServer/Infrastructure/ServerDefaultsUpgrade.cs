using System.Text.Json;
using DfoServer.Game.Events;
using DfoServer.Game.Raids;
using DfoServer.Game.Settings;
using DfoServer.Persistence;

namespace DfoServer.Infrastructure;

// 只在持有 server.lock 且尚未启动监听时执行。跨文件中断可重试，全部完成前不向玩家提供服务。
internal static class ServerDefaultsUpgrade
{
    internal const int Generation = 1;

    internal static bool Apply(string directory, bool epicRoadAvailable)
    {
        string upgrade = Path.Combine(directory, ".95jp-upgrades", "server-defaults-" + Generation);
        Directory.CreateDirectory(upgrade);
        using var gate = new FileStream(Path.Combine(upgrade, "upgrade.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        string completed = Path.Combine(upgrade, "completed");
        if (File.Exists(completed))
        {
            if (File.ReadAllText(completed).Trim() != Generation.ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw new InvalidDataException("服务端默认配置升级标记损坏");
            return false;
        }
        var ids = ServerEventService.Definitions.Select(entry => entry.Id)
            .Where(id => id != ServerEventService.EpicRoadId || epicRoadAvailable).ToArray();
        var events = new ServerEventRepository(Path.Combine(directory, "game.db"));
        string eventBackup = Path.Combine(upgrade, "events.before.json");
        if (!File.Exists(eventBackup)) WriteNew(eventBackup, JsonSerializer.Serialize(ids.Select(events.Read)));
        foreach (string name in new[] { "gm-raid-windows.json", "support-companions.json" })
        {
            string source = Path.Combine(directory, name), backup = Path.Combine(upgrade, name + ".before");
            if (!File.Exists(backup) && !File.Exists(backup + ".absent"))
            {
                if (File.Exists(source)) File.Copy(source, backup, false);
                else WriteNew(backup + ".absent", "absent");
            }
        }
        events.ResetDefaults(ids);
        new RaidEntryStore(directory).Replace(RaidEntryStore.FullTimeJson());
        new SupportCompanionStore(directory).Write(SupportCompanionConfiguration.Default);
        WriteNew(completed, Generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }

    static void WriteNew(string path, string value)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, value);
            File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
