using Kern.Compiler.Semantics.Binding.Flow;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Лямбды как значения (spec §6): <c>fun(i32 n) -&gt; i32 n * n</c> синтезирует отдельную
/// функцию, а выражение даёт <c>fun(...) -&gt; R</c>. Захваты считаются по кадрам:
/// слоты уникальны только внутри функции, поэтому чтение переменной другого кадра —
/// захват. VM хранит захваты по значению (<c>KClosure.Captures</c>), поэтому запись в
/// захваченную переменную запрещена (KRN2109), а не «изобретается» ячейка.
/// </summary>
public sealed partial class Binder
{
	/// <summary>Идентификатор текущего кадра (функции или лямбды).</summary>
	private int _frame;

	private int _nextFrame;

	private readonly Dictionary<int, HashSet<int>> _boxedByFrame = [];

	/// <summary>Стек кадров лямбд (для захватов и диагностик).</summary>
	private readonly Stack<LambdaContext> _lambdaFrames = [];

	/// <summary>
	/// Кадр лямбды во время привязки её тела: захваты в порядке первого упоминания,
	/// поток объемлющей функции в точке создания и родительский кадр (для вложенных лямбд).
	/// </summary>
	private sealed class LambdaContext(int enclosingFrame, FlowState enclosingFlow, LambdaContext? parent)
	{
		private readonly Dictionary<BoundVariable, int> _indices = new(ReferenceEqualityComparer.Instance);

		public int EnclosingFrame { get; } = enclosingFrame;

		/// <summary>Поток объемлющей функции в точке создания лямбды (проверка «до присваивания»).</summary>
		public FlowState EnclosingFlow { get; } = enclosingFlow;

		public LambdaContext? Parent { get; } = parent;

		public List<BoundCapture> Captures { get; } = [];

		public bool TryGetIndex(BoundVariable variable, out int index) =>
			_indices.TryGetValue(variable, out index);

		/// <summary>Добавить захват и вернуть его индекс внутри кадра этой лямбды.</summary>
		public int Add(BoundVariable variable, BoundExpression source, KernType type)
		{
			var index = Captures.Count;
			Captures.Add(new BoundCapture(variable.Name, source, type));
			_indices[variable] = index;
			return index;
		}
	}

	/// <summary>
	/// Чтение переменной: своя переменная кадра — <c>LoadLocal</c>, переменная объемлющей
	/// функции — захват (<c>LoadCapture</c>).
	/// </summary>
	private BoundExpression AccessVariable(BoundVariable variable, TextSpan span)
	{
		if (variable.Frame != _frame)
			return BindCapturedVariable(variable, span);

		if (_flow.Reachable && _noneReadDepth == 0 && !_flow.IsAssigned(variable.Slot) &&
			(variable.IsSingleAssignment || variable.Type is not NullableType))
			Report("KRN2105", $"Переменная '{variable.Name}' используется до присваивания", span);

		return new BoundVariableExpression(variable.Name, variable.Slot, CurrentTypeOf(variable), span);
	}

	/// <summary>
	/// Захват переменной из объемлющего кадра. Для вложенных лямбд захват идёт транзитивно:
	/// промежуточная лямбда тоже получает копию значения (иначе её кадр её не видит).
	/// </summary>
	private BoundExpression BindCapturedVariable(BoundVariable variable, TextSpan span)
	{
		if (variable.Passing == ParameterPassing.Ref)
		{
			Report(
				"KRN2109",
				$"ref-параметр '{variable.Name}' нельзя захватить замыканием",
				span);
			return new BoundErrorExpression(span);
		}

		if (_lambdaFrames.Count == 0)
		{
			// Возможно только при ошибке в коде самого binder'а: кадр меняют лишь лямбды.
			Report("KRN2109", $"Переменная '{variable.Name}' недоступна в этом кадре", span);
			return new BoundErrorExpression(span);
		}

		var context = _lambdaFrames.Peek();
		var index = CaptureIn(context, variable, span, out var type);
		return new BoundCaptureExpression(index, type, span, IsCell: true);
	}

	private int CaptureIn(LambdaContext context, BoundVariable variable, TextSpan span, out KernType type)
	{
		if (context.TryGetIndex(variable, out var existing))
		{
			type = context.Captures[existing].Type;
			return existing;
		}

		BoundExpression source;
		KernType sourceType;
		if (variable.Frame == context.EnclosingFrame)
		{
			// Определённая присвоенность проверяется в точке создания лямбды: значение
			// копируется именно здесь.
			if (context.EnclosingFlow.Reachable && !context.EnclosingFlow.IsAssigned(variable.Slot) &&
				(variable.IsSingleAssignment || variable.Type is not NullableType))
				Report("KRN2105", $"Переменная '{variable.Name}' используется до присваивания", span);

			sourceType = context.EnclosingFlow.NarrowedType(variable.Slot) ?? variable.Type;
			// Захватываем ячейку: значение копируется по ссылке, изменения видны обеим сторонам.
			_boxedByFrame[variable.Frame].Add(variable.Slot);
			source = new BoundCellReferenceExpression(variable.Slot, sourceType, span);
		}
		else if (context.Parent is { } parent)
		{
			var parentIndex = CaptureIn(parent, variable, span, out var parentType);
			sourceType = parentType;
			source = new BoundCaptureExpression(parentIndex, parentType, span, IsCell: true);
		}
		else
		{
			Report("KRN2109", $"Переменная '{variable.Name}' недоступна в этом кадре", span);
			type = ErrorType.Instance;
			return context.Add(variable, new BoundErrorExpression(span), ErrorType.Instance);
		}

		type = sourceType;
		return context.Add(variable, source, sourceType);
	}

	/// <summary>
	/// Лямбда: свежий кадр, параметры в новой области видимости, тело с ожидаемым типом
	/// результата. Тип результата берётся из <c>-&gt; R</c>, иначе выводится из тела-выражения;
	/// тело-блок без <c>-&gt; R</c> — <c>void</c> (spec §6).
	/// </summary>
	private BoundExpression BindLambda(LambdaExpression lambda, KernType? hint)
	{
		var functionHint = hint as FunctionType;
		var context = new LambdaContext(_frame, _flow, _lambdaFrames.Count > 0 ? _lambdaFrames.Peek() : null);
		var outerFrame = _frame;
		var outerScope = _scope;
		var savedNextSlot = _nextSlot;
		var savedReturn = _returnType;
		var savedAsync = _isAsyncFunction;
		var savedGenerator = _isGeneratorFunction;
		var savedGeneratorElement = _generatorElement;
		var savedFlow = _flow;
		var savedNoneDepth = _noneReadDepth;
		var savedConstruction = _constructionType;
		var savedStaticInit = _staticInitializerType;
		var savedLoops = _loopContexts.ToArray();
		var savedLabels = _functionLabels.ToArray();

		var frame = ++_nextFrame;
		_frame = frame;
		_boxedByFrame[frame] = [];
		_lambdaFrames.Push(context);
		try
		{
			_nextSlot = 0;
			_flow = new FlowState();
			// `await` внутри лямбды разрешён только для локальной `async fun`.
			_isAsyncFunction = lambda.IsAsync;
			_isGeneratorFunction = false;
			_generatorElement = null;
			_loopContexts.Clear();
			_functionLabels.Clear();
			_noneReadDepth = 0;
			// Контексты создания/`!`-статики лямбда не наследует (Q15b).
			_constructionType = null;
			_staticInitializerType = null;
			// Область видимости лямбды: свои параметры и локальные ищутся первыми,
			// объемлющие переменные остаются видимы и становятся захватами.
			_scope = new BoundScope(outerScope);

			var parameters = new List<BoundParameter>(lambda.Parameters.Count);
			var symbols = new List<ParameterSymbol>(lambda.Parameters.Count);
			var parameterTypes = new List<KernType>(lambda.Parameters.Count);
			for (var index = 0; index < lambda.Parameters.Count; index++)
			{
				var parameter = lambda.Parameters[index];
				KernType type;
				if (parameter.Type is not null)
					type = _resolver.Resolve(parameter.Type, _typeScope);
				else if (functionHint is not null && index < functionHint.Parameters.Count)
					type = functionHint.Parameters[index];
				else
				{
					Report("KRN2102", $"У параметра лямбды '{parameter.Name}' нужен явный тип", parameter.Span);
					type = ErrorType.Instance;
				}

				if (parameter.DefaultValue is not null)
					Report(
						"KRN2109",
						$"Значение по умолчанию у параметра лямбды '{parameter.Name}' не поддерживается",
						parameter.Span);

				var symbol = new ParameterSymbol(parameter.Name, type, null, false, false, parameter.Span);
				symbols.Add(symbol);
				parameterTypes.Add(type);

				var variable = new BoundVariable(parameter.Name, type, _nextSlot++, isSingleAssignment: true)
				{
					IsParameter = true,
					Frame = frame
				};
				if (!_scope.TryDeclare(variable))
					Report("KRN2106", $"Параметр '{parameter.Name}' уже объявлен в этой лямбде", parameter.Span);
				_flow.MarkAssigned(variable.Slot);
				parameters.Add(new BoundParameter(symbol, variable.Slot));
			}

			var name = $"<lambda#{frame}>";
			FunctionSymbol? lambdaSymbol;
			KernType returnType;
			BoundBlock body;
			if (lambda.ReturnType is not null)
			{
				returnType = _resolver.Resolve(lambda.ReturnType, _typeScope);
				if (lambda.IsAsync && lambda.HasYield)
					Report(
						"KRN2101",
						"async-генераторы не поддерживаются: используйте async_iter<T>",
						lambda.Span);
				if (lambda.HasYield && TryGetSeqElement(returnType, out KernType? generatorElement))
				{
					_isGeneratorFunction = true;
					_generatorElement = generatorElement;
				}
				else if (lambda.HasYield)
				{
					Report(
						"KRN2101",
						"yield допустим только в функции с результатом seq<T>",
						lambda.Span);
				}

				lambdaSymbol = NewLambdaSymbol(name, symbols, returnType, lambda);
				_returnType = _isGeneratorFunction ? PrimitiveType.Void : returnType;
				body = BindBody(lambda.Body, lambdaSymbol);
			}
			else if (lambda.Body is ExpressionNode expression)
			{
				// Вывод типа результата из тела-выражения (у лямбд — разрешён, spec §6/§16).
				var value = BindExpression(expression);
				returnType = value.Type;
				lambdaSymbol = NewLambdaSymbol(name, symbols, returnType, lambda);
				body = new BoundBlock([], value, FallsThrough: false, expression.Span);
			}
			else
			{
				// Тело-блок без `-> R` — void (spec §6).
				returnType = PrimitiveType.Void;
				lambdaSymbol = NewLambdaSymbol(name, symbols, returnType, lambda);
				_returnType = returnType;
				body = BindBody(lambda.Body, lambdaSymbol);
			}

			// Валидатор .kbc требует captures ≤ LocalCount − ParamCount: захваты живут
			// в кадре за последним локальным слотом, значения берутся из массива захватов.
			var localCount = System.Math.Max(_nextSlot, parameters.Count + context.Captures.Count);
			var function = new BoundFunction(
				lambdaSymbol,
				parameters,
				body,
				_isGeneratorFunction ? PrimitiveType.Void : returnType,
				localCount)
			{
				IsGenerator = _isGeneratorFunction,
				BoxedSlots = _boxedByFrame[frame]
			};
			var functionType = new FunctionType(parameterTypes, CallResultTypeForAsync(lambda.IsAsync, returnType));
			return new BoundLambdaExpression(lambdaSymbol, function, [.. context.Captures], functionType, lambda.Span);
		}
		finally
		{
			_lambdaFrames.Pop();
			_frame = outerFrame;
			_scope = outerScope;
			_nextSlot = savedNextSlot;
			_returnType = savedReturn;
			_isAsyncFunction = savedAsync;
			_isGeneratorFunction = savedGenerator;
			_generatorElement = savedGeneratorElement;
			_flow = savedFlow;
			_noneReadDepth = savedNoneDepth;
			_constructionType = savedConstruction;
			_staticInitializerType = savedStaticInit;
			_loopContexts.Clear();
			for (var index = savedLoops.Length - 1; index >= 0; index--)
				_loopContexts.Push(savedLoops[index]);
			_functionLabels.Clear();
			_functionLabels.UnionWith(savedLabels);
		}
	}

	private static FunctionSymbol NewLambdaSymbol(
		string name,
		IReadOnlyList<ParameterSymbol> parameters,
		KernType returnType,
		LambdaExpression lambda) =>
		new(name, FunctionKind.Function)
		{
			Parameters = parameters,
			ReturnType = returnType,
			IsAsync = lambda.IsAsync,
			HasYield = lambda.HasYield,
			Body = lambda.Body,
			Declaration = lambda
		};

	/// <summary>
	/// Вызов значения функционального типа: <c>f(1)</c>, <c>(fun(i32 n) -&gt; i32 n)(2)</c>.
	/// <paramref name="variable"/> задан для простого имени (тогда учитывается захват).
	/// </summary>
	private BoundExpression BindValueCall(
		ExpressionNode target,
		BoundVariable? variable,
		IReadOnlyList<CallArgumentNode> arguments,
		IReadOnlyList<TypeSyntax> typeArguments,
		TextSpan span)
	{
		if (typeArguments.Count > 0)
		{
			Report("KRN2109", "Generic-аргументы у вызова функции-значения не поддерживаются", span);
			return new BoundErrorExpression(span);
		}

		var callee = variable is not null
			? AccessVariable(variable, target.Span)
			: BindExpression(target);
		if (IsError(callee.Type))
			return new BoundErrorExpression(span);

		if (callee.Type is not FunctionType function)
		{
			Report("KRN2101", $"Значение типа '{callee.Type}' не является функцией", target.Span);
			return new BoundErrorExpression(span);
		}

		if (function.Parameters.Count != arguments.Count)
		{
			Report(
				"KRN1401",
				$"Функция-значение ожидает {function.Parameters.Count} аргумент(ов), передано {arguments.Count}",
				span);
			return new BoundErrorExpression(span);
		}

		var bound = new List<BoundExpression>(arguments.Count);
		for (var index = 0; index < arguments.Count; index++)
		{
			var argument = arguments[index];
			if (argument.Name is not null)
				Report("KRN2108", "Именованные аргументы у вызова функции-значения не поддерживаются", argument.Span);

			var value = BindExpression(argument.Value, function.Parameters[index]);
			if (value is BoundReferenceArgumentExpression referenceArgument)
			{
				Report(
					"KRN2101",
					"Маркеры '&'/'*' не поддерживаются у вызова функции-значения",
					argument.Span);
				value = referenceArgument.Operand;
			}
			bound.Add(Convert(value, function.Parameters[index], argument.Span));
		}

		return new BoundValueCallExpression(callee, bound, function.ReturnType, span);
	}
}
