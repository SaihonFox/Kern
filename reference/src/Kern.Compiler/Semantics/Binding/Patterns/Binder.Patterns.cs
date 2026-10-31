using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Bound.Patterns;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Core;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Binding.Core;

public sealed partial class Binder
{
	/// <summary>
	/// Привязывает паттерн к значению типа <paramref name="subject"/>; связанные имена объявляются в текущей области
	/// и добавляются в <paramref name="bindings"/>. <paramref name="requireIrrefutable"/> — паттерн обязан всегда совпадать
	/// (`loop`, деструктуризация присваиванием).
	/// </summary>
	public BoundPattern BindPattern(Syn.Pattern p, TypeSymbol subject, List<LocalSymbol> bindings, bool requireIrrefutable)
	{
		switch (p)
		{
			case Syn.WildcardPattern w:
				return new BoundWildcardPattern(subject, w.Span);

			case Syn.BindingPattern b:
			{
				// голое имя в голове `loop`: присваивание уже объявленной переменной
				if (!b.Declares) return BindLoopAssignTarget(b, subject);

				// Одиночное имя юнита/типа (`Red -> …`) — проверка типа, как в Rust-подобных enum
				if (Scope.Lookup(b.Name) is { Kind: SymbolKind.Type })
					return BindPattern(new Syn.TypePattern(new Syn.NamedType(b.Name, [], b.Span), null, b.Span),
						subject, bindings, requireIrrefutable);
				var local = DeclareLocal(b.Name, subject, b.Span);
				Flow.Declare(local, true);
				bindings.Add(local);
				return new BoundBindingPattern(local, subject, b.Span);
			}

			case Syn.ValuePattern v:
			{
				if (requireIrrefutable)
				{
					Error(Krn.RefutablePattern, "здесь нужен паттерн, совпадающий всегда (имя, `_` или деструктуризация)", v.Span);
					return new BoundWildcardPattern(subject, v.Span);
				}
				var value = Bind(v.Value, subject);
				if (value is BoundRange) return new BoundValuePattern(value, null, subject, v.Span);
				if (!value.IsError && !subject.IsError)
				{
					if (TryConvert(value, subject, out var conv)) value = conv;
					else if (!Conversions.IsImplicit(subject, value.Type) && value is not BoundNilLiteral)
						Error(Krn.PatternTypeMismatch, $"значение типа '{value.Type.ToDisplayString()}' нельзя сопоставить с '{subject.ToDisplayString()}'", v.Span);
				}
				return new BoundValuePattern(value, null, subject, v.Span);
			}

			case Syn.TypePattern t:
			{
				var test = ResolveType(t.Type);
				if (!test.IsError && !subject.IsError)
				{
					bool related = Conversions.IsImplicit(test, subject) || Conversions.IsImplicit(subject, test)
						|| subject is PrimitiveType { Kind: Kern.Bytecode.PrimKind.Object } || subject is TypeParamType;
					if (!related)
						Error(Krn.PatternTypeMismatch, $"тип '{test.ToDisplayString()}' несовместим с типом значения '{subject.ToDisplayString()}'", t.Span);
					else if (requireIrrefutable && !Conversions.IsImplicit(subject, test))
						Error(Krn.RefutablePattern, $"'{subject.ToDisplayString()}' не всегда является '{test.ToDisplayString()}'", t.Span);
				}
				LocalSymbol? local = null;
				if (t.Name is not null)
				{
					local = DeclareLocal(t.Name, test, t.Span);
					Flow.Declare(local, true);
					bindings.Add(local);
				}
				return new BoundTypePattern(test, local, test, t.Span);
			}

			case Syn.DestructurePattern d:
				return BindDestructurePattern(d, subject, bindings, requireIrrefutable);

			case Syn.RestPattern r:
				Error(Krn.InvalidPattern, "`..rest` допустим только внутри деструктуризации", r.Span);
				return new BoundWildcardPattern(subject, r.Span);

			default:
				return new BoundWildcardPattern(subject, p.Span);
		}
	}

	/// <summary>`loop i in 0..5`, где `i` уже объявлена: значение каждой итерации присваивается ей. Объявляют `loop i32 i in …` / `loop var i in …`.</summary>
	private BoundPattern BindLoopAssignTarget(Syn.BindingPattern b, TypeSymbol subject)
	{
		if (Scope.Lookup(b.Name) is not LocalSymbol existing)
		{
			Error(Krn.NameNotFound,
				$"переменная '{b.Name}' не объявлена: для новой переменной цикла пишите `loop i32 {b.Name} in …` или `loop var {b.Name} in …`, без переменной — `loop 0..5 …`",
				b.Span);
			return new BoundWildcardPattern(subject, b.Span);
		}
		if (existing.Type is { } declared && !subject.IsError && !declared.IsError && !declared.Equals(subject)
			&& Conversions.Classify(subject, declared) is not (ConversionKind.ToNullable or ConversionKind.ToObject or ConversionKind.Subtype or ConversionKind.ToUnion))
			Error(Krn.TypeMismatch, $"переменная '{b.Name}' имеет тип '{declared.ToDisplayString()}', а элементы цикла — '{subject.ToDisplayString()}'", b.Span);
		Flow.AssignLocal(existing, b.Span, subject);
		return new BoundBindingPattern(existing, subject, b.Span, IsAssignment: true);
	}

	public BoundDestructurePattern BindDestructurePattern(Syn.DestructurePattern d, TypeSymbol subject, List<LocalSymbol> bindings, bool requireIrrefutable)
	{
		var elementsSyntax = d.Elements;
		int restIndex = -1;
		for (int i = 0; i < elementsSyntax.Count; i++)
			if (elementsSyntax[i] is Syn.RestPattern) { restIndex = i; break; }

		DestructureKind kind;
		TypeSymbol test = subject;
		var members = new List<Symbol>();
		var itemTypes = new List<TypeSymbol>();
		TypeSymbol? restElement = null;

		if (d.Type is null)
		{
			var s = subject is NullableType ? subject : subject;
			switch (s)
			{
				case TupleType tt:
					kind = DestructureKind.Tuple;
					itemTypes.AddRange(tt.Items);
					break;
				case ListType lt:
					kind = DestructureKind.Collection; restElement = lt.Element; break;
				case ArrayType at:
					kind = DestructureKind.Collection; restElement = at.Element; break;
				case DeclaredType { Decl.PrimaryConstructor: { } upc } udt:
				{
					// `(f64 x, f64 y) = point` — разложение unit/класса по параметрам первичного конструктора
					kind = DestructureKind.Object;
					var umap = udt.TypeMap();
					foreach (var prm in upc.Parameters)
					{
						var field = (Symbol?)prm.Field ?? udt.Decl.Properties.FirstOrDefault(x => x.Name == prm.Name);
						members.Add(field ?? prm);
						itemTypes.Add(umap.Count == 0 ? prm.Type : prm.Type.Substitute(umap));
					}
					break;
				}
				default:
					if (!s.IsError)
						Error(Krn.InvalidPattern, $"значение типа '{s.ToDisplayString()}' нельзя разложить по `(…)`", d.Span);
					return BindElementsAsErrors(d, subject, bindings);
			}
		}
		else if (d.Type is Syn.NamedType { Name: "Ok" or "Err" } named && subject is ResultType rt)
		{
			bool ok = named.Name == "Ok";
			kind = ok ? DestructureKind.ResultOk : DestructureKind.ResultErr;
			itemTypes.Add(ok ? rt.Ok : rt.Err);
		}
		else
		{
			test = ResolveType(d.Type);
			kind = DestructureKind.Object;
			if (test is DeclaredType dt && dt.Decl.PrimaryConstructor is { } pc)
			{
				var map = dt.TypeMap();
				foreach (var prm in pc.Parameters)
				{
					var field = (Symbol?)prm.Field ?? dt.Decl.Properties.FirstOrDefault(x => x.Name == prm.Name);
					members.Add(field ?? prm);
					itemTypes.Add(map.Count == 0 ? prm.Type : prm.Type.Substitute(map));
				}
			}
			else if (!test.IsError)
			{
				Error(Krn.InvalidPattern, $"у типа '{test.ToDisplayString()}' нет первичного конструктора — деструктурировать нельзя", d.Span);
				return BindElementsAsErrors(d, subject, bindings);
			}
			if (!test.IsError && !subject.IsError && !Conversions.IsImplicit(test, subject) && !Conversions.IsImplicit(subject, test))
				Error(Krn.PatternTypeMismatch, $"тип '{test.ToDisplayString()}' несовместим с типом значения '{subject.ToDisplayString()}'", d.Span);
			else if (requireIrrefutable && !subject.IsError && !test.IsError && !Conversions.IsImplicit(subject, test))
				Error(Krn.RefutablePattern, $"'{subject.ToDisplayString()}' не всегда является '{test.ToDisplayString()}'", d.Span);
		}

		// структурный паттерн по именам полей: `Point(x: 0, y: var y)` → позиционный список (пропущенные поля — `_`)
		if (elementsSyntax.Any(x => x is Syn.NamedPattern))
		{
			elementsSyntax = ReorderNamedFields(d, elementsSyntax, kind, members);
			restIndex = -1;
		}

		// число элементов
		int nonRest = elementsSyntax.Count - (restIndex >= 0 ? 1 : 0);
		if (kind != DestructureKind.Collection)
		{
			bool bad = restIndex >= 0 ? nonRest > itemTypes.Count : nonRest != itemTypes.Count;
			if (bad)
				Error(Krn.DestructureCountMismatch, $"в паттерне элементов: {nonRest}, у значения: {itemTypes.Count}", d.Span);
		}

		var elements = new List<BoundPattern>();
		for (int i = 0; i < elementsSyntax.Count; i++)
		{
			var e = elementsSyntax[i];
			if (e is Syn.RestPattern rp)
			{
				TypeSymbol listType = rp.Type is not null ? ResolveType(rp.Type)
					: restElement is not null ? new ListType(restElement) : (TypeSymbol)ErrorType.Instance;
				LocalSymbol? local = null;
				if (rp.Name is not null)
				{
					local = DeclareLocal(rp.Name, listType, rp.Span);
					Flow.Declare(local, true);
					bindings.Add(local);
				}
				elements.Add(new BoundRestPattern(local, listType, rp.Span));
				continue;
			}
			TypeSymbol itemType = kind == DestructureKind.Collection
				? restElement ?? ErrorType.Instance
				: i < itemTypes.Count ? itemTypes[i] : ErrorType.Instance;
			elements.Add(BindPattern(e, itemType, bindings, requireIrrefutable));
		}
		return new BoundDestructurePattern(kind, test, members, elements, subject, d.Span);
	}

	private IReadOnlyList<Syn.Pattern> ReorderNamedFields(Syn.DestructurePattern d, IReadOnlyList<Syn.Pattern> elements, DestructureKind kind, List<Symbol> members)
	{
		if (kind != DestructureKind.Object)
		{
			Error(Krn.InvalidPattern, "паттерн `имя: …` допустим только для unit и классов с первичным конструктором", d.Span);
			return [.. elements.Select(e => e is Syn.NamedPattern n ? n.Inner : e)];
		}
		var slots = new Syn.Pattern?[members.Count];
		foreach (var e in elements)
		{
			if (e is Syn.RestPattern { Name: null }) continue;   // `..` — «остальные поля», и так `_`
			if (e is not Syn.NamedPattern np)
			{
				Error(Krn.InvalidPattern, "в паттерне с именами полей все элементы должны быть вида `имя: паттерн` (или `..`)", e.Span);
				continue;
			}
			int idx = members.FindIndex(m => m.Name == np.Name);
			if (idx < 0) Error(Krn.InvalidPattern, $"в типе нет поля '{np.Name}'", np.Span);
			else if (slots[idx] is not null) Error(Krn.InvalidPattern, $"поле '{np.Name}' указано дважды", np.Span);
			else slots[idx] = np.Inner;
		}
		var result = new List<Syn.Pattern>(members.Count);
		for (int i = 0; i < slots.Length; i++) result.Add(slots[i] ?? new Syn.WildcardPattern(new TextSpan(d.Span.Start, 0)));
		return result;
	}

	private BoundDestructurePattern BindElementsAsErrors(Syn.DestructurePattern d, TypeSymbol subject, List<LocalSymbol> bindings)
	{
		var elements = new List<BoundPattern>();
		foreach (var e in d.Elements)
		{
			if (e is Syn.RestPattern rp)
			{
				LocalSymbol? local = null;
				if (rp.Name is not null) { local = DeclareLocal(rp.Name, ErrorType.Instance, rp.Span); bindings.Add(local); }
				elements.Add(new BoundRestPattern(local, ErrorType.Instance, rp.Span));
			}
			else elements.Add(BindPattern(e, ErrorType.Instance, bindings, false));
		}
		return new BoundDestructurePattern(DestructureKind.Object, ErrorType.Instance, [], elements, subject, d.Span);
	}
}
