using Kern.Compiler.Semantics.Attributes;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics;

public enum SymbolKind
{
	Type,
	Unit,
	Function,
	GlobalField
}

public sealed record Symbol(string Name, SymbolKind Kind, DeclarationNode Declaration)
{
	public TextSpan Span => Declaration.Span;
}

public sealed class SymbolTable
{
	private readonly Dictionary<string, List<Symbol>> _symbols;

	private SymbolTable(Dictionary<string, List<Symbol>> symbols) => _symbols = symbols;

	public IReadOnlyCollection<string> Names => _symbols.Keys;

	public IReadOnlyList<Symbol> GetSymbols(string name) =>
		_symbols.TryGetValue(name, out var symbols) ? symbols : [];

	public static (SymbolTable Table, IReadOnlyList<Diagnostic> Diagnostics) Collect(CompilationUnit unit)
	{
		var symbols = new Dictionary<string, List<Symbol>>(StringComparer.Ordinal);
		var diagnostics = new List<Diagnostic>();
		foreach (var declaration in unit.Declarations)
		{
			if (!ConditionalCompilation.IsIncluded(declaration))
				continue;
			var symbol = CreateSymbol(declaration);
			if (symbol is null)
				continue;

			if (!symbols.TryGetValue(symbol.Name, out var group))
			{
				symbols.Add(symbol.Name, [symbol]);
				continue;
			}

			if (symbol.Kind == SymbolKind.Function && group.All(existing => existing.Kind == SymbolKind.Function))
			{
				var signature = GetFunctionSignature((FunctionDeclaration)declaration);
				var previous = group.FirstOrDefault(existing =>
					GetFunctionSignature((FunctionDeclaration)existing.Declaration) == signature);
				if (previous is not null)
					diagnostics.Add(new Diagnostic(
						"KRN1400",
						$"Повторная сигнатура функции '{symbol.Name}'",
						symbol.Span,
						DiagnosticSeverity.Error,
						[new DiagnosticNote("первое объявление здесь", previous.Span)]));
				else
					group.Add(symbol);
				continue;
			}

			var previousName = group[0];
			diagnostics.Add(new Diagnostic(
				"KRN1400",
				$"Повторное объявление имени '{symbol.Name}'",
				symbol.Span,
				DiagnosticSeverity.Error,
				[new DiagnosticNote("первое объявление здесь", previousName.Span)]));
		}

		return (new SymbolTable(symbols), diagnostics);
	}

	private static string GetFunctionSignature(FunctionDeclaration function) =>
		$"{function.GenericArity}|{string.Join(",", function.GenericParameters.Select(parameter => parameter.Name))}|" +
		$"{string.Join(",", function.Parameters.Select(parameter => NormalizeTypeName(parameter.Type)))}";

	private static string NormalizeTypeName(TypeSyntax? type) =>
		type is null ? "?" : type.ToString();

	private static Symbol? CreateSymbol(DeclarationNode declaration) => declaration switch
	{
		TypeDeclaration type => new(type.Name, SymbolKind.Type, declaration),
		UnitDeclaration unit => new(unit.Name, SymbolKind.Unit, declaration),
		FunctionDeclaration function => new(function.Name, SymbolKind.Function, declaration),
		GlobalFieldDeclaration field => new(field.Name, SymbolKind.GlobalField, declaration),
		_ => null
	};
}
