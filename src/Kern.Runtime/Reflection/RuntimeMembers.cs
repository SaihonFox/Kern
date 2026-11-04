using System.Runtime.CompilerServices;
using Kern.Bytecode;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Reflection;

/// <summary>
/// Метод динамического типа (<c>reflect.define_type(...).method(...)</c>): тело — Kern-функция,
/// которую VM вызывает через <see cref="Kern.Vm.Execution.Interpreter.RunValue"/>.
/// </summary>
public sealed record RuntimeMethod(
	string Name,
	IReadOnlyList<KType> Parameters,
	KType ReturnType,
	KValue Function,
	bool IsStatic);

/// <summary>Свойство динамического типа: геттер/сеттер — Kern-функции (могут отсутствовать).</summary>
public sealed record RuntimeProperty(
	string Name,
	KType Type,
	KValue? Getter,
	KValue? Setter);

/// <summary>Члены, добавленные в динамический тип во время выполнения.</summary>
internal sealed class RuntimeMembers
{
	public List<RuntimeMethod> Methods { get; } = [];

	public List<RuntimeProperty> Properties { get; } = [];
}

/// <summary>
/// Реестр динамических членов по <see cref="TypeDef"/>. Хранится отдельно от <c>.kbc</c>:
/// динамические типы существуют только в текущем процессе.
/// </summary>
internal static class RuntimeTypeRegistry
{
	private static readonly ConditionalWeakTable<TypeDef, RuntimeMembers> Table = new();

	public static RuntimeMembers For(TypeDef definition) => Table.GetOrCreateValue(definition);

	public static RuntimeMembers? Find(TypeDef definition) =>
		Table.TryGetValue(definition, out RuntimeMembers? members) ? members : null;
}

/// <summary>Reified-тип VM → подпись в пуле модуля (для динамических членов).</summary>
internal static class KTypeSignature
{
	public static TypeSig From(KType type) => type switch
	{
		KPrimType primitive => TypeSig.Prim(primitive.Prim),
		KNullableType nullable => new TypeSig.NullableSig(From(nullable.Inner)),
		KClassType @class => new TypeSig.NamedSig(
			@class.Definition.FullName,
			[.. @class.TypeArgs.Select(From)]),
		_ => TypeSig.Object,
	};
}
