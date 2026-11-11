using System.Collections.Immutable;
using System.Text.Json;

namespace DfoServer.Game.Settings;

// 只保存开启无限疲劳的账号；未记录的账号（含关闭后）一律使用正常疲劳。
internal sealed class AccountFatigueStore(string directory)
{
    readonly string path = Path.Combine(directory, "account-fatigue.json");

    public ImmutableHashSet<string> Read()
    {
        if (!File.Exists(path))
        {
            if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "account-fatigue.json.*.tmp").Any())
                throw new InvalidDataException("账号疲劳配置缺失且存在未提交恢复文件，请检查后重试");
            return ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
        }
        var accounts = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path))
            ?? throw new InvalidDataException("账号疲劳配置不能为空");
        if (accounts.Any(account => string.IsNullOrWhiteSpace(account) || account.Length > 64 ||
                account.Any(value => char.IsWhiteSpace(value) || char.IsControl(value))) ||
            accounts.Distinct(StringComparer.Ordinal).Count() != accounts.Length)
            throw new InvalidDataException("账号疲劳配置包含无效或重复账号");
        return accounts.ToImmutableHashSet(StringComparer.Ordinal);
    }

    public void Write(ImmutableHashSet<string> accounts)
    {
        Directory.CreateDirectory(directory);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, accounts.Order(StringComparer.Ordinal).ToArray());
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
