using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace DfoServer.Game.Dungeons;

internal static class HostedDungeonEligibilityProcess
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static async Task<HostedDungeonEligibilityResult> RunAsync(
        HostedDungeonEligibilityOptions options, HostedDungeonEligibilityWireRequest request, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(options.HostFileName))
            return HostedDungeonEligibilityRules.Refuse(HostedDungeonEligibilityRules.ReasonMissingCombatProfile, request.ProbeId);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, Json);
        var start = new ProcessStartInfo(options.HostFileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in options.HostArguments)
            start.ArgumentList.Add(argument);

        Process? process = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(options.Timeout);
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("hosted eligibility host did not start");
            await process.StandardInput.BaseStream.WriteAsync(payload, timeout.Token);
            process.StandardInput.Close();
            var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, options.OutputBudgetBytes, timeout.Token);
            var stderr = ReadBoundedAsync(process.StandardError.BaseStream, options.OutputBudgetBytes, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            byte[] output;
            try { output = await stdout; }
            catch (InvalidDataException) { return HostedDungeonEligibilityRules.Refuse(HostedDungeonEligibilityRules.ReasonOverBudget, request.ProbeId); }
            try { _ = await stderr; }
            catch (InvalidDataException) { return HostedDungeonEligibilityRules.Refuse(HostedDungeonEligibilityRules.ReasonOverBudget, request.ProbeId); }
            if (output.Length == 0)
                return HostedDungeonEligibilityRules.Refuse(HostedDungeonEligibilityRules.ReasonEof, request.ProbeId);
            HostedDungeonEligibilityWireReply? reply;
            try { reply = JsonSerializer.Deserialize<HostedDungeonEligibilityWireReply>(output, Json); }
            catch (JsonException) { return HostedDungeonEligibilityRules.Refuse(HostedDungeonEligibilityRules.ReasonInvalidReply, request.ProbeId); }
            return HostedDungeonEligibilityRules.FromReply(reply, request.ProbeId);
        }
        catch (OperationCanceledException)
        {
            return HostedDungeonEligibilityRules.Refuse(
                cancellation.IsCancellationRequested
                    ? HostedDungeonEligibilityRules.ReasonCancelled
                    : HostedDungeonEligibilityRules.ReasonTimeout, request.ProbeId);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or Win32Exception)
        {
            return HostedDungeonEligibilityRules.Refuse(HostedDungeonEligibilityRules.ReasonEof, request.ProbeId);
        }
        finally
        {
            await ReclaimAsync(process);
        }
    }

    static async Task<byte[]> ReadBoundedAsync(Stream stream, int budget, CancellationToken cancellation)
    {
        byte[] buffer = new byte[budget + 1];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), cancellation);
            if (read == 0) break;
            total += read;
        }
        if (total > budget) throw new InvalidDataException(HostedDungeonEligibilityRules.ReasonOverBudget);
        return buffer.AsSpan(0, total).ToArray();
    }

    static async Task ReclaimAsync(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception) when (process.HasExited) { }
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await process.WaitForExitAsync(wait.Token); }
                catch (OperationCanceledException) { }
            }
        }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }
}
