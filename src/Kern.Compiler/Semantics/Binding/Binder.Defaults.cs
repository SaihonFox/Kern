using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Значения параметров по умолчанию (spec §3): выражение привязывается в точке вызова и
/// приводится к типу параметра. Параметр, ссылающийся на другой параметр, пока не поддержан
/// (в точке вызова такого имени нет — будет обычная диагностика).
/// </summary>
public sealed partial class Binder
{
	private BoundExpression? BindDefaultArgument(ParameterSymbol parameter, TextSpan span)
	{
		if (parameter.DefaultValue is not { } defaultValue)
			return null;

		var value = BindExpression(defaultValue, parameter.Type);
		if (IsError(value.Type))
			return null;

		return Convert(value, parameter.Type, defaultValue.Span);
	}
}
