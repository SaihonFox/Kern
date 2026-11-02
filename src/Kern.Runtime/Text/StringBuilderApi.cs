using System.Text;
using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Text;

/// <summary>
/// Эффективная сборка строк: <c>string_builder</c> хранит <see cref="StringBuilder"/> и
/// дописывает за O(1) амортизированно, поэтому цикл не превращается в O(n²) из-за
/// конкатенации <c>str</c>. Значение — handle-тип, передаётся по ссылке.
/// </summary>
[KernType("string_builder")]
public sealed class KernStringBuilder
{
	public KernStringBuilder(int capacity = 16) => Value = new StringBuilder(capacity);

	public StringBuilder Value { get; }
}

/// <summary>
/// Фабрика и члены <c>string_builder</c> (spec §16): <c>string_builder()</c>,
/// <c>append(...)</c>, <c>append_line(...)</c>, <c>to_str()</c>, <c>len</c>, <c>clear()</c>.
/// </summary>
[KernType("string_builder")]
public static class StringBuilderApi
{
	/// <summary><c>string_builder()</c>: пустой буфер.</summary>
	[KernCtor]
	[KernName("string_builder")]
	public static KernStringBuilder Create() => new();

	/// <summary><c>string_builder(capacity)</c>: буфер с начальной ёмкостью.</summary>
	[KernCtor]
	[KernName("string_builder")]
	public static KernStringBuilder Create(int capacity)
	{
		if (capacity < 0 || capacity > 100_000_000)
			throw new KernPanic("text.string_builder: ёмкость должна быть 0..100 000 000");
		return new KernStringBuilder(capacity);
	}

	/// <summary><c>b.append(text)</c>: дописать строку.</summary>
	[KernMember]
	public static KernStringBuilder Append(KNativeBox self, string text)
	{
		Require(self).Value.Append(text);
		return Require(self);
	}

	/// <summary><c>b.append(char)</c>: дописать символ.</summary>
	[KernMember]
	public static KernStringBuilder Append(KNativeBox self, char symbol)
	{
		Require(self).Value.Append(symbol);
		return Require(self);
	}

	/// <summary><c>b.append(i32)</c>: дописать целое.</summary>
	[KernMember]
	public static KernStringBuilder Append(KNativeBox self, int value)
	{
		Require(self).Value.Append(value);
		return Require(self);
	}

	/// <summary><c>b.append(i64)</c>: дописать длинное целое.</summary>
	[KernMember]
	public static KernStringBuilder Append(KNativeBox self, long value)
	{
		Require(self).Value.Append(value);
		return Require(self);
	}

	/// <summary><c>b.append(f64)</c>: дописать дробное (инвариантная культура).</summary>
	[KernMember]
	public static KernStringBuilder Append(KNativeBox self, double value)
	{
		Require(self).Value.Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
		return Require(self);
	}

	/// <summary><c>b.append(bool)</c>: дописать <c>true</c>/<c>false</c>.</summary>
	[KernMember]
	public static KernStringBuilder Append(KNativeBox self, bool value)
	{
		Require(self).Value.Append(value ? "true" : "false");
		return Require(self);
	}

	/// <summary><c>b.append_line(text)</c>: дописать строку и перевод строки.</summary>
	[KernMember]
	public static KernStringBuilder AppendLine(KNativeBox self, string text)
	{
		Require(self).Value.Append(text).Append('\n');
		return Require(self);
	}

	/// <summary><c>b.append_line()</c>: дописать перевод строки.</summary>
	[KernMember]
	public static KernStringBuilder AppendLine(KNativeBox self)
	{
		Require(self).Value.Append('\n');
		return Require(self);
	}

	/// <summary><c>b.len</c>: число уже добавленных символов.</summary>
	[KernProperty]
	public static int Len(KNativeBox self) => Require(self).Value.Length;

	/// <summary><c>b.is_empty</c>: буфер пуст.</summary>
	[KernProperty]
	[KernName("is_empty")]
	public static bool IsEmpty(KNativeBox self) => Require(self).Value.Length == 0;

	/// <summary><c>b.clear()</c>: очистить буфер для повторного использования.</summary>
	[KernMember]
	public static void Clear(KNativeBox self) => Require(self).Value.Clear();

	/// <summary><c>b.to_str()</c>: готовый текст.</summary>
	[KernMember]
	[KernName("to_str")]
	public static string ToStr(KNativeBox self) => Require(self).Value.ToString();

	private static KernStringBuilder Require(KNativeBox self) => self.Value switch
	{
		KernStringBuilder builder => builder,
		_ => throw new KernPanic($"ожидался string_builder, получено {self.Value.GetType().Name}"),
	};
}
