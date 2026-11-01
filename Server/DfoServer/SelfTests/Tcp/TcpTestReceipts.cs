using System.Security.Cryptography;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace DfoServer.Testing;

// 仅测试驱动收据：不改变用例成功/异常语义，不把旧式控制台 PASS 当作完整覆盖证明。
internal static class TcpTestReceipts
{
    static readonly AsyncLocal<Group?> current = new();
    // 只列已逐一检查全部条件省略分支的入口；共享用例变化须重新审核，未知组保守不复用。
    static readonly HashSet<string> audited = new(StringComparer.Ordinal)
    {
        "equipment-power-rules", "frame-crc-rules", "chat-rate-rules", "clear-avatar", "aura-action", "quest-hell-challenge"
    };

    internal sealed record Skip(string Code, string Reason);
    internal sealed record Identity(string? Source, string? Input, string? Runner);
    internal sealed record Receipt
    {
        public int SchemaVersion { get; init; } = 2;
        public string Execution { get; init; } = "executed";
        public string Scope { get; init; } = "group";
        public required string RunId { get; init; }
        public required string Group { get; init; }
        public string? Profile { get; init; }
        public required string[] SelectedGroups { get; init; }
        public required string Status { get; init; }
        public required string Coverage { get; init; }
        public required Identity Identity { get; init; }
        public required Skip[] Skips { get; init; }
        public DateTimeOffset StartedUtc { get; init; }
        public DateTimeOffset? CompletedUtc { get; init; }
        public bool Passed => Status == "passed";
        public bool CacheEligible => Passed && CompletedUtc is not null && CompletedUtc >= StartedUtc &&
            Coverage == "complete" && Skips.Length == 0 &&
            IsDigest(Identity.Source) && IsDigest(Identity.Input) && IsDigest(Identity.Runner);
        public int Assertions { get; init; }
        public long ElapsedMs { get; init; }
        public long DriverPrivateBytes { get; init; }
        public long DriverWorkingSetBytes { get; init; }
        public long AllocatedBytes { get; init; }
        public bool SharedResourcesLoaded { get; init; }
        public long ResourceLoadMs { get; init; }
        public long FixtureStartMs { get; init; }
        public long FixtureStarts { get; init; }
        // 只记异常类型，不把可能含账号、路径或资源正文的 Message 写入共享收据。
        public string? ErrorType { get; init; }
    }

    internal sealed class Group : IDisposable
    {
        readonly Group? parent;
        readonly List<Skip> skips = [];
        readonly bool complete;
        readonly DateTimeOffset started = DateTimeOffset.UtcNow;
        readonly string name;
        readonly string runId;
        readonly Identity identity;
        readonly string[] selectedGroups;
        public Group(string name, string runId, Identity identity, bool? coverageAudited = null, string[]? selection = null)
        {
            this.name = name; this.runId = runId; this.identity = identity;
            selectedGroups = selection is null ? [name] : [.. selection];
            complete = coverageAudited ?? audited.Contains(name);
            parent = current.Value;
            current.Value = this;
        }
        internal void Add(Skip skip) { lock (skips) skips.Add(skip); }
        public Receipt Snapshot(string status, string? errorType = null)
        {
            Skip[] copy;
            lock (skips) copy = [.. skips];
            return new()
            {
                RunId = runId, Group = name, Profile = name switch { "equipment-power-rules" => "equipment-power-rules-v1", "frame-crc-rules" => "frame-crc-rules-v1", "chat-rate-rules" => "chat-rate-rules-v1", _ => null }, SelectedGroups = [.. selectedGroups], Status = status,
                Coverage = copy.Length != 0 ? "partial" : complete ? "complete" : "unknown",
                Identity = identity, Skips = copy, StartedUtc = started,
                CompletedUtc = status == "running" ? null : DateTimeOffset.UtcNow, ErrorType = errorType
            };
        }
        public void Dispose() => current.Value = parent;
    }

    public static void RecordSkip(string code, string reason)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 96 || code.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '.')))
            throw new ArgumentException("测试跳过原因必须使用有限的稳定标识", nameof(code));
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1024)
            throw new ArgumentException("测试跳过说明必须为有限非空文本", nameof(reason));
        current.Value?.Add(new(code, reason));
        Console.WriteLine("SKIP " + reason);
    }

    internal static bool IsDigest(string? value) => value is { Length: 64 } && value.All(c => char.IsAsciiHexDigit(c));
    public static Identity CaptureIdentity()
    {
        string? Key(string name) => Environment.GetEnvironmentVariable(name) is { } value && IsDigest(value) ? value.ToLowerInvariant() : null;
        // 只接受已审 dotnet <实际DLL> 入口；apphost/单文件/反射宿主不借旁置文件自报身份。
        var assembly = typeof(TcpTestReceipts).Assembly;
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, assembly.GetName().Name + ".dll"));
        string? runner = null;
        string[] arguments = Environment.GetCommandLineArgs();
        if (System.Reflection.Assembly.GetEntryAssembly() == assembly &&
            IsDirectDllInvocation(Environment.ProcessPath, arguments.FirstOrDefault(), path))
            runner = ReadRunnerDigest(path, assembly.ManifestModule.ModuleVersionId);
        return new(Key("DFO_TEST_SOURCE_KEY"), Key("DFO_TEST_INPUT_KEY"), runner);
    }

    internal static bool IsDirectDllInvocation(string? processPath, string? commandPath, string path) =>
        Path.GetFileNameWithoutExtension(processPath) == "dotnet" && !string.IsNullOrEmpty(commandPath) &&
        string.Equals(Path.GetFullPath(commandPath), Path.GetFullPath(path),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static string? ReadRunnerDigest(string path, Guid loadedModuleVersionId)
    {
        if (loadedModuleVersionId == Guid.Empty) return null;
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!pe.HasMetadata) return null;
            var metadata = pe.GetMetadataReader();
            if (metadata.GetGuid(metadata.GetModuleDefinition().Mvid) != loadedModuleVersionId) return null;
            stream.Position = 0;
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException)
        { return null; }
    }

    // 旧 schema 或缺字段一律不能升级为完整成功；工具侧仍须核对输入前后稳定及实际 DLL。
    public static bool IsReusable(JsonElement json)
    {
        try
        {
            return json.GetProperty("SchemaVersion").GetInt32() == 2 &&
                json.GetProperty("Execution").GetString() == "executed" && json.GetProperty("Scope").GetString() == "group" &&
                json.GetProperty("Status").GetString() == "passed" && json.GetProperty("Coverage").GetString() == "complete" &&
                json.GetProperty("Passed").GetBoolean() && json.GetProperty("CacheEligible").GetBoolean() &&
                json.GetProperty("Skips").GetArrayLength() == 0 &&
                json.GetProperty("StartedUtc").TryGetDateTimeOffset(out var started) &&
                json.GetProperty("CompletedUtc").TryGetDateTimeOffset(out var completed) && completed >= started &&
                Guid.TryParseExact(json.GetProperty("RunId").GetString(), "N", out _) &&
                !string.IsNullOrWhiteSpace(json.GetProperty("Group").GetString()) &&
                json.GetProperty("SelectedGroups").GetArrayLength() > 0 &&
                IsDigest(json.GetProperty("Identity").GetProperty("Source").GetString()) &&
                IsDigest(json.GetProperty("Identity").GetProperty("Input").GetString()) &&
                IsDigest(json.GetProperty("Identity").GetProperty("Runner").GetString());
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException) { return false; }
    }

    public static string DurableDirectory(string runId) => Path.Combine(
        Environment.GetEnvironmentVariable("DFO_TEST_RECEIPTS_DIR") ?? Path.Combine("build", "tcp-receipts"), runId);

    public static void WriteAtomic(string path, Receipt receipt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(receipt));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) TestFiles.DeleteFile(temporary); }
    }
}
