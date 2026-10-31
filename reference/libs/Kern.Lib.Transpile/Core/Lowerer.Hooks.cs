using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Types;

namespace Kern.Lib.Transpile.Core;

// Всё, чем языки различаются на уровне операторов и выражений.
public abstract partial class Lowerer
{
	// операторы
	protected abstract void DeclareLocal(string name, TypeSymbol? type, string? init, bool single);
	protected abstract string ThrowText(string message);
	protected abstract string AddToText(string list, string value);
	protected abstract string NewListText(TypeSymbol element);
	protected abstract void OpenForRange(string name, string start, string end, bool inclusive, string? step, bool negativeStep);
	protected abstract void OpenForEach(string variable, string iterable, bool isAsync);
	protected abstract string DeconstructVariable(IReadOnlyList<string> names);
	protected abstract void CloseDoWhile(string condition);
	protected abstract bool AlwaysStatement { get; }
	protected abstract string DiscardText(string value);

	// выражения
	protected abstract string NumberLit(Kern.Compiler.Lexing.NumberValue v, TypeSymbol type);
	protected abstract string NilLit { get; }
	protected abstract string NoneLit { get; }
	protected abstract string Ternary(string cond, string a, string b);
	protected abstract string Lambda(IReadOnlyList<(string Name, string Type)> parameters, string? expression, string? block, TypeSymbol returnType, bool isAsync);
	protected abstract string ListLit(TypeSymbol element, IReadOnlyList<string> items);
	protected abstract string ArrayLit(TypeSymbol element, IReadOnlyList<string> items);
	protected abstract string DictLit(DictType type, IReadOnlyList<(string Key, string Value)> items);
	protected abstract string TupleLit(IReadOnlyList<string> items);
	protected abstract string RangeLit(string? start, string? end, bool inclusive, string? step);
	protected abstract string Pow(string a, string b);
	protected abstract string InText(string item, string collection, bool negated);
	protected abstract string RefEq(string a, string b, bool negated);
	protected abstract string Cast(string subject, TypeSymbol type);
	protected abstract string IsType(string subject, TypeSymbol type, bool negated);
	protected abstract string NonNullText(string x);
	protected abstract string CoalesceText(string a, string b);
	protected abstract string TupleItemText(string subject, int index, int count);
	protected abstract string SafeIndexText(string target, string index);
	protected abstract string ResultText(bool ok, string? value, TypeSymbol type);
	protected abstract string ResultIsOk(string subject);
	protected abstract string ResultValue(string subject);
	protected abstract string ResultError(string subject);
	protected abstract string LenText(string subject);
	protected abstract string RestSlice(string subject, int from, int tailCount);
	protected abstract string Interpolated(IReadOnlyList<(string? Text, string? Value)> parts);
	protected abstract string TypeOfText(TypeSymbol type);
	protected abstract string AwaitText(string x);
	protected abstract string NewText(TypeSymbol type, IReadOnlyList<string> args);
	protected abstract string FunctionRefText(string? receiver, string name, bool isGlobal);
	protected abstract string ConvertText(string value, TypeSymbol from, TypeSymbol to);
	protected abstract string BaseAccess(TypeSymbol parent);
	protected abstract string NullDefault(TypeSymbol type);
	protected abstract string CallText(string? receiver, string name, IReadOnlyList<TypeSymbol> typeArgs, IReadOnlyList<string> args, bool safe);

	/// <summary>Ссылка на свободную функцию/глобал модуля (при необходимости с квалификацией).</summary>
	protected abstract string GlobalRef(Kern.Compiler.Semantics.Symbols.Symbol symbol, string name);
}
