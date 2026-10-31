using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Деструктуризация <c>(i32 a, list&lt;i32&gt; ..rest, _) = xs</c> (spec §3): правая часть —
/// <c>list</c>/<c>array</c>. Фиксированные позиции читаются по индексу, <c>..rest</c> —
/// срезом от своей позиции до <c>len - (число целей после rest)</c>, элементы после rest
/// индексируются от конца. Кортежи как правая часть пока не поддержаны (нет bound-узлов).
/// </summary>
public sealed partial class Binder
{
	private BoundStatement BindTupleDeclaration(TupleDeclarationStatement statement)
	{
		if (statement.Name is not null)
		{
			Report(
				"KRN2101",
				"Именованная деструктуризация кортежа пока не поддержана",
				statement.Span);
			return new BoundBlock([], null, FallsThrough: true, statement.Span);
		}

		var value = BindExpression(statement.Initializer);
		if (IsError(value.Type))
			return new BoundBlock([], null, FallsThrough: true, statement.Span);

		if (value.Type is not BuiltinGenericType
			{ BuiltinKind: BuiltinGenericKind.List or BuiltinGenericKind.Array } collection ||
			collection.Arguments.Count != 1)
		{
			Report(
				"KRN2101",
				$"Деструктуризация применима к list/array, а не к '{value.Type}'",
				statement.Span);
			return new BoundBlock([], null, FallsThrough: true, statement.Span);
		}

		var elementType = collection.Arguments[0];
		if (!CheckDestructuringArity(statement, value, out var restIndex))
			return new BoundBlock([], null, FallsThrough: true, statement.Span);

		var source = DeclareLocal($"#destr{_nextSlot}", value.Type, false, statement.Span);
		DeclareFlow(source, true, value.Type);
		var statements = new List<BoundStatement>(statement.Bindings.Count + 1)
		{
			new BoundLocalDeclaration(
				source.Name,
				source.Slot,
				value.Type,
				value,
				false,
				statement.Span)
		};

		var sourceExpression = new BoundVariableExpression(source.Name, source.Slot, value.Type, statement.Span);
		var afterRest = restIndex < 0 ? 0 : statement.Bindings.Count - restIndex - 1;
		var length = restIndex < 0 ? null : new BoundCallExpression(
			LoopLengthFunction(value.Type),
			[],
			PrimitiveType.I32,
			statement.Span,
			sourceExpression);

		for (var position = 0; position < statement.Bindings.Count; position++)
		{
			var binding = statement.Bindings[position];
			if (position == restIndex)
			{
				var declared = binding.Type is null ? null : _resolver.Resolve(binding.Type, _typeScope);
				var restType = declared ?? new BuiltinGenericType(BuiltinGenericKind.List, [elementType]);
				var end = new BoundBinaryExpression(
					length!,
					"-",
					IntLiteral(afterRest.ToString(), statement.Span),
					PrimitiveType.I32,
					statement.Span);
				var slice = new BoundSliceExpression(
					sourceExpression,
					IntLiteral(position.ToString(), binding.Span),
					end,
					Inclusive: false,
					restType,
					binding.Span);
				DeclareDestructured(binding.RestName!, restType, slice, statements, binding.Span);
				continue;
			}

			if (binding.IsIgnored || binding.Name is null or "_")
				continue;

			var index = position < restIndex || restIndex < 0
				? IntLiteral(position.ToString(), binding.Span)
				: RestRelativeIndex(length!, afterRest, restIndex, position, binding.Span);
			var declaredType = binding.Type is null ? null : _resolver.Resolve(binding.Type, _typeScope);
			var type = declaredType ?? elementType;
			var element = new BoundIndexExpression(sourceExpression, index, elementType, binding.Span);
			var converted = Convert(element, type, binding.Span);
			if (IsError(converted.Type))
				return new BoundBlock([], null, FallsThrough: true, statement.Span);
			DeclareDestructured(binding.Name, type, converted, statements, binding.Span);
		}

		return new BoundBlock(statements, null, FallsThrough: true, statement.Span);
	}

	/// <summary>Индекс цели после <c>..rest</c>: <c>len - afterRest + смещение</c>.</summary>
	private BoundExpression RestRelativeIndex(
		BoundExpression length,
		int afterRest,
		int restIndex,
		int position,
		TextSpan span)
	{
		var offset = position - restIndex - 1;
		var index = new BoundBinaryExpression(
			length,
			"-",
			IntLiteral((afterRest - offset).ToString(), span),
			PrimitiveType.I32,
			span);
		return index;
	}

	private void DeclareDestructured(
		string name,
		KernType type,
		BoundExpression value,
		List<BoundStatement> statements,
		TextSpan span)
	{
		var variable = DeclareLocal(name, type, false, span);
		DeclareFlow(variable, true, type);
		statements.Add(new BoundLocalDeclaration(
			variable.Name,
			variable.Slot,
			type,
			value,
			false,
			span));
	}

	/// <summary>Проверка числа целей для литерала списка; возвращает индекс <c>..rest</c> или -1.</summary>
	private bool CheckDestructuringArity(
		TupleDeclarationStatement statement,
		BoundExpression value,
		out int restIndex)
	{
		restIndex = -1;
		for (var index = 0; index < statement.Bindings.Count; index++)
		{
			if (statement.Bindings[index].RestName is not null)
			{
				restIndex = index;
				break;
			}
		}

		if (value is not BoundCollectionLiteral literal || literal.Items.Any(item => item.IsSpread))
			return true;

		var required = statement.Bindings.Count - (restIndex < 0 ? 0 : 1);
		var actual = literal.Items.Count;
		if (restIndex < 0 ? actual != required : actual < required)
		{
			Report(
				"KRN2102",
				$"Число целей деструктуризации ({required}) не совпадает с числом элементов ({actual})",
				statement.Span);
			return false;
		}

		return true;
	}
}
