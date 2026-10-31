using Kern.Compiler.Semantics.Project;
using Kern.Compiler.Syntax;
using Kern.Core;

namespace Kern.Cli.Kbc;

/// <summary>
/// Интерфейсные исходники библиотеки. При `kernc dll` в .kbc записывается копия исходников проекта, где тела функций
/// заменены на `panic("kbc")`: потребитель компилирует их как обычные модули (из них берутся типы, сигнатуры и раскладка полей),
/// а <see cref="KbcLinker"/> после генерации кода подставляет настоящий байткод библиотеки.
/// </summary>
public static class KbcStubs
{
	/// <summary>Имя атрибута уровня модуля, в котором хранится один интерфейсный файл.</summary>
	public const string AttributeName = "kern.stub";

	/// <summary>Префикс путей интерфейсных файлов (по нему линкер узнаёт функции-заглушки).</summary>
	public const string PathPrefix = "<kbc>/";

	private const string StubBody = "panic(\"kbc\")";

	/// <summary>Атрибуты-заглушки для всех пользовательских файлов проекта.</summary>
	public static List<Kern.Bytecode.AttributeUse> Build(ProjectModel project)
	{
		var result = new List<Kern.Bytecode.AttributeUse>();
		foreach (var file in project.Files)
		{
			if (file.IsStd) continue;
			var src = file.Unit.File;
			if (src.Path.StartsWith(PathPrefix, StringComparison.Ordinal)) continue; // заглушки чужих .kbc сюда не попадают
			string rel = project.SourceDirectory is { } dir && Path.IsPathRooted(src.Path)
				? Path.GetRelativePath(dir, src.Path).Replace('\\', '/')
				: Path.GetFileName(src.Path);
			result.Add(new Kern.Bytecode.AttributeUse
			{
				TypeName = AttributeName,
				Args =
				[
					new Kern.Bytecode.ConstantArg { Name = "path", Value = rel },
					new Kern.Bytecode.ConstantArg { Name = "module", Value = file.Module.Name },
					new Kern.Bytecode.ConstantArg { Name = "text", Value = Strip(file.Unit) },
				],
			});
		}
		return result;
	}

	/// <summary>Интерфейсные файлы из .kbc как исходники для компилятора.</summary>
	public static List<SourceText> Read(Kern.Bytecode.KbcModule lib)
	{
		var list = new List<SourceText>();
		foreach (var a in lib.Attributes)
		{
			if (a.TypeName != AttributeName) continue;
			string? path = null, module = null, text = null;
			foreach (var arg in a.Args)
				switch (arg.Name)
				{
					case "path": path = arg.Value as string; break;
					case "module": module = arg.Value as string; break;
					case "text": text = arg.Value as string; break;
				}
			if (path is null || module is null || text is null) continue;
			list.Add(new SourceText($"{PathPrefix}{lib.Name}/{path}", text, module));
		}
		return list;
	}

	// ---------------------------------------------------------------- вырезание тел

	private static string Strip(CompilationUnit unit)
	{
		var edits = new List<(int Start, int Length, string Text)>();
		Collect(unit.Decls, edits, topLevel: true);
		string text = unit.File.Text;
		int last = int.MaxValue;
		foreach (var (start, length, repl) in edits.OrderByDescending(e => e.Start))
		{
			if (start + length > last) continue; // вложенные правки (после вырезания main) пропускаем
			text = string.Concat(text.AsSpan(0, start), repl, text.AsSpan(start + length));
			last = start;
		}
		return text;
	}

	private static void Collect(IEnumerable<Decl> decls, List<(int, int, string)> edits, bool topLevel)
	{
		foreach (var d in decls)
		{
			switch (d)
			{
				case FunDecl { Name: "main", ParentSelector: null } when topLevel:
					edits.Add((d.Span.Start, d.Span.Length, "")); // точка входа библиотеки не нужна потребителю
					break;
				case FunDecl f:
					Body(f.ReturnType is not null ? f.Body : null, edits);
					break;
				case ExtensionFunDecl ef:
					Body(ef.ReturnType is not null ? ef.Body : null, edits);
					break;
				case ExtensionPropDecl ep:
					Body(ep.Body, edits);
					break;
				case ExtensionOpDecl eo:
					Body(eo.ReturnType is not null ? eo.Body : null, edits);
					break;
				case OpDecl op:
					Body(op.ReturnType is not null ? op.Body : null, edits);
					break;
				case CtorDecl c:
					Body(c.Body, edits);
					break;
				case PropertyDecl p:
					foreach (var acc in p.Accessors) Body(acc.Body, edits);
					break;
				case TypeDecl t:
					Collect(t.Members, edits, false);
					break;
				case EnumDecl e:
					Collect(e.Members, edits, false);
					break;
			}
		}
	}

	/// <summary>
	/// Тело заменяется заглушкой в той же форме (блок остаётся блоком). Функции без объявленного возвращаемого типа
	/// оставляются как есть: их тип выводится из тела, и заглушка изменила бы сигнатуру.
	/// </summary>
	private static void Body(Expr? body, List<(int, int, string)> edits)
	{
		if (body is null) return;
		edits.Add((body.Span.Start, body.Span.Length, body is BlockExpr ? "{ " + StubBody + " }" : StubBody));
	}
}
