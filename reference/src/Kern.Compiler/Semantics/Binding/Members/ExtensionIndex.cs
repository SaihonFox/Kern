using Kern.Binding.Catalog;
using Kern.Compiler.Semantics.Binding.Core;
using Kern.Compiler.Semantics.Declare;
using Kern.Compiler.Semantics.Project;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.Semantics.Binding.Members;

/// <summary>
/// Индекс НАТИВНЫХ расширений встроенных типов (`str`, `list&lt;T&gt;`, `i32`, `num`…): статические C#-классы с `self`
/// (<see cref="NativeTypeCategory.Extension"/>) из prelude-модулей и модулей, упомянутых в `use` проекта.
/// Видимость упрощена: индекс общий для проекта (допущение: достаточно, чтобы модуль был импортирован хоть в одном файле).
/// Пользовательские расширения (`fun str.shout()`) здесь не хранятся — их находит <see cref="MemberLookup"/> по модулям.
/// </summary>
public sealed class ExtensionIndex
{
	private readonly Dictionary<string, List<FunctionSymbol>> _functions = [];
	private readonly Dictionary<string, List<PropertySymbol>> _properties = [];
	private readonly Dictionary<string, List<FunctionSymbol>> _operators = [];
	private readonly Dictionary<string, List<FunctionSymbol>> _constructors = [];
	private readonly Dictionary<(string Owner, string Name), List<FunctionSymbol>> _statics = [];
	private readonly Dictionary<(string Owner, string Name), PropertySymbol> _staticProperties = [];

	public ExtensionIndex(ProjectModel project)
	{
		var source = project.Binder.Source;
		var names = new HashSet<string>(DeclarationCollector.PreludeModules);
		foreach (var file in project.Files)
			foreach (var use in file.Unit.Uses)
			{
				if (use.IsDotnet) continue;
				string path = string.Join('.', use.Path);
				names.Add(path);
				foreach (var item in use.Items) names.Add(path + "." + item);
			}

		foreach (var name in names)
		{
			if (source.FindModule(name) is not { } module) continue;
			foreach (var type in module.Types.Values.Where(t => t.IsExtension))
				Index(project.Binder, source, type);
		}
	}

	private void Index(NativeBinder binder, INativeSymbolSource source, NativeTypeSymbol type)
	{
		foreach (var member in source.GetMembers(type))
		{
			var bound = binder.Bind(member);
			switch (bound)
			{
				case PropertySymbol p when p.IsStatic:
					_staticProperties[(type.Name, p.Name)] = p;
					break;
				case PropertySymbol p:
					Add(_properties, p.Name, p);
					break;
				case FunctionSymbol f when f.IsConstructor:
					Add(_constructors, type.Name, f);
					break;
				case FunctionSymbol f when f.IsOperator:
					Add(_operators, f.OpSymbol ?? "", f);
					break;
				case FunctionSymbol f when f.IsStatic:
					if (!_statics.TryGetValue((type.Name, f.Name), out var list)) _statics[(type.Name, f.Name)] = list = [];
					list.Add(f);
					break;
				case FunctionSymbol f:
					Add(_functions, f.Name, f);
					break;
			}
		}
	}

	private static void Add<T>(Dictionary<string, List<T>> map, string key, T value)
	{
		if (!map.TryGetValue(key, out var list)) map[key] = list = [];
		list.Add(value);
	}

	/// <summary>Нативные методы-расширения с этим именем (у каждого заполнен <see cref="FunctionSymbol.ExtensionOf"/>).</summary>
	public IReadOnlyList<FunctionSymbol> Functions(string name) => _functions.GetValueOrDefault(name) ?? [];

	public IReadOnlyList<PropertySymbol> Properties(string name) => _properties.GetValueOrDefault(name) ?? [];

	/// <summary>Нативные операторы-расширения по тексту оператора (`+`, `in`, `[]`…).</summary>
	public IReadOnlyList<FunctionSymbol> Operators(string symbol) => _operators.GetValueOrDefault(symbol) ?? [];

	/// <summary>Конструкторы встроенного типа по имени (`list`, `str`…).</summary>
	public IReadOnlyList<FunctionSymbol> Constructors(string typeName) => _constructors.GetValueOrDefault(typeName) ?? [];

	/// <summary>Статические функции встроенного типа (`i32.parse`).</summary>
	public IReadOnlyList<FunctionSymbol> Statics(string typeName, string name) => _statics.GetValueOrDefault((typeName, name)) ?? [];

	public PropertySymbol? StaticProperty(string typeName, string name) => _staticProperties.GetValueOrDefault((typeName, name));
}
