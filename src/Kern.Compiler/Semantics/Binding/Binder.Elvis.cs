using Kern.Compiler.Semantics.Binding.Flow;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Elvis <c>val ?: default</c> и <c>ret</c> в его правой ветви (spec §3/§4).
/// Типизация и проверка потока — здесь, генерация — в <c>CodeGenerator.Elvis.cs</c>.
/// </summary>
public sealed partial class Binder
{
	/// <summary>
	/// Elvis <c>val ?: default</c> (spec §3/§4): значение слева вычисляется один раз; если оно
	/// <c>nil</c> или <c>none</c>, результатом становится правая ветвь. Для <c>T?</c> результат —
	/// <c>T</c> (или <c>T?</c>, если правая ветвь тоже nullable), для <c>nil</c>/<c>none</c>
	/// слева — тип правой ветви, иначе — тип слева (оператор разрешён, но статически не
	/// срабатывает). Правая ветвь приводится к результату (<c>KRN2102</c> при несовместимости).
	/// </summary>
	private BoundExpression BindElvis(BinaryExpression binary)
	{
		var left = BindExpression(binary.Left);
		if (IsError(left.Type))
			return new BoundErrorExpression(binary.Span);

		var entry = _flow.Clone();
		_flow = entry.Clone();
		var right = BindExpression(binary.Right);
		var afterRight = _flow;
		var rightFallsThrough = ExpressionFallsThrough(right);
		_flow = rightFallsThrough ? FlowState.Join(entry, afterRight) : entry;

		var leftIsAbsent = left.Type is NilType ||
			left.Type is PrimitiveType { Primitive: PrimitiveKind.None };
		var resultType = leftIsAbsent
			? right.Type
			: left.Type is NullableType nullable
				? ElvisResultType(nullable, right.Type)
				: left.Type;
		if (IsError(resultType))
			return new BoundErrorExpression(binary.Span);

		right = Convert(right, resultType, binary.Right.Span);
		return new BoundElvisExpression(left, right, resultType, binary.Span);
	}

	/// <summary>
	/// Тип результата Elvis для левой части <c>T?</c>: правая ветвь может быть и <c>T</c>,
	/// и <c>T?</c> (тогда результат остаётся nullable); <c>never</c> сужает до <c>T</c>.
	/// </summary>
	private static KernType ElvisResultType(NullableType left, KernType right)
	{
		if (IsNever(right))
			return left.Underlying;
		return right is NilType or NullableType
			? NullableType.Create(left.Underlying)
			: left.Underlying;
	}

	/// <summary>
	/// <c>ret value</c> как выражение — только в правой ветви Elvis (parser §3).
	/// Возвращает узел с типом <c>never</c>; сам возврат выполняет codegen.
	/// </summary>
	private BoundExpression BindReturnExpression(UnaryExpression unary)
	{
		var value = BindExpression(unary.Operand, _returnType);
		if (IsVoid(_returnType) && !IsError(_returnType) && !IsNever(value.Type))
			Report("KRN2102", "Выражение возвращает значение из void-функции", unary.Span);
		value = Convert(value, _returnType, unary.Operand.Span);
		return new BoundReturnExpression(value, unary.Span);
	}
}
