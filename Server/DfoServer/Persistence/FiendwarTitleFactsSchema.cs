namespace DfoServer.Persistence;

internal static class FiendwarTitleFactsSchema
{
    internal const string Definition = """
        CREATE TABLE character_fiendwar_title_facts(
          character_id INTEGER NOT NULL, account TEXT NOT NULL,
          quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 7003 AND 7008),
          total INTEGER NOT NULL CHECK(total BETWEEN 1 AND 65535),
          applied INTEGER NOT NULL DEFAULT 0 CHECK(applied>=0 AND applied<=total),
          PRIMARY KEY(character_id,quest_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        """;
}
