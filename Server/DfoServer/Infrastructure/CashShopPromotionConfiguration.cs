using System.Text.Json;
using System.Text.Json.Nodes;
using DfoServer.GameWorld.Items;

namespace DfoServer.Infrastructure;

// 原礼包配置是唯一启停权威；候选校验和临时文件写入不占用购买提交的短门禁。
internal sealed class CashShopPromotionConfiguration
{
    readonly object gate = new();
    readonly string path;
    readonly Func<string, CashShopPromotionCatalog> parse;
    CashShopPromotionCatalog current;
    string source;
    public CashShopPromotionCatalog Current => Volatile.Read(ref current);
    public event Action? Changed;

    public CashShopPromotionConfiguration(string path, string json, Func<string, CashShopPromotionCatalog> parse)
    {
        this.path = path;
        this.parse = parse;
        current = parse(json);
        source = json;
    }

    public IDisposable AcquireCommitLease()
    {
        Monitor.Enter(gate);
        return new CommitLease(gate);
    }

    public CashShopPromotionCatalog SetEnabled(PromotionScope scope, long expectedRevision, bool enabled)
    {
        string before;
        lock (gate)
        {
            RequireCurrent(scope, expectedRevision);
            before = source;
        }
        var entries = JsonNode.Parse(before)?.AsArray() ?? throw new InvalidDataException("礼包配置不是数组");
        var selected = entries.Single(entry => entry?.Deserialize<PromotionConfiguration>()?.Rules.Scope == scope)!;
        selected["Enabled"] = enabled;
        string json = entries.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var candidate = parse(json);
        var target = candidate.Activities.Single(entry => entry.Configuration.Rules.Scope == scope);
        if (enabled && target.Availability != PromotionAvailability.Active)
            throw new InvalidOperationException(target.Error ?? "礼包资源或奖励消费者校验未通过");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json);
            lock (gate)
            {
                RequireCurrent(scope, expectedRevision);
                // 外部运维修改不能被一个旧页面的开关请求静默覆盖。
                if (File.Exists(path) && File.ReadAllText(path) != before)
                    throw new InvalidOperationException("外置礼包配置已改变，请重载服务端后操作");
                File.Move(temporary, path, true);
                source = json;
                Volatile.Write(ref current, candidate);
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        Changed?.Invoke();
        return candidate;
    }

    void RequireCurrent(PromotionScope scope, long expectedRevision)
    {
        if (current.Revision != expectedRevision ||
            !current.Activities.Any(entry => entry.Configuration.Rules.Scope == scope))
            throw new InvalidOperationException("礼包身份或配置版本已改变，请重新读取");
    }

    sealed class CommitLease(object gate) : IDisposable
    {
        bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Monitor.Exit(gate);
        }
    }
}
