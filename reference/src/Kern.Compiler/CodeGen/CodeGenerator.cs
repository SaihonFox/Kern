using Kern.Binding.Catalog;
using Kern.Bytecode;
using Kern.Bytecode.Layout;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Declarations;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using MemberFlags = Kern.Bytecode.MemberFlags;
using TypeKind = Kern.Bytecode.TypeKind;

namespace Kern.Compiler.CodeGen;

/// <summary>
/// Генератор байткода: <see cref="BoundProgram"/> → <see cref="KbcModule"/>.
/// Фазы: типы → поля/глобалы → объявления функций → раскладка полей → тела → статические инициализаторы → точка входа.
/// Соглашение: КАЖДОЕ выражение оставляет на стеке ровно одно значение (void → Void); инструкция-выражение делает Pop.
/// </summary>
public sealed partial class CodeGenerator
{
	private readonly BoundProgram _program;
	public KbcModule Module { get; }
	/// <summary>0 — без оптимизаций, 1 — свёртка констант, 2 — плюс вырезание Line.</summary>
	public int OptimizationLevel { get; private init; } = 1;

	internal Dictionary<TypeDeclSymbol, int> TypeIndex { get; } = [];
	internal Dictionary<FunctionSymbol, int> FuncIndex { get; } = [];
	internal Dictionary<FieldSymbol, int> GlobalIndex { get; } = [];
	internal Dictionary<FieldSymbol, FieldDef> FieldDefs { get; } = [];
	/// <summary>Сгенерированный конструктор по умолчанию для типов без объявленных конструкторов.</summary>
	internal Dictionary<TypeDeclSymbol, int> DefaultCtor { get; } = [];
	private readonly Dictionary<TypeDeclSymbol, BoundTypeDeclaration> _bound = [];
	private readonly Dictionary<FunctionSymbol, TypeDeclSymbol> _ctorOwner = [];

	private CodeGenerator(BoundProgram program, string name)
	{
		_program = program;
		Module = new KbcModule { Name = name };
	}

	public static KbcModule Generate(BoundProgram program, string? name = null, int optimizationLevel = 1)
	{
		var g = new CodeGenerator(program, name ?? program.Project.Name) { OptimizationLevel = optimizationLevel };
		g.Run();
		return g.Module;
	}

	private void Run()
	{
		foreach (var body in _program.Modules)
			foreach (var t in body.Types) _bound[t.Type] = t;

		foreach (var t in _program.Project.Types) DeclareType(t);
		foreach (var body in _program.Modules)
			foreach (var g in body.Globals) DeclareGlobal(g.Field);
		foreach (var t in _program.Project.Types) DeclareTypeMembers(t);
		foreach (var body in _program.Modules)
			foreach (var f in body.Functions) DeclareFunction(f.Symbol);
		foreach (var f in _program.Functions.Keys) DeclareFunction(f);

		foreach (var (sym, idx) in FuncIndex.ToArray())
			if (FindOverridden(sym) is { } o && FuncIndex.TryGetValue(o, out int oi)) Module.Functions[idx].Overrides = oi;

		FieldLayout.Assign(Module);

		foreach (var t in _program.Project.Types) CompileType(t);
		foreach (var body in _program.Modules)
			foreach (var f in body.Functions) CompileFunction(f);
		foreach (var (sym, bf) in _program.Functions)
			if (!_compiled.Contains(sym) && !IsCtorOfKnownType(sym)) CompileFunction(bf);

		foreach (var body in _program.Modules) EmitStaticInit(body);

		if (_program.EntryPoint is { } ep && FuncIndex.TryGetValue(ep.Symbol, out int ei))
		{
			Module.EntryPoint = ei;
			Module.EntryTakesArgs = ep.Symbol.Parameters.Count == 1;
			Module.EntryIsAsync = ep.Symbol.IsAsync;
		}
		foreach (var (sym, idx) in FuncIndex)
			if (sym.Attributes.Any(a => string.Equals(a.AttributeType?.Name ?? a.Syntax.Name, "test", StringComparison.OrdinalIgnoreCase)))
			{
				Module.Functions[idx].Flags |= MemberFlags.Test;
				Module.Tests.Add(idx);
			}
	}

	private readonly HashSet<FunctionSymbol> _compiled = [];
	private bool IsCtorOfKnownType(FunctionSymbol f) => f.IsConstructor && _ctorOwner.ContainsKey(f);

	// ------------------------------------------------------------ типы

	private void DeclareType(TypeDeclSymbol t)
	{
		var def = new TypeDef
		{
			Name = t.Name,
			FullName = t.FullName,
			Kind = t.DeclKind switch
			{
				TypeDeclKind.AbstractClass => TypeKind.AbstractClass,
				TypeDeclKind.Unit => TypeKind.Unit,
				TypeDeclKind.ImmutableUnit => TypeKind.ImmutableUnit,
				TypeDeclKind.Enum => TypeKind.Enum,
				TypeDeclKind.Union => TypeKind.Union,
				TypeDeclKind.Attribute => TypeKind.Attribute,
				_ => TypeKind.Class,
			},
			Flags = AccessFlags(t.Access) | (t.IsOpen ? MemberFlags.Open : 0) | (t.IsAbstract ? MemberFlags.Abstract : 0),
		};
		def.Attributes.AddRange(EmitAttributes(t.Attributes));
		foreach (var p in t.TypeParams) def.GenericParams.Add(new GenericParamDef { Name = p.Name });
		foreach (var p in t.Parents) def.Parents.Add(p.Type.ToTypeSig());
		foreach (var m in t.UnionMembers) def.UnionMembers.Add(m.ToTypeSig());
		Module.Types.Add(def);
		TypeIndex[t] = Module.Types.Count - 1;
	}

	/// <summary>Атрибуты → байткод: имя и константные аргументы (литералы, `-число`); прочие выражения пропускаются.</summary>
	private static IEnumerable<Kern.Bytecode.AttributeUse> EmitAttributes(IReadOnlyList<AttributeApplication> applied) =>
		applied.Select(a => new Kern.Bytecode.AttributeUse
		{
			TypeName = a.AttributeType?.Name ?? a.Syntax.Name,
			Args = [.. a.Syntax.Args.Select(arg => (arg, v: AttributeConstant(arg.Value))).Where(x => x.v.Ok)
				.Select(x => new ConstantArg { Name = x.arg.Name, Value = x.v.Value })],
		});

	private static (bool Ok, object? Value) AttributeConstant(Expr e) => e switch
	{
		StringLitExpr s => (true, s.Value),
		BoolLitExpr b => (true, b.Value),
		NilLitExpr => (true, null),
		CharLitExpr c => (true, (char)c.CodePoint),
		NumberLitExpr n => (true, n.Value.IsFloat ? n.Value.Float : (object)(long)n.Value.Integer),
		UnaryExpr { Op: UnaryOp.Negate, Operand: NumberLitExpr n } => (true, n.Value.IsFloat ? -n.Value.Float : (object)(-(long)n.Value.Integer)),
		_ => (false, null),
	};

	private void DeclareGlobal(FieldSymbol f)
	{
		if (GlobalIndex.ContainsKey(f)) return;
		var def = new FieldDef { Name = f.FullName, Type = (f.Type ?? PrimitiveType.Object).ToTypeSig(), Flags = MemberFlags.Static | AccessFlags(f.Access) };
		Module.Globals.Add(def);
		GlobalIndex[f] = Module.Globals.Count - 1;
		FieldDefs[f] = def;
	}

	private void DeclareTypeMembers(TypeDeclSymbol t)
	{
		var def = Module.Types[TypeIndex[t]];
		var fields = new List<FieldSymbol>(t.Fields.Where(f => !f.IsEnumEntry));
		foreach (var p in t.Properties)
			if (p.BackingField is { } bf && !fields.Contains(bf)) fields.Add(bf);
		foreach (var f in fields)
		{
			if (f.IsStatic) { DeclareGlobal(f); continue; }
			var fd = new FieldDef { Name = f.Name, Type = (f.Type ?? PrimitiveType.Object).ToTypeSig(), Flags = AccessFlags(f.Access), Attributes = [.. EmitAttributes(f.Attributes)] };
			def.Fields.Add(fd);
			FieldDefs[f] = fd;
		}
		if (t.DeclKind == TypeDeclKind.Enum)
		{
			var entries = _bound.TryGetValue(t, out var bt) ? bt.EnumEntries : [];
			long next = 0;
			foreach (var e in t.EnumEntries)
			{
				var entry = entries.FirstOrDefault(x => x.Field == e);
				long v = entry?.Value is BoundNumberLiteral n ? (long)n.Value.Integer : next;
				def.EnumNames.Add(e.Name);
				def.EnumValues.Add(v);
				next = v + 1;
			}
		}
		foreach (var c in AllConstructors(t)) { DeclareFunction(c); _ctorOwner[c] = t; }
		foreach (var f in t.Functions) DeclareFunction(f);
		foreach (var p in t.Properties)
		{
			foreach (var acc in new[] { p.Getter, p.Setter, p.Initer }) if (acc is not null) DeclareFunction(acc);
			def.Properties.Add(new PropertyDef
			{
				Name = p.Name, Type = p.Type.ToTypeSig(), Flags = AccessFlags(p.Access) | (p.IsStatic ? MemberFlags.Static : 0),
				Getter = Idx(p.Getter), Setter = Idx(p.Setter), Init = Idx(p.Initer),
				BackingField = p.BackingField is { } b && FieldDefs.TryGetValue(b, out var bd) ? def.Fields.IndexOf(bd) : -1,
				Attributes = [.. EmitAttributes(p.Attributes)],
			});
		}
		if (t.DeclKind is not (TypeDeclKind.Enum or TypeDeclKind.Union or TypeDeclKind.Attribute) && !AllConstructors(t).Any())
			DeclareDefaultCtor(t);
	}

	private int Idx(FunctionSymbol? f) => f is not null && FuncIndex.TryGetValue(f, out int i) ? i : -1;

	private static IEnumerable<FunctionSymbol> AllConstructors(TypeDeclSymbol t)
	{
		if (t.PrimaryConstructor is { } p) yield return p;
		foreach (var c in t.Constructors) if (c != t.PrimaryConstructor) yield return c;
	}

	private void DeclareDefaultCtor(TypeDeclSymbol t)
	{
		var def = new FunctionDef
		{
			Name = "ctor", Owner = TypeIndex[t], Flags = MemberFlags.Ctor | MemberFlags.Generated | MemberFlags.Pub,
		};
		Module.Functions.Add(def);
		int idx = Module.Functions.Count - 1;
		Module.Types[TypeIndex[t]].Ctors.Add(idx);
		DefaultCtor[t] = idx;
	}

	// ------------------------------------------------------------ функции

	private static MemberFlags AccessFlags(Access a) => a switch
	{
		Access.Pub => MemberFlags.Pub, Access.Priv => MemberFlags.Priv, Access.Prot => MemberFlags.Prot, Access.Int => MemberFlags.Int, _ => MemberFlags.Pub,
	};

	internal void DeclareFunction(FunctionSymbol f)
	{
		if (f is NativeMemberSymbol || FuncIndex.ContainsKey(f)) return;
		var def = new FunctionDef
		{
			Name = f.Name,
			Owner = f.DeclaringType is { } dt && TypeIndex.TryGetValue(dt, out int ti) ? ti : -1,
			Flags = AccessFlags(f.Access)
				| (f.IsStatic ? MemberFlags.Static : 0) | (f.IsOpen ? MemberFlags.Open : 0) | (f.IsAbstract ? MemberFlags.Abstract : 0)
				| (f.IsOverride ? MemberFlags.Override : 0) | (f.IsAsync ? MemberFlags.Async : 0)
				| (f.IsGetter ? MemberFlags.Getter : 0) | (f.IsSetter ? MemberFlags.Setter : 0) | (f.IsInit ? MemberFlags.Init : 0)
				| (f.IsOperator ? MemberFlags.Operator : 0) | (f.IsExtension ? MemberFlags.Extension : 0) | (f.IsConstructor ? MemberFlags.Ctor : 0),
			OpSymbol = f.OpSymbol,
			ExtensionOf = f.ExtensionOf?.ToTypeSig(),
			Return = f.IsConstructor ? TypeSig.Void : (_program.Functions.TryGetValue(f, out var bf) ? bf.ReturnType : f.ReturnType ?? PrimitiveType.Void).ToTypeSig(),
			SourceFile = f.File?.Path,
		};
		def.Attributes.AddRange(EmitAttributes(f.Attributes));
		foreach (var tp in f.TypeParams) def.GenericParams.Add(new GenericParamDef { Name = tp.Name });
		foreach (var p in f.Parameters)
			def.Params.Add(new ParamDef { Name = p.Name, Type = p.EffectiveType.ToTypeSig(), HasDefault = p.HasDefault, IsVariadic = p.IsVariadic });
		Module.Functions.Add(def);
		int idx = Module.Functions.Count - 1;
		FuncIndex[f] = idx;
		if (def.Owner >= 0)
		{
			var td = Module.Types[def.Owner];
			if (f.IsConstructor) td.Ctors.Add(idx); else td.Methods.Add(idx);
		}
	}

	/// <summary>Функция базового типа, которую переопределяет <paramref name="f"/> (по сигнатуре).</summary>
	private FunctionSymbol? FindOverridden(FunctionSymbol f)
	{
		if (!f.IsOverride || f.DeclaringType is not { } t) return null;
		var seen = new HashSet<TypeDeclSymbol>();
		var queue = new Queue<TypeDeclSymbol>(t.Parents.Select(p => p.Decl).OfType<TypeDeclSymbol>());
		while (queue.Count > 0)
		{
			var b = queue.Dequeue();
			if (!seen.Add(b)) continue;
			IEnumerable<FunctionSymbol> cands = b.Functions.Concat(b.Properties.SelectMany(p => new[] { p.Getter, p.Setter }.OfType<FunctionSymbol>()));
			foreach (var c in cands)
				if (c.Name == f.Name && c.FunctionKind == f.FunctionKind && c.HasSameSignature(f)) return c;
			foreach (var p in b.Parents) if (p.Decl is { } d) queue.Enqueue(d);
		}
		return null;
	}

	// ------------------------------------------------------------ тела

	private void CompileType(TypeDeclSymbol t)
	{
		_bound.TryGetValue(t, out var bt);
		foreach (var c in AllConstructors(t))
		{
			if (!FuncIndex.ContainsKey(c)) continue;
			BoundFunction? bf = bt?.Constructors.FirstOrDefault(x => x.Symbol == c)?.Function;
			if (bf is null) _program.Functions.TryGetValue(c, out bf);
			bf ??= new BoundFunction(c, c.Parameters, null, PrimitiveType.Void);
			new FunctionCompiler(this, c, bf, t, bt).CompileConstructor();
			_compiled.Add(c);
		}
		if (DefaultCtor.TryGetValue(t, out int di))
			new FunctionCompiler(this, null, null, t, bt, Module.Functions[di]).CompileConstructor();
	}

	private void CompileFunction(BoundFunction bf)
	{
		if (!_compiled.Add(bf.Symbol)) return;
		if (!FuncIndex.ContainsKey(bf.Symbol)) return;
		var t = bf.Symbol.DeclaringType;
		var bt = t is not null && _bound.TryGetValue(t, out var found) ? found : null;
		new FunctionCompiler(this, bf.Symbol, bf, t, bt).CompileFunction();
	}

	private void EmitStaticInit(BoundModuleBody body)
	{
		var inits = new List<(FieldSymbol Field, BoundExpr Value)>();
		foreach (var g in body.Globals) if (g.Initializer is not null) inits.Add((g.Field, g.Initializer));
		foreach (var t in body.Types)
			foreach (var fi in t.FieldInitializers) if (fi.Field.IsStatic) inits.Add((fi.Field, fi.Value));
		if (inits.Count == 0) return;
		var def = new FunctionDef { Name = "<static-init>", Flags = MemberFlags.Static | MemberFlags.Generated, Return = TypeSig.Void, SourceFile = body.Module.Name };
		Module.Functions.Add(def);
		Module.StaticInit.Add(Module.Functions.Count - 1);
		new FunctionCompiler(this, null, null, null, null, def).CompileStaticInit(inits);
	}

	// ------------------------------------------------------------ пулы

	internal int Sig(TypeSymbol t) => Module.Intern(t.ToTypeSig());

	/// <summary>Индекс списка подписей либо -1 для пустого.</summary>
	internal int SigList(IEnumerable<TypeSymbol> types)
	{
		var sigs = types.Select(t => t.ToTypeSig()).ToList();
		return sigs.Count == 0 ? -1 : Module.InternList(sigs);
	}

	internal FunctionDef NewLambdaDef(string name, bool isAsync, IEnumerable<ParameterSymbol> ps, TypeSymbol ret, int captureCount, string? file)
	{
		var def = new FunctionDef
		{
			Name = name, Flags = MemberFlags.Lambda | (isAsync ? MemberFlags.Async : 0), Return = ret.ToTypeSig(),
			CaptureCount = captureCount, SourceFile = file,
		};
		foreach (var p in ps) def.Params.Add(new ParamDef { Name = p.Name, Type = p.EffectiveType.ToTypeSig(), IsVariadic = p.IsVariadic });
		Module.Functions.Add(def);
		return def;
	}

	internal int IndexOf(FunctionDef def) => Module.Functions.IndexOf(def);

	private readonly Dictionary<string, int> _nativeDefs = [];

	/// <summary>FunctionDef-обёртка нативного члена (для ссылок на нативные функции как на значения).</summary>
	internal int NativeDef(NativeMemberSymbol nm)
	{
		string key = nm.Member.Key;
		if (_nativeDefs.TryGetValue(key, out int i)) return i;
		var m = nm.Member;
		var def = new FunctionDef
		{
			Name = nm.Name,
			Flags = MemberFlags.Native | MemberFlags.Pub | (m.IsInstance && !m.IsStatic ? MemberFlags.Extension : MemberFlags.Static),
			NativeKey = key,
			Return = m.Return,
			OpSymbol = m.OpSymbol,
		};
		foreach (var p in m.Params) def.Params.Add(new ParamDef { Name = p.Name, Type = p.Type, HasDefault = p.HasDefault, IsVariadic = p.IsVariadic });
		Module.Functions.Add(def);
		_nativeDefs[key] = Module.Functions.Count - 1;
		return _nativeDefs[key];
	}
}
