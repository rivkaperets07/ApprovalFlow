/// <summary>
/// Seeds one account per role on startup so the system is usable the moment it comes up —
/// without this, a fresh environment would have zero accounts and nobody could log in to
/// register the first one. TryRegisterAsync is idempotent (an existing email is left
/// untouched), so this runs safely on every restart. Credentials are intentionally
/// published here and in README — this is a demo store, not a production identity system,
/// and hiding a "secret" that's checked into a public repo would only be theater.
/// </summary>
public static class DemoUserSeeder
{
    public static readonly (string Email, string Password, string Role, string Name)[] Accounts =
    [
        ("submitter@zionet.demo", "Submitter123!", "submitter", "Demo Submitter"),
        ("approver@zionet.demo", "Approver123!", "approver", "Demo Approver"),
        ("admin@zionet.demo", "Admin123!", "admin", "Demo Admin"),
    ];

    // Bounded retry, not a single attempt: the Dapr sidecar's own readiness gate is "wait
    // until the app is accepting connections on its port" (docker-compose has no other
    // ordering signal), so on a cold start the sidecar is reliably still unreachable for a
    // moment after this app starts — verified against a real `docker compose up` run,
    // where a single unguarded attempt failed on every single boot, not occasionally.
    public static async Task SeedAsync(IUserStore userStore, ILogger logger, CancellationToken cancellationToken = default)
    {
        const int maxAttempts = 10;
        var delay = TimeSpan.FromSeconds(1);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                foreach (var (email, password, role, name) in Accounts)
                {
                    await userStore.TryRegisterAsync(new UserAccount
                    {
                        Email = email,
                        PasswordHash = PasswordHasher.Hash(password),
                        Role = role,
                        Name = name
                    });
                }
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                logger.LogDebug(ex, "Demo account seeding attempt {Attempt}/{MaxAttempts} failed (Dapr sidecar likely still starting); retrying in {Delay}.", attempt, maxAttempts, delay);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex)
            {
                // Not fatal even after exhausting retries: self-registration via
                // /register still works, so this only costs the "usable immediately"
                // convenience, not the feature itself.
                logger.LogWarning(ex, "Could not seed demo accounts after {MaxAttempts} attempts; they can still be created via /register.", maxAttempts);
            }
        }
    }
}
