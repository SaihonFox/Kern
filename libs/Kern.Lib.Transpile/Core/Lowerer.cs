using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Lib.Transpile.Mapping;

namespace Kern.Lib.Transpile.Core;

/// <summary>
/// Общая часть транспилятора: обход типизированного дерева (<see cref="BoundModule"/>) и
/// «выпрямление» выражений в операторы. Выражения <c>if</c>/<c>match</c>/<c>loop</c>/блок
/// в позиции значения выносятся во временные переменные, поэтому порядок вычисления
/// сохраняется для обычного кода, но не для побочных эффектов внутри сложных подвыражений.
/// Синтаксис конкретного языка поставляют наследники (<c>CSharpLowerer</c>, <c>KotlinLowerer</c>).
/// </summary>
public abstract partial class Lowerer(IMappings maps)
{
	protected IMappings Maps { get; } = maps;

	protected CodeWriter W { get; set; } = new();

	protected BoundModule Module { get; private set; } = null!;

	protected TypeSymbol? CurrentType { get; set; }

	/// <summary>Имя получателя в позиции <c>$</c>; у свободных функций null.</summary>
	protected string? ThisAlias { get; set; }

	/// <summary>Нативные члены без правила: попадают в заголовок файла как напоминание.</summary>
	public SortedSet<string> Unmapped { get; } = new(StringComparer.Ordinal);

	public List<string> Warnings { get; } = [];

	private readonly Dictionary<FunctionSymbol, BoundFunction> _functions = [];
	private readonly Dictionary<int, string> _slotNames = [];
	private readonly HashSet<string> _usedNames = new(StringComparer.Ordinal);
	private readonly Stack<Dictionary<int, string>> _captureScopes = new();
	private readonly Stack<List<string>> _disposers = new();
	private int _temp;

	public abstract string Id { get; }

	public abstract string Extension { get; }

	protected virtual string LineComment => "//";

	protected abstract string End { get; }

	protected abstract bool AlwaysStatement { get; }

	protected abstract string RuntimeFileName { get; }

	protected abstract string RuntimeText { get; }

	protected abstract string FileFor(BoundModule module);

	protected abstract void EmitModule();

	protected abstract string LocalName(string kernName);

	protected abstract string MemberName(string kernName);

	protected abstract string TypeName(KernType type);

	/// <summary>Все файлы результата: модуль (включая заголовок) и вспомогательный runtime.</summary>
	public Dictionary<string, string> Run(BoundModule module)
	{
		ArgumentNullException.ThrowIfNull(module);
		Module = module;
		_functions.Clear();
		_temp = 0;
		Unmapped.Clear();
		Warnings.Clear();
		foreach (BoundFunction function in module.Functions)
			_functions.TryAdd(function.Symbol, function);

		W = new CodeWriter();
		EmitModule();
		string body = Header() + W;
		return new Dictionary<string, string>(StringComparer.Ordinal)
		{
			[FileFor(module)] = body,
			[RuntimeFileName] = RuntimeText,
		};
	}

	private string Header()
	{
		var header = new List<string>
		{
			$"{LineComment} Сгенерировано транспилятором Kern → {Id}. Не редактируйте вручную."
		};
		if (Unmapped.Count > 0)
			header.Add($"{LineComment} Нет правила подстановки (задайте через map_script): {string.Join(", ", Unmapped)}");
		foreach (string warning in Warnings.Distinct(StringComparer.Ordinal))
			header.Add($"{LineComment} ВНИМАНИЕ: {warning}");
		return string.Join('\n', header) + "\n";
	}

	// ------------------------------------------------------------------ общие сведения о модуле

	protected BoundFunction? BoundOf(FunctionSymbol symbol) =>
		_functions.TryGetValue(symbol, out BoundFunction? function) ? function : null;

	protected IEnumerable<TypeSymbol> TypeDeclarations =>
		Module.Symbol.Types.Values
			.Where(type => type.Kind != SymbolKind.Native && type.Declaration is not null)
			.Where(type => !ResultTypes.IsResultVariant(type));

	/// <summary>Функции типа в порядке объявления: методы, операторы, конструкторы, accessor'ы.</summary>
	protected IEnumerable<BoundFunction> MembersOf(TypeSymbol type) =>
		Module.Functions.Where(function => ReferenceEquals(function.Symbol.Owner, type));

	protected IEnumerable<BoundFunction> TopLevelFunctions =>
		Module.Functions.Where(function =>
			function.Symbol.Owner is null &&
			function.Symbol.ReceiverType is null &&
			!function.Symbol.IsNative &&
			function.Symbol.Kind == FunctionKind.Function);

	protected IEnumerable<BoundFunction> ExtensionFunctions =>
		Module.Functions.Where(function =>
			function.Symbol.Owner is null &&
			function.Symbol.ReceiverType is not null &&
			!function.Symbol.IsNative);

	protected static bool IsVoid(KernType type) =>
		type is PrimitiveType { Primitive: PrimitiveKind.Void or PrimitiveKind.Never or PrimitiveKind.None };

	protected static bool IsNever(KernType type) =>
		type is PrimitiveType { Primitive: PrimitiveKind.Never };

	protected string Temp(string prefix = "t")
	{
		string name;
		do
		{
			name = prefix + ++_temp;
		}
		while (!_usedNames.Add(name));

		return name;
	}

	/// <summary>Имя локальной переменной по слоту binder'а; повторные ссылки дают то же имя.</summary>
	protected string NameOfSlot(int slot, string kernName)
	{
		if (slot < 0)
			return LocalName(kernName);
		if (_slotNames.TryGetValue(slot, out string? known))
			return known;

		string name = LocalName(kernName);
		if (!_usedNames.Add(name))
		{
			var index = 2;
			string unique;
			do
			{
				unique = name + index++;
			}
			while (!_usedNames.Add(unique));
			name = unique;
		}

		_slotNames[slot] = name;
		return name;
	}

	/// <summary>Явно связать слот с готовым именем (<c>$</c> → <c>this</c>/<c>self</c>).</summary>
	protected void RegisterSlot(int slot, string name) => _slotNames[slot] = name;

	protected bool IsSlotNamed(int slot) => slot >= 0 && _slotNames.ContainsKey(slot);

	/// <summary>Сохранить состояние имён на время вложенной функции (лямбды) и восстановить после.</summary>
	protected IDisposable PushFunctionScope()
	{
		var state = new FunctionScope(this);
		BeginFunction();
		return state;
	}

	private sealed class FunctionScope(Lowerer owner) : IDisposable
	{
		private readonly Dictionary<int, string> _slots = new(owner._slotNames);
		private readonly HashSet<string> _used = new(owner._usedNames, StringComparer.Ordinal);
		private readonly Dictionary<int, string>[] _captures = [.. owner._captureScopes];

		public void Dispose()
		{
			owner._slotNames.Clear();
			foreach (var (slot, name) in _slots)
				owner._slotNames[slot] = name;
			owner._usedNames.Clear();
			owner._usedNames.UnionWith(_used);
			owner._captureScopes.Clear();
			for (var index = _captures.Length - 1; index >= 0; index--)
				owner._captureScopes.Push(_captures[index]);
		}
	}

	protected void BeginFunction()
	{
		_slotNames.Clear();
		_usedNames.Clear();
		_captureScopes.Clear();
		_disposers.Clear();
	}

	protected string This => ThisAlias ?? "this";

	// ------------------------------------------------------------------ захват вывода

	/// <summary>Выполняет <paramref name="body"/> в отдельном писателе и возвращает (значение, операторы).</summary>
	protected (string Value, string Text) Capture(Func<string> body)
	{
		CodeWriter saved = W;
		W = new CodeWriter();
		try
		{
			string value = body();
			return (value, W.ToString());
		}
		finally
		{
			W = saved;
		}
	}

	// ------------------------------------------------------------------ назначение значения

	protected enum DestKind
	{
		Discard,
		Return,
		Assign,
		AddTo
	}

	protected readonly record struct Dest(DestKind Kind, string? Target = null)
	{
		public static readonly Dest Discard = new(DestKind.Discard);
		public static readonly Dest Return = new(DestKind.Return);

		public static Dest Assign(string name) => new(DestKind.Assign, name);

		public static Dest AddTo(string list) => new(DestKind.AddTo, list);
	}

	protected void EmitBody(BoundBlock body, KernType returnType, bool isCtor = false) =>
		WithResultHint(returnType, () =>
			EmitBlock(body, isCtor || IsVoid(returnType) ? Dest.Discard : Dest.Return));

	protected void EmitBlock(BoundBlock block, Dest dest)
	{
		_disposers.Push([]);
		try
		{
			foreach (BoundStatement statement in block.Statements)
			{
				if (block.Value is not null && statement is BoundExpressionStatement expressionStatement &&
					ReferenceEquals(expressionStatement.Expression, block.Value))
					continue;
				if (block.Value is not null && statement is BoundDiscardStatement discardStatement &&
					ReferenceEquals(discardStatement.Value, block.Value))
					continue;
				Stmt(statement);
			}

			string? value = null;
			if (block.Value is { } tail)
				value = Value(tail);

			string[] disposers = [.. _disposers.Peek()];
			for (var index = disposers.Length - 1; index >= 0; index--)
				W.Line(disposers[index]);

			if (value is not null)
				Finish(dest, value, block.Value!);
		}
		finally
		{
			_disposers.Pop();
		}
	}

	/// <summary>Доставляет готовое значение по назначению.</summary>
	protected void Finish(Dest dest, string value, BoundExpression source)
	{
		switch (dest.Kind)
		{
			case DestKind.Discard:
				if (IsVoid(source.Type) && source is not BoundCallExpression)
					break;
				W.Line(StatementText(value));
				break;
			case DestKind.Return:
				W.Line(AlwaysStatement ? $"return {StripParens(value)}" : $"return {StripParens(value)};");
				break;
			case DestKind.Assign:
				W.Line($"{dest.Target} = {StripParens(value)}{End}");
				break;
			case DestKind.AddTo:
				W.Line(AddToText(dest.Target!, StripParens(value)));
				break;
		}
	}

	private string StatementText(string value)
	{
		if (AlwaysStatement)
			return value;
		if (value.StartsWith("throw ", StringComparison.Ordinal) ||
			value.StartsWith("await ", StringComparison.Ordinal) ||
			(value.EndsWith(')') && !value.StartsWith('(')))
			return value + ";";
		return DiscardText(value);
	}

	// ------------------------------------------------------------------ операторы

	protected void Stmt(BoundStatement statement)
	{
		switch (statement)
		{
			case BoundBlock block:
				EmitBlock(block, Dest.Discard);
				break;

			case BoundLocalDeclaration declaration:
				LocalDeclaration(declaration);
				break;

			case BoundUseStatement use:
				UseStatement(use);
				break;

			case BoundAssignment assignment:
				Assignment(assignment);
				break;

			case BoundFieldAssignment fieldAssignment:
				W.Line(FieldTarget(fieldAssignment.Receiver, fieldAssignment.Field) +
					$" = {StripParens(Value(fieldAssignment.Value))}{End}");
				break;

			case BoundIndexStore store:
				W.Line($"{Value(store.Target)}[{Value(store.Index)}] = {StripParens(Value(store.Value))}{End}");
				break;

			case BoundExpressionStatement expressionStatement:
				Control(expressionStatement.Expression, Dest.Discard);
				break;

			case BoundDiscardStatement discard:
				Control(discard.Value, Dest.Discard);
				break;

			case BoundReturn ret:
				Return(ret);
				break;

			case BoundLoopStatement loop:
				LoopStatement(loop);
				break;

			case BoundStopStatement stop:
				if (stop.TargetDepth > 0)
					Warnings.Add($"stop с меткой внешнего цикла упрощён до break (глубина {stop.TargetDepth})");
				W.Line("break" + End);
				break;

			case BoundNextStatement next:
				if (next.TargetDepth > 0)
					Warnings.Add($"next с меткой внешнего цикла упрощён до continue (глубина {next.TargetDepth})");
				W.Line("continue" + End);
				break;

			case BoundCapturedAssignment captured:
				W.Line($"{CaptureName(captured.CaptureIndex)} = {StripParens(Value(captured.Value))}{End}");
				break;

			case BoundChainStore chainStore:
				Warnings.Add("запись по цепи членов (a.b.c = v) перенесена без обратного копирования unit");
				ChainStore(chainStore);
				break;

			default:
				Unsupported(statement);
				break;
		}
	}

	private void Unsupported(BoundStatement statement)
	{
		Warnings.Add($"оператор {statement.GetType().Name} не поддержан транспилятором");
		W.Line($"{LineComment} не поддержано: {statement.GetType().Name}");
	}

	private void LocalDeclaration(BoundLocalDeclaration declaration)
	{
		string name = NameOfSlot(declaration.Slot, declaration.Name);
		if (declaration.Initializer is null)
		{
			DeclareLocal(name, declaration.Type, null, declaration.IsSingleAssignment);
		}
		else
		{
			var (value, text) = Capture(() => WithResultHint(declaration.Type, () => Value(declaration.Initializer)));
			if (text.Length == 0)
			{
				DeclareLocal(name, declaration.Type, StripParens(value), declaration.IsSingleAssignment);
			}
			else
			{
				DeclareLocal(name, declaration.Type, null, declaration.IsSingleAssignment);
				W.Line(text.TrimEnd('\n'));
				W.Line($"{name} = {StripParens(value)}{End}");
			}
		}

		foreach (BoundLocalBinding binding in declaration.DeclaredBindings)
		{
			string bindingName = NameOfSlot(binding.Slot, binding.Name);
			if (binding.Initializer is null)
			{
				DeclareLocal(bindingName, null, null, false);
				continue;
			}

			var (value, text) = Capture(() => Value(binding.Initializer));
			if (text.Length > 0)
				W.Line(text.TrimEnd('\n'));
			DeclareLocal(bindingName, binding.Initializer.Type, StripParens(value), false);
		}
	}

	private void UseStatement(BoundUseStatement use)
	{
		string name = NameOfSlot(use.Slot, use.Name);
		var (value, text) = Capture(() => Value(use.Initializer));
		if (text.Length > 0)
			W.Line(text.TrimEnd('\n'));
		DeclareLocal(name, use.Type, StripParens(value), false);
		if (use.Dispose is not null && _disposers.Count > 0)
			_disposers.Peek().Add($"{name}.{MemberName(use.Dispose.Name)}(){End}");
	}

	private void Assignment(BoundAssignment assignment)
	{
		bool declared = IsSlotNamed(assignment.Slot);
		string name = NameOfSlot(assignment.Slot, assignment.Name);
		var (value, text) = Capture(() => WithResultHint(assignment.Type, () => Value(assignment.Value)));
		if (text.Length > 0)
			W.Line(text.TrimEnd('\n'));
		if (!declared)
			DeclareLocal(name, assignment.Type, StripParens(value), false);
		else
			W.Line($"{name} = {StripParens(value)}{End}");
	}

	private void Return(BoundReturn ret)
	{
		foreach (List<string> frame in _disposers)
			for (var index = frame.Count - 1; index >= 0; index--)
				W.Line(frame[index]);

		if (ret.Expression is null)
		{
			W.Line("return" + End);
			return;
		}

		string value = Value(ret.Expression);
		W.Line($"return {StripParens(value)}{End}");
	}

	private void LoopStatement(BoundLoopStatement loop)
	{
		if (loop.Label is { Length: > 0 } label)
			W.Line($"{LineComment} loop@{label}");

		if (loop.IsDoWhile && loop.Condition is not null)
		{
			OpenDoWhileLoop();
			EmitBlock(loop.Body, Dest.Discard);
			CloseDoWhile(StripParens(Value(loop.Condition)));
			return;
		}

		if (loop.Condition is null || loop.IsInfinite)
		{
			OpenInfiniteLoop();
		}
		else
		{
			var (condition, text) = Capture(() => Value(loop.Condition));
			if (text.Length == 0)
			{
				OpenWhile(StripParens(condition));
			}
			else
			{
				OpenInfiniteLoop();
				W.Line(text.TrimEnd('\n'));
				W.Open($"if (!({StripParens(condition)}))");
				W.Line("break" + End);
				W.Close();
			}
		}

		EmitBlock(loop.Body, Dest.Discard);
		W.Close();
	}

	// ------------------------------------------------------------------ имена/получатели

	/// <summary>Квалификатор члена: явный приёмник, <c>this</c>, тип (static) или модуль (global).</summary>
	protected string Qualifier(BoundExpression? receiver, FieldSymbol? field, bool isStatic, out bool global)
	{
		global = false;
		if (receiver is not null)
			return Value(receiver);
		if (field is { Owner: null })
		{
			global = true;
			return "";
		}

		if (isStatic && field?.Owner is { } owner)
			return TypeName(InstanceType(owner));
		return This;
	}

	protected string FieldTarget(BoundExpression? receiver, FieldSymbol field)
	{
		string member = FieldMemberName(field);
		if (field.Owner is null && receiver is null)
			return GlobalRef(member);
		string qualifier = Qualifier(receiver, field, field.IsStatic, out _);
		return qualifier.Length == 0 ? member : $"{qualifier}.{member}";
	}

	protected string FieldMemberName(FieldSymbol field)
	{
		if (field.IsBackingField)
		{
			PropertySymbol? property = field.Owner?.Members.Properties
				.FirstOrDefault(candidate => ReferenceEquals(candidate.BackingField, field));
			if (property is { IsAuto: true })
				return MemberName(property.Name);
			return "_" + MemberName(field.Name.TrimStart('_'));
		}

		return MemberName(field.Name);
	}

	/// <summary>Backing-поле авто-свойства не выводится отдельно (его заменяет свойство).</summary>
	protected static bool IsAutoBacking(FieldSymbol field) =>
		field.IsBackingField && field.Owner?.Members.Properties
			.FirstOrDefault(candidate => ReferenceEquals(candidate.BackingField, field)) is { IsAuto: true };

	protected static KernType InstanceType(TypeSymbol symbol) =>
		symbol.IsValueType
			? new UnitType(symbol, [], symbol.IsImmutable)
			: new ClassType(symbol, []);

	protected static string StripParens(string text)
	{
		if (text.Length < 2 || text[0] != '(' || text[^1] != ')')
			return text;
		var depth = 0;
		for (var index = 0; index < text.Length; index++)
		{
			if (text[index] == '(')
				depth++;
			else if (text[index] == ')')
			{
				depth--;
				if (depth == 0 && index < text.Length - 1)
					return text;
			}
		}

		return text[1..^1];
	}

	protected static bool IsSimple(string text) =>
		text.All(symbol => char.IsLetterOrDigit(symbol) || symbol is '_' or '.');

	// ------------------------------------------------------------------ хуки языков (операторы)

	protected abstract void DeclareLocal(string name, KernType? type, string? init, bool single);

	protected abstract string DiscardText(string value);

	protected abstract string ThrowText(string message);

	protected abstract string AddToText(string list, string value);

	protected abstract string NewListText(KernType element);

	protected abstract void OpenWhile(string condition);

	protected abstract void OpenInfiniteLoop();

	protected abstract void OpenDoWhileLoop();

	protected abstract void CloseDoWhile(string condition);

	// ------------------------------------------------------------------ хуки языков (выражения)

	protected abstract string NumberLit(BoundLiteralExpression literal);

	protected abstract string NilLit { get; }

	protected abstract string NoneLit { get; }

	protected abstract string Ternary(string condition, string whenTrue, string whenFalse);

	protected abstract string Lambda(
		IReadOnlyList<(string Name, string Type)> parameters,
		string? expression,
		string? block,
		KernType returnType,
		bool isAsync);

	protected abstract string ListLit(KernType element, IReadOnlyList<string> items);

	protected abstract string ArrayLit(KernType element, IReadOnlyList<string> items);

	protected abstract string DictLit(KernType key, KernType value, IReadOnlyList<(string Key, string Value)> items);

	protected abstract string TupleLit(IReadOnlyList<string> items);

	protected abstract string Pow(string left, string right);

	protected abstract string InText(string item, string collection, bool negated);

	protected abstract string RefEq(string left, string right, bool negated);

	protected abstract string Cast(string subject, KernType type);

	protected abstract string IsType(string subject, KernType type, bool negated);

	protected abstract string NonNullText(string value);

	protected abstract string CoalesceText(string left, string right);

	protected abstract string TupleItemText(string subject, int index, int count);

	protected abstract string LenText(string subject);

	protected abstract string RestSlice(string subject, string start, string end, bool inclusive);

	protected abstract string ResultText(bool ok, string value, KernType type);

	protected abstract string ResultIsOk(string subject, bool ok);

	protected abstract string ResultValue(string subject);

	protected abstract string ResultError(string subject);

	protected abstract string TypeOfText(KernType type);

	protected abstract string TypeOfValueText(string value);

	protected abstract string AwaitText(string value);

	protected abstract string NewText(KernType type, IReadOnlyList<string> args);

	protected abstract string FunctionRefText(string? receiver, string name, bool global);

	protected abstract string ConvertText(string value, KernType from, KernType to);

	protected abstract string BaseAccess(KernType parent);

	protected abstract string NullDefault(KernType type);

	protected abstract string CallText(
		string? receiver,
		string name,
		IReadOnlyList<KernType> typeArguments,
		IReadOnlyList<string> arguments,
		bool safe);

	protected abstract string GlobalRef(string name);

	protected abstract string HashText(string value, bool structural);

	protected abstract string CompareText(string left, string right, bool structural);

	protected abstract string CloneText(string value);

	protected abstract string ZeroOrNull(KernType? type);

	protected abstract void EmitProperty(
		PropertySymbol property,
		TypeSymbol owner,
		BoundFunction? getter,
		BoundFunction? setter,
		BoundFunction? init);

	protected abstract void EmitConstructor(TypeSymbol type, BoundFunction constructor);

	protected abstract void EmitMethod(TypeSymbol owner, BoundFunction method);

	protected abstract void EmitField(FieldSymbol field);

	protected abstract void EmitTypeDeclaration(TypeSymbol type);

	protected abstract void EmitGlobal(FieldSymbol field);
}
