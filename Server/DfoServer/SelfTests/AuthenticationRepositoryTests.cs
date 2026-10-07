using DfoServer.Persistence;
using System.Net;
using DfoServer.Game.Auth;
using DfoServer.Testing;

namespace DfoServer;

internal static class AuthenticationRepositoryTests
{
    public static void Run()
    {
        int checks = TestCheck.Count;
        string folder = Path.Combine(Path.GetTempPath(), "ServerS5A9-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "auth.db");
            var repository = new AuthenticationService(new AuthenticationRepository(path));
            repository.Create("User-1", "correct-password");
            var ok = repository.Verify("user-1", "correct-password", IPAddress.Loopback, "matched-client", TimeSpan.FromMinutes(5));
            Check(ok.Succeeded && ok.Identity?.AccountId == "user-1", "correct credentials authenticate");
            Check(repository.Verify("user-1", "wrong", IPAddress.Loopback, "matched-client", TimeSpan.FromMinutes(5)).Failure == AuthenticationFailure.InvalidCredentials, "wrong password rejected");
            repository.Disable("user-1");
            Check(repository.Verify("user-1", "correct-password", IPAddress.Loopback, "matched-client", TimeSpan.FromMinutes(5)).Failure == AuthenticationFailure.AccountDisabled, "disabled account rejected");
            repository.Enable("user-1");
            repository.ChangePassword("user-1", "new-password");
            Check(repository.Verify("user-1", "correct-password", IPAddress.Loopback, "matched-client", TimeSpan.FromMinutes(5)).Failure == AuthenticationFailure.InvalidCredentials, "old password rejected after change");
            var changed = repository.Verify("user-1", "new-password", IPAddress.Loopback, "matched-client", TimeSpan.FromMinutes(5));
            Check(changed.Succeeded && changed.Identity!.AuthGeneration > ok.Identity!.AuthGeneration, "password change advances revision");
            var reopened = new AuthenticationService(new AuthenticationRepository(path));
            Check(reopened.Verify("user-1", "new-password", IPAddress.Loopback, "matched-client", TimeSpan.FromMinutes(5)).Succeeded, "database reopens with credentials");
            Check(File.ReadAllBytes(path).AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes("new-password")) < 0, "database does not contain plaintext password");
            Console.WriteLine($"PASS {TestCheck.Count - checks} authentication repository assertions");
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    static void Check(bool condition, string message)
    {
        TestCheck.That(condition, "auth repository: " + message);
    }
}
