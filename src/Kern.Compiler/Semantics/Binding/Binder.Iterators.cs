using Kern.Compiler.Semantics.Binding.Flow;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>Итерация по протоколу <c>iter&lt;T&gt;</c> (spec §16): разворачивание в бесконечный цикл.</summary>
public sealed partial class Binder
{
	/// <summary>
	/// spec §16: <c>iter&lt;T&gt;</c> — протокол с <c>~fun next -> T?</c> (<c>nil</c> = конец).
	/// Приёмник тип, у которого прямой или унаследованный родитель — <c>iter&lt;T&gt;</c>.
	/// Цикл разворачивается в бесконечный: скрытая локальная получает <c>it.next()</c>, проверка
	/// на <c>nil</c> выходит из цикла через обычный <c>stop</c>, затем значение привязывается
	/// существующим <see cref="BindIterationVariables"/>; инкремента нет. <c>next</c>/<c>stop</c>
	/// и метки внутри тела работают как у обычного цикла.
	/// </summary>
	private bool TryBindIteratorProtocol(
		LoopExpression source,
		BoundExpression iterable,
		BuiltinGenericType protocol,
		List<BoundStatement> statements,
		out BoundExpression element,
		out KernType elementType,
		out BoundExpression condition,
		out BoundStatement increment,
		out List<BoundStatement> prefix)
	{
		element = new BoundErrorExpression(source.Span);
		condition = element;
		increment = EmptyLoopBlock(source.Span);
		prefix = [];
		elementType = ErrorType.Instance;

		var produced = protocol.Arguments[0];
		FunctionSymbol? next = null;
		var wrongShape = false;
		if (OwnerOf(iterable.Type) is { } owner)
		{
			foreach (var function in owner.Members.GetFunctions("next"))
			{
				if (function.IsStatic || function.Parameters.Count != 0)
					continue;
				if (function.ReturnType is NullableType nullable && nullable.Underlying.Equals(produced))
				{
					next = function;
					break;
				}

				wrongShape = true;
			}
		}

		if (next is null)
		{
			Report(
				"KRN2101",
				wrongShape
					? $"Метод 'next' типа '{iterable.Type}' должен возвращать '{produced}?' для iter<{produced}>"
					: $"Тип '{iterable.Type}' объявляет iter<{produced}>, но не имеет метода 'next() -> {produced}?'",
				source.Span);
			return false;
		}

		var nullableType = NullableType.Create(produced);
		var iterableLocal = DeclareLocal($"#iter{_nextSlot}", iterable.Type, false, source.Span);
		DeclareFlow(iterableLocal, true, iterable.Type);
		statements.Add(new BoundLocalDeclaration(
			iterableLocal.Name,
			iterableLocal.Slot,
			iterable.Type,
			iterable,
			false,
			source.Span));

		var nextLocal = DeclareLocal($"#next{_nextSlot}", nullableType, false, source.Span);
		DeclareFlow(nextLocal, true, nullableType);
		statements.Add(new BoundLocalDeclaration(
			nextLocal.Name,
			nextLocal.Slot,
			nullableType,
			null,
			false,
			source.Span));

		var receiver = Variable(iterableLocal, source.Span);
		var nextCall = new BoundCallExpression(next, [], nullableType, source.Span, receiver)
		{
			Virtual = IsVirtualCall(receiver, next),
		};

		var nextValue = Variable(nextLocal, source.Span);
		var stopAtEnd = new BoundBlockExpression(
			[new BoundStopStatement(source.Span) { TargetDepth = 0 }],
			null,
			FallsThrough: false,
			PrimitiveType.Void,
			source.Span);
		var exitWhenNil = new BoundIfExpression(
			new BoundBinaryExpression(
				nextValue,
				"==",
				new BoundNilExpression(source.Span),
				PrimitiveType.Bool,
				source.Span),
			stopAtEnd,
			null,
			PrimitiveType.Void,
			source.Span);

		prefix =
		[
			new BoundAssignment(nextLocal.Name, nextLocal.Slot, nullableType, nextCall, source.Span),
			new BoundExpressionStatement(exitWhenNil, source.Span),
		];

		// После проверки на nil значение не nullable: привязка `var i`/`T i` видит T.
		element = new BoundVariableExpression(nextLocal.Name, nextLocal.Slot, produced, source.Span);
		elementType = produced;
		condition = new BoundLiteralExpression(true, null, PrimitiveType.Bool, source.Span);
		return true;
	}

	/// <summary>Прямой или унаследованный родитель-<c>iter&lt;T&gt;</c> типа (spec §16).</summary>
	private static bool TryFindIteratorBase(KernType type, out BuiltinGenericType iterator)
	{
		iterator = null!;
		if (type is not ClassType @class)
			return false;

		foreach (var baseType in IteratorBases(@class.Symbol))
		{
			if (baseType is BuiltinGenericType { BuiltinKind: BuiltinGenericKind.Iter, Arguments.Count: 1 } found)
			{
				iterator = found;
				return true;
			}
		}

		return false;
	}

	/// <summary>Есть ли у типа родитель <c>async_iter&lt;T&gt;</c> (пока не поддержан, spec §16).</summary>
	private static bool HasAsyncIteratorBase(KernType type) =>
		type is ClassType @class &&
		IteratorBases(@class.Symbol).Any(baseType =>
			baseType is BuiltinGenericType { BuiltinKind: BuiltinGenericKind.AsyncIter });

	/// <summary>Все родители типа и его предков (аргументы generic-родителей не подставляются:
	/// протокол <c>iter&lt;T&gt;</c> всегда объявлен напрямую).</summary>
	private static IEnumerable<KernType> IteratorBases(TypeSymbol symbol)
	{
		foreach (var baseType in symbol.Bases)
			yield return baseType;
		foreach (var ancestor in Inheritance.Ancestors(symbol))
			foreach (var baseType in ancestor.Bases)
				yield return baseType;
	}
}
