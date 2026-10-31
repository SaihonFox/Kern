using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Core.Diagnostics;
using Kern.Core.Text;

namespace Kern.Compiler.CodeGen;

/// <summary>
/// M4 codegen для слайса M3-A: функции, литералы, локальные слоты, присваивания,
/// арифметика/сравнения/логика, <c>if</c> (с коротким замыканием) и вызовы top-level функций.
/// Вызовы нативных/builtin функций и неподдержанные конструкции дают KRN2201/2202.
/// </summary>
public sealed partial class CodeGenerator
{
	private readonly KbcModule _module = new() { Name = "module" };
	private readonly Dictionary<FunctionSymbol, int> _functionIndices = new(ReferenceEqualityComparer.Instance);
	private readonly Dictionary<TypeSymbol, int> _typeIndices = new(ReferenceEqualityComparer.Instance);
	private readonly Dictionary<TypeSymbol, TypeDef> _typeDefs = new(ReferenceEqualityComparer.Instance);
	private readonly Dictionary<FieldSymbol, int> _fieldSlots = new(ReferenceEqualityComparer.Instance);
	private readonly List<Diagnostic> _diagnostics = [];
	private readonly Stack<LoopContext> _loops = new();
	private List<int> _code = [];
	private FunctionDef _current = null!;
	private SourceText? _source;
	private SourceBundle? _bundle;

	/// <summary>Слоты текущей функции, захваченные замыканиями: живут в <c>KCell</c>.</summary>
	private IReadOnlySet<int> _boxedSlots = new HashSet<int>();

	/// <summary>
	/// Генерирует байткод модуля. Если передан <paramref name="source"/>, у функций заполняются
	/// <c>SourceFile</c> (непустой путь) и <c>LineTable</c> (границы операторов).
	/// </summary>
	public CodegenResult Generate(BoundModule module, SourceText? source = null)
	{
		ArgumentNullException.ThrowIfNull(module);
		_bundle = null;
		_source = source;
		return GenerateCore(module);
	}

	/// <summary>
	/// Генерирует байткод мультифайлового модуля: <c>SourceFile</c> — путь файла объявления
	/// функции, <c>LineTable</c> — локальные строки этого файла (см. <see cref="SourceBundle"/>).
	/// </summary>
	public CodegenResult Generate(BoundModule module, SourceBundle sources)
	{
		ArgumentNullException.ThrowIfNull(module);
		ArgumentNullException.ThrowIfNull(sources);
		_source = null;
		_bundle = sources;
		return GenerateCore(module);
	}

	private CodegenResult GenerateCore(BoundModule module)
	{
		_module.Name = module.Symbol.Name;
		DefineTypes(module);

		foreach (var function in module.Functions)
			Define(function);

		foreach (FunctionSymbol native in module.Symbol.NativeFunctions)
			DefineNative(native);

		foreach (var function in module.Functions)
			EmitFunction(function);

		return new CodegenResult(_module, _diagnostics);
	}

	private void EmitStatement(BoundStatement statement, bool leaveValue)
	{
		RecordLine(statement.Span);
		switch (statement)
		{
			case BoundExpressionStatement expression:
				EmitExpression(expression.Expression);
				if (!leaveValue && LeavesValue(expression.Expression))
					Emit(OpCode.Pop);
				break;

			// `_ = expr`: вычислить ради побочных эффектов и снять значение со стека.
			case BoundDiscardStatement discard:
				EmitExpression(discard.Value);
				if (LeavesValue(discard.Value))
					Emit(OpCode.Pop);
				break;

			case BoundLocalDeclaration declaration:
				if (declaration.Initializer is not null)
					EmitLocalInitializer(declaration.Slot, declaration.Type, declaration.Initializer);
				foreach (var binding in declaration.DeclaredBindings)
				{
					if (binding.Initializer is null)
						continue;
					EmitLocalInitializer(binding.Slot, binding.Initializer.Type, binding.Initializer);
				}
				break;

			// use T x = expr (spec §12): объявить и зарегистрировать dispose на выходе из блока.
			case BoundUseStatement use:
				EmitUse(use);
				break;

			case BoundAssignment assignment:
				if (_boxedSlots.Contains(assignment.Slot))
				{
					Emit(OpCode.LoadLocal, assignment.Slot);
					EmitExpression(assignment.Value);
					EmitCopyIfUnit(assignment.Type);
					Emit(OpCode.StoreCell);
					break;
				}

				EmitExpression(assignment.Value);
				EmitCopyIfUnit(assignment.Type);
				Emit(OpCode.StoreLocal, assignment.Slot);
				break;

			case BoundCapturedAssignment capturedAssignment:
				Emit(OpCode.LoadCapture, capturedAssignment.CaptureIndex);
				EmitExpression(capturedAssignment.Value);
				EmitCopyIfUnit(capturedAssignment.Type);
				Emit(OpCode.StoreCell);
				break;

			case BoundFieldAssignment fieldAssignment:
				if (fieldAssignment.Field.IsStatic)
				{
					EmitStaticFieldAssignment(fieldAssignment);
					break;
				}
				if (fieldAssignment.Receiver is not null)
					EmitExpression(fieldAssignment.Receiver);
				else
					Emit(OpCode.LoadLocal, 0);
				EmitExpression(fieldAssignment.Value);
				EmitCopyIfUnit(fieldAssignment.Type);
				if (_fieldSlots.TryGetValue(fieldAssignment.Field, out var fieldSlot))
					Emit(OpCode.StoreField, fieldSlot);
				else
				{
					Report("KRN2201", $"Поле '{fieldAssignment.Field.Name}' не попало в TypeDef (M3-D)", fieldAssignment.Span);
					Emit(OpCode.PushVoid);
				}
				Emit(OpCode.Pop);
				break;

			case BoundChainStore chainStore:
				EmitChainStore(chainStore);
				break;

			case BoundReturn ret:
				if (ret.Expression is null)
					Emit(OpCode.PushVoid);
				else
				{
					EmitExpression(ret.Expression);
					EmitCopyIfUnit(ret.Expression.Type);
				}
				EmitDisposalsThrough(0);
				Emit(OpCode.Ret);
				break;

			case BoundBlock block:
				EmitBlock(block, leaveValue: false);
				break;

			case BoundLoopStatement loop:
				EmitLoopStatement(loop);
				break;

			case BoundStopStatement:
				EmitLoopJump(breakJump: true, statement.Span);
				break;

			case BoundNextStatement:
				EmitLoopJump(breakJump: false, statement.Span);
				break;
		}
	}

	private void EmitExpression(BoundExpression expression)
	{
		switch (expression)
		{
			case BoundLiteralExpression literal:
				EmitLiteral(literal);
				break;

			case BoundVariableExpression variable:
				Emit(OpCode.LoadLocal, variable.Slot);
				if (_boxedSlots.Contains(variable.Slot))
					Emit(OpCode.LoadCell);
				break;

			case BoundNilExpression:
				Emit(OpCode.PushNil);
				break;

			case BoundNoneExpression:
				Emit(OpCode.PushNone);
				break;

			case BoundTruthinessExpression truthiness:
				EmitTruthiness(truthiness);
				break;

			case BoundTypeTestExpression typeTest:
				EmitExpression(typeTest.Operand);
				EmitTypeTestOnStack(typeTest.TestType, typeTest.Span);
				if (typeTest.Negated)
					Emit(OpCode.Not);
				break;

			case BoundBinaryExpression binary:
				EmitBinary(binary);
				break;

			case BoundElvisExpression elvis:
				EmitElvis(elvis);
				break;

			case BoundReturnExpression ret:
				EmitReturnExpression(ret);
				break;

			case BoundConditionalAccessExpression conditionalAccess:
				EmitConditionalAccess(conditionalAccess);
				break;

			case BoundUnaryExpression unary:
				EmitExpression(unary.Operand);
				Emit(unary.Operator switch
				{
					"!" => OpCode.Not,
					"~" => OpCode.BitNot,
					_ => OpCode.Neg,
				});
				break;

			case BoundConversionExpression conversion:
				EmitExpression(conversion.Operand);
				EmitConversion(conversion.Type);
				break;

			case BoundBlockExpression block:
				EmitBlock(
					new BoundBlock(block.Statements, block.Value, block.FallsThrough, block.Span),
					leaveValue: block.FallsThrough || block.Value is not null);
				break;

			case BoundIfExpression conditional:
				EmitIf(conditional);
				break;

			case BoundLoopExpression loop:
				EmitLoopExpression(loop);
				break;

			case BoundMatchExpression match:
				EmitMatch(match);
				break;

			case BoundCallExpression call:
				EmitCall(call);
				break;

			// Лямбды и функции-значения (spec §6).
			case BoundLambdaExpression lambda:
				EmitLambda(lambda);
				break;

			case BoundCaptureExpression capture:
				Emit(OpCode.LoadCapture, capture.Index);
				if (capture.IsCell)
					Emit(OpCode.LoadCell);
				break;

			case BoundCellReferenceExpression cellReference:
				Emit(OpCode.LoadLocal, cellReference.Slot);
				break;

			case BoundValueCallExpression valueCall:
				EmitValueCall(valueCall);
				break;

			case BoundResultConstructionExpression result:
				EmitResultConstruction(result);
				break;

			case BoundFieldExpression field:
				if (field.Field.IsStatic)
				{
					EmitStaticFieldRead(field);
					break;
				}
				if (field.Receiver is not null)
					EmitExpression(field.Receiver);
				else
					Emit(OpCode.LoadLocal, 0);
				if (_fieldSlots.TryGetValue(field.Field, out var fieldSlot))
				{
					Emit(OpCode.LoadField, fieldSlot);
					EmitCopyIfUnit(field.Type);
				}
				else
				{
					Report("KRN2201", $"Поле '{field.Field.Name}' не попало в TypeDef (M3-D)", field.Span);
					Emit(OpCode.PushVoid);
				}
				break;

			case BoundStoreCurrentExpression current:
				EmitStoreCurrent(current);
				break;

			case BoundObjectCreationExpression creation:
				EmitObjectCreation(creation);
				break;

			case BoundCollectionLiteral collection:
				EmitCollectionLiteral(collection);
				break;

			case BoundTupleExpression tuple:
				EmitTupleLiteral(tuple);
				break;

			case BoundTupleItemExpression tupleItem:
				EmitTupleItem(tupleItem);
				break;

			case BoundDictionaryLiteral dictionary:
				EmitDictionaryLiteral(dictionary);
				break;

			case BoundIndexExpression indexing:
				EmitIndex(indexing);
				break;

			case BoundSliceExpression slice:
				EmitSlice(slice);
				break;

			// B6b1/B8a/B8c: протоколы hash/compare/clone (spec §9/§16).
			case BoundHashExpression hash:
				EmitHash(hash);
				break;

			case BoundCompareExpression compare:
				EmitCompare(compare);
				break;

			case BoundCloneExpression clone:
				EmitClone(clone);
				break;

			case BoundErrorExpression:
				Emit(OpCode.PushVoid);
				break;
		}
	}

	private void EmitBinary(BoundBinaryExpression binary)
	{
		// `n * "abc"` / `'-' * 3`: результат str, повтор делает VM (spec §3).
		if (binary.Operator == "*" && binary.Type is PrimitiveType { Primitive: PrimitiveKind.Str })
		{
			EmitExpression(binary.Left);
			EmitExpression(binary.Right);
			Emit(OpCode.Repeat);
			return;
		}

		if (binary.Operator == "&&")
		{			EmitExpression(binary.Left);
			var falseJump = EmitJump(OpCode.JmpIfNot);
			EmitExpression(binary.Right);
			var endJump = EmitJump(OpCode.Jmp);
			Patch(falseJump, Position);
			Emit(OpCode.PushFalse);
			Patch(endJump, Position);
			return;
		}

		if (binary.Operator == "||")
		{
			EmitExpression(binary.Left);
			var trueJump = EmitJump(OpCode.JmpIf);
			EmitExpression(binary.Right);
			var endJump = EmitJump(OpCode.Jmp);
			Patch(trueJump, Position);
			Emit(OpCode.PushTrue);
			Patch(endJump, Position);
			return;
		}

		EmitExpression(binary.Left);
		EmitExpression(binary.Right);
		switch (binary.Operator)
		{
			case "+": Emit(OpCode.Add); break;
			case "-": Emit(OpCode.Sub); break;
			case "*": Emit(OpCode.Mul); break;
			case "/": Emit(OpCode.Div); break;
			case "%": Emit(OpCode.Mod); break;
			case "**": Emit(OpCode.Pow); break;
			case "==": Emit(OpCode.Eq); break;
			case "!=": Emit(OpCode.Ne); break;
			case "<": Emit(OpCode.Lt); break;
			case "<=": Emit(OpCode.Le); break;
			case ">": Emit(OpCode.Gt); break;
			case ">=": Emit(OpCode.Ge); break;
			case "&": Emit(OpCode.BitAnd); break;
			case "|": Emit(OpCode.BitOr); break;
			case "^": Emit(OpCode.BitXor); break;
			case "<<": Emit(OpCode.Shl); break;
			case ">>": Emit(OpCode.Shr); break;
			default:
				Report("KRN2201", $"Оператор '{binary.Operator}' пока не поддержан codegen'ом (M4-A)", binary.Span);
				Emit(OpCode.PushVoid);
				break;
		}
	}

	private void EmitLoopStatement(BoundLoopStatement loop)
	{
		if (loop.Condition is null && !loop.IsInfinite)
		{
			Report("KRN2201", "У цикла нет условия (ожидался condition/infinite loop)", loop.Span);
			return;
		}

		var context = new LoopContext();
		_loops.Push(context);
		try
		{
			if (loop.IsInfinite)
			{
				var start = Position;
				context.ContinueTarget = start;
				EmitBlock(loop.Body, leaveValue: false, loopBody: true);
				EmitJumpTo(start);
				PatchLoop(context);
				return;
			}

			if (loop.IsDoWhile)
			{
				var start = Position;
				EmitBlock(loop.Body, leaveValue: false, loopBody: true);
				context.ContinueTarget = Position;
				EmitExpression(loop.Condition!);
				var repeat = EmitJump(OpCode.JmpIf);
				Patch(repeat, start);
				PatchLoop(context);
				return;
			}

			var conditionStart = Position;
			context.ContinueTarget = conditionStart;
			EmitExpression(loop.Condition!);
			var exit = EmitJump(OpCode.JmpIfNot);
			EmitBlock(loop.Body, leaveValue: false, loopBody: true);
			EmitJumpTo(conditionStart);
			Patch(exit, Position);
			PatchLoop(context);
		}
		finally
		{
			_loops.Pop();
		}
	}

	private void EmitLoopJump(bool breakJump, TextSpan span)
	{
		if (_loops.Count == 0)
		{
			Report("KRN2201", "stop/next вне цикла не может быть сгенерирован", span);
			return;
		}

		// use внутри тела цикла освобождается перед jump'ом (spec §12: stop/next тоже выход).
		if (_loopDisposeFrames.Count > 0)
			EmitDisposalsThrough(_loopDisposeFrames[^1]);

		var jump = EmitJump(OpCode.Jmp);
		if (breakJump)
			_loops.Peek().BreakJumps.Add(jump);
		else
			_loops.Peek().ContinueJumps.Add(jump);
	}

	private void PatchLoop(LoopContext context)
	{
		var end = Position;
		foreach (var jump in context.BreakJumps)
			Patch(jump, end);
		foreach (var jump in context.ContinueJumps)
			Patch(jump, context.ContinueTarget);
	}

	private void EmitJumpTo(int target)
	{
		var jump = EmitJump(OpCode.Jmp);
		Patch(jump, target);
	}

	private void EmitIf(BoundIfExpression conditional)
	{
		EmitExpression(conditional.Condition);
		if (conditional.Else is null)
		{
			var endJump = EmitJump(OpCode.JmpIfNot);
			EmitExpression(conditional.Then);
			var skipJump = EmitJump(OpCode.Jmp);
			Patch(endJump, Position);
			Emit(OpCode.PushVoid);
			Patch(skipJump, Position);
			return;
		}

		var elseJump = EmitJump(OpCode.JmpIfNot);
		EmitExpression(conditional.Then);
		var end = EmitJump(OpCode.Jmp);
		Patch(elseJump, Position);
		EmitExpression(conditional.Else);
		Patch(end, Position);
	}

	private void EmitCall(BoundCallExpression call)
	{
		if (call.Receiver is not null)
			EmitExpression(call.Receiver);
		foreach (var argument in call.Arguments)
		{
			EmitExpression(argument);
			EmitCopyIfUnit(argument.Type);
		}

		if (!_functionIndices.TryGetValue(call.Function, out var index))
		{
			Report("KRN2201", $"Функция '{call.Function.Name}' не имеет байткода (нативная привязка появится в M6)", call.Span);
			for (var argument = 0; argument < call.Arguments.Count; argument++)
				Emit(OpCode.Pop);
			if (call.Receiver is not null)
				Emit(OpCode.Pop);
			Emit(OpCode.PushVoid);
			return;
		}

		var argumentCount = call.Arguments.Count + (call.Receiver is null ? 0 : 1);
		Emit(OpCode.Call);
		EmitOperand(index);
		EmitOperand(argumentCount);
	}

	private void EmitObjectCreation(BoundObjectCreationExpression creation)
	{
		if (!_typeIndices.TryGetValue(creation.TypeSymbol, out var typeIndex))
		{
			Report("KRN2201", $"Тип '{creation.TypeSymbol.Name}' не попал в TypePool (M3-D)", creation.Span);
			Emit(OpCode.PushVoid);
			return;
		}

		Emit(OpCode.NewObject, typeIndex);
		if (!_functionIndices.TryGetValue(creation.Constructor, out var constructorIndex))
		{
			Report("KRN2201", $"Конструктор типа '{creation.TypeSymbol.Name}' не найден (M3-D)", creation.Span);
			return;
		}

		Emit(OpCode.Dup);
		foreach (var argument in creation.Arguments)
		{
			EmitExpression(argument);
			EmitCopyIfUnit(argument.Type);
		}
		Emit(OpCode.Call);
		EmitOperand(constructorIndex);
		EmitOperand(creation.Arguments.Count + 1);
		Emit(OpCode.Pop);
	}

	private void EmitLiteral(BoundLiteralExpression literal)
	{
		if (literal.Type.Equals(PrimitiveType.Str))
		{
			var text = literal.Value as string ?? string.Empty;
			Emit(OpCode.PushConst, _module.InternConst(KConst.Str(text)));
			return;
		}

		if (literal.Type.Equals(PrimitiveType.Char))
		{
			var symbolValue = literal.Value is char symbol ? symbol : '\0';
			Emit(OpCode.PushConst, _module.InternConst(KConst.Char(symbolValue)));
			return;
		}

		if (literal.Type.Equals(PrimitiveType.Bool))
		{
			Emit(literal.Value is true ? OpCode.PushTrue : OpCode.PushFalse);
			return;
		}

		var numeric = literal.Numeric;
		if (numeric is null)
		{
			Emit(OpCode.PushVoid);
			return;
		}

		if (numeric.IsFloat)
		{
			EmitPushF64(numeric.FloatValue);
			EmitConversion(literal.Type);
			return;
		}

		// u64-литералы выходят за long.MaxValue, поэтому берём биты через ulong:
		// BigInteger -> long упал бы OverflowException на 0xFFFF_FFFF_FFFF_FFFF.
		var integerValue = literal.Type is PrimitiveType { Primitive: PrimitiveKind.U64 }
			? unchecked((long)(ulong)numeric.IntegerValue)
			: unchecked((long)numeric.IntegerValue);
		if (literal.Type is PrimitiveType { Primitive: PrimitiveKind.I64 or PrimitiveKind.U64 })
		{
			EmitPushI64(integerValue);
			EmitConversion(literal.Type);
		}
		else
		{
			Emit(OpCode.PushI32, unchecked((int)integerValue));
			EmitConversion(literal.Type);
		}
	}

	private void EmitConversion(KernType target)
	{
		if (!TypeSigMap.TryPrimitive(target, out var primitive))
			return;

		switch (primitive)
		{
			case PrimitiveKind.I8: Emit(OpCode.ConvI8); break;
			case PrimitiveKind.I16: Emit(OpCode.ConvI16); break;
			case PrimitiveKind.I32: Emit(OpCode.ConvI32); break;
			case PrimitiveKind.I64: Emit(OpCode.ConvI64); break;
			case PrimitiveKind.U8: Emit(OpCode.ConvU8); break;
			case PrimitiveKind.U16: Emit(OpCode.ConvU16); break;
			case PrimitiveKind.U32: Emit(OpCode.ConvU32); break;
			case PrimitiveKind.U64: Emit(OpCode.ConvU64); break;
			case PrimitiveKind.F16: Emit(OpCode.ConvF16); break;
			case PrimitiveKind.F32: Emit(OpCode.ConvF32); break;
			case PrimitiveKind.F64: Emit(OpCode.ConvF64); break;
			case PrimitiveKind.Char: Emit(OpCode.ConvChar); break;
			case PrimitiveKind.Bool: Emit(OpCode.ConvBool); break;
		}
	}

	/// <summary>
	/// Запоминает строку начала оператора для текущего смещения. Пишутся только смены строки
	/// и только со строго возрастающим смещением — этого достаточно для <c>LineAt</c>.
	/// </summary>
	private void RecordLine(TextSpan span)
	{
		var line = _bundle is not null
			? _bundle.GetLocalLine(span.Start)
			: _source?.GetLine(span.Start);
		if (line is null)
			return;

		var offset = Position;
		var lines = _current.LineTable;
		if (lines.Count > 0)
		{
			var last = lines[^1];
			if (last.Offset >= offset || last.Line == line)
				return;
		}
		lines.Add(new LineEntry(offset, line.Value));
	}

	/// <summary>
	/// Файл функции в мультифайловом режиме: объявление функции, иначе объявление типа-владельца
	/// (синтезированные ctor/&lt;static-init&gt;) и только потом первый файл проекта.
	/// </summary>
	private static string SourceFileFor(BoundFunction function, SourceBundle bundle)
	{
		if (function.Symbol.Declaration is { } declaration)
			return bundle.PathAt(declaration.Span.Start);
		if (function.Symbol.Owner?.Declaration is { } owner)
			return bundle.PathAt(owner.Span.Start);
		return bundle.Paths[0];
	}

	private int Position => _code.Count;

	private int Emit(OpCode op)
	{
		_code.Add((int)op);
		return _code.Count - 1;
	}

	/// <summary>Инструкция перехода с операндом-заглушкой; возвращает позицию операнда для Patch.</summary>
	private int EmitJump(OpCode op)
	{
		_code.Add((int)op);
		_code.Add(0);
		return _code.Count - 1;
	}

	private void Emit(OpCode op, int operand)
	{
		_code.Add((int)op);
		_code.Add(operand);
	}

	private void EmitOperand(int operand) => _code.Add(operand);

	private void Patch(int operandPosition, int target) => _code[operandPosition] = target;

	private void EmitPushI64(long value)
	{
		Emit(OpCode.PushI64);
		EmitOperand(unchecked((int)value));
		EmitOperand(unchecked((int)(value >> 32)));
	}

	private void EmitPushF64(double value)
	{
		var bits = BitConverter.DoubleToInt64Bits(value);
		Emit(OpCode.PushF64);
		EmitOperand(unchecked((int)bits));
		EmitOperand(unchecked((int)(bits >> 32)));
	}

	private void Report(string code, string message, TextSpan span) =>
		_diagnostics.Add(new Diagnostic(code, message, span));

	/// <summary>Метки текущего цикла для stop/next; continue-цель известна после генерации тела.</summary>
	private sealed class LoopContext
	{
		public List<int> BreakJumps { get; } = [];
		public List<int> ContinueJumps { get; } = [];
		public int ContinueTarget { get; set; }
	}
}
