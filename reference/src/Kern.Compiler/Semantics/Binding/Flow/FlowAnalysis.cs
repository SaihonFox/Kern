using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Binding.Core;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Core;

namespace Kern.Compiler.Semantics.Binding.Flow;

/// <summary>
/// Анализ потока управления, выполняемый ВО ВРЕМЯ привязки (структурные конструкции языка делают это возможным за один проход):
/// <list type="bullet">
/// <item>smart-cast: после `x != nil`, `if x`, `x is T`, присваивания значения типа T переменной T?; сужение переживает слияние путей,
/// только если оно есть на всех путях (в том числе после `if !x { ret }`);</item>
/// <item>обязательное присваивание: чтение переменной `T! x` до присваивания — ошибка, обычной переменной без инициализатора — предупреждение (`none`);</item>
/// <item>single-assignment `T! x`: повторное присваивание на одном пути — ошибка (взаимоисключающие ветки допустимы);</item>
/// <item>недостижимый код после `ret`/`stop`/`next`/`panic` — предупреждение.</item>
/// </list>
/// </summary>
public sealed class FlowAnalysis(Binder binder)
{
	private readonly List<LoopFlow> _loops = [];

	/// <summary>Текущее состояние (мутабельное; сохраняйте копию через <see cref="Fork"/>).</summary>
	public FlowState Current { get; set; } = new();

	public bool IsReachable => Current.Reachable;

	public FlowState Fork() => Current.Clone();

	public void MarkUnreachable() => Current.MarkUnreachable();

	// ---------------------------------------------------------------- типы переменных

	/// <summary>Тип локальной переменной в текущей точке (с учётом сужения).</summary>
	public TypeSymbol TypeOf(LocalSymbol local) => Current.NarrowedType(local) ?? local.Type ?? ErrorType.Instance;

	public TypeSymbol TypeOf(ParameterSymbol p) => Current.NarrowedType(p) ?? p.EffectiveType;

	// ---------------------------------------------------------------- объявление, чтение, присваивание

	public void Declare(LocalSymbol local, bool initialized)
	{
		Current.ResetVariable(local);
		if (initialized) Current.MarkAssigned(local);
	}

	/// <summary>Чтение локальной переменной. <paramref name="allowNone"/> — чтение допустимо в состоянии `none` (`x == none`, `x ?: y`, `if x`).</summary>
	public void ReadLocal(LocalSymbol local, TextSpan span, bool allowNone)
	{
		if (!IsReachable || Current.IsAssigned(local)) return;
		if (local.IsSingleAssign)
		{
			binder.Error(Krn.UseBeforeAssign, $"переменная '{local.Name}' (`!`) читается до первого присваивания", span);
			Current.MarkAssigned(local); // без каскада
		}
		else if (!allowNone && local.Type is { } t && !t.IsError && t is not NullableType)
		{
			binder.Warn(Krn.MaybeNone, $"переменная '{local.Name}' может находиться в состоянии `none` (значение не присвоено на всех путях)", span);
			Current.MarkAssigned(local);
		}
	}

	/// <summary>
	/// Присваивание локальной переменной значения типа <paramref name="valueType"/>: проверка single-assign, отметка присвоенности и сужения.
	/// </summary>
	public void AssignLocal(LocalSymbol local, TextSpan span, TypeSymbol? valueType)
	{
		if (local.IsSingleAssign && IsReachable && Current.IsMaybeAssigned(local))
			binder.Error(Krn.SingleAssignRepeated, $"переменная '{local.Name}' (`!`) уже получила значение, повторное присваивание запрещено", span);
		Current.MarkAssigned(local);
		Current.ClearNarrowing(local);
		if (valueType is null || local.Type is not { } declared) return;

		// T? = значение типа T → T;  union = член → член.
		if (declared is NullableType n && valueType is not (NilType or NoneType or NullableType or ErrorType)
			&& Conversions.IsImplicit(valueType, n.Inner))
			Current.Narrow(local, valueType.Equals(n.Inner) || valueType is NeverType or PrimitiveType ? n.Inner : valueType);
		else if (declared is UnionType && valueType is not (NilType or NoneType or ErrorType or UnionType) && !valueType.Equals(declared))
			Current.Narrow(local, valueType);
	}

	public void AssignParameter(ParameterSymbol p, TypeSymbol? valueType)
	{
		Current.ClearNarrowing(p);
		var declared = p.EffectiveType;
		if (valueType is not null && declared is NullableType n && valueType is not (NilType or NoneType or NullableType or ErrorType)
			&& Conversions.IsImplicit(valueType, n.Inner))
			Current.Narrow(p, n.Inner);
	}

	// ---------------------------------------------------------------- факты условий (для состояний ветвей)

	private static Symbol? VariableOf(BoundExpr e) => e switch
	{
		BoundLocal l => l.Local,
		BoundParameter p => p.Parameter,
		_ => null,
	};

	/// <summary>Выражение — устойчивая переменная (локальная/параметр), доступная для smart-cast.</summary>
	public static bool IsNarrowable(BoundExpr e) => VariableOf(e) is not null;

	/// <summary>`x` содержит значение (не nil и не none): `T?` → `T`; присвоенность.</summary>
	public void ApplyHasValue(FlowState st, BoundExpr e)
	{
		if (VariableOf(e) is not { } sym) return;
		if (e.Type is NullableType n) st.Narrow(sym, n.Inner);
		if (sym is LocalSymbol l) st.MarkAssigned(l);
	}

	/// <summary>`x` не в состоянии `none` (но может быть nil): только присвоенность.</summary>
	public void ApplyNotNone(FlowState st, BoundExpr e)
	{
		if (VariableOf(e) is LocalSymbol l) st.MarkAssigned(l);
	}

	/// <summary>
	/// `x is T` (<paramref name="positive"/> = true) либо его отрицание на ветке, где проверка не прошла:
	/// сужение до T; для отрицания — исключение члена из union/nullable.
	/// </summary>
	public void ApplyIs(FlowState st, BoundExpr e, TypeSymbol test, bool positive)
	{
		if (VariableOf(e) is not { } sym || test.IsError) return;
		var current = e.Type;
		if (positive)
		{
			var inner = current is NullableType n ? n.Inner : current;
			if (!inner.Equals(test) && Conversions.IsImplicit(test, inner)) st.Narrow(sym, test);
			else if (current is NullableType) st.Narrow(sym, inner);
			return;
		}
		// отрицательная ветка: убираем test из union
		if (current is UnionType u)
		{
			var rest = u.Members.Where(m => !m.Equals(test)).ToList();
			if (rest.Count == 1) st.Narrow(sym, rest[0]);
		}
		else if (current is NullableType { Inner: UnionType nu })
		{
			var rest = nu.Members.Where(m => !m.Equals(test)).ToList();
			if (rest.Count == 1) st.Narrow(sym, NullableType.Of(rest[0]));
		}
	}

	// ---------------------------------------------------------------- циклы

	/// <summary>Вход в цикл: сбрасывает сужение переменных, присваиваемых в теле (на второй итерации оно неверно).</summary>
	public void EnterLoop(IReadOnlySet<string> assignedInBody)
	{
		Current.ClearNarrowingByName(assignedInBody);
		_loops.Add(new LoopFlow());
	}

	/// <summary>Выход из цикла: возвращает состояния всех `stop` (слить с состоянием «цикл не выполнился/закончился»).</summary>
	public List<FlowState> ExitLoop()
	{
		var l = _loops[^1];
		_loops.RemoveAt(_loops.Count - 1);
		return l.Breaks;
	}

	public bool InLoop => _loops.Count > 0;

	/// <summary>Вызывается при привязке `stop` (после проверки <see cref="InLoop"/> биндером).</summary>
	public void Break()
	{
		if (_loops.Count > 0 && IsReachable) _loops[^1].Breaks.Add(Current.Clone());
		Current.MarkUnreachable();
	}

	public void Continue() => Current.MarkUnreachable();

	/// <summary>
	/// Границы цикла для лямбд и вложенных функций: `stop`/`next` внутри лямбды не относятся к внешнему циклу.
	/// Возвращает токен для <see cref="RestoreLoops"/>.
	/// </summary>
	public List<LoopFlow> HideLoops()
	{
		var saved = new List<LoopFlow>(_loops);
		_loops.Clear();
		return saved;
	}

	public void RestoreLoops(List<LoopFlow> saved)
	{
		_loops.Clear();
		_loops.AddRange(saved);
	}

	public sealed class LoopFlow
	{
		public List<FlowState> Breaks { get; } = [];
	}
}
