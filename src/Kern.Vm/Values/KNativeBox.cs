using Kern.Vm.Types;

namespace Kern.Vm.Values;

/// <summary>
/// Коробка для C#-объекта, попавшего в Kern через нативную привязку (spec §12, `use dotnet`).
/// Внутри VM такой объект непрозрачен: его видят только нативные методы, которые знают
/// настоящий CLR-тип.
/// </summary>
public sealed class KNativeBox
{
	public KNativeBox(object value)
	{
		Value = value ?? throw new ArgumentNullException(nameof(value));
	}

	/// <summary>Настоящий CLR-объект.</summary>
	public object Value { get; }

	/// <summary>Reified Kern-тип, если он был известен при упаковке.</summary>
	public KType? NativeType { get; init; }

	public override string ToString() => Value.ToString() ?? Value.GetType().Name;
}
