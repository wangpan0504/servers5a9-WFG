using DfoServer.Game.Accounts;

namespace DfoServer;

internal static class AccountOwnerTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition)
                throw new Exception("FAIL owner: " + message);
        }

        var owners = new AccountOwners();
        int disconnects = 0;
        Check(owners.TryAcquire("account-a", () => disconnects++, DuplicateLoginPolicy.Reject, out var first, out var reason) && reason == OwnerRejectReason.None, "first acquisition");
        Check(!owners.TryAcquire("account-a", () =>
        {
        }, DuplicateLoginPolicy.Reject, out var rejected, out reason) && rejected is null && reason == OwnerRejectReason.DuplicateAccountOwner, "duplicate reject");
        Check(first!.Execute(() => true) && disconnects == 0, "rejection preserves original owner");
        Check(owners.TryAcquire("account-b", () =>
        {
        }, DuplicateLoginPolicy.Reject, out var other, out _), "independent account");
        Check(other!.Account != "account-a" && other.Execute(() => true), "independent lease");
        Check(owners.TryAcquire("account-a", () =>
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

        Check(stale && replacement!.Generation > first.Generation, "stale generation rejected");
        first.Dispose();
        Check(replacement!.Execute(() => true), "stale dispose preserves replacement");
        replacement.Dispose();
        replacement.Dispose();
        other.Dispose();
        var contenders = new AccountOwners.Lease?[16];
        Parallel.For(0, contenders.Length, i => owners.TryAcquire("parallel", () =>
        {
        }, DuplicateLoginPolicy.Reject, out contenders[i], out _));
        Check(contenders.Count(lease => lease is not null) == 1, "parallel rejection admits one owner");
        foreach (var lease in contenders)
            lease?.Dispose();
        Console.WriteLine($"PASS {checks} account owner assertions");
    }
}
