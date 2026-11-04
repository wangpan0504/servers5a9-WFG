namespace DfoServer.Persistence;

internal static class EndlessAdventureSchema
{
    internal const string Definition = """
        CREATE TABLE account_endless_adventure_days(
          account TEXT NOT NULL,
          day INTEGER NOT NULL CHECK(day BETWEEN 0 AND 3652058),
          earned INTEGER NOT NULL CHECK(earned BETWEEN 0 AND 200),
          PRIMARY KEY(account,day)
        ) STRICT;
        """;
}
