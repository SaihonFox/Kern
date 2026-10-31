using System.Reflection;
using System.Text;
using Kern.Binding.Catalog;
using Kern.Binding.Marshalling;
using Kern.Bytecode;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Project;
using Kern.Lib.Transpile.Core;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Lib.Transpile.Mapping;

/// <summary>
/// Пользовательские подстановки, написанные на Kern. Скрипт — обычный .kern-файл; если в нём есть функции
/// <c>map_type(str target, str kern_type, list&lt;str&gt; args) -&gt; str?</c>,
/// <c>map_call(str target, str owner, str name, str receiver, list&lt;str&gt; args) -&gt; str?</c>,
/// <c>map_member(str target, str owner, str name, str receiver) -&gt; str?</c> — они вызываются для каждого нативного типа/члена.
/// Результат nil означает «правила нет».
/// </summary>
public sealed class ScriptMappings : IMappings
{
	private readonly Interpreter _vm;
	private readonly int _type, _call, _member;

	private ScriptMappings(Interpreter vm, KbcModule module)
	{
		_vm = vm;
		_type = Find(module, "map_type");
		_call = Find(module, "map_call");
		_member = Find(module, "map_member");
	}

	private static int Find(KbcModule m, string name) => m.Functions.FindIndex(f => f.Name == name && f.Owner < 0);

	public static ScriptMappings Load(string path)
	{
		path = Path.GetFullPath(path);
		var libs = LibraryLoader.Load(LibraryLoader.StandardRoots(Path.GetDirectoryName(path)));
		var catalog = new NativeCatalog([typeof(Kern.Runtime.Core.CoreApi).Assembly, .. libs]);
		var loader = new ProjectLoader(new CatalogNativeSource(catalog), new EmbeddedStdSource(Assembly.Load("Kern.Std")));
		var name = Path.GetFileNameWithoutExtension(path);
		var project = loader.LoadText(name, path, File.ReadAllText(path, new UTF8Encoding(false)));
		var errors = project.Diagnostics.Where(d => d.Severity == Kern.Core.Severity.Error).Select(d => d.ToString()).ToList();
		if (errors.Count == 0)
		{
			var program = SemanticAnalyzer.Analyze(project);
			errors = [.. project.Diagnostics.Where(d => d.Severity == Kern.Core.Severity.Error).Select(d => d.ToString())];
			if (errors.Count == 0)
			{
				var module = CodeGenerator.Generate(program, name, 0);
				var vm = new Interpreter(module, new NativeResolver());
				vm.Initialize();
				return new ScriptMappings(vm, module);
			}
		}
		throw new InvalidOperationException($"ошибки в скрипте подстановок {path}:\n" + string.Join('\n', errors));
	}

	private static KValue List(IReadOnlyList<string> items) =>
		KValue.Obj(new KListObject(TypeFactory.Str, [.. items.Select(KValue.Str)]));

	private string? Invoke(int fn, params KValue[] args)
	{
		if (fn < 0) return null;
		var r = _vm.CallFunctionIndex(fn, args);
		return r.IsNilOrNone ? null : r.AsStr;
	}

	public string? MapType(string target, string kernType, IReadOnlyList<string> args) =>
		Invoke(_type, KValue.Str(target), KValue.Str(kernType), List(args));

	public string? MapCall(string target, string owner, string name, string receiver, IReadOnlyList<string> args) =>
		Invoke(_call, KValue.Str(target), KValue.Str(owner), KValue.Str(name), KValue.Str(receiver), List(args));

	public string? MapMember(string target, string owner, string name, string receiver) =>
		Invoke(_member, KValue.Str(target), KValue.Str(owner), KValue.Str(name), KValue.Str(receiver));
}
