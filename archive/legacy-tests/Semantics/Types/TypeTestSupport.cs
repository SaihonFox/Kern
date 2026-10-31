using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Xunit;

namespace Kern.Tests.Semantics.Types;

/// <summary>Общие помощники: синтаксис типа из параметра функции и резолвер по исходнику модуля.</summary>
internal static class TypeTestSupport
{
	public static TypeSyntax ParseType(string text)
	{
		var result = Parser.Parse($"fun probe({text} value) {{ }}");
		Assert.Empty(result.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(Assert.Single(result.Tree.Declarations));
		return Assert.Single(function.Parameters).Type!;
	}

	public static TypeResolver ResolverFor(string source)
	{
		var result = Parser.Parse(source);
		Assert.Empty(result.Diagnostics);
		var (table, diagnostics) = SymbolTable.Collect(result.Tree);
		Assert.Empty(diagnostics);
		return new TypeResolver(table);
	}

	public static KernType Resolve(string source, string type)
	{
		var resolver = ResolverFor(source);
		var resolved = resolver.Resolve(ParseType(type));
		Assert.Empty(resolver.Diagnostics);
		return resolved;
	}

	public static Diagnostic SingleDiagnostic(string source, string type)
	{
		var resolver = ResolverFor(source);
		Assert.IsType<ErrorType>(resolver.Resolve(ParseType(type)));
		return Assert.Single(resolver.Diagnostics);
	}
}
