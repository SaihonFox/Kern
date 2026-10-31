using Kern.Core;

namespace Kern.Compiler.Syntax;

/// <summary>Корень AST файла: `project name`, `use`-директивы и объявления верхнего уровня.</summary>
public sealed record CompilationUnit(
	SourceFile File, string? ProjectName, IReadOnlyList<UseDecl> Uses, IReadOnlyList<Decl> Decls, TextSpan Span) : Node(Span);
