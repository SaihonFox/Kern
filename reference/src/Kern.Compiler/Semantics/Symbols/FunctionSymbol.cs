using Kern.Binding.Catalog;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using FuncType = Kern.Compiler.Semantics.Types.FuncType;

namespace Kern.Compiler.Semantics.Symbols;

public enum FunctionKind { Function, Method, Constructor, Operator, Getter, Setter, Init }

/// <summary>Параметр функции/конструктора. Для variadic <see cref="Type"/> — тип элемента.</summary>
public sealed class ParameterSymbol(string name, TypeSymbol type, int index) : Symbol(name)
{
	public override SymbolKind Kind => SymbolKind.Parameter;
	public TypeSymbol Type { get; set; } = type;
	public int Index { get; } = index;
	public Expr? Default { get; set; }
	public bool IsVariadic { get; set; }
	/// <summary>`ctor(str $name)` — автоприсваивание полю.</summary>
	public bool AutoInit { get; set; }
	/// <summary>Модификаторы параметра первичного конструктора (`pub str name`).</summary>
	public Modifiers Mods { get; set; }
	/// <summary>Поле, порождённое параметром первичного конструктора.</summary>
	public FieldSymbol? Field { get; set; }
	/// <summary>Для нативных параметров — значение по умолчанию задано, но выражения нет.</summary>
	public bool HasNativeDefault { get; set; }

	public bool HasDefault => Default is not null || HasNativeDefault;
	/// <summary>Тип параметра внутри тела (variadic → list&lt;T&gt;).</summary>
	public TypeSymbol EffectiveType => IsVariadic ? new ListType(Type) : Type;
	public override string FullName => Name;
}

/// <summary>
/// Функция, метод, конструктор, оператор или аксессор. ОТДЕЛЬНЫЙ символ на каждую перегрузку.
/// Идентичность перегрузки = Name + generic-арность + типы параметров (возвращаемый тип не участвует).
/// </summary>
public class FunctionSymbol(string name, FunctionKind functionKind) : Symbol(name)
{
	public override SymbolKind Kind => SymbolKind.Function;
	public FunctionKind FunctionKind { get; } = functionKind;

	public List<TypeParamSymbol> TypeParams { get; } = [];
	public List<ParameterSymbol> Parameters { get; } = [];
	/// <summary>null — тип не указан в исходнике (выводится по телу, часть 2).</summary>
	public TypeSymbol? ReturnType { get; set; }

	public bool IsAsync { get; set; }
	public bool IsStatic { get; set; }
	public bool IsOpen { get; set; }
	public bool IsOverride { get; set; }
	public bool IsAbstract { get; set; }
	/// <summary>Были ли скобки параметров (`fun length -> f64` — без них).</summary>
	public bool HasParams { get; set; } = true;
	public bool IsPrimaryCtor { get; set; }

	/// <summary>Расширяемый тип: `fun str.shout()`, `op i32.*(str)`. null — не расширение.</summary>
	public TypeSymbol? ExtensionOf { get; set; }
	/// <summary>Для операторов: `+`, `==`, `in`, `[]`…</summary>
	public string? OpSymbol { get; set; }
	/// <summary>Свойство, которому принадлежит аксессор.</summary>
	public PropertySymbol? Property { get; set; }
	/// <summary>Выбор родителя `fun $[A].foo(...)`: имя и разрешённый тип родителя.</summary>
	public string? ParentSelector { get; set; }
	public TypeSymbol? ParentSelectorType { get; set; }

	/// <summary>AST объявления (FunDecl, ExtensionFunDecl, OpDecl, CtorDecl, Accessor…).</summary>
	public Node? Syntax { get; set; }
	public Expr? Body { get; set; }

	public bool IsConstructor => FunctionKind == FunctionKind.Constructor;
	public bool IsOperator => FunctionKind == FunctionKind.Operator;
	public bool IsGetter => FunctionKind == FunctionKind.Getter;
	public bool IsSetter => FunctionKind == FunctionKind.Setter;
	public bool IsInit => FunctionKind == FunctionKind.Init;
	public bool IsAccessor => IsGetter || IsSetter || IsInit;
	public bool IsExtension => ExtensionOf is not null;
	public bool IsNative => this is NativeMemberSymbol;

	public int Arity => TypeParams.Count;
	public int RequiredParameterCount => Parameters.Count(p => !p.HasDefault && !p.IsVariadic);
	public IEnumerable<TypeSymbol> ParameterTypes => Parameters.Select(p => p.Type);

	/// <summary>Тип функции как значения (для ссылок на функцию и вывода делегатов).</summary>
	public FuncType AsFuncType() => new([.. Parameters.Select(p => p.EffectiveType)], ReturnType ?? PrimitiveType.Void);

	/// <summary>
	/// Совпадает ли сигнатура: имя, арность, типы параметров (generic-параметры сопоставляются по позиции),
	/// variadic, получатель расширения. Возвращаемый тип не учитывается.
	/// </summary>
	public bool HasSameSignature(FunctionSymbol other)
	{
		if (Name != other.Name || Arity != other.Arity || Parameters.Count != other.Parameters.Count) return false;
		if (IsExtension != other.IsExtension) return false;
		if (FunctionKind != other.FunctionKind && (IsAccessor || other.IsAccessor)) return false;

		IReadOnlyDictionary<TypeParamSymbol, TypeSymbol> map = TypeParams.Count == 0
			? new Dictionary<TypeParamSymbol, TypeSymbol>()
			: TypeParams.Zip(other.TypeParams, (mine, theirs) => (theirs, mine)).ToDictionary(t => t.theirs, t => (TypeSymbol)t.mine.AsType);

		if (IsExtension && !ExtensionOf!.Equals(other.ExtensionOf!.Substitute(map))) return false;
		for (int i = 0; i < Parameters.Count; i++)
		{
			var a = Parameters[i];
			var b = other.Parameters[i];
			if (a.IsVariadic != b.IsVariadic || !a.Type.Equals(b.Type.Substitute(map))) return false;
		}
		return true;
	}

	/// <summary>`имя<T>(i32, str)` для сообщений.</summary>
	public string ToDisplayString()
	{
		var sb = new System.Text.StringBuilder();
		if (ExtensionOf is not null) sb.Append(ExtensionOf.ToDisplayString()).Append('.');
		else if (DeclaringType is { } t && FunctionKind != FunctionKind.Function) sb.Append(t.Name).Append('.');
		sb.Append(IsConstructor ? "ctor" : Name);
		if (TypeParams.Count > 0) sb.Append('<').Append(string.Join(", ", TypeParams.Select(p => p.Name))).Append('>');
		if (HasParams || Parameters.Count > 0)
			sb.Append('(').Append(string.Join(", ", Parameters.Select(p => (p.IsVariadic ? ".." : "") + p.Type.ToDisplayString()))).Append(')');
		if (ReturnType is { } r && r != PrimitiveType.Void) sb.Append(" -> ").Append(r.ToDisplayString());
		return sb.ToString();
	}

	public override string ToString() => ToDisplayString();
}

/// <summary>Нативный член из каталога, представленный как функция (метод, ctor, оператор, статик, аксессор).</summary>
public sealed class NativeMemberSymbol(string name, FunctionKind functionKind, NativeMember member) : FunctionSymbol(name, functionKind)
{
	public NativeMember Member { get; } = member;
}
