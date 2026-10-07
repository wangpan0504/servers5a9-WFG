using ArchitectureCheck;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

// 合成编译验证规则，既覆盖越界引用，也证明允许的领域值不会误报。
var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
const string definitions = """
namespace DfoServer.Persistence { public class Store { public static int Read() => 1; } }
namespace DfoServer.Development { public class Probe {} }
namespace DfoServer.Game.Session { public class GameSession { public void Save() {} } }
namespace DfoServer.Game.Characters { public record Character(int Id); }
namespace DfoServer.Game.Auth { public interface ILoginAuthenticator {} public interface IAccountCredentialStore {} public class AuthenticationService {} public record AuthenticatedIdentity(string AccountId); }
namespace DfoServer.GameWorld.Items { public record Item(int Id); }
namespace PvfLib { public class PvfArchive {} }
""";
(string Layer, string Code, bool Reject)[] cases =
[
    ("Game", "namespace DfoServer.Game { class Test { DfoServer.Persistence.Store? value; } }", true),
    ("Game", "using Store = DfoServer.Persistence.Store; namespace DfoServer.Game { class Test { Store? value; } }", true),
    ("Game", "using static DfoServer.Persistence.Store; namespace DfoServer.Game { class Test { int Value => Read(); } }", true),
    ("Game", "namespace DfoServer.Game { class Test { int Value => global::DfoServer.Persistence.Store.Read(); } }", true),
    ("Game", "namespace Escaped { class Test { DfoServer.Persistence.Store? value; } }", true),
    ("Game", "global using DfoServer.Persistence;", true),
    ("Game", "namespace DfoServer.Game { class Test { PvfLib.PvfArchive? value; } }", true),
    ("GameWorld", "namespace DfoServer.GameWorld { class Test { DfoServer.Game.Characters.Character? value; } }", true),
    ("Network/Handlers", "namespace DfoServer.Network.Handlers { class Test { DfoServer.Development.Probe? value; } }", true),
    ("Network/Builders", "namespace DfoServer.Network.Builders { class Test { void Save(DfoServer.Game.Session.GameSession session) => session.Save(); } }", true),
    ("Network/Parsers", "namespace DfoServer.Network.Parsers { class Test { string Read(string path) => System.IO.File.ReadAllText(path); } }", true),
    ("Game", "namespace DfoServer.Game { class Test { DfoServer.GameWorld.Items.Item? value; } }", false),
    ("Network/Builders", "namespace DfoServer.Network.Builders { class Test { int Id(DfoServer.Game.Characters.Character c) => c.Id; } }", false),
    ("Infrastructure", "namespace DfoServer.Infrastructure { class Test { DfoServer.Persistence.Store? value; } }", false),
    ("Persistence", "namespace DfoServer.Persistence { class Test : DfoServer.Game.Auth.ILoginAuthenticator {} }", true),
    ("Persistence", "namespace DfoServer.Persistence { class Test { DfoServer.Game.Auth.AuthenticationService? value; } }", true),
    ("Network/Handlers", "namespace DfoServer.Network.Handlers { class Test { DfoServer.Game.Auth.IAccountCredentialStore? value; } }", true),
    ("Network/Parsers", "namespace DfoServer.Network.Parsers { class Test { DfoServer.Game.Auth.IAccountCredentialStore? value; } }", true),
    ("Network/Builders", "namespace DfoServer.Network.Builders { class Test { DfoServer.Game.Auth.AuthenticationService? value; } }", true),
    ("Network/Builders", "namespace DfoServer.Network.Builders { class Test { DfoServer.Game.Auth.AuthenticatedIdentity? value; } }", false),
    ("Game/Auth", "namespace DfoServer.Game.Auth { class Test { IAccountCredentialStore? value; } }", false)
];
foreach (var test in cases)
{
    var compilation = CSharpCompilation.Create("Fixture", [CSharpSyntaxTree.ParseText(definitions),
        CSharpSyntaxTree.ParseText(test.Code, path: "/Fixture/Server/DfoServer/" + test.Layer + "/Test.cs")], references,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
    if (errors.Length != 0)
        throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Select(d => d.ToString())));
    var diagnostics = await compilation.WithAnalyzers([new LayerDependencyAnalyzer()]).GetAnalyzerDiagnosticsAsync();
    if (diagnostics.Any(d => d.Id == "AD0001") || diagnostics.Any(d => d.Id == LayerDependencyAnalyzer.DiagnosticId) != test.Reject)
        throw new InvalidOperationException("分层检查未按预期拦截：" + test.Code);
}
Console.WriteLine($"PASS {cases.Length} architecture assertions");
