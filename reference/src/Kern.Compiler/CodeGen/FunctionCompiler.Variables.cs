using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Bound.Patterns;
using Kern.Compiler.Semantics.Symbols;

namespace Kern.Compiler.CodeGen;

/// <summary>Локальные переменные, параметры, ячейки замыканий.</summary>
internal sealed partial class FunctionCompiler
{
	private int _nextLocal;
	/// <summary>Слот локальной переменной (или ячейки у «celled»-параметра).</summary>
	private readonly Dictionary<Symbol, int> _slots = [];
	/// <summary>Переменные, лежащие в KCell (захватываются замыканиями).</summary>
	private readonly HashSet<Symbol> _celled = [];
	/// <summary>Для лямбды: захваченная переменная → индекс в Captures.</summary>
	private readonly Dictionary<Symbol, int> _captures = [];
	private readonly Dictionary<ParameterSymbol, int> _paramIndex = [];

	private int AllocTemp() => _nextLocal++;

	private void BeginParams(IReadOnlyList<ParameterSymbol> ps, object? _)
	{
		for (int i = 0; i < ps.Count; i++) _paramIndex[ps[i]] = i;
	}

	/// <summary>Параметры, захваченные замыканиями, переносятся в ячейки при входе.</summary>
	private void BeginParamCells(IReadOnlyList<ParameterSymbol> ps)
	{
		foreach (var p in ps)
		{
			if (!_celled.Contains(p)) continue;
			int slot = AllocTemp();
			_slots[p] = slot;
			B.Emit(OpCode.LdArg, _paramIndex[p]);
			B.Emit(OpCode.NewCell, slot);
		}
	}

	/// <summary>Находит переменные, захватываемые лямбдами внутри <paramref name="node"/>: они хранятся в ячейках.</summary>
	private void PreScan(BoundNode node)
	{
		if (node is BoundLambda lam)
			foreach (var c in lam.Captures) _celled.Add(c.Variable);
		foreach (var ch in node.Children()) PreScan(ch);
	}

	private void LoadVar(Symbol s)
	{
		if (_captures.TryGetValue(s, out int ci)) { B.Emit(OpCode.LdCell, ci); return; }
		if (_celled.Contains(s) && _slots.TryGetValue(s, out int cs)) { B.Emit(OpCode.LdCell, ~cs); return; }
		switch (s)
		{
			case ParameterSymbol p: B.Emit(OpCode.LdArg, _paramIndex.TryGetValue(p, out int i) ? i : p.Index); break;
			case LocalSymbol l: B.Emit(OpCode.LdLoc, SlotOf(l)); break;
			default: throw new InvalidOperationException($"нельзя загрузить символ {s}");
		}
	}

	private void LoadParam(ParameterSymbol p) => LoadVar(p);

	/// <summary>Сохраняет значение с вершины стека в существующую переменную.</summary>
	private void StoreVar(Symbol s)
	{
		if (_captures.TryGetValue(s, out int ci)) { B.Emit(OpCode.StCell, ci); return; }
		if (_celled.Contains(s) && _slots.TryGetValue(s, out int cs)) { B.Emit(OpCode.StCell, ~cs); return; }
		switch (s)
		{
			case ParameterSymbol p: B.Emit(OpCode.StArg, _paramIndex.TryGetValue(p, out int i) ? i : p.Index); break;
			case LocalSymbol l: B.Emit(OpCode.StLoc, SlotOf(l)); break;
			default: throw new InvalidOperationException($"нельзя сохранить в символ {s}");
		}
	}

	private int SlotOf(LocalSymbol l)
	{
		if (!_slots.TryGetValue(l, out int slot)) _slots[l] = slot = AllocTemp();
		return slot;
	}

	/// <summary>Связывание паттерна: объявление новой переменной либо присваивание существующей (`loop i in …`, где `i` уже есть).</summary>
	private void StoreBinding(BoundBindingPattern b)
	{
		if (b.IsAssignment) StoreVar(b.Local);
		else DeclareAndStore(b.Local);
	}

	/// <summary>Объявляет переменную и сохраняет в неё значение с вершины стека (новая ячейка, если переменная захвачена).</summary>
	private void DeclareAndStore(LocalSymbol l)
	{
		int slot = SlotOf(l);
		if (_celled.Contains(l)) B.Emit(OpCode.NewCell, slot);
		else B.Emit(OpCode.StLoc, slot);
	}
}
