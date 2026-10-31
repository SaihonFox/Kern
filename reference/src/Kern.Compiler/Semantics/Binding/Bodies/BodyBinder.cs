using Kern.Compiler.Semantics.Binding.Core;
using Kern.Compiler.Semantics.Binding.Members;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Declarations;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Project;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Binding.Bodies;

/// <summary>
/// Обходит все объявления проекта (включая std) и привязывает их тела. Привязка «по требованию»: возвращаемый тип функции
/// без аннотации и тип поля с `var` выводятся при первом обращении (<see cref="EnsureSignature"/>, <see cref="EnsureFieldType"/>).
/// Для каждого тела создаётся свежий <see cref="Binder"/> — состояния потока разных тел не смешиваются.
/// </summary>
public sealed class BodyBinder(ProjectModel project, ExtensionIndex extensions)
{
	private readonly Dictionary<FunctionSymbol, BoundFunction> _functions = [];
	private readonly HashSet<FunctionSymbol> _functionsInProgress = [];
	private readonly Dictionary<FieldSymbol, BoundExpr?> _fieldInits = [];
	private readonly HashSet<FieldSymbol> _fieldsInProgress = [];

	public IReadOnlyDictionary<FunctionSymbol, BoundFunction> Functions => _functions;

	private Binder NewBinder(Symbol owner)
	{
		var file = project.Files.FirstOrDefault(f => ReferenceEquals(f.File, owner.File))
			?? project.Files.First(f => f.Module == FindModule(owner));
		return new Binder(project, extensions, file) { Owner = this };
	}

	private static ModuleSymbol? FindModule(Symbol s)
	{
		for (var c = s; c is not null; c = c.Container)
			if (c is ModuleSymbol m) return m;
		return null;
	}

	// ------------------------------------------------------------ по требованию

	public void EnsureSignature(FunctionSymbol f)
	{
		if (f.ReturnType is not null || f.Body is null || f.IsNative || f.IsConstructor) return;
		BindFunction(f);
	}

	public void EnsureFieldType(FieldSymbol f)
	{
		if (f.Type is not null || f.Init is null) return;
		BindField(f);
	}

	public BoundFunction? BindFunction(FunctionSymbol f)
	{
		if (_functions.TryGetValue(f, out var cached)) return cached;
		if (!_functionsInProgress.Add(f)) return null; // рекурсия: тип возврата ещё не выведен
		try
		{
			var bound = NewBinder(f).BindFunction(f);
			_functions[f] = bound;
			return bound;
		}
		finally { _functionsInProgress.Remove(f); }
	}

	private BoundExpr? BindField(FieldSymbol f)
	{
		if (_fieldInits.TryGetValue(f, out var cached)) return cached;
		if (!_fieldsInProgress.Add(f)) return null;
		try
		{
			var init = NewBinder(f).BindFieldInitializer(f, f.Init);
			_fieldInits[f] = init;
			return init;
		}
		finally { _fieldsInProgress.Remove(f); }
	}

	// ------------------------------------------------------------ обход

	public IReadOnlyList<BoundModuleBody> BindAll()
	{
		// 1) поля и глобальные переменные: типы нужны функциям
		foreach (var module in project.Modules.All)
			foreach (var g in module.Fields) BindField(g);
		foreach (var t in project.Types)
			foreach (var f in t.Fields) if (f.Init is not null && f.BackingFor is null) BindField(f);

		// 2) тела
		var result = new List<BoundModuleBody>();
		foreach (var module in project.Modules.All)
		{
			var globals = module.Fields.Select(g => new BoundGlobal(g, BindField(g))).ToList();
			var functions = new List<BoundFunction>();
			foreach (var f in module.Functions)
				if (BindFunction(f) is { } bf) functions.Add(bf);
			foreach (var p in module.ExtensionProperties)
				foreach (var acc in new[] { p.Getter, p.Setter, p.Initer })
					if (acc is not null && BindFunction(acc) is { } ba) functions.Add(ba);

			var types = project.Types.Where(t => FindModule(t) == module).Select(BindType).ToList();
			result.Add(new BoundModuleBody(module, globals, functions, types));
		}
		return result;
	}

	private BoundTypeDeclaration BindType(TypeDeclSymbol t)
	{
		var binder = NewBinder(t);
		var parentInits = t.Parents.Where(p => p.Decl is not null || p.Type is NativeType)
			.Select(p => binder.BindParentInit(t, p)).ToList();

		var fieldInits = new List<BoundFieldInitializer>();
		foreach (var f in t.Fields)
		{
			if (f.FromParameter is { } prm) fieldInits.Add(Binder.ParameterFieldInitializer(f, prm));
			else if (f.BackingFor is { Init: { } pinit } prop)
			{
				var value = NewBinder(t).BindFieldInitializer(f, pinit);
				if (value is not null) fieldInits.Add(new BoundFieldInitializer(f, value));
			}
			else if (f.Init is not null && !f.IsEnumEntry && BindField(f) is { } v)
				fieldInits.Add(new BoundFieldInitializer(f, v));
		}

		var ctors = new List<BoundConstructor>();
		foreach (var c in t.Constructors)
			if (BindFunction(c) is { } bc) ctors.Add(new BoundConstructor(c, bc));

		var functions = new List<BoundFunction>();
		foreach (var f in t.Functions)
			if (BindFunction(f) is { } bf) functions.Add(bf);

		var accessors = new List<BoundFunction>();
		foreach (var p in t.Properties)
			foreach (var acc in new[] { p.Getter, p.Setter, p.Initer })
				if (acc is not null && BindFunction(acc) is { } ba) accessors.Add(ba);

		var entries = t.EnumEntries.Select(e => NewBinder(t).BindEnumEntry(t, e)).ToList();
		return new BoundTypeDeclaration(t, parentInits, fieldInits, ctors, functions, accessors, entries);
	}
}
