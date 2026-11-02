using System.Text;
using System.Text.Json;

namespace DfoServer.Game.Dungeons;

// 仅测试命令使用：读 stdin JSON、写 stdout，不加载 PVF/SQL，不进入 Runtime。
internal static class HostedDungeonEligibilityFakeHost
{
    public static int Run(string scenario) =>
        Run(scenario, Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.OpenStandardError());

    internal static int Run(string scenario, Stream stdin, Stream stdout, Stream stderr)
    {
        if (string.IsNullOrWhiteSpace(scenario))
            return 2;
        using var input = stdin;
        using var output = stdout;
        using var error = stderr;
        switch (scenario)
        {
            case "hang":
                ReadUtf8(input);
                new ManualResetEventSlim(false).Wait();
                return 0;
            case "overflow":
                ReadUtf8(input);
                output.Write(Encoding.UTF8.GetBytes(new string('A', 65 * 1024)));
                output.Flush();
                return 0;
            case "stderr-overflow":
                ReadUtf8(input);
                error.Write(Encoding.UTF8.GetBytes(new string('B', 65 * 1024)));
                error.Flush();
                return 0;
            case "invalid":
                ReadUtf8(input);
                output.Write("not-json"u8);
                output.Flush();
                return 0;
            case "eof":
                ReadUtf8(input);
                return 0;
            case "missing-profile":
            case "stale":
            case "canfight":
                return Reply(scenario, input, output);
            default:
                return 2;
        }
    }

    static int Reply(string scenario, Stream input, Stream output)
    {
        string text = ReadUtf8(input);
        var request = JsonSerializer.Deserialize<HostedDungeonEligibilityWireRequest>(text, HostedDungeonEligibilityProcess.Json)
            ?? throw new InvalidDataException("empty hosted eligibility request");
        Guid probeId = scenario == "stale" ? Guid.NewGuid() : request.ProbeId;
        bool canFight = scenario == "canfight";
        var reply = new HostedDungeonEligibilityWireReply(probeId, canFight,
            canFight ? null : HostedDungeonEligibilityRules.ReasonMissingCombatProfile, null);
        output.Write(JsonSerializer.SerializeToUtf8Bytes(reply, HostedDungeonEligibilityProcess.Json));
        output.Flush();
        return 0;
    }

    static string ReadUtf8(Stream input)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        return reader.ReadToEnd();
    }
}
