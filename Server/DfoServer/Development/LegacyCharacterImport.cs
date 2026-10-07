using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Towns;
using DfoServer.Game.Characters;
using DfoServer.Persistence;

namespace DfoServer.Development;

internal static class LegacyCharacterImport
{
    public static int Run(CharacterRepository repository, AccountOwners.Lease owner, TownCatalog towns, string path)
    {
        string[] lines = File.ReadAllLines(path);
        if (lines.Length < 1 || lines[0] != "matched-client\tordinary-job0\t1")
            throw new InvalidDataException("Legacy store identity");
        var rows = lines.Skip(1).Select((line, index) =>
        {
            var fields = line.Split('\t');
            if (fields.Length != 3 || !ushort.TryParse(fields[0], out ushort id) || id == 0 || !ushort.TryParse(fields[1], out ushort slot) || slot != index || !CharacterName.IsValid(fields[2]))
                throw new InvalidDataException("Invalid legacy character");
            return new Character(id, slot, fields[2], 0, towns.Room, towns.InitialReturn);
        }).ToArray();
        return repository.Import(owner, rows);
    }
}
