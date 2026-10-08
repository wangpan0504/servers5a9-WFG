using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Accounts;

namespace DfoServer;

internal static class AccountOwnerTests
{
    public static void Run()
    {
        int checks = Count;

        var owners = new AccountOwners();
        int disconnects = 0;
        That(owners.TryAcquire("account-a", () => disconnects++, DuplicateLoginPolicy.Reject, out var first, out var reason) && reason == OwnerRejectReason.None, "first acquisition");
        That(!owners.TryAcquire("account-a", () =>
        {
        }, DuplicateLoginPolicy.Reject, out var rejected, out reason) && rejected is null && reason == OwnerRejectReason.DuplicateAccountOwner, "duplicate reject");
        That(first!.Execute(() => true) && disconnects == 0, "rejection preserves original owner");
        That(owners.TryAcquire("account-b", () =>
        {
        }, DuplicateLoginPolicy.Reject, out var other, out _), "independent account");
        That(other!.Account != "account-a" && other.Execute(() => true), "independent lease");
        That(owners.TryAcquire("account-a", () =>
        {
        }, DuplicateLoginPolicy.ReplaceExisting, out var replacement, out _) && disconnects == 1, "explicit replacement disconnects old owner");
        bool stale = false;
        try
        {
            first.Execute(() => true);
        }
        catch (InvalidOperationException)
        {
            stale = true;
        }

        That(stale && replacement!.Generation > first.Generation, "stale generation rejected");
        first.Dispose();
        That(replacement!.Execute(() => true), "stale dispose preserves replacement");
        replacement.Dispose();
        replacement.Dispose();
        other.Dispose();
        var contenders = new AccountOwners.Lease?[16];
        Parallel.For(0, contenders.Length, i => owners.TryAcquire("parallel", () =>
        {
        }, DuplicateLoginPolicy.Reject, out contenders[i], out _));
        That(contenders.Count(lease => lease is not null) == 1, "parallel rejection admits one owner");
        foreach (var lease in contenders)
            lease?.Dispose();
        Console.WriteLine($"PASS {Count - checks} account owner assertions");
    }
}
