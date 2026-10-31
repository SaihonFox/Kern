using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound.Declarations;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Lib.Transpile.Core;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Lib.Transpile.Targets.Kotlin;

public sealed partial class KotlinLowerer
{
	protected override void EmitModule(BoundModuleBody m)
	{
		W.Line($"package {PackageOf(m.Module)}");
		W.Line();
		W.Line("import kern.runtime.*");
		foreach (var o in OtherModules) W.Line($"import {PackageOf(o.Module)}.*");
		W.Line();
		foreach (var g in m.Globals) EmitGlobal(g);
		if (m.Globals.Count > 0) W.Line();
		foreach (var f in m.Functions)
		{
			if (f.Symbol.IsAccessor && !f.Symbol.IsExtension) continue;
			EmitFunction(f, null);
			W.Line();
		}
		foreach (var t in m.Types) EmitType(t);
	}

	private void EmitGlobal(BoundGlobal g)
	{
		BeginFunction();
		string type = TypeName(g.Field.Type ?? PrimitiveType.Object);
		string init = "TODO()";
		if (g.Initializer is { } e)
		{
			var (v, text) = Capture(() => Value(e));
			if (text.Length != 0) Warnings.Add($"инициализатор глобальной '{g.Field.Name}' требует операторов — подставлен TODO()");
			else init = StripParens(v);
		}
		W.Line($"var {MemberName(g.Field.Name)}: {type} = {init}");
	}

	private static string Vis(Symbol s) => s.Access switch
	{
		Syn.Access.Priv => "private ",
		Syn.Access.Prot => "protected ",
		Syn.Access.Int => "internal ",
		_ => "",
	};

	private string TypeParams(IEnumerable<TypeParamSymbol> ps)
	{
		var list = ps.Select(p => p.Name).ToList();
		return list.Count == 0 ? "" : $"<{string.Join(", ", list)}>";
	}

	private static string OperatorName(string op) => op switch
	{
		"+" => "plus", "-" => "minus", "*" => "times", "/" => "div", "%" => "rem", _ => "",
	};

	// ------------------------------------------------------------------ функции

	private void EmitFunction(BoundFunction bf, TypeDeclSymbol? owner)
	{
		var f = bf.Symbol;
		BeginFunction();
		var ret = bf.ReturnType;
		var ps = string.Join(", ", bf.Parameters.Select(p => $"{NameOf(p)}: {TypeName(p.EffectiveType)}"));
		string mods = Vis(f);
		string name = MemberName(f.Name);
		if (f.IsOperator && f.OpSymbol is { } op)
		{
			string opName = OperatorName(op);
			if (opName.Length > 0) { mods += "operator "; name = opName; }
			else name = "op" + Naming.Pascal(f.Name);
		}
		if (owner is not null)
		{
			if (f.IsAbstract) mods += "abstract ";
			else if (f.IsOverride) mods += "override ";
			else if (f.IsOpen) mods += "open ";
		}
		if (f.IsAsync) mods += "suspend ";
		string recv = f.IsExtension ? TypeName(f.ExtensionOf!) + "." : "";
		string retText = IsVoid(ret) ? "" : ": " + TypeName(ret);
		string head = $"{mods}fun {(f.TypeParams.Count > 0 ? TypeParams(f.TypeParams) + " " : "")}{recv}{name}({ps}){retText}";
		if (bf.Body is null || f.IsAbstract) { W.Line(head); return; }
		W.Open(head);
		EmitBody(bf.Body, ret);
		W.Close();
	}

	// ------------------------------------------------------------------ типы

	private void EmitType(BoundTypeDeclaration t)
	{
		var d = t.Type;
		CurrentType = d;
		try
		{
			switch (d.DeclKind)
			{
				case TypeDeclKind.Enum:
					if (t.EnumEntries.Any(e => e.Construction is { Arguments.Count: > 0 })) Warnings.Add($"enum {d.Name}: данные элементов не переносятся");
					W.Open($"enum class {d.Name}");
					W.Line(string.Join(", ", t.EnumEntries.Select(e => Naming.Pascal(e.Field.Name))));
					W.Close();
					break;
				case TypeDeclKind.Union:
					W.Line($"// union {d.Name} = {string.Join(" | ", d.UnionMembers.Select(TypeName))} (в Kotlin представлен как Any)");
					break;
				case TypeDeclKind.Attribute:
					W.Line($"// атрибут {d.Name} не переносится");
					break;
				default: EmitClassLike(t); break;
			}
			W.Line();
		}
		finally { CurrentType = null; }
	}

	private string ZeroOrNull(TypeSymbol? t) => t switch
	{
		PrimitiveType { Kind: PrimKind.Bool } => "false",
		PrimitiveType { Kind: PrimKind.Str } => "\"\"",
		PrimitiveType { Kind: PrimKind.Char } => "'\\u0000'",
		PrimitiveType { IsFloat: true } p => p.Kind == PrimKind.F64 ? "0.0" : "0.0f",
		PrimitiveType { Kind: PrimKind.I64 or PrimKind.INum } => "0L",
		PrimitiveType { IsSignedInt: true } => "0",
		NullableType => "null",
		ListType l => $"mutableListOf<{TypeName(l.Element)}>()",
		_ => "",
	};

	private void EmitClassLike(BoundTypeDeclaration t)
	{
		var d = t.Type;
		bool unit = d.IsValueType;
		var fromParams = new HashSet<FieldSymbol>();
		var primary = t.Constructors.FirstOrDefault(c => c.Symbol.IsPrimaryCtor);

		// первичный конструктор
		string primaryText = "";
		if (unit || primary is not null)
		{
			var parts = new List<string>();
			if (unit && primary is null)
			{
				foreach (var f in d.Fields.Where(f => !f.IsStatic && !f.IsEnumEntry && f.BackingFor is null && f.Init is null))
				{
					fromParams.Add(f);
					parts.Add($"{(d.DeclKind == TypeDeclKind.ImmutableUnit ? "val" : "var")} {MemberName(f.Name)}: {TypeName(f.Type ?? PrimitiveType.Object)}");
				}
			}
			else if (primary is not null)
			{
				BeginFunction();
				foreach (var p in primary.Function.Parameters)
				{
					var fld = p.Field ?? (p.AutoInit ? d.Fields.FirstOrDefault(x => x.Name == p.Name) : null);
					string kw = fld is null ? "" : (d.DeclKind == TypeDeclKind.ImmutableUnit ? "val " : "var ");
					if (fld is not null) fromParams.Add(fld);
					parts.Add($"{kw}{NameOf(p)}: {TypeName(p.EffectiveType)}");
				}
			}
			primaryText = "(" + string.Join(", ", parts) + ")";
		}

		var parents = d.Parents.Select(p => p.Type).ToList();
		var supers = new List<string>();
		if (!unit && parents.Count > 0)
		{
			string args = "";
			if (t.ParentInits.Count > 0 && primary is not null)
			{
				BeginFunction();
				args = string.Join(", ", t.ParentInits[0].Arguments.Select(Value));
			}
			bool hasPrimary = primaryText.Length > 0 || t.Constructors.Count == 0;
			supers.Add(TypeName(parents[0]) + (hasPrimary ? $"({args})" : ""));
			if (parents.Count > 1) Warnings.Add($"{d.Name}: множественное наследование не поддерживается, оставлен только {TypeName(parents[0])}");
		}
		string kind = unit ? "data class" : d.IsAbstract ? "abstract class" : d.IsOpen ? "open class" : "class";
		if (!unit && primaryText.Length == 0 && t.Constructors.Count == 0) primaryText = "()";
		string supersText = supers.Count == 0 ? "" : " : " + string.Join(", ", supers);
		W.Open($"{Vis(d)}{kind} {d.Name}{TypeParams(d.TypeParams)}{primaryText}{supersText}");

		bool secondaryOnly = primary is null && t.Constructors.Count > 0;
		foreach (var f in d.Fields)
		{
			if (f.BackingFor is not null || f.IsEnumEntry || fromParams.Contains(f)) continue;
			string type = TypeName(f.Type ?? PrimitiveType.Object);
			string init = "";
			if (!secondaryOnly && t.FieldInitializers.FirstOrDefault(fi => fi.Field == f) is { } fi)
			{
				BeginFunction();
				var (v, text) = Capture(() => Value(fi.Value));
				if (text.Length == 0) init = StripParens(v);
				else Warnings.Add($"{d.Name}.{f.Name}: сложный инициализатор поля");
			}
			if (init.Length == 0) init = ZeroOrNull(f.Type);
			if (init.Length == 0) W.Line($"{Vis(f)}lateinit var {MemberName(f.Name)}: {type}");
			else W.Line($"{Vis(f)}var {MemberName(f.Name)}: {type} = {init}");
		}
		foreach (var p in d.Properties) EmitProperty(p, t);

		if (primary?.Function.Body is { } pb && pb is not BoundBlock { Statements.Count: 0, Result: null })
		{
			BeginFunction();
			W.Open("init");
			EmitBody(pb, PrimitiveType.Void, isCtor: true);
			W.Close();
		}
		foreach (var c in t.Constructors.Where(c => !c.Symbol.IsPrimaryCtor)) EmitSecondary(t, c, primary is not null);
		foreach (var f in t.Functions) { EmitFunction(f, d); W.Line(); }
		W.Close();
	}

	private void EmitProperty(PropertySymbol p, BoundTypeDeclaration t)
	{
		if (p.ExtensionOf is not null) return;
		var get = t.Accessors.FirstOrDefault(a => a.Symbol.Property == p && a.Symbol.IsGetter);
		var set = t.Accessors.FirstOrDefault(a => a.Symbol.Property == p && (a.Symbol.IsSetter || a.Symbol.IsInit));
		string type = TypeName(p.Type);
		string name = MemberName(p.Name);
		bool custom = (get?.Body is not null && !get.IsSynthesized) || (set?.Body is not null && !set.IsSynthesized);
		string init = "";
		if (p.BackingField is { } bf && t.FieldInitializers.FirstOrDefault(fi => fi.Field == bf) is { } fi)
		{
			BeginFunction();
			var (v, text) = Capture(() => Value(fi.Value));
			if (text.Length == 0) init = StripParens(v);
		}
		if (init.Length == 0) init = ZeroOrNull(p.Type);
		string decl = init.Length == 0 && !custom ? $"{Vis(p)}lateinit var {name}: {type}" : $"{Vis(p)}var {name}: {type}" + (init.Length == 0 ? "" : $" = {init}");
		if (!custom) { W.Line(decl); return; }
		W.Line(decl);
		W.Level++;
		if (get?.Body is not null)
		{
			BeginFunction();
			W.Open("get()");
			EmitBody(get.Body, get.ReturnType);
			W.Close();
		}
		if (set?.Body is not null)
		{
			BeginFunction();
			string pn = set.Parameters.Count > 0 ? NameOf(set.Parameters[0]) : "value";
			W.Open($"set({pn})");
			EmitBody(set.Body, PrimitiveType.Void);
			W.Close();
		}
		W.Level--;
	}

	private void EmitSecondary(BoundTypeDeclaration t, BoundConstructor c, bool hasPrimary)
	{
		var d = t.Type;
		var f = c.Function;
		BeginFunction();
		var ps = string.Join(", ", f.Parameters.Select(p => $"{NameOf(p)}: {TypeName(p.EffectiveType)}"));
		string tail;
		if (f.Chain is { } ch) tail = $" : this({string.Join(", ", ch.Arguments.Select(Value))})";
		else if (hasPrimary) tail = " : this()";
		else if (t.ParentInits.Count > 0) tail = $" : super({string.Join(", ", t.ParentInits[0].Arguments.Select(Value))})";
		else tail = "";
		W.Open($"{Vis(c.Symbol)}constructor({ps}){tail}");
		if (f.Chain is null)
		{
			var done = new HashSet<FieldSymbol>();
			foreach (var fi in t.FieldInitializers)
			{
				if (fi.Field.IsStatic || fi.Field.FromParameter is not null) continue;
				string target = fi.Field.BackingFor is { } bp ? MemberName(bp.Name) : MemberName(fi.Field.Name);
				W.Line($"this.{target} = {StripParens(Value(fi.Value))}");
				done.Add(fi.Field);
			}
			foreach (var p in f.Parameters)
				if (p.AutoInit && (p.Field ?? d.Fields.FirstOrDefault(x => x.Name == p.Name)) is { } fld && !fld.IsStatic && !done.Contains(fld))
					W.Line($"this.{MemberName(fld.Name)} = {NameOf(p)}");
		}
		EmitBody(f.Body, PrimitiveType.Void, isCtor: true);
		W.Close();
		W.Line();
	}
}
