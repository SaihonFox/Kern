using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Declarations;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Bound.Patterns;
using Kern.Compiler.Semantics.Bound.Statements;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Lib.Transpile.Mapping;

namespace Kern.Lib.Transpile.Core;

/// <summary>Куда положить значение выражения при выводе в виде операторов.</summary>
public enum DestKind { Discard, Return, Assign, AddTo }

public readonly record struct Dest(DestKind Kind, string? Target = null)
{
	public static readonly Dest Discard = new(DestKind.Discard);
	public static readonly Dest Return = new(DestKind.Return);
	public static Dest Assign(string name) => new(DestKind.Assign, name);
	public static Dest AddTo(string list) => new(DestKind.AddTo, list);
}

/// <summary>
/// Общая часть транспилятора: обход типизированного дерева (BoundProgram) и «выпрямление» выражений в операторы.
/// Выражения if/match/loop/block в позиции значения выносятся во временные переменные (hoisting), поэтому
/// порядок вычисления сохраняется для обычного кода, но не для побочных эффектов внутри сложных подвыражений.
/// Синтаксис конкретного языка поставляют наследники (<c>CSharpLowerer</c>, <c>KotlinLowerer</c>).
/// </summary>
public abstract partial class Lowerer(IMappings maps)
{
	protected IMappings Maps { get; } = maps;
	protected CodeWriter W { get; set; } = new();
	protected BoundProgram Program { get; private set; } = null!;
	protected BoundModuleBody Module { get; set; } = null!;
	protected TypeDeclSymbol? CurrentType { get; set; }
	protected string? ThisAlias { get; set; }

	/// <summary>Нативные члены без правила: попадают в заголовок файла как напоминание.</summary>
	public SortedSet<string> Unmapped { get; } = [];
	public List<string> Warnings { get; } = [];

	private readonly Dictionary<Symbol, string> _names = [];
	private readonly Dictionary<Symbol, string> _aliases = [];
	private HashSet<string> _used = [];
	private int _temp;

	public abstract string Id { get; }
	public abstract string Extension { get; }
	protected virtual string LineComment => "//";

	/// <summary>Все файлы результата: относительный путь → текст.</summary>
	public Dictionary<string, string> Run(BoundProgram program)
	{
		Program = program;
		var files = new Dictionary<string, string>();
		foreach (var m in program.Modules)
		{
			if (m.Module.IsStd) continue;
			if (m.Functions.Count == 0 && m.Types.Count == 0 && m.Globals.Count == 0) continue;
			Module = m;
			W = new CodeWriter();
			EmitModule(m);
			files[FileFor(m)] = W.ToString();
		}
		var header = new List<string> { $"{LineComment} Сгенерировано транспилятором Kern → {Id}. Не редактируйте вручную." };
		if (Unmapped.Count > 0)
			header.Add($"{LineComment} Нет правила подстановки (задайте через map_script): {string.Join(", ", Unmapped)}");
		foreach (var w in Warnings.Distinct()) header.Add($"{LineComment} ВНИМАНИЕ: {w}");
		string prefix = string.Join('\n', header) + "\n";
		foreach (var key in files.Keys.ToList()) files[key] = prefix + files[key];
		files[RuntimeFileName] = RuntimeText;
		return files;
	}

	protected abstract string FileFor(BoundModuleBody m);
	protected abstract string RuntimeFileName { get; }
	protected abstract string RuntimeText { get; }
	protected abstract void EmitModule(BoundModuleBody m);

	protected IEnumerable<BoundModuleBody> OtherModules => Program.Modules.Where(m => !m.Module.IsStd && m != Module && (m.Functions.Count > 0 || m.Globals.Count > 0 || m.Types.Count > 0));

	// ------------------------------------------------------------------ имена

	protected void BeginFunction() => _used = [];

	protected string NameOf(Symbol s)
	{
		if (_aliases.TryGetValue(s, out var alias)) return alias;
		if (_names.TryGetValue(s, out var n)) return n;
		string name = LocalName(s.Name);
		string unique = name;
		for (int i = 2; !_used.Add(unique); i++) unique = name + i;
		return _names[s] = unique;
	}

	protected string Temp(string prefix = "t") => $"{prefix}{++_temp}";

	protected abstract string LocalName(string kernName);
	protected abstract string MemberName(string kernName);
	protected abstract string TypeName(TypeSymbol t);

	// ------------------------------------------------------------------ захват вывода

	/// <summary>Выполняет <paramref name="body"/> в отдельном писателе и возвращает (значение, выведенные операторы).</summary>
	protected (string Value, string Text) Capture(Func<string> body)
	{
		var saved = W;
		W = new CodeWriter { Level = 0 };
		try
		{
			var v = body();
			return (v, W.ToString());
		}
		finally { W = saved; }
	}

	// ------------------------------------------------------------------ точка входа вывода тел

	protected void EmitBody(BoundExpr? body, TypeSymbol returnType, bool isCtor = false)
	{
		if (body is null) return;
		bool isVoid = isCtor || returnType is PrimitiveType { Kind: Kern.Bytecode.PrimKind.Void } or NoneType;
		Stmt(body, isVoid ? Dest.Discard : Dest.Return);
	}

	protected static bool IsVoid(TypeSymbol t) => t is PrimitiveType { Kind: Kern.Bytecode.PrimKind.Void } or NoneType or NeverType;

	protected static string StripParens(string s)
	{
		if (s.Length < 2 || s[0] != '(' || s[^1] != ')') return s;
		int depth = 0;
		for (int i = 0; i < s.Length; i++)
		{
			if (s[i] == '(') depth++;
			else if (s[i] == ')') { depth--; if (depth == 0 && i < s.Length - 1) return s; }
		}
		return s[1..^1];
	}
}
