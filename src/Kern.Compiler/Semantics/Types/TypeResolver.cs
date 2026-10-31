using Kern.Compiler.Semantics;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Types;

/// <summary>
/// Превращает <see cref="TypeSyntax"/> в <see cref="KernType"/> с диагностиками KRN1001–KRN1006.
/// Символы типов кешируются по узлу объявления; глобальной статики нет.
/// </summary>
public sealed class TypeResolver
{
	private readonly SymbolTable _table;
	private readonly IReadOnlyDictionary<string, TypeSymbol>? _knownTypes;
	private readonly Dictionary<DeclarationNode, TypeSymbol> _symbols = new(ReferenceEqualityComparer.Instance);
	private readonly List<Diagnostic> _diagnostics = [];
	private readonly HashSet<DeclarationNode> _basesInProgress = new(ReferenceEqualityComparer.Instance);
	private readonly HashSet<DeclarationNode> _reportedCycles = new(ReferenceEqualityComparer.Instance);
	private bool _cycleDetected;

	public TypeResolver(SymbolTable table, IReadOnlyDictionary<string, TypeSymbol>? knownTypes = null)
	{
		_table = table;
		_knownTypes = knownTypes;
	}

	public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

	/// <summary>
	/// Символ объявленного типа, который использует и сборщик объявлений. Один и тот же
	/// экземпляр <see cref="TypeSymbol"/> нужен, чтобы <see cref="ClassType.Symbol"/> видел члены.
	/// </summary>
	public TypeSymbol GetSymbol(TypeDeclaration declaration) => GetOrCreateSymbol(declaration);

	/// <summary>Символ объявленного unit-типа (см. <see cref="GetSymbol(TypeDeclaration)"/>).</summary>
	public TypeSymbol GetSymbol(UnitDeclaration declaration) => GetOrCreateSymbol(declaration);

	public KernType Resolve(TypeSyntax syntax, TypeParameterScope? scope = null) => syntax switch
	{
		NamedTypeSyntax named => ResolveNamed(named, scope ?? TypeParameterScope.Empty),
		FunctionTypeSyntax function => ResolveFunction(function, scope ?? TypeParameterScope.Empty),
		TupleTypeSyntax tuple => ResolveTuple(tuple, scope ?? TypeParameterScope.Empty),
		UnionTypeSyntax union => ResolveUnion(union, scope ?? TypeParameterScope.Empty),
		_ => ErrorType.Instance
	};

	public IReadOnlyList<KernType> ResolveAll(IEnumerable<TypeSyntax> syntaxes, TypeParameterScope? scope = null) =>
		[.. syntaxes.Select(syntax => Resolve(syntax, scope))];

	/// <summary>Строит область параметров объявления; ограничения разрешаются в уже собранной области.</summary>
	public TypeParameterScope CreateScope(IEnumerable<GenericParameterNode> parameters)
	{
		var scope = TypeParameterScope.Empty;
		foreach (var parameter in parameters)
		{
			var constraints = parameter.Constraints.Select(constraint => Resolve(constraint, scope)).ToArray();
			scope = scope.Add(parameter.Name, constraints);
		}
		return scope;
	}

	private KernType ResolveNamed(NamedTypeSyntax syntax, TypeParameterScope scope)
	{
		var resolved = ResolveNamedCore(syntax, scope);
		return syntax.IsNullable ? NullableType.Create(resolved) : resolved;
	}

	private KernType ResolveNamedCore(NamedTypeSyntax syntax, TypeParameterScope scope)
	{
		// 1. Примитивы не перекрываются даже объявлением типа в модуле.
		if (TypeCatalog.TryGetPrimitive(syntax.Name, out var primitive))
			return syntax.Arguments.Count == 0 ? primitive : ReportNotGeneric(syntax);

		// 2. Параметры обобщения текущего объявления.
		if (scope.TryLookup(syntax.Name, out var parameter))
			return syntax.Arguments.Count == 0 ? parameter : ReportNotGeneric(syntax);

		// 3. Символы модуля: классы, unit-типы и union-алиасы.
		foreach (var symbol in _table.GetSymbols(syntax.Name))
		{
			switch (symbol.Declaration)
			{
				case TypeDeclaration type:
					return ResolveTypeDeclaration(type, syntax, scope);
				case UnitDeclaration unit:
					return ResolveUnitDeclaration(unit, syntax, scope);
			}
		}

		// 3a. Нативные типы каталога (spec §12.9): у такого символа нет объявления в исходнике,
		// поэтому таблица символов его не видит, а имя всё равно должно быть допустимо в аннотациях.
		if (_knownTypes is not null &&
			_knownTypes.TryGetValue(syntax.Name, out var known) &&
			known.Kind == SymbolKind.Native)
		{
			return syntax.Arguments.Count == 0
				? new ClassType(known, [])
				: ReportNotGeneric(syntax);
		}

		// 4. Встроенные generic-типы.
		if (TypeCatalog.TryGetBuiltin(syntax.Name, out var builtin))
		{
			var arity = BuiltinGenericType.GetArity(builtin);
			if (syntax.Arguments.Count != arity)
				return ReportArity(syntax.Name, arity, syntax.Arguments.Count, syntax);

			var arguments = ResolveArguments(syntax, scope);
			return arguments is null ? ErrorType.Instance : new BuiltinGenericType(builtin, arguments);
		}

		// 5. Всё остальное — неизвестный тип.
		return ReportUnknown(syntax);
	}

	private KernType ResolveTypeDeclaration(TypeDeclaration declaration, NamedTypeSyntax syntax, TypeParameterScope scope)
	{
		if (declaration.DeclaredUnionVariants.Count > 0)
		{
			if (syntax.Arguments.Count > 0)
				return ReportNotGeneric(syntax);

			GetOrCreateSymbol(declaration);
			var aliasScope = scope;
			foreach (var parameter in declaration.GenericParameters)
				aliasScope = aliasScope.Add(parameter.Name);

			var alternatives = ResolveAll(declaration.DeclaredUnionVariants, aliasScope);
			return UnionType.Create(alternatives);
		}

		if (declaration.GenericArity == 0 && syntax.Arguments.Count > 0)
			return ReportNotGeneric(syntax);

		var arity = declaration.GenericArity;
		if (syntax.Arguments.Count != arity)
			return ReportArity(syntax.Name, arity, syntax.Arguments.Count, syntax);

		var arguments = ResolveArguments(syntax, scope);
		if (arguments is null)
			return ErrorType.Instance;

		return new ClassType(GetOrCreateSymbol(declaration), arguments);
	}

	private KernType ResolveUnitDeclaration(UnitDeclaration declaration, NamedTypeSyntax syntax, TypeParameterScope scope)
	{
		if (declaration.GenericParameters.Count == 0 && syntax.Arguments.Count > 0)
			return ReportNotGeneric(syntax);

		var arity = declaration.GenericParameters.Count;
		if (syntax.Arguments.Count != arity)
			return ReportArity(syntax.Name, arity, syntax.Arguments.Count, syntax);

		var arguments = ResolveArguments(syntax, scope);
		if (arguments is null)
			return ErrorType.Instance;

		return new UnitType(GetOrCreateSymbol(declaration), arguments, declaration.IsImmutable);
	}

	private KernType ResolveFunction(FunctionTypeSyntax syntax, TypeParameterScope scope)
	{
		var parameters = new KernType[syntax.Parameters.Count];
		var hasError = false;
		for (var index = 0; index < syntax.Parameters.Count; index++)
		{
			parameters[index] = Resolve(syntax.Parameters[index], scope);
			hasError |= parameters[index] is ErrorType;
		}

		var returnType = syntax.ReturnType is null
			? PrimitiveType.Void
			: Resolve(syntax.ReturnType, scope);
		hasError |= returnType is ErrorType;
		return hasError ? ErrorType.Instance : new FunctionType(parameters, returnType);
	}

	private KernType ResolveTuple(TupleTypeSyntax syntax, TypeParameterScope scope)
	{
		if (syntax.Elements.Count < 2)
			return Report("KRN1004", "Кортеж должен содержать не менее двух элементов", syntax.Span);

		var elements = new TupleElement[syntax.Elements.Count];
		var hasError = false;
		for (var index = 0; index < syntax.Elements.Count; index++)
		{
			var element = syntax.Elements[index];
			var type = Resolve(element.Type, scope);
			hasError |= type is ErrorType;
			elements[index] = new TupleElement(type, element.Name);
		}

		return hasError ? ErrorType.Instance : new TupleType(elements);
	}

	private KernType ResolveUnion(UnionTypeSyntax syntax, TypeParameterScope scope)
	{
		if (syntax.Alternatives.Count == 0)
			return Report("KRN1005", "Union-тип должен содержать хотя бы одну альтернативу", syntax.Span);

		return UnionType.Create(ResolveAll(syntax.Alternatives, scope));
	}

	/// <summary>Возвращает null, если хотя бы один аргумент уже стал <see cref="ErrorType"/>.</summary>
	private KernType[]? ResolveArguments(NamedTypeSyntax syntax, TypeParameterScope scope)
	{
		var arguments = new KernType[syntax.Arguments.Count];
		for (var index = 0; index < syntax.Arguments.Count; index++)
		{
			arguments[index] = Resolve(syntax.Arguments[index], scope);
			if (arguments[index] is ErrorType)
				return null;
		}
		return arguments;
	}

	private TypeSymbol GetOrCreateSymbol(TypeDeclaration declaration)
	{
		if (_symbols.TryGetValue(declaration, out var existing))
		{
			if (_basesInProgress.Contains(declaration))
			{
				ReportInheritanceCycle(declaration, existing);
				_cycleDetected = true;
			}
			return existing;
		}

		if (TryGetKnownSymbol(declaration, declaration.Name, out var known))
			return known;

		var kind = declaration.DeclaredUnionVariants.Count > 0
			? SymbolKind.UnionAlias
			: declaration.IsAbstract ? SymbolKind.AbstractClass : SymbolKind.Class;
		var symbol = new TypeSymbol
		{
			Name = declaration.Name,
			Kind = kind,
			GenericParameterNames = [.. declaration.GenericParameters.Select(parameter => parameter.Name)],
			Declaration = declaration
		};
		_symbols.Add(declaration, symbol);
		if (declaration.DeclaredUnionVariants.Count == 0)
			PopulateBases(declaration, symbol);
		return symbol;
	}

	/// <summary>
	/// Разрешает прямых родителей объявления в области его generic-параметров. Цикл наследования
	/// обрывается (KRN1006 один раз на символ), уже собранные родители сохраняются.
	/// </summary>
	private void PopulateBases(TypeDeclaration declaration, TypeSymbol symbol)
	{
		if (declaration.Parents.Count == 0)
			return;

		_basesInProgress.Add(declaration);
		try
		{
			var scope = CreateScope(declaration.GenericParameters);
			var bases = new List<KernType>(declaration.Parents.Count);
			foreach (var parent in declaration.Parents)
			{
				var outerCycle = _cycleDetected;
				_cycleDetected = false;
				var resolved = Resolve(parent.Type, scope);
				var cycle = _cycleDetected;
				_cycleDetected = outerCycle;
				if (cycle || resolved is ErrorType)
					continue;
				bases.Add(resolved);
			}

			symbol.SetBases([.. bases]);
		}
		finally
		{
			_basesInProgress.Remove(declaration);
		}
	}

	private void ReportInheritanceCycle(DeclarationNode declaration, TypeSymbol symbol)
	{
		if (!_reportedCycles.Add(declaration))
			return;

		_diagnostics.Add(new Diagnostic(
			"KRN1006",
			$"Циклическое наследование типа '{symbol.Name}'",
			declaration.Span));
	}

	private TypeSymbol GetOrCreateSymbol(UnitDeclaration declaration)
	{
		if (_symbols.TryGetValue(declaration, out var existing))
			return existing;

		if (TryGetKnownSymbol(declaration, declaration.Name, out var known))
			return known;

		var symbol = new TypeSymbol
		{
			Name = declaration.Name,
			Kind = declaration.IsImmutable ? SymbolKind.ImmutableUnit : SymbolKind.Unit,
			GenericParameterNames = [.. declaration.GenericParameters.Select(parameter => parameter.Name)],
			Declaration = declaration
		};
		_symbols.Add(declaration, symbol);
		return symbol;
	}

	private bool TryGetKnownSymbol(DeclarationNode declaration, string name, out TypeSymbol symbol)
	{
		symbol = null!;
		if (_knownTypes is null || !_knownTypes.TryGetValue(name, out var known))
			return false;
		if (!ReferenceEquals(known.Declaration, declaration))
			return false;

		_symbols[declaration] = known;
		symbol = known;
		return true;
	}

	private KernType ReportUnknown(NamedTypeSyntax syntax) =>
		Report("KRN1001", $"Неизвестный тип '{syntax.Name}'", syntax.Span);

	private KernType ReportNotGeneric(NamedTypeSyntax syntax) =>
		Report("KRN1003", $"Тип '{syntax.Name}' не является generic", syntax.Span);

	private KernType ReportArity(string name, int expected, int actual, TypeSyntax syntax) =>
		Report("KRN1002", $"Тип '{name}' ожидает {expected} аргументов типа, получено {actual}", syntax.Span);

	private KernType Report(string code, string message, TextSpan span)
	{
		_diagnostics.Add(new Diagnostic(code, message, span));
		return ErrorType.Instance;
	}
}
