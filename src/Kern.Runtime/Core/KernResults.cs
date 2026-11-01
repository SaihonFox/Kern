using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Core;

/// <summary>
/// Сборка <c>Ok&lt;T&gt;</c>/<c>Err&lt;E&gt;</c> из нативного кода с reified-аргументами
/// (spec §16): CLR-закрытие generic-метода теряет настоящий тип, а <c>match</c> в Kern
/// сравнивает именно его. Общий помощник для <c>kern.threading</c>, <c>kern.tasks</c> и
/// <c>kern.http</c>.
/// </summary>
public static class KernResults
{
	/// <summary>Тип <c>Ok&lt;T&gt;</c> модуля интерпретатора.</summary>
	public static KClassType OkType(Interpreter interpreter, KType value) =>
		Variant(interpreter, "Ok", value ?? throw new ArgumentNullException(nameof(value)));

	/// <summary>Тип <c>Err&lt;E&gt;</c> модуля интерпретатора.</summary>
	public static KClassType ErrType(Interpreter interpreter, KType error) =>
		Variant(interpreter, "Err", error ?? throw new ArgumentNullException(nameof(error)));

	/// <summary>Значение <c>Ok&lt;T&gt;(payload)</c>.</summary>
	public static KValue Ok(Interpreter interpreter, KType value, KValue payload) =>
		Make(OkType(interpreter, value), payload);

	/// <summary>Значение <c>Err&lt;E&gt;(payload)</c>.</summary>
	public static KValue Err(Interpreter interpreter, KType error, KValue payload) =>
		Make(ErrType(interpreter, error), payload);

	/// <summary>Значение <c>Ok&lt;T&gt;(payload)</c> по готовому типу варианта.</summary>
	public static KValue Make(KClassType variant, KValue payload) =>
		KValue.Obj(new KObject(variant, [payload]));

	/// <summary>Reified примитивный тип по <see cref="PrimKind"/>.</summary>
	public static KType TypeOf(Interpreter interpreter, PrimKind kind) =>
		interpreter.Types.Prim(kind);

	private static KClassType Variant(Interpreter interpreter, string name, KType argument)
	{
		ArgumentNullException.ThrowIfNull(interpreter);
		foreach (TypeDef type in interpreter.Module.TypePool)
		{
			if (type.Name != name || type.GenericParams.Count != 1 || type.Fields.Count != 1)
				continue;
			return interpreter.Types.Named(type, argument);
		}

		throw new KernPanic($"встроенный тип '{name}' не найден в модуле '{interpreter.Module.Name}'");
	}
}
