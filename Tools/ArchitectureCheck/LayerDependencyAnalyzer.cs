using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ArchitectureCheck;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LayerDependencyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "S5A9001";
    static readonly DiagnosticDescriptor Rule = new(DiagnosticId, "分层依赖越界",
        "{0} 不允许引用 {1}", "Architecture", DiagnosticSeverity.Error, isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Check, SyntaxKind.IdentifierName, SyntaxKind.GenericName);
    }

    static void Check(SyntaxNodeAnalysisContext context)
    {
        var name = (SimpleNameSyntax)context.Node;
        var enclosing = context.SemanticModel.GetEnclosingSymbol(name.SpanStart);
        string source = enclosing?.ContainingNamespace?.ToDisplayString() ?? "";
        // 文件目录和声明空间均受约束，改 namespace 或使用 global using 不能绕过目录规则。
        string path = name.SyntaxTree.FilePath.Replace('\\', '/');
        var symbol = context.SemanticModel.GetSymbolInfo(name).Symbol;
        if (symbol is IAliasSymbol alias)
            symbol = alias.Target;
        if (symbol is null)
            return; // 无法解析的符号由编译器报错。
        string target = symbol is INamespaceSymbol ns ? ns.ToDisplayString() :
            (symbol as ITypeSymbol ?? symbol.ContainingType)?.ToDisplayString() ?? "";
        if (Forbidden(source, path, target))
            context.ReportDiagnostic(Diagnostic.Create(Rule, name.GetLocation(), source.Length == 0 ? path : source, target));
    }

    static bool In(string name, string prefix) => name == prefix || name.StartsWith(prefix + ".", StringComparison.Ordinal) ||
        name.StartsWith(prefix + "<", StringComparison.Ordinal);

    static bool Forbidden(string source, string path, string target)
    {
        bool Layer(string layer) => In(source, "DfoServer." + layer.Replace('/', '.')) ||
            path.Contains("/Server/DfoServer/" + layer + "/", StringComparison.Ordinal);
        bool Targets(params string[] names) => names.Any(name => In(target, name));
        if (Layer("Game") && Targets("DfoServer.Network", "DfoServer.Persistence", "DfoServer.Development",
            "DfoServer.Application", "DfoServer.Infrastructure", "DfoServer.Testing", "PvfLib", "Microsoft.Data.Sqlite"))
            return true;
        if (Layer("GameWorld") && Targets("DfoServer.Game", "DfoServer.Network", "DfoServer.Persistence", "DfoServer.Development",
            "DfoServer.Application", "DfoServer.Infrastructure", "DfoServer.Testing", "Microsoft.Data.Sqlite"))
            return true;
        if (Layer("Persistence") && Targets("DfoServer.Network", "DfoServer.Development", "DfoServer.Application", "PvfLib"))
            return true;
        if (Layer("Persistence") && Targets("DfoServer.Game.Auth.ILoginAuthenticator", "DfoServer.Game.Auth.AuthenticationService"))
            return true;
        if (Layer("Network/Handlers") && Targets("DfoServer.Game.Auth.IAccountCredentialStore"))
            return true;
        if ((Layer("Network/Handlers") || Layer("Network/Parsers") || Layer("Network/Builders") || Layer("Network/Projection")) &&
            Targets("DfoServer.Persistence", "DfoServer.Development", "PvfLib", "Microsoft.Data.Sqlite"))
            return true;
        if ((Layer("Network/Parsers") || Layer("Network/Builders")) &&
            Targets("DfoServer.Game.Session", "DfoServer.Game.Accounts", "DfoServer.Game.Characters.ICharacterRepository",
                "DfoServer.Game.Auth.ILoginAuthenticator", "DfoServer.Game.Auth.AuthenticationService", "DfoServer.Game.Auth.IAccountCredentialStore",
                "DfoServer.Network.Handlers", "DfoServer.Network.Protocol",
                "DfoServer.Network.Projection", "System.IO.File", "System.IO.Directory", "System.IO.FileStream"))
            return true;
        return In(source, "PvfLib") && In(target, "DfoServer");
    }
}
