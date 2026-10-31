using Kern.Compiler.Semantics.Types;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Overloads;

/// <summary>
/// Один аргумент вызова до выбора перегрузки: тип выражения, имя (для именованных) и
/// необязательное константное значение литерала для правила константных преобразований.
/// </summary>
public sealed record CallArgument(KernType Type, string? Name = null, NumericLiteralValue? Constant = null)
{
	public bool IsPositional => Name is null;

	public TextSpan Span { get; init; }

	public static CallArgument Positional(KernType type, NumericLiteralValue? constant = null, TextSpan span = default) =>
		new(type, null, constant) { Span = span };

	public static CallArgument Named(string name, KernType type, NumericLiteralValue? constant = null, TextSpan span = default)
	{
		ArgumentNullException.ThrowIfNull(name);
		return new(type, name, constant) { Span = span };
	}
}

/// <summary>
/// Полный вызов для выбора перегрузки: аргументы в исходном порядке, необязательный тип
/// получателя расширения и явные generic-аргументы.
/// </summary>
public sealed class CallArguments
{
	public CallArguments(
		IReadOnlyList<CallArgument> arguments,
		KernType? receiverType = null,
		IReadOnlyList<KernType>? explicitTypeArguments = null,
		TextSpan span = default,
		string? name = null)
	{
		Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
		ReceiverType = receiverType;
		ExplicitTypeArguments = explicitTypeArguments;
		Span = span;
		Name = name;
	}

	public IReadOnlyList<CallArgument> Arguments { get; }

	public KernType? ReceiverType { get; }

	public IReadOnlyList<KernType>? ExplicitTypeArguments { get; }

	public TextSpan Span { get; }

	/// <summary>Имя вызываемой функции для диагностик; null — неизвестно.</summary>
	public string? Name { get; }
}
