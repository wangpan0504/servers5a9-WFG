using System.Text.RegularExpressions;
using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class ItemModelTests
{
    public static void Run()
    {
        // 对照既有行首字段合同，覆盖 CRLF、空字段、重复标签和不完整边界。
        string[] parts = ["", "\n", "\r\n", "[name]", "[NaMe]", "[price] 0", "[/name]", "[broken", "[]", " [name]", "`text`", "  \t", "[name]\r\n`甲`", "\r"];
        var random = new Random(17);
        for (int i = 0; i < 1000; i++)
        {
            string text = string.Concat(Enumerable.Range(0, 20).Select(_ => parts[random.Next(parts.Length)]));
            var document = new PvfDocument(text);
            foreach (string tag in new[] { "name", "price", "/name", "missing" })
            {
                bool has = Regex.Matches(text, @"(?m)^\[([^\]\r\n]+)\]")
                    .Any(match => string.Equals(match.Groups[1].Value, tag, StringComparison.OrdinalIgnoreCase));
                string expected = has ? Regex.Match(text, @"^\[" + Regex.Escape(tag) + @"\](.*?)(?=\r?\n\[|\z)",
                    RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups[1].Value.TrimStart() : "";
                That(document.Has(tag) == has && document.Section(tag) == expected, "indexed field preserves line and whitespace semantics");
            }
        }
        var parent = new PvfDocument("[name]\n`parent`\n[price]\n20\n[items]\n1\n[/items]");
        var child = new PvfDocument("[name]\n\n[price]\n0\n[items]\n[/items]", parent);
        That(child.Has("items") && child.Section("name") == "" && child.Section("price") == "0", "local empty and zero override parent");
        That(child.Block("items").Trim() == "" && parent.Block("items").Trim() == "1", "empty block shadows without mutating parent");
        That(child.TryGetInt32("price", out int zero) && zero == 0, "strict integer preserves explicit zero override");
        var subtypeParent = new PvfDocument("[sub type]\n4");
        That(new PvfDocument("[name]\n`child`", subtypeParent).TryGetInt32("sub type", out int inherited) && inherited == 4,
            "strict integer inherits the nearest declared parent");
        foreach (string value in new[] { "", "4 19", "4.0", "`4`", "2147483648", "4\n[sub type]\n4" })
            That(!new PvfDocument("[sub type]\n" + value, subtypeParent).TryGetInt32("sub type", out _),
                "empty ambiguous or invalid integer cannot fall back to parent");
        That(!new PvfDocument("").TryGetInt32("sub type", out _), "missing strict integer stays unknown");
        Reject<InvalidDataException>(() => new PvfDocument("[x]1[/x]\n[x]2[/x]").Block("x"));
        var scripts = new Dictionary<string, string>
        {
            ["equipment/base.equ"] = "[price]\n50\n[name]\n`base`",
            ["equipment/child.equ"] = "[price]\n0\n[import script]\n`./BASE.equ`"
        };
        string Read(string path) => scripts.TryGetValue(path, out var text) ? text : throw new FileNotFoundException(path);
        var effective = EquipmentFile.ReadScript(Read, "Equipment/child.equ");
        That(effective.Section("price") == "0" && effective.Section("name") == "`base`", "normalized root-relative import and child priority");
        foreach (string reference in new[] { "`child.equ`", "`missing.equ`", "", "`base.equ` `base.equ`", "`base.equ`\n[import script]\n`base.equ`" })
        {
            scripts["equipment/child.equ"] = "[import script]\n" + reference;
            Reject<InvalidDataException>(() => EquipmentFile.ReadScript(Read, "equipment/child.equ"));
        }
        scripts["equipment/child.equ"] = "[import script]\n`base.equ`";
        scripts["equipment/base.equ"] = "[import script]\n`child.equ`";
        Reject<InvalidDataException>(() => EquipmentFile.ReadScript(Read, "equipment/child.equ"));

        string[] strings = ["[price]", "[stack limit]", "[stackable type]", "[material]", "fixture.stk", "../outside.stk", "[equipment type]", "[weapon]"];
        string Resolve(int value) => strings[value];
        var equipment = EquipmentFile.Parse(1, "equipment/a.equ", [new(3, 0), new(0, 0), new(3, 6), new(6, 7), new(0, 19)], Resolve);
        That(equipment.DeclaredPrice == 0 && equipment.DeclaredEquipmentType == new EquipmentTypeDeclaration("[weapon]", 19), "equipment raw zero and type parameter");
        That(equipment.DeclaredMinimumLevel is null && equipment.UnsupportedFields.Contains("minimum level"), "missing fields stay distinct from default values");
        equipment = EquipmentFile.Parse(1, "equipment/a.equ", [new(3, 0), new(0, 1), new(3, 0), new(0, 2)], Resolve);
        That(equipment.DeclaredPrice is null && equipment.UnsupportedFields.Count(x => x == "price") == 1, "duplicate raw declaration is unsupported");
        var stackable = StackableFile.Parse(0, "stackable/a.stk", [new(3, 1), new(0, 0), new(3, 2), new(6, 3), new(0, -1), new(0, 2)], Resolve);
        That(stackable.DeclaredStackLimit == 0 && stackable.TypeDeclaration!.Parameters.SequenceEqual(new[] { -1, 2 }), "raw type parameters have no invented nonnegative constraint");
        Reject<InvalidDataException>(() => StackableFile.Parse(1, "stackable/a.stk", [new(3, 0), new(0, -1)], Resolve));
        That(EquipmentFile.Parse(1, "equipment/a.equ", [new(3, 0), new(0, -1)], Resolve).DeclaredPrice is null, "equipment unsupported scalar remains nonfatal");
        That(PvfItemIndex.Stackable([new(0, 0), new(6, 4)], Resolve, _ => true)[0] == "stackable/fixture.stk", "stackable zero identity is retained");
        string[] ticketStrings = ["[action type]","[quest clear]","[maximum level]","[use action packet]","[usable job]","[all]","[/action type]","[/usable job]","[any quest clear]",
            "[action usable place]","[village]","[/action usable place]"];
        string TicketResolve(int value) => ticketStrings[value];
        var ticket = StackableFile.Parse(100,"stackable/ticket.stk",
            [new(3,0),new(6,1),new(3,6),new(3,2),new(0,84),new(3,3),new(0,0),new(3,4),new(6,5),new(3,7)],TicketResolve);
        That(ticket.ActionDeclaration is { Label: "[quest clear]", Parameters.Count: 0 } && ticket.DeclaredMaximumLevel == 84 &&
            ticket.HasMaximumLevel && ticket.DeclaredUseActionPacket == 0 && ticket.HasUseActionPacket &&
            ticket.DeclaredUsableJobs!.SequenceEqual(new[] { "[all]" }),"ticket declarations preserve empty action arguments, explicit zero and job list");
        ticket = StackableFile.Parse(100,"stackable/ticket.stk",[new(3,0),new(6,8),new(0,674),new(0,649),new(3,6)],TicketResolve);
        That(ticket.ActionDeclaration!.Parameters.SequenceEqual(new[] { 674,649 }) && !ticket.HasMaximumLevel && ticket.DeclaredMaximumLevel is null,
            "ticket raw parameters and missing maximum remain distinct");
        ticket = StackableFile.Parse(100,"stackable/ticket.stk",[new(3,2),new(0,84),new(0,85),new(3,4),new(0,1)],TicketResolve);
        That(ticket.HasMaximumLevel && ticket.DeclaredMaximumLevel is null && ticket.HasUsableJobs && ticket.DeclaredUsableJobs is null,
            "malformed ticket bounds and job tokens cannot become absent defaults");
        ticket = StackableFile.Parse(100,"stackable/ticket.stk",[new(3,9),new(6,10),new(3,11)],TicketResolve);
        That(ticket.HasActionUsablePlace && ticket.DeclaredActionUsablePlaces!.SequenceEqual(new[] { "[village]" }),
            "ticket raw village declaration is retained");
        ticket = StackableFile.Parse(100,"stackable/ticket.stk",[],TicketResolve);
        That(!ticket.HasActionUsablePlace && ticket.DeclaredActionUsablePlaces is null,"missing usable place has no implicit village restriction");
        foreach (PvfToken[] values in new PvfToken[][]
            { [new(3,9),new(0,1)], [new(3,9),new(6,10),new(3,11),new(3,9),new(6,10),new(3,11)] })
        {
            ticket = StackableFile.Parse(100,"stackable/ticket.stk",values,TicketResolve);
            That(ticket.HasActionUsablePlace && ticket.DeclaredActionUsablePlaces is null,
                "malformed or duplicate usable place cannot become an absent default");
        }
        Reject<InvalidDataException>(() => PvfItemIndex.Equipment([new(0, 0), new(6, 4)], Resolve, _ => true));
        Reject<InvalidDataException>(() => PvfItemIndex.Stackable([new(0, 1), new(6, 5)], Resolve, _ => true));
        Reject<InvalidDataException>(() => PvfItemIndex.Stackable([new(0, 1), new(6, 4)], Resolve, _ => false));
    }
}
