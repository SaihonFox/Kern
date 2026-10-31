using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Declarations;

/// <summary>
/// Строит символы модуля из разобранного <see cref="CompilationUnit"/>: типы и unit-типы с членами,
/// generic-параметрами и родителями, верхнеуровневые функции/поля и реестр расширений (slice D).
/// Экземпляр держит кеши; изменяемой статики нет.
/// </summary>
public sealed partial class DeclarationCollector
{
	private readonly SymbolTable _table;
	private readonly TypeResolver _resolver;
	private readonly List<Diagnostic> _diagnostics = [];
	private readonly Dictionary<TypeSymbol, TypeParameterScope> _typeScopes = new(ReferenceEqualityComparer.Instance);
	private readonly HashSet<DeclarationNode> _duplicateTypes = new(ReferenceEqualityComparer.Instance);
	private readonly Dictionary<string, TypeSymbol> _types = new(StringComparer.Ordinal);
	private readonly Dictionary<string, List<FunctionSymbol>> _functions = new(StringComparer.Ordinal);
	private readonly Dictionary<string, FieldSymbol> _globals = new(StringComparer.Ordinal);
	private readonly Dictionary<string, ExtensionBuilder> _extensions = new(StringComparer.Ordinal);
	private string _moduleName = "";

	private DeclarationCollector(SymbolTable table)
	{
		_table = table;
		_resolver = new TypeResolver(table);
	}

	public static (ModuleSymbol Module, IReadOnlyList<Diagnostic> Diagnostics) Collect(
		CompilationUnit unit,
		SymbolTable table,
		string moduleName)
	{
		ArgumentNullException.ThrowIfNull(unit);
		ArgumentNullException.ThrowIfNull(table);
		ArgumentNullException.ThrowIfNull(moduleName);
		return new DeclarationCollector(table).Run(unit, moduleName);
	}

	private (ModuleSymbol Module, IReadOnlyList<Diagnostic> Diagnostics) Run(CompilationUnit unit, string moduleName)
	{
		_moduleName = moduleName;
		ValidateAttributes(unit);

		// 1. Символы типов и их заголовки: generic-параметры, ограничения, родители, union-альтернативы.
		foreach (var declaration in unit.Declarations)
		{
			switch (declaration)
			{
				case TypeDeclaration type:
					PrepareType(type);
					break;
				case UnitDeclaration unitType:
					PrepareUnit(unitType);
					break;
			}
		}

		// 2. Члены типов, верхнеуровневые функции/поля и расширения. Ошибки не прерывают сбор.
		foreach (var declaration in unit.Declarations)
		{
			switch (declaration)
			{
				case TypeDeclaration type when !_duplicateTypes.Contains(type):
					CollectTypeMembers(type);
					break;
				case UnitDeclaration unitType when !_duplicateTypes.Contains(unitType):
					CollectUnitMembers(unitType);
					break;
				case FunctionDeclaration function:
					CollectTopLevelFunction(function);
					break;
				case GlobalFieldDeclaration global:
					CollectGlobal(global);
					break;
				case ExtensionPropertyDeclaration property:
					CollectExtensionProperty(property);
					break;
				case ExtensionOperatorDeclaration extensionOperator:
					CollectExtensionOperator(extensionOperator);
					break;
			}
		}

		_diagnostics.AddRange(_resolver.Diagnostics);
		var extensions = BuildExtensions();
		var module = new ModuleSymbol(
			moduleName,
			_types,
			FreezeFunctions(),
			_globals,
			extensions);
		return (module, [.. _diagnostics]);
	}

	private void PrepareType(TypeDeclaration declaration)
	{
		var symbol = _resolver.GetSymbol(declaration);
		symbol.ModuleName = _moduleName;
		symbol.Accessibility = declaration.Accessibility;
		symbol.IsAbstract = declaration.IsAbstract;
		symbol.IsOpen = declaration.IsOpen;
		symbol.IsStatic = declaration.IsStatic;
		var scope = BuildTypeScope(symbol, declaration.GenericParameters, $"типа '{declaration.Name}'");

		if (declaration.DeclaredUnionVariants.Count > 0)
		{
			var alternatives = new List<KernType>();
			foreach (var variant in declaration.DeclaredUnionVariants)
			{
				var resolved = _resolver.Resolve(variant, scope);
				if (resolved is not ErrorType)
					alternatives.Add(resolved);
			}
			symbol.UnionAlternatives = alternatives;
		}

		RegisterType(declaration, symbol);
	}

	private void PrepareUnit(UnitDeclaration declaration)
	{
		var symbol = _resolver.GetSymbol(declaration);
		symbol.ModuleName = _moduleName;
		symbol.Accessibility = declaration.Accessibility;
		symbol.IsAbstract = false;
		symbol.IsOpen = false;
		symbol.IsStatic = false;
		BuildTypeScope(symbol, declaration.GenericParameters, $"unit-типа '{declaration.Name}'");
		RegisterType(declaration, symbol);
	}

	private void RegisterType(DeclarationNode declaration, TypeSymbol symbol)
	{
		if (!_types.TryAdd(symbol.Name, symbol))
			_duplicateTypes.Add(declaration);
	}

	private Dictionary<string, IReadOnlyList<FunctionSymbol>> FreezeFunctions()
	{
		var result = new Dictionary<string, IReadOnlyList<FunctionSymbol>>(StringComparer.Ordinal);
		foreach (var (name, group) in _functions)
			result[name] = [.. group];
		return result;
	}

	private void CollectGlobal(GlobalFieldDeclaration declaration)
	{
		if (_globals.ContainsKey(declaration.Name))
			return;

		// `var!` без выводимого типа: тип определит binder; KRN1001 здесь не нужен.
		var type = declaration.IsInferred
			? ErrorType.Instance
			: _resolver.Resolve(declaration.Type, TypeParameterScope.Empty);
		_globals.Add(declaration.Name, new FieldSymbol(
			declaration.Name,
			type,
			declaration.IsRequired,
			true,
			declaration.Accessibility,
			declaration.Initializer,
			declaration.IsRequired)
		{
			Declaration = declaration
		});
	}
}
