using Kern.Bytecode;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Declarations;

/// <summary>
/// Функция, метод, конструктор, оператор или accessor. Идентичность перегрузки —
/// имя + число generic-параметров + канонические типы параметров (spec §15);
/// возвращаемый тип в идентичность не входит.
/// </summary>
public sealed class FunctionSymbol(string name, FunctionKind kind)
{
	public string Name { get; } = name;

	public FunctionKind Kind { get; } = kind;

	public IReadOnlyList<TypeParameterType> GenericParameters { get; init; } = [];

	public IReadOnlyList<ParameterSymbol> Parameters { get; init; } = [];

	/// <summary>null — тип не указан; для функции это void либо вывод из тела binder'ом.</summary>
	public KernType? ReturnType { get; init; }

	/// <summary>Тип-владелец; null для top-level функции или расширения.</summary>
	public TypeSymbol? Owner { get; init; }

	/// <summary>Тип-получатель расширения (<c>fun str.shout</c>); null для обычной функции.</summary>
	public KernType? ReceiverType { get; init; }

	/// <summary>
	/// Тип получателя нативного члена (`str`, `list&lt;T&gt;`). В отличие от
	/// <see cref="ReceiverType"/> задаётся и для встроенных типов, у которых нет TypeSymbol.
	/// </summary>
	public KernType? NativeSelfType { get; init; }

	/// <summary>Ключ нативной привязки; null у обычной функции (spec §12).</summary>
	public string? NativeKey { get; init; }

	public bool IsNative => NativeKey is not null;

	public Accessibility Accessibility { get; init; }

	public bool IsStatic { get; init; }

	public bool IsAbstract { get; init; }

	/// <summary>
	/// Метод открыт для переопределения: <c>open fun</c> или абстрактный <c>@fun</c>
	/// (spec §2, Q29).
	/// </summary>
	public bool IsOpen { get; init; }

	public bool IsOverride { get; init; }

	/// <summary>
	/// <c>async fun</c>: тело возвращает объявленный тип <c>T</c>, а видимый тип функции —
	/// <c>task&lt;T&gt;</c> (spec §16). Флаг попадает в <c>MemberFlags.Async</c> байткода;
	/// VM заворачивает результат <c>Ret</c> в завершённую задачу.
	/// </summary>
	public bool IsAsync { get; init; }

	public bool IsConstructor => Kind == FunctionKind.Constructor;

	public bool IsOperator => Kind == FunctionKind.Operator;

	public bool IsAccessor => Kind is FunctionKind.Getter or FunctionKind.Setter or FunctionKind.Init;

	public bool IsExtension => ReceiverType is not null;

	/// <summary>Primary-конструктор, синтезированный из <c>type X(...)</c>.</summary>
	public bool IsPrimaryConstructor { get; init; }

	/// <summary>Операторный символ для <see cref="FunctionKind.Operator"/>, иначе null.</summary>
	public string? OpSymbol { get; init; }

	/// <summary>Тело из объявления (блок, выражение accessor'а или expression body).</summary>
	public SyntaxNode? Body { get; init; }

	/// <summary>Цепочка конструктора <c>: $(...)</c>, сохраняется для binder'а.</summary>
	public ConstructorChainNode? Chain { get; init; }

	/// <summary>Родитель из выбора <c>fun $[A].foo</c>; null для обычного члена.</summary>
	public string? ParentSelector { get; init; }

	public SyntaxNode? Declaration { get; init; }

	/// <summary>Атрибуты функции/метода/оператора/accessor'а в байткодовой форме (spec §7).</summary>
	public IReadOnlyList<AttributeUse> Attributes { get; init; } = [];

	public int GenericArity => GenericParameters.Count;

	public IReadOnlyList<KernType> ParameterTypes => [.. Parameters.Select(parameter => parameter.Type)];

	/// <summary>Имя + generic-арность + типы параметров; имена generic-параметров не входят.</summary>
	public string Signature => SignatureText.For(this);

	public string DisplayName => Kind == FunctionKind.Constructor ? "ctor" : Name;

	public override string ToString() => Signature;
}
