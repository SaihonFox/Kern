using System.Text;
using Kern.Compiler.Parsing;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Project;
using Kern.Compiler.Syntax;
using Kern.Core;
using Kern.Runtime.Macros;

namespace Kern.Cli.Macros;

internal sealed partial class MacroEngine
{
	/// <summary>
	/// Атрибутные макросы на типах верхнего уровня: `#[имя]` и `#[derive(a, b)]`. Члены (`m.member`, а также возвращённый текст)
	/// вставляются в конец тела типа, `m.top` — в конец файла. Сгенерированный код повторно не раскрывается.
	/// </summary>
	private string ExpandAttributes(SourceText file, string text, Dictionary<string, MacroInfo> macros, DiagnosticBag diagnostics)
	{
		var byName = macros.Values.Where(m => !m.IsExpr).ToDictionary(m => m.Name);
		if (byName.Count == 0 || !text.Contains("#[")) return text;

		var sf = new SourceFile(file.Path, text);
		var unit = ParserFacade.ParseText(file.Path, text, null, new DiagnosticBag());
		var edits = new List<(int Pos, string Text)>();
		var tops = new StringBuilder();

		foreach (var decl in unit.Decls.OfType<TypeDecl>())
		{
			var triggered = Triggered(decl, text, byName, sf, diagnostics);
			if (triggered.Count == 0) continue;

			var target = BuildTarget(decl, text);
			int line = sf.GetLineColumn(decl.Span.Start).Line;
			var members = new List<string>();
			foreach (var (macro, use, name) in triggered)
			{
				var ctx = new MacroCtx(name, file.Path, line, target, []);
				if (Run(macro, ctx, sf, use.Span, diagnostics) is { } returned) members.Add(returned);
				members.AddRange(ctx.Members);
				foreach (var top in ctx.Tops) tops.Append('\n').Append(top);
			}
			if (members.Count == 0) continue;

			int end = decl.Span.End;
			while (end > decl.Span.Start && char.IsWhiteSpace(text[end - 1])) end--;
			var code = string.Join("\n", members);
			if (end > 0 && text[end - 1] == '}') edits.Add((end - 1, "\n" + code + "\n"));
			else edits.Add((end, " {\n" + code + "\n}"));
		}

		foreach (var (pos, insert) in edits.OrderByDescending(e => e.Pos))
			text = text.Insert(pos, insert);
		return tops.Length == 0 ? text : text + "\n" + tops;
	}

	private static List<(MacroInfo Macro, AttributeUse Use, string Name)> Triggered(
		TypeDecl decl, string text, Dictionary<string, MacroInfo> byName, SourceFile sf, DiagnosticBag diagnostics)
	{
		var result = new List<(MacroInfo, AttributeUse, string)>();
		foreach (var a in decl.Attributes)
		{
			if (a.Name.Equals("derive", StringComparison.OrdinalIgnoreCase))
			{
				foreach (var arg in a.Args)
				{
					var name = Src(text, arg.Value.Span).Trim();
					if (byName.TryGetValue(name, out var m)) result.Add((m, a, name));
					else diagnostics.Error(Krn.MacroError, $"derive: неизвестный макрос '{name}'", sf, arg.Span);
				}
			}
			else if (byName.TryGetValue(a.Name, out var m)) result.Add((m, a, a.Name));
		}
		return result;
	}

	// ------------------------------------------------------------- описание цели

	private static MacroTarget BuildTarget(TypeDecl decl, string text)
	{
		var fields = new List<MacroField>();
		foreach (var p in decl.PrimaryCtor ?? [])
			fields.Add(ParamField(p, text));
		foreach (var m in decl.Members)
			switch (m)
			{
				case FieldDecl f:
					fields.Add(new MacroField(f.Name, f.Type is null ? "" : Src(text, f.Type.Span), "field",
						f.Init is null ? null : Src(text, f.Init.Span), f.Mods.Access != Access.Priv));
					break;
				case PropertyDecl p:
					fields.Add(new MacroField(p.Name, Src(text, p.Type.Span), "property",
						p.Init is null ? null : Src(text, p.Init.Span), p.Mods.Access != Access.Priv));
					break;
			}

		var methods = decl.Members.OfType<FunDecl>()
			.Select(f => new MacroMethod(f.Name, [.. f.Params.Select(p => ParamField(p, text))],
				f.ReturnType is null ? null : Src(text, f.ReturnType.Span), f.Mods.IsStatic, f.Mods.IsAbstract))
			.ToList();

		var attrs = decl.Attributes.Select(a => new MacroAttr(
			a.Name,
			[.. a.Args.Where(x => x.Name is null).Select(x => Src(text, x.Value.Span).Trim())],
			a.Args.Where(x => x.Name is not null).ToDictionary(x => x.Name!, x => Src(text, x.Value.Span).Trim()))).ToList();

		string kind = decl.Kind switch
		{
			TypeKind.AbstractType => "abstract",
			TypeKind.Unit => "unit",
			TypeKind.ImmutableUnit => "unit!",
			TypeKind.Attribute => "attribute",
			_ => "type",
		};
		return new MacroTarget(decl.Name, kind, [.. decl.TypeParams.Select(t => t.Name)], fields, methods, attrs,
			[.. decl.Parents.Select(p => Src(text, p.Type.Span))], Src(text, decl.Span));
	}

	private static MacroField ParamField(Param p, string text) =>
		new(p.Name, Src(text, p.Type.Span), "param", p.Default is null ? null : Src(text, p.Default.Span), p.Mods.Access != Access.Priv);
}
