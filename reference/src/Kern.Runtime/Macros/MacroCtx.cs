using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;

namespace Kern.Runtime.Macros;

/// <summary>
/// Контекст вызова макроса. Макрос — функция `#[macro]` (атрибутный макрос) или `#[macro("expr")]` (вызов `имя!(…)`):
/// <code>
/// #[macro]
/// fun to_text(macro_ctx m) {
///     var t = m.decl
///     m.member(f"fun describe() -> str ${m.quote(t.name)}")
/// }
/// </code>
/// Результат: `m.member(код)` — члены, добавляемые в тип; `m.top(код)` — объявления верхнего уровня;
/// для выражений — вернуть текст выражения (`ret "…"`) либо вызвать `m.expr(код)`.
/// </summary>
[KernType("macro_ctx")]
public sealed class MacroCtx
{
	private readonly MacroTarget? _target;
	private readonly IReadOnlyList<string> _args;
	private static int _fresh;

	public MacroCtx(string macroName, string file, int line, MacroTarget? target, IReadOnlyList<string> args)
	{
		Name = macroName;
		File = file;
		Line = line;
		_target = target;
		_args = args;
	}

	// ---- собранный результат (читает компилятор)
	public List<string> Members { get; } = [];
	public List<string> Tops { get; } = [];
	public string? ExprResult { get; private set; }
	public List<(bool IsError, string Message)> Messages { get; } = [];

	[KernProperty] public string Name { get; }
	[KernProperty] public string File { get; }
	[KernProperty] public int Line { get; }
	/// <summary>Тип, к которому применён атрибутный макрос (nil у макросов-выражений).</summary>
	[KernProperty] public MacroTarget? Target => _target;
	/// <summary>То же, что `target`, но без nil: в атрибутном макросе всегда есть; в макросе-выражении — паника.</summary>
	[KernProperty] public MacroTarget Decl => _target ?? throw new Kern.Vm.Execution.KernPanic($"макрос '{Name}' вызван без цели (это макрос-выражение?)");
	/// <summary>Аргументы вызова `имя!(a, b)` как исходный текст.</summary>
	[KernProperty] public KList<string> Args => Kv.Strs(_args);

	[KernMember] public void Member(string code) => Members.Add(code);
	[KernMember] public void Top(string code) => Tops.Add(code);
	/// <summary>Результат макроса-выражения (альтернатива `ret "…"`).</summary>
	[KernMember] public void Expr(string code) => ExprResult = code;

	/// <summary>Ошибка компиляции в позиции применения макроса; сборка остановится после раскрытия.</summary>
	[KernMember] public void Error(string message) => Messages.Add((true, message));
	[KernMember] public void Warn(string message) => Messages.Add((false, message));

	/// <summary>Строка → литерал Kern с экранированием.</summary>
	[KernMember] public string Quote(string text) => MacroText.Quote(text);

	/// <summary>Уникальное имя для вспомогательных объявлений: `m.fresh("tmp")` → `tmp__1`.</summary>
	[KernMember] public string Fresh(string prefix) => $"{prefix}__{Interlocked.Increment(ref _fresh)}";
}
