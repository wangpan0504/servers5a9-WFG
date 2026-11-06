using System.Globalization;
using System.Text;
using PvfLib;

// 只导出房间NPC引用与隐藏表，原版NPC定义、EVT和XUI均不修改。
if (args.Length != 2) throw new ArgumentException("Usage: MonsterMockBattleResourcePatch source.pvf output-directory");
string output = Path.GetFullPath(args[1]);
if (File.Exists(output) || Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
    throw new IOException("Output must be a new or empty directory");
using var archive = PvfArchive.Open(args[0]);
const string room = "map/common/(r)gate_seria.map";
var tokens = archive.ReadTokens(room);
int start = Array.FindIndex(tokens, token => token.Type == 3 && archive.ResolveString(token.Value) == "[NPC]");
int end = Array.FindIndex(tokens, start + 1, token => token.Type == 3 && archive.ResolveString(token.Value) == "[/NPC]");
if (start < 0 || end < 0 || (end - start - 1) % 5 != 0) throw new InvalidDataException("Unsupported room NPC table");
int[] rows = Enumerable.Range(0, (end - start - 1) / 5).Select(index => start + 1 + index * 5).ToArray();
int[] primary = rows.Where(index => tokens[index] is { Type: 0, Value: 400001107 }).ToArray();
int[] overlay = rows.Where(index => tokens[index] is { Type: 0, Value: 400001112 }).ToArray();
if (primary.Length != 1 || overlay.Length > 1) throw new InvalidDataException("Unexpected Nolan placement count");
if (overlay.Length == 1 && !tokens.AsSpan(primary[0] + 1, 4).SequenceEqual(tokens.AsSpan(overlay[0] + 1, 4)))
    throw new InvalidDataException("Nolan variants are not overlapping");
string[] roles = archive.ReadTokens("npc/chn_monstercolosseum_nolan.npc")
    .Where(token => token.Type == 6).Select(token => archive.ResolveString(token.Value)).ToArray();
if (!roles.Contains("[item shop]") || !roles.Contains("[open monster mock battle]"))
    throw new InvalidDataException("Original Nolan is missing its shop or game entry");
const string hiddenPath = "etc/hiddennpc.etc";
var hidden = archive.ReadTokens(hiddenPath);
int hiddenStart = Array.FindIndex(hidden, token => token.Type == 3 && archive.ResolveString(token.Value) == "[hidden npc]");
int hiddenEnd = Array.FindIndex(hidden, hiddenStart + 1, token => token.Type == 3 && archive.ResolveString(token.Value) == "[/hidden npc]");
if (hiddenStart < 0 || hiddenEnd < 0 || hidden.Skip(hiddenStart + 1).Take(hiddenEnd - hiddenStart - 1).Any(token => token.Type != 0))
    throw new InvalidDataException("Unsupported hidden NPC table");
Export(room, tokens.Where((_, index) => overlay.Length == 0 || index < overlay[0] || index >= overlay[0] + 5));
Export(hiddenPath, hidden.Where((token, index) => index <= hiddenStart || index >= hiddenEnd || token.Value != 400001107));
Console.WriteLine($"EXPORT {room}; original NPC400001107 retained; overlapping shop-only reference removed={overlay.Length}");
Console.WriteLine($"EXPORT {hiddenPath}; NPC400001107 removed from hidden list");

void Export(string name, IEnumerable<PvfToken> values)
{
    var text = new StringBuilder("#PVF_File\n");
    foreach (var token in values)
    {
        if (token.Type == 3) text.Append('\n').Append(archive.ResolveString(token.Value)).Append('\n');
        else if (token.Type == 6)
        {
            string value = archive.ResolveString(token.Value);
            if (value.Contains('`')) throw new InvalidDataException("Unsupported resource string delimiter");
            text.Append('`').Append(value).Append("` ");
        }
        else if (token.Type == 0) text.Append(token.Value.ToString(CultureInfo.InvariantCulture)).Append(' ');
        else if (token.Type == 2)
        {
            string value = BitConverter.Int32BitsToSingle(token.Value).ToString("R", CultureInfo.InvariantCulture);
            if (!value.Contains('.'))
            {
                int exponent = value.IndexOf('E');
                value = exponent < 0 ? value + ".0" : value.Insert(exponent, ".0");
            }
            text.Append(value).Append(' ');
        }
        else throw new InvalidDataException("Unsupported resource token; refusing lossy export");
    }
    string path = Path.Combine(output, "pvf", name);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
}
