namespace DfoServer.Persistence;

internal static class EvildomWarRecoverySchema
{
    public const string Battles = """
        CREATE TABLE IF NOT EXISTS evildom_war_battles(
          battle_id TEXT PRIMARY KEY NOT NULL CHECK(length(battle_id)=32),revision INTEGER NOT NULL CHECK(revision>=0),
          deadline INTEGER NOT NULL CHECK(deadline>=0),checkpoint_json TEXT NOT NULL CHECK(json_valid(checkpoint_json)));
        """;
    public const string Seats = """
        CREATE TABLE IF NOT EXISTS character_evildom_war_seats(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,battle_id TEXT NOT NULL,binding_token TEXT NOT NULL CHECK(length(binding_token)=32),
          state INTEGER NOT NULL DEFAULT 0 CHECK(state BETWEEN 0 AND 3),reconnect_until INTEGER CHECK(reconnect_until>=0),
          payment_json TEXT NOT NULL CHECK(json_valid(payment_json)),latest_instance TEXT NOT NULL CHECK(length(latest_instance)=32),
          PRIMARY KEY(character_id,battle_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account),FOREIGN KEY(battle_id) REFERENCES evildom_war_battles(battle_id));
        """;
}
