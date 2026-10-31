using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Declarations;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Bound.Statements;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Core;

namespace Kern.Compiler.CodeGen;

/// <summary>
/// Компилятор тела одной функции (метода, конструктора, лямбды, статического инициализатора).
/// Разбит на partial-файлы: Variables, Statements, Expressions, Calls, Operators, Assign, Control, Patterns.
/// </summary>
internal sealed partial class FunctionCompiler
{
	private readonly CodeGenerator G;
	private readonly FunctionSymbol? _sym;
	private readonly BoundFunction? _fn;
	private readonly TypeDeclSymbol? _type;
	private readonly BoundTypeDeclaration? _boundType;
	private FunctionDef _def;
	private readonly CodeBuilder B = new();
	private SourceFile? _file;

	private KbcModule M => G.Module;

	public FunctionCompiler(CodeGenerator g, FunctionSymbol? sym, BoundFunction? fn, TypeDeclSymbol? type, BoundTypeDeclaration? boundType, FunctionDef? def = null)
	{
		G = g; _sym = sym; _fn = fn; _type = type; _boundType = boundType;
		_def = def!;
		_file = sym?.File;
		if (sym is not null && g.FuncIndex.TryGetValue(sym, out int i)) _def = g.Module.Functions[i];
	}

	/// <summary>Конструктор для лямбды: наследует файл родителя.</summary>
	private FunctionCompiler(CodeGenerator g, FunctionCompiler parent, FunctionDef def)
	{
		G = g; _type = parent._type; _boundType = parent._boundType; _def = def; _file = parent._file;
		_sym = null; _fn = null;
	}

	// ------------------------------------------------------------ точки входа

	public void CompileFunction()
	{
		if (_fn?.Body is null) return;
		BeginParams(_fn.Parameters, null);
		PreScan(_fn.Body);
		Mark(_fn.Body.Span);
		BeginParamCells(_fn.Parameters);
		EmitBodyReturn(_fn.Body, IsVoidType(_fn.ReturnType));
		Finish();
	}

	public void CompileConstructor()
	{
		var ps = _fn?.Parameters ?? [];
		BeginParams(ps, null);
		if (_fn?.Body is not null) PreScan(_fn.Body);
		if (_boundType is not null)
		{
			foreach (var fi in _boundType.FieldInitializers) PreScan(fi.Value);
			foreach (var pi in _boundType.ParentInits) foreach (var a in pi.Arguments) PreScan(a);
		}
		BeginParamCells(ps);

		var chain = _fn?.Chain;
		if (chain is not null)
		{
			foreach (var a in chain.Arguments) PreScan(a);
			B.Emit(OpCode.LdThis);
			EmitArguments(chain.Constructor, chain.Arguments, chain.EvaluationOrder);
			B.Emit(OpCode.Call, FunctionIndexFor(chain.Constructor), chain.Arguments.Count, -1);
			B.Emit(OpCode.Pop);
		}
		// 1) родители
		if (chain is null && _boundType is not null)
			foreach (var pi in _boundType.ParentInits) EmitParentInit(pi);
		// 2) поля из параметров (если биндер не создал инициализатор) и инициализаторы полей
		var initialized = new HashSet<FieldSymbol>();
		if (chain is null && _boundType is not null)
			foreach (var fi in _boundType.FieldInitializers)
			{
				if (fi.Field.IsStatic) continue;
				// инициализатор поля-параметра уже ссылается на BoundParameter текущего ctor, поэтому только для первичного
				if (fi.Field.FromParameter is not null && (_sym is null || !_sym.IsPrimaryCtor)) continue;
				Mark(fi.Value.Span);
				B.Emit(OpCode.LdThis);
				EmitCopy(fi.Value);
				B.Emit(OpCode.StField, FieldSlot(fi.Field));
				initialized.Add(fi.Field);
			}
		// 3) `ctor(str $name)` — автоприсваивание
		if (chain is null && _sym is { IsPrimaryCtor: false } && _type is not null)
			foreach (var p in ps)
				if (p.AutoInit && (p.Field ?? _type.Fields.FirstOrDefault(f => f.Name == p.Name)) is { } fld && !fld.IsStatic && !initialized.Contains(fld))
				{
					B.Emit(OpCode.LdThis);
					LoadParam(p);
					B.Emit(OpCode.StField, FieldSlot(fld));
				}
		// 4) тело
		if (_fn?.Body is { } body)
		{
			Mark(body.Span);
			EmitDiscard(body);
		}
		B.Emit(OpCode.RetVoid);
		Finish();
	}

	public void CompileStaticInit(IReadOnlyList<(FieldSymbol Field, BoundExpr Value)> inits)
	{
		foreach (var (_, v) in inits) PreScan(v);
		foreach (var (f, v) in inits)
		{
			Mark(v.Span);
			EmitCopy(v);
			B.Emit(OpCode.StStatic, G.GlobalIndex[f]);
		}
		B.Emit(OpCode.RetVoid);
		Finish();
	}

	private void EmitBodyReturn(BoundExpr body, bool isVoid)
	{
		if (isVoid) { EmitDiscard(body); B.Emit(OpCode.RetVoid); }
		else { EmitCopy(body); B.Emit(OpCode.Ret); }
	}

	private static bool IsVoidType(TypeSymbol? t) => t is null || t is PrimitiveType { Kind: PrimKind.Void };

	private void EmitParentInit(BoundParentInit pi)
	{
		if (pi.Parent.Type is not DeclaredType pd) return; // нативные родители создаются через нативный ctor (не поддержано)
		int ctor;
		if (pi.Constructor is { } c && G.FuncIndex.TryGetValue(c, out int ci)) ctor = ci;
		else if (G.DefaultCtor.TryGetValue(pd.Decl, out int di)) ctor = di;
		else return;
		B.Emit(OpCode.LdThis);
		EmitArguments(pi.Constructor, pi.Arguments, pi.EvaluationOrder);
		B.Emit(OpCode.Call, ctor, pi.Arguments.Count, G.SigList(pd.Args));
		B.Emit(OpCode.Pop);
	}

	private void Finish()
	{
		foreach (var (label, msg) in _stubs)
		{
			B.Mark(label);
			B.Emit(OpCode.LdStr, M.Intern(msg));
			B.Emit(OpCode.Panic);
		}
		_def.Code = B.ToArray();
		_def.LocalCount = _nextLocal;
		_def.MaxStack = B.InstructionCount + 8;
		_def.LineTable = B.LineTable;
		if (_def.SourceFile is null) _def.SourceFile = _file?.Path;
		if (G.OptimizationLevel >= 2) Optimization.PeepholeOptimizer.Optimize(_def);
	}

	private readonly List<(Label, string)> _stubs = [];

	/// <summary>Метка заглушки «panic(msg)» в конце функции.</summary>
	private Label PanicLabel(string message)
	{
		var l = new Label();
		_stubs.Add((l, message));
		return l;
	}

	private void Mark(TextSpan span)
	{
		if (_file is null) return;
		B.SetLine(_file.GetLineColumn(span.Start).Line);
	}

	private int FieldSlot(FieldSymbol f) => G.FieldDefs[f].Slot;
}
