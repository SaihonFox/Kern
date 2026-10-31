using Kern.Compiler.Semantics.Bound.Declarations;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Lib.Transpile.Core;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Lib.Transpile.Targets.CSharp;

public sealed partial class CSharpLowerer
{
	protected override void EmitModule(BoundModuleBody m)
	{
		W.Line("#nullable enable");
		W.Line("using System;");
		W.Line("using System.Collections.Generic;");
		W.Line("using System.Linq;");
		W.Line("using System.Threading.Tasks;");
		W.Line();
		W.Line($"namespace {Namespace};");
		W.Line();
		if (m.Globals.Count > 0 || m.Functions.Count > 0)
		{
			W.Open($"public static partial class {ModuleClass(m.Module)}");
			foreach (var g in m.Globals) EmitGlobal(g);
			foreach (var f in m.Functions)
			{
				if (f.Symbol.IsExtension || !f.Symbol.IsAccessor) EmitFunction(f, null);
				W.Line();
			}
			W.Close();
			W.Line();
		}
		foreach (var t in m.Types) EmitType(t);
	}

	private void EmitGlobal(BoundGlobal g)
	{
		BeginFunction();
		string init = "default!";
		if (g.Initializer is { } e)
		{
			var (v, text) = Capture(() => Value(e));
			if (text.Length != 0) Warnings.Add($"инициализатор глобальной '{g.Field.Name}' требует операторов — подставлен default");
			else init = StripParens(v);
		}
		W.Line($"public static {TypeName(g.Field.Type ?? PrimitiveType.Object)} {MemberName(g.Field.Name)} = {init};");
	}

	private static string Access(Symbol s) => s.Access switch
	{
		Syn.Access.Priv => "private ",
		Syn.Access.Prot => "protected ",
		Syn.Access.Int => "internal ",
		_ => "public ",
	};

	private string TypeParams(IEnumerable<TypeParamSymbol> ps)
	{
		var list = ps.Select(p => p.Name).ToList();
		return list.Count == 0 ? "" : $"<{string.Join(", ", list)}>";
	}

	private string ParamList(FunctionSymbol f, IReadOnlyList<ParameterSymbol> ps)
	{
		var parts = new List<string>();
		if (f.IsExtension) parts.Add($"this {TypeName(f.ExtensionOf!)} self");
		foreach (var p in ps) parts.Add($"{TypeName(p.EffectiveType)} {NameOf(p)}");
		return string.Join(", ", parts);
	}

	// ------------------------------------------------------------------ функции

	private void EmitFunction(BoundFunction bf, TypeDeclSymbol? owner)
	{
		var f = bf.Symbol;
		BeginFunction();
		var savedAlias = ThisAlias;
		if (f.IsExtension || f.IsOperator) ThisAlias = "self";
		try
		{
			var ret = bf.ReturnType;
			string retText = f.IsAsync ? (IsVoid(ret) ? "Task" : $"Task<{TypeName(ret)}>") : TypeName(ret);
			if (f.IsOperator && f.OpSymbol is { } op && IsSupportedOperator(op))
			{
				var ps = new List<string> { $"{TypeName(owner!.ThisType)} self" };
				ps.AddRange(bf.Parameters.Select(p => $"{TypeName(p.EffectiveType)} {NameOf(p)}"));
				W.Open($"public static {retText} operator {op}({string.Join(", ", ps)})");
				EmitBody(bf.Body, ret);
				W.Close();
				return;
			}
			string mods = Access(f);
			bool inType = owner is not null;
			if (!inType || f.IsStatic || f.IsExtension) mods += "static ";
			else if (f.IsAbstract) mods += "abstract ";
			else if (f.IsOverride) mods += "override ";
			else if (f.IsOpen) mods += "virtual ";
			if (f.IsAsync) mods += "async ";
			string name = f.IsOperator ? "Op" + f.Name : MemberName(f.Name);
			string head = $"{mods}{retText} {name}{TypeParams(f.TypeParams)}({ParamList(f, bf.Parameters)})";
			if (bf.Body is null || f.IsAbstract) { W.Line(head + ";"); return; }
			W.Open(head);
			EmitBody(bf.Body, ret);
			W.Close();
		}
		finally { ThisAlias = savedAlias; }
	}

	private static bool IsSupportedOperator(string op) => op is "+" or "-" or "*" or "/" or "%" or "<" or ">" or "<=" or ">=" or "==" or "!=";

	// ------------------------------------------------------------------ типы

	private void EmitType(BoundTypeDeclaration t)
	{
		var decl = t.Type;
		CurrentType = decl;
		try
		{
			switch (decl.DeclKind)
			{
				case TypeDeclKind.Enum: EmitEnum(t); break;
				case TypeDeclKind.Union:
					W.Line($"// union {decl.Name} = {string.Join(" | ", decl.UnionMembers.Select(TypeName))} (в C# представлен как object)");
					break;
				case TypeDeclKind.Attribute:
					W.Line($"// атрибут {decl.Name} не переносится");
					break;
				default: EmitClassLike(t); break;
			}
			W.Line();
		}
		finally { CurrentType = null; }
	}

	private void EmitEnum(BoundTypeDeclaration t)
	{
		var d = t.Type;
		if (t.EnumEntries.Any(e => e.Construction is { Arguments.Count: > 0 }))
			Warnings.Add($"enum {d.Name}: данные элементов не переносятся");
		W.Open($"public enum {d.Name}");
		for (int i = 0; i < t.EnumEntries.Count; i++)
		{
			var e = t.EnumEntries[i];
			string val = e.Value is BoundNumberLiteral n ? $" = {n.Value.Integer}" : "";
			W.Line($"{Naming.Pascal(e.Field.Name)}{val}{(i < t.EnumEntries.Count - 1 ? "," : "")}");
		}
		W.Close();
	}

	private void EmitClassLike(BoundTypeDeclaration t)
	{
		var d = t.Type;
		bool unit = d.IsValueType;
		var parents = d.Parents.Select(p => TypeName(p.Type)).ToList();
		string baseList = parents.Count == 0 || unit ? "" : " : " + parents[0];
		if (parents.Count > 1) Warnings.Add($"{d.Name}: множественное наследование не поддерживается, оставлен только {parents[0]}");

		var positional = unit ? PositionalMembers(d) : [];
		if (unit)
		{
			string ro = d.DeclKind == TypeDeclKind.ImmutableUnit ? "readonly " : "";
			string ps = string.Join(", ", positional.Select(f => $"{TypeName(f.Type ?? PrimitiveType.Object)} {MemberName(f.Name)}"));
			W.Open($"{Access(d)}{ro}record struct {d.Name}{TypeParams(d.TypeParams)}({ps})");
		}
		else
		{
			string mod = d.IsAbstract ? "abstract " : d.IsOpen ? "" : "sealed ";
			W.Open($"{Access(d)}{mod}partial class {d.Name}{TypeParams(d.TypeParams)}{baseList}");
		}

		bool hasCtors = t.Constructors.Count > 0;
		foreach (var f in d.Fields)
		{
			if (f.BackingFor is not null || f.IsEnumEntry || positional.Contains(f)) continue;
			string init = "";
			if (!hasCtors && t.FieldInitializers.FirstOrDefault(fi => fi.Field == f) is { } fi)
			{
				BeginFunction();
				var (v, text) = Capture(() => Value(fi.Value));
				if (text.Length == 0) init = " = " + StripParens(v);
				else Warnings.Add($"{d.Name}.{f.Name}: сложный инициализатор поля");
			}
			W.Line($"{Access(f)}{(f.IsStatic ? "static " : "")}{TypeName(f.Type ?? PrimitiveType.Object)} {MemberName(f.Name)}{init};");
		}
		foreach (var p in d.Properties) EmitProperty(p, t, hasCtors);
		foreach (var c in t.Constructors) EmitCtor(t, c);
		foreach (var f in t.Functions) { EmitFunction(f, d); W.Line(); }
		W.Close();
	}

	private static List<FieldSymbol> PositionalMembers(TypeDeclSymbol d)
	{
		if (d.PrimaryConstructor is { } pc)
			return [.. pc.Parameters.Select(p => p.Field).Where(f => f is not null)!];
		return [.. d.Fields.Where(f => !f.IsStatic && !f.IsEnumEntry && f.BackingFor is null && f.Init is null)];
	}

	private void EmitProperty(PropertySymbol p, BoundTypeDeclaration t, bool hasCtors)
	{
		if (p.ExtensionOf is not null) return;
		var get = t.Accessors.FirstOrDefault(a => a.Symbol.Property == p && a.Symbol.IsGetter);
		var set = t.Accessors.FirstOrDefault(a => a.Symbol.Property == p && (a.Symbol.IsSetter || a.Symbol.IsInit));
		string head = $"{Access(p)}{(p.IsStatic ? "static " : "")}{TypeName(p.Type)} {MemberName(p.Name)}";
		bool custom = (get?.Body is not null && !get.IsSynthesized) || (set?.Body is not null && !set.IsSynthesized);
		if (!custom)
		{
			string init = "";
			if (!hasCtors && p.BackingField is { } bf && t.FieldInitializers.FirstOrDefault(fi => fi.Field == bf) is { } fi)
			{
				BeginFunction();
				var (v, text) = Capture(() => Value(fi.Value));
				if (text.Length == 0) init = " = " + StripParens(v) + ";";
			}
			W.Line($"{head} {{ get; {(p.CanWrite || p.CanInit ? "set; " : "private set; ")}}}{init}");
			return;
		}
		W.Open(head);
		if (get?.Body is not null)
		{
			BeginFunction();
			W.Open("get");
			EmitBody(get.Body, get.ReturnType);
			W.Close();
		}
		if (set?.Body is not null)
		{
			BeginFunction();
			W.Open(set.Symbol.IsInit ? "init" : "set");
			if (set.Parameters.Count > 0) W.Line($"var {NameOf(set.Parameters[0])} = value;");
			EmitBody(set.Body, PrimitiveType.Void);
			W.Close();
		}
		W.Close();
	}

	private void EmitCtor(BoundTypeDeclaration t, BoundConstructor c)
	{
		var d = t.Type;
		var f = c.Function;
		BeginFunction();
		var ps = string.Join(", ", f.Parameters.Select(p => $"{TypeName(p.EffectiveType)} {NameOf(p)}"));
		bool primary = c.Symbol.IsPrimaryCtor;
		if (d.IsValueType && primary) return; // позиционные параметры record struct
		string tail = "";
		if (f.Chain is { } ch)
			tail = $" : this({string.Join(", ", ch.Arguments.Select(Value))})";
		else if (t.ParentInits.Count > 0 && t.ParentInits[0].Constructor is not null)
			tail = $" : base({string.Join(", ", t.ParentInits[0].Arguments.Select(Value))})";
		W.Open($"{Access(c.Symbol)}{d.Name}({ps}){tail}");
		if (f.Chain is null)
		{
			var done = new HashSet<FieldSymbol>();
			foreach (var fi in t.FieldInitializers)
			{
				if (fi.Field.IsStatic) continue;
				if (fi.Field.FromParameter is not null && !primary) continue;
				string target = fi.Field.BackingFor is { } bp ? MemberName(bp.Name) : MemberName(fi.Field.Name);
				W.Line($"this.{target} = {StripParens(Value(fi.Value))};");
				done.Add(fi.Field);
			}
			if (!primary)
				foreach (var p in f.Parameters)
					if (p.AutoInit && (p.Field ?? d.Fields.FirstOrDefault(x => x.Name == p.Name)) is { } fld && !fld.IsStatic && !done.Contains(fld))
						W.Line($"this.{MemberName(fld.Name)} = {NameOf(p)};");
		}
		EmitBody(f.Body, PrimitiveType.Void, isCtor: true);
		W.Close();
		W.Line();
	}
}
