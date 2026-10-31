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
	private readonly Func<string, TypeSymbol?>? _fallback;
	private readonly IReadOnlyDictionary<DeclarationNode, TypeSymbol>? _primes;
	private readonly Dictionary<DeclarationNode, TypeSymbol> _symbols = new(ReferenceEqualityComparer.Instance);
	private readonly List<Diagnostic> _diagnostics = [];
	private readonly HashSet<DeclarationNode> _basesInProgress = new(ReferenceEqualityComparer.Instance);
	private readonly HashSet<DeclarationNode> _reportedCycles = new(ReferenceEqualityComparer.Instance);
	private bool _cycleDetected;

	public TypeResolver(
		SymbolTable table,
		IReadOnlyDictionary<string, TypeSymbol>? knownTypes = null,
		Func<string, TypeSymbol?>? fallback = null,
		IReadOnlyDictionary<DeclarationNode, TypeSymbol>? primes = null)
	{
		_table = table;
		_knownTypes = knownTypes;
		_fallback = fallback;
		_primes = primes;
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

	/// <summary>
	/// Имя обозначает тип в этом модуле, каталоге или импорте (spec §10): нужно, чтобы
	/// отличить свободную переменную приёмника расширения от настоящего типа.
	/// </summary>
	public bool IsTypeName(string name) =>
		TypeCatalog.TryGetPrimitive(name, out _) ||
		TypeCatalog.TryGetBuiltin(name, out _) ||
		_table.GetSymbols(name).Any(symbol => symbol.Declaration is TypeDeclaration or UnitDeclaration) ||
		(_knownTypes is not null && _knownTypes.ContainsKey(name)) ||
		_fallback?.Invoke(name) is not null;

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
		// 0. Метки generic-аргументов допустимы только в аргументах result (spec §16).
		if (syntax.Name != ResultTypes.ResultName && syntax.HasArgumentLabels)
		{
			var label = syntax.ArgumentLabels.First(item => item is not null);
			return Report(
				"KRN1007",
				$"Метка '{label}' недопустима: метки разрешены только в аргументах result (первая — ok, вторая — error)",
				syntax.Span);
		}

		// 1. Примитивы не перекрываются даже объявлением типа в модуле.
		if (TypeCatalog.TryGetPrimitive(syntax.Name, out var primitive))
			return syntax.Arguments.Count == 0 ? primitive : ReportNotGeneric(syntax);

		// 2. Параметры обобщения текущего объявления.
		if (scope.TryLookup(syntax.Name, out var parameter))
			return syntax.Arguments.Count == 0 ? parameter : ReportNotGeneric(syntax);

		// 2a. result<T, E> — union из встроенных unit Ok<T>/Err<E> (spec §16).
		if (syntax.Name == ResultTypes.ResultName)
		{
			if (syntax.Arguments.Count != 2)
				return ReportArity(ResultTypes.ResultName, 2, syntax.Arguments.Count, syntax);

			if (!ValidateResultLabels(syntax))
				return ErrorType.Instance;

			var resultArguments = ResolveArguments(syntax, scope);
			if (resultArguments is null)
				return ErrorType.Instance;

			var okArgument = CheckResultConstraint(syntax, 0, resultArguments[0]);
			var errorArgument = CheckResultConstraint(syntax, 1, resultArguments[1]);
			if (okArgument is null || errorArgument is null)
				return ErrorType.Instance;

			return ResultTypes.CreateType(okArgument, errorArgument);
		}

		// 2b. Базовые варианты result: ok/error (маркеры), ok<T> и error<E> (spec §16).
		if (syntax.Name is ResultTypes.OkBaseName or ResultTypes.ErrBaseName)
		{
			var marker = syntax.Name == ResultTypes.OkBaseName ? ResultTypes.OkMarker : ResultTypes.ErrMarker;
			if (syntax.Arguments.Count == 0)
				return new ClassType(marker, []);

			if (syntax.Arguments.Count != 1)
				return ReportArity(syntax.Name, 1, syntax.Arguments.Count, syntax);

			var baseArguments = ResolveArguments(syntax, scope);
			if (baseArguments is null)
				return ErrorType.Instance;

			var symbol = syntax.Name == ResultTypes.OkBaseName ? ResultTypes.Ok : ResultTypes.Err;
			return new UnitType(symbol, [baseArguments[0]], false);
		}

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

		// 3a. Встроенные/нативные типы каталога (spec §12.9): у символа нет объявления в
		// исходнике, поэтому таблица символов его не видит, а имя должно быть допустимо.
		if (_knownTypes is not null && _knownTypes.TryGetValue(syntax.Name, out var known))
		{
			IReadOnlyList<KernType>? resolved =
				syntax.Arguments.Count == 0 ? [] : ResolveArguments(syntax, scope);
			if (resolved is null)
				return ErrorType.Instance;
			var arguments = resolved;
			if (syntax.Arguments.Count > 0 && known.GenericArity != arguments.Count)
				return ReportArity(syntax.Name, known.GenericArity, syntax.Arguments.Count, syntax);

			return known.Kind switch
			{
				SymbolKind.Unit => new UnitType(known, arguments, false),
				SymbolKind.ImmutableUnit => new UnitType(known, arguments, true),
				SymbolKind.Class or SymbolKind.AbstractClass or SymbolKind.Native =>
					new ClassType(known, arguments),
				SymbolKind.UnionAlias when known.UnionAlternatives.Count > 0 =>
					ResolveGenericUnionAlias(known, arguments, syntax),
				_ => ReportUnknown(syntax),
			};
		}

		// 3b. Импортированные типы (M3-F): символ найден по области видимости модуля.
		if (_fallback is not null && _fallback(syntax.Name) is { } imported)
		{
			var arguments = syntax.Arguments.Count == 0 ? [] : ResolveArguments(syntax, scope);
			if (arguments is null && syntax.Arguments.Count > 0)
				return ErrorType.Instance;

			return imported.Kind switch
			{
				SymbolKind.Unit => new UnitType(imported, arguments ?? [], false),
				SymbolKind.ImmutableUnit => new UnitType(imported, arguments ?? [], true),
				SymbolKind.Class or SymbolKind.AbstractClass or SymbolKind.Native =>
					new ClassType(imported, arguments ?? []),
				SymbolKind.UnionAlias when imported.UnionAlternatives.Count > 0 =>
					ResolveGenericUnionAlias(imported, arguments ?? [], syntax),
				_ => ReportUnknown(syntax),
			};
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
			return ResolveUnionAliasDeclaration(declaration, syntax, scope);

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

	/// <summary>
	/// Generic union-алиас <c>type Option&lt;T&gt; = [Some&lt;T&gt;, Nothing]</c> (spec §14):
	/// альтернативы подставляются с аргументами (<c>Option&lt;i32&gt;</c> → <c>Some&lt;i32&gt; | Nothing</c>).
	/// </summary>
	private KernType ResolveUnionAliasDeclaration(TypeDeclaration declaration, NamedTypeSyntax syntax, TypeParameterScope scope)
	{
		if (declaration.GenericArity == 0 && syntax.Arguments.Count > 0)
			return ReportNotGeneric(syntax);

		var arity = declaration.GenericArity;
		if (syntax.Arguments.Count != arity)
			return ReportArity(syntax.Name, arity, syntax.Arguments.Count, syntax);

		var symbol = GetOrCreateSymbol(declaration);
		var arguments = syntax.Arguments.Count == 0 ? [] : ResolveArguments(syntax, scope);
		if (arguments is null)
			return ErrorType.Instance;

		var alternatives = symbol.UnionAlternatives.Count > 0
			? symbol.UnionAlternatives
			: ResolveAll(declaration.DeclaredUnionVariants, CreateScope(declaration.GenericParameters));
		return UnionType.Create(SubstituteAlias(alternatives, symbol.GenericParameterNames, arguments));
	}

	/// <summary>Union-алиас из каталога/импорта: арность и подстановка как у объявленного.</summary>
	private KernType ResolveGenericUnionAlias(TypeSymbol symbol, IReadOnlyList<KernType> arguments, NamedTypeSyntax syntax)
	{
		if (symbol.GenericArity != arguments.Count)
			return ReportArity(syntax.Name, symbol.GenericArity, arguments.Count, syntax);

		return UnionType.Create(SubstituteAlias(symbol.UnionAlternatives, symbol.GenericParameterNames, arguments));
	}

	/// <summary>Подстановка аргументов generic-алиаса в его альтернативы; пустые аргументы — без изменений.</summary>
	private static IReadOnlyList<KernType> SubstituteAlias(
		IReadOnlyList<KernType> alternatives,
		IReadOnlyList<string> names,
		IReadOnlyList<KernType> arguments)
	{
		var count = System.Math.Min(names.Count, arguments.Count);
		if (count == 0)
			return alternatives;

		var substitution = new Dictionary<string, KernType>(count, StringComparer.Ordinal);
		for (var index = 0; index < count; index++)
			substitution[names[index]] = arguments[index];

		return [.. alternatives.Select(alternative => TypeSubstitution.Apply(alternative, substitution))];
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

	/// <summary>
	/// Метки <c>result</c>: первая — <c>ok</c>, вторая — <c>error</c> (spec §16). Нарушение
	/// имени или позиции — <c>KRN1007</c>; возвращает false, чтобы разрешение остановилось.
	/// </summary>
	private bool ValidateResultLabels(NamedTypeSyntax syntax)
	{
		for (var index = 0; index < syntax.ArgumentLabels.Count; index++)
		{
			if (syntax.ArgumentLabels[index] is not { } label)
				continue;
			var expected = index switch
			{
				0 => ResultTypes.OkBaseName,
				1 => ResultTypes.ErrBaseName,
				_ => null
			};
			if (label == expected)
				continue;

			Report(
				"KRN1007",
				$"Метка '{label}' недопустима: аргументы result принимают 'ok' (первый) и 'error' (второй)",
				syntax.Span);
			return false;
		}

		return true;
	}

	/// <summary>
	/// Ограничение помеченного аргумента <c>result</c>: тип должен наследовать маркер
	/// <c>ok</c>/<c>error</c> (spec §16), иначе <c>KRN1008</c>. Возвращает null при ошибке.
	/// </summary>
	private KernType? CheckResultConstraint(NamedTypeSyntax syntax, int index, KernType argument)
	{
		if (index >= syntax.ArgumentLabels.Count || syntax.ArgumentLabels[index] is null)
			return argument;

		var marker = index == 0 ? ResultTypes.OkMarker : ResultTypes.ErrMarker;
		if (argument is ClassType @class && Inheritance.SymbolChain(@class.Symbol).Contains(marker))
			return argument;

		Report(
			"KRN1008",
			$"Аргумент {index + 1} типа '{argument}' не наследует маркер '{marker.Name}'",
			syntax.Span);
		return null;
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

		// Предварительно созданный символ проекта (M3-F): сохраняем идентичность.
		if (_primes is not null && _primes.TryGetValue(declaration, out var primed))
		{
			_symbols.Add(declaration, primed);
			EnsureGenericParameters(primed, declaration.GenericParameters);
			if (declaration.DeclaredUnionVariants.Count == 0)
				PopulateBases(declaration, primed);
			return primed;
		}

		var kind = declaration.DeclaredUnionVariants.Count > 0
			? SymbolKind.UnionAlias
			: declaration.IsAbstract ? SymbolKind.AbstractClass : SymbolKind.Class;
		var symbol = new TypeSymbol
		{
			Name = declaration.Name,
			Kind = kind,
			GenericParameterNames = [.. declaration.GenericParameters.Select(parameter => parameter.Name)],
			GenericParameters = [.. declaration.GenericParameters.Select(parameter => new TypeParameterType(parameter.Name, []))],
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

		if (_primes is not null && _primes.TryGetValue(declaration, out var primed))
		{
			_symbols.Add(declaration, primed);
			EnsureGenericParameters(primed, declaration.GenericParameters);
			return primed;
		}

		var symbol = new TypeSymbol
		{
			Name = declaration.Name,
			Kind = declaration.IsImmutable ? SymbolKind.ImmutableUnit : SymbolKind.Unit,
			GenericParameterNames = [.. declaration.GenericParameters.Select(parameter => parameter.Name)],
			GenericParameters = [.. declaration.GenericParameters.Select(parameter => new TypeParameterType(parameter.Name, []))],
			Declaration = declaration
		};
		_symbols.Add(declaration, symbol);
		return symbol;
	}

	private static void EnsureGenericParameters(TypeSymbol symbol, IReadOnlyList<GenericParameterNode> parameters)
	{
		if (symbol.GenericParameters.Count > 0 || parameters.Count == 0)
			return;

		symbol.GenericParameters = [.. parameters.Select(parameter => new TypeParameterType(parameter.Name, []))];
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
