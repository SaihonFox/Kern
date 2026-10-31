using Kern.Compiler.Semantics.Binding.Bodies;
using Kern.Compiler.Semantics.Binding.Members;
using Kern.Compiler.Semantics.Bound.Declarations;
using Kern.Compiler.Semantics.Project;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics;

/// <summary>Точка входа «части 2» семантики: типизированное дерево всего проекта и поиск точки входа.</summary>
public static class SemanticAnalyzer
{
	public static BoundProgram Analyze(ProjectModel project)
	{
		var extensions = new ExtensionIndex(project);
		var binder = new BodyBinder(project, extensions);
		var modules = binder.BindAll();
		var entry = FindEntryPoint(project, binder);
		return new BoundProgram(project, modules, entry, binder.Functions);
	}

	private static BoundFunction? FindEntryPoint(ProjectModel project, BodyBinder binder)
	{
		var candidates = project.ProjectModules
			.SelectMany(m => m.Functions)
			.Where(f => f.Name == "main" && f.ExtensionOf is null && f.DeclaringType is null
				|| f.Attributes.Any(a => a.Syntax.Name == "entry"))
			.ToList();
		if (candidates.Count == 0) return null;
		if (candidates.Count > 1)
		{
			project.Diagnostics.Error(Krn.DuplicateMain, "в проекте несколько точек входа (`main`/`#[entry]`)", candidates[1].File, candidates[1].Span);
			return null;
		}
		var main = candidates[0];
		bool paramsOk = main.Parameters.Count == 0
			|| (main.Parameters.Count == 1 && main.Parameters[0].Type is ListType { Element: PrimitiveType { Kind: Kern.Bytecode.PrimKind.Str } });
		if (!paramsOk)
		{
			project.Diagnostics.Error(Krn.InvalidMain, "`main` принимает либо ничего, либо `list<str> args`", main.File, main.Span);
			return null;
		}
		return binder.Functions.GetValueOrDefault(main);
	}
}
