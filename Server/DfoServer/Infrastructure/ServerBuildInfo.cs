using System.Reflection;

namespace DfoServer.Infrastructure;

/// <summary>
/// 构建期写进程序集的版本与提交号：SDK 的 SourceLink 把构建时的 git 提交追加在
/// AssemblyInformationalVersion 的 + 之后（形如 1.0.0+cbdcb76…）。
/// 从源码 zip 构建时没有 git 信息，这里返回空串，调用方按「未知」处理，不填占位值。
/// </summary>
internal static class ServerBuildInfo
{
    private static readonly (string Version, string Commit) Stamp = Read();

    public static string Version => Stamp.Version;

    /// <summary>完整提交号；程序集没写时为空串。</summary>
    public static string Commit => Stamp.Commit;

    /// <summary>就绪文件与启动日志用的短提交号（前 7 位）；未知时为空串。</summary>
    public static string ShortCommit => Commit.Length > 7 ? Commit[..7] : Commit;

    private static (string Version, string Commit) Read()
    {
        string text = typeof(ServerBuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        int plus = text.IndexOf('+');
        return plus < 0 ? (text, "") : (text[..plus], text[(plus + 1)..]);
    }
}
