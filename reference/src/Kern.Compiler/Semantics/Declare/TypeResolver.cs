using Kern.Compiler.Semantics.Scopes;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Core;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Declare;

/// <summary>Разрешает синтаксические ссылки на типы (<see cref="Syn.TypeRef"/>) в <see cref="TypeSymbol"/>.</summary>
public sealed class TypeResolver(ModuleTable modules, NativeBinder binder, DiagnosticBag diags)
{
	/// <summary>Проверять ли ограничения generic-аргументов (включается после разрешения заголовков типов).</summary>
	public bool CheckConstraints { get; set; }

	public DiagnosticBag Diagnostics { get; } = diags;
	public ModuleTable Modules { get; } = modules;
	public NativeBinder Binder { get; } = binder;

	// ------------------------------------------------------------ типовые ссылки

	public TypeSymbol Resolve(Syn.TypeRef? type, Scope scope, SourceFile file)
	{
		switch (type)
		{
			case null:
				return PrimitiveType.Void;
			case Syn.NamedType nt:
				return ResolveNamed(nt, scope, file);
			case Syn.NullableType n:
				return NullableType.Of(Resolve(n.Inner, scope, file));
			case Syn.FuncType f:
				return new FuncType([.. f.Params.Select(p => Resolve(p, scope, file))], f.Return is null ? PrimitiveType.Void : Resolve(f.Return, scope, file));
			case Syn.TupleType t:
				return new TupleType([.. t.Elements.Select(e => Resolve(e.Type, scope, file))], [.. t.Elements.Select(e => e.Name)]);
			case Syn.ArrayType a:
				return new ArrayType(Resolve(a.Element, scope, file));
			case Syn.UnionType u:
				Diagnostics.Error(Krn.NotAType, "объединение `[A, B]` допустимо только справа в объявлении `type Имя = [A, B]`", file, u.Span);
				return ErrorType.Instance;
			default:
				return ErrorType.Instance;
		}
	}

	public IReadOnlyList<TypeSymbol> ResolveAll(IEnumerable<Syn.TypeRef> types, Scope scope, SourceFile file) =>
		[.. types.Select(t => Resolve(t, scope, file))];

	private TypeSymbol ResolveNamed(Syn.NamedType nt, Scope scope, SourceFile file)
	{
		if (nt.Name == "<missing>") return ErrorType.Instance; // ошибка парсера уже репортнута

		if (!nt.Name.Contains('.'))
		{
			// Встроенные имена видны всегда
			if (PrimitiveType.FromName(nt.Name) is { } prim)
			{
				if (nt.TypeArgs.Count > 0)
				{
					Diagnostics.Error(Krn.TypeArgsOnNonGeneric, $"тип '{nt.Name}' не имеет generic-параметров", file, nt.Span);
					return ErrorType.Instance;
				}
				return prim;
			}
			int arity = BuiltinGenericType.ArityOf(nt.Name);
			if (arity >= 0)
			{
				if (nt.TypeArgs.Count != arity)
				{
					Diagnostics.Error(Krn.WrongTypeArgCount, $"тип '{nt.Name}' ожидает аргументов: {arity}, указано: {nt.TypeArgs.Count}", file, nt.Span);
					return ErrorType.Instance;
				}
				var args = ResolveAll(nt.TypeArgs, scope, file);
				return (TypeSymbol?)BuiltinGenericType.Create(nt.Name, args) ?? ErrorType.Instance;
			}

			var sym = scope.LookupType(nt.Name);
			if (sym is null)
			{
				if (scope.Lookup(nt.Name) is { } other && other.Kind != SymbolKind.TypeParam)
					Diagnostics.Error(Krn.NotAType, $"'{nt.Name}' не является типом", file, nt.Span);
				else
					Diagnostics.Error(Krn.TypeNotFound, $"тип '{nt.Name}' не найден", file, nt.Span);
				return ErrorType.Instance;
			}
			return Instantiate(sym, nt, scope, file);
		}

		var found = ResolveQualified(nt.Name.Split('.'), scope);
		if (found is null)
		{
			Diagnostics.Error(Krn.TypeNotFound, $"тип '{nt.Name}' не найден", file, nt.Span);
			return ErrorType.Instance;
		}
		return Instantiate(found, nt, scope, file);
	}

	/// <summary>Подставляет аргументы в найденное объявление типа и проверяет арность/ограничения.</summary>
	private TypeSymbol Instantiate(Symbol sym, Syn.NamedType nt, Scope scope, SourceFile file)
	{
		switch (sym)
		{
			case TypeParamSymbol tp:
				if (nt.TypeArgs.Count > 0)
				{
					Diagnostics.Error(Krn.TypeArgsOnNonGeneric, $"параметр типа '{tp.Name}' не принимает аргументов", file, nt.Span);
					return ErrorType.Instance;
				}
				return tp.AsType;

			case TypeDeclSymbol d:
			{
				if (nt.TypeArgs.Count != d.TypeParams.Count)
				{
					Diagnostics.Error(Krn.WrongTypeArgCount,
						$"тип '{d.Name}' ожидает generic-аргументов: {d.TypeParams.Count}, указано: {nt.TypeArgs.Count}", file, nt.Span);
					return ErrorType.Instance;
				}
				var args = ResolveAll(nt.TypeArgs, scope, file);
				if (CheckConstraints) CheckArgs(d.Name, d.TypeParams, args, file, nt.Span);
				return args.Count == 0 ? d.ThisType : d.Instantiate(args);
			}

			case NativeTypeBinding b:
			{
				int n = b.Native.GenericParams.Count;
				if (nt.TypeArgs.Count != n)
				{
					Diagnostics.Error(Krn.WrongTypeArgCount,
						$"тип '{b.Name}' ожидает generic-аргументов: {n}, указано: {nt.TypeArgs.Count}", file, nt.Span);
					return ErrorType.Instance;
				}
				var args = ResolveAll(nt.TypeArgs, scope, file);
				if (CheckConstraints) CheckArgs(b.Name, Binder.TypeParamsOf(b.Native), args, file, nt.Span);
				return Binder.TypeOf(b.Native, args);
			}

			default:
				Diagnostics.Error(Krn.NotAType, $"'{sym.Name}' не является типом", file, nt.Span);
				return ErrorType.Instance;
		}
	}

	private void CheckArgs(string name, IReadOnlyList<TypeParamSymbol> ps, IReadOnlyList<TypeSymbol> args, SourceFile file, TextSpan span)
	{
		if (ps.Count == 0) return;
		var map = new Dictionary<TypeParamSymbol, TypeSymbol>();
		for (int i = 0; i < ps.Count && i < args.Count; i++) map[ps[i]] = args[i];
		for (int i = 0; i < ps.Count && i < args.Count; i++)
			if (!Conversions.SatisfiesConstraints(ps[i], args[i], map, out var reason))
				Diagnostics.Error(Krn.ConstraintViolated, $"{reason} (тип '{name}')", file, span);
	}

	// ------------------------------------------------------------ квалифицированные имена

	/// <summary>`a.b.Type`, `alias.Type`, `Outer.Inner`. null — не найдено.</summary>
	public Symbol? ResolveQualified(string[] segments, Scope scope)
	{
		string last = segments[^1];

		// 1) алиас модуля первым сегментом: `io.File`, `IO.path.Thing`
		if (scope.Lookup(segments[0]) is ModuleAliasSymbol alias)
		{
			string path = alias.Path;
			for (int i = 1; i < segments.Length - 1; i++) path += "." + segments[i];
			return LookupInModule(path, last);
		}

		// 2) вложенный тип `Outer.Inner`
		if (scope.LookupType(segments[0]) is TypeDeclSymbol outer)
		{
			Symbol? cur = outer;
			for (int i = 1; i < segments.Length && cur is TypeDeclSymbol td; i++)
				cur = td.MemberScope?.LookupLocal(segments[i]);
			if (cur is TypeDeclSymbol) return cur;
		}

		// 3) полный путь модуля
		return LookupInModule(string.Join('.', segments[..^1]), last);
	}

	/// <summary>Имя типа в модуле: пользовательский модуль, нативный Kern-модуль или dotnet-пространство.</summary>
	public Symbol? LookupInModule(string modulePath, string name)
	{
		if (Modules.Find(modulePath) is { } m && m.Scope.LookupLocal(name) is { } s && s.Access != Syn.Access.Priv)
			return s;
		if (Binder.Source.FindType(modulePath, name) is { } nt) return new NativeTypeBinding(name, nt);
		if (modulePath.StartsWith("dotnet.", StringComparison.Ordinal) && Binder.Source.FindDotnetType(modulePath[7..], name) is { } dt)
			return new NativeTypeBinding(name, dt);
		return null;
	}

	// ------------------------------------------------------------ generic-параметры

	/// <summary>Шаг 1: создаёт символы параметров и область, в которой они видны.</summary>
	public Scope DeclareTypeParams(
		IReadOnlyList<Syn.TypeParam> syntax, Symbol owner, List<TypeParamSymbol> into, Scope parent, SourceFile file)
	{
		var scope = new Scope(parent, $"generic {owner.Name}");
		foreach (var tp in syntax)
		{
			var sym = new TypeParamSymbol(tp.Name, into.Count, owner) { File = file, Span = tp.Span, Container = owner };
			if (scope.Declare(sym) is not null)
			{
				Diagnostics.Error(Krn.DuplicateTypeParam, $"параметр типа '{tp.Name}' объявлен повторно", file, tp.Span);
				continue;
			}
			into.Add(sym);
		}
		return scope;
	}

	/// <summary>Шаг 2: разрешает ограничения `T[IHashable]`, `V[ctor(i32)]` (после того, как видны все типы).</summary>
	public void ResolveConstraints(IReadOnlyList<Syn.TypeParam> syntax, IReadOnlyList<TypeParamSymbol> symbols, Scope scope, SourceFile file)
	{
		foreach (var tp in syntax)
		{
			var sym = symbols.FirstOrDefault(s => s.Name == tp.Name);
			if (sym is null) continue;
			foreach (var c in tp.Constraints)
			{
				switch (c)
				{
					case Syn.BoundConstraint bc:
						sym.Bounds.Add(Resolve(bc.Type, scope, file));
						break;
					case Syn.CtorConstraint cc:
						sym.CtorConstraints.Add(ResolveAll(cc.ParamTypes, scope, file));
						break;
				}
			}
		}
	}
}
