using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Xunit;

namespace Kern.Tests.Semantics.Declarations;

/// <summary>Помощники сбора объявлений: разбор без ошибок, таблица модуля, сборщик.</summary>
internal static class DeclarationTestSupport
{
	public static (ModuleSymbol Module, IReadOnlyList<Diagnostic> Diagnostics) Collect(
		string source,
		string moduleName = "test")
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		// SymbolTable сам сообщает о повторах имён (KRN1400) и покрыт отдельными тестами;
		// здесь проверяются диагностики DeclarationCollector, поэтому они не смешиваются.
		var (table, _) = SymbolTable.Collect(parsed.Tree);
		return DeclarationCollector.Collect(parsed.Tree, table, moduleName);
	}

	public static ModuleSymbol Module(string source, string moduleName = "test")
	{
		var (module, diagnostics) = Collect(source, moduleName);
		Assert.Empty(diagnostics);
		return module;
	}

	public static TypeSymbol Type(string source, string name) =>
		Assert.IsType<TypeSymbol>(Module(source).FindType(name));

	public static IReadOnlyList<Diagnostic> Diagnostics(string source)
	{
		var (_, diagnostics) = Collect(source);
		return diagnostics;
	}

	public static KernType FieldType(string source, string typeName, string fieldName)
	{
		var type = Type(source, typeName);
		return Assert.IsType<FieldSymbol>(type.Members.FindField(fieldName)).Type;
	}
}
