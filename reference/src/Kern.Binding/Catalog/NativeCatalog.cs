using System.Reflection;
using Kern.Binding.Attributes;
using Kern.Bytecode;

namespace Kern.Binding.Catalog;

/// <summary>
/// Каталог нативных символов: Kern-модули (из сборок с [KernType]/[KernModule]) и dotnet-типы (<c>use dotnet.System.IO[File]</c>).
/// Компилятор обращается к нему через адаптер INativeSymbolSource. Потокобезопасен (внутренняя блокировка).
/// </summary>
public sealed class NativeCatalog
{
	private readonly ModuleMapIndex _modules = new();
	private readonly TypeMapper _mapper;
	private readonly NativeScanner _scanner;
	private readonly Dictionary<string, NativeModule> _kern = [];
	private readonly Dictionary<Type, NativeTypeSymbol> _dotnet = [];
	private readonly Lock _lock = new();
	private HashSet<string>? _dotnetNamespaces;
	private Dictionary<(string Ns, string Name), Type>? _dotnetTypes;

	public NativeCatalog(params Assembly[] assemblies)
	{
		_mapper = new TypeMapper(_modules);
		_scanner = new NativeScanner(_mapper);
		foreach (var a in assemblies) _modules.AddAssembly(a);
		foreach (var a in assemblies) Scan(a);
		LinkParents();
	}

	/// <summary>Связывает базовые нативные типы (Parent → ParentSymbol) после того, как просканированы все сборки.</summary>
	private void LinkParents()
	{
		foreach (var module in _kern.Values)
			foreach (var type in module.Types.Values)
			{
				if (type.Parent is not TypeSig.Named named) continue;
				int dot = named.FullName.LastIndexOf('.');
				if (dot > 0 && _kern.TryGetValue(named.FullName[..dot], out var pm) && pm.Types.TryGetValue(named.FullName[(dot + 1)..], out var ps))
					type.ParentSymbol = ps;
			}
	}

	public ModuleMapIndex Modules => _modules;
	public IEnumerable<NativeModule> KernModules => _kern.Values;

	private NativeModule ModuleFor(string name)
	{
		if (!_kern.TryGetValue(name, out var m)) _kern[name] = m = new NativeModule(name);
		return m;
	}

	private void Scan(Assembly assembly)
	{
		Type[] all;
		try { all = assembly.GetTypes(); }
		catch (ReflectionTypeLoadException e) { all = [.. e.Types.Where(t => t is not null)!]; }

		var groups = new Dictionary<(string Module, string Name), List<Type>>();
		var holders = new List<(string Module, Type Type)>();
		foreach (var t in all)
		{
			if (!t.IsPublic && !t.IsNestedPublic) continue;
			if (t.GetCustomAttribute<KernTypeAttribute>(false) is { } kt)
			{
				var key = (_modules.ModuleOf(t), kt.Name);
				if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
				list.Add(t);
			}
			else if (t.IsAbstract && t.IsSealed && t.GetCustomAttribute<KernModuleAttribute>(false) is { } km)
				holders.Add((km.Name, t));
		}

		// self-типы расширений регистрируются до построения символов (BigInteger, decimal… → Kern-имена)
		foreach (var ((module, name), types) in groups) _scanner.RegisterSelfTypes(module, name, types);
		foreach (var ((module, name), types) in groups)
			ModuleFor(module).Types[name] = _scanner.BuildKernSymbol(module, name, types);
		foreach (var (module, holder) in holders) _scanner.AddFunctions(ModuleFor(module), holder);
	}

	// ---------- Kern ----------

	public NativeModule? FindModule(string name) => _kern.GetValueOrDefault(name);

	public NativeTypeSymbol? FindType(string module, string name)
	{
		if (module.StartsWith("dotnet.", StringComparison.Ordinal)) return FindDotnetType(module[7..], name);
		return _kern.TryGetValue(module, out var m) && m.Types.TryGetValue(name, out var t) ? t : null;
	}

	public IReadOnlyList<NativeMember> GetMembers(NativeTypeSymbol type) => type.Members;

	// ---------- dotnet (способ C) ----------

	private void EnsureDotnetIndex()
	{
		if (_dotnetTypes is not null) return;
		var types = new Dictionary<(string, string), Type>();
		var nss = new HashSet<string>();
		foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
		{
			if (asm.IsDynamic) continue;
			Type[] exported;
			try { exported = asm.GetExportedTypes(); } catch { continue; }
			foreach (var t in exported)
			{
				if (t.Namespace is not { Length: > 0 } ns || t.IsNested) continue;
				string name = t.Name;
				int tick = name.IndexOf('`');
				if (tick >= 0) name = name[..tick];
				types.TryAdd((ns, name), t);
				for (var p = ns; ; )
				{
					nss.Add(p);
					int dot = p.LastIndexOf('.');
					if (dot < 0) break;
					p = p[..dot];
				}
			}
		}
		_dotnetTypes = types;
		_dotnetNamespaces = nss;
	}

	public NativeTypeSymbol? FindDotnetType(string ns, string name)
	{
		lock (_lock)
		{
			EnsureDotnetIndex();
			if (!_dotnetTypes!.TryGetValue((ns, name), out var t)) return null;
			if (!_dotnet.TryGetValue(t, out var symbol)) _dotnet[t] = symbol = _scanner.BuildDotnetSymbol(t);
			return symbol;
		}
	}

	public bool DotnetNamespaceExists(string ns)
	{
		lock (_lock)
		{
			EnsureDotnetIndex();
			return _dotnetNamespaces!.Contains(ns);
		}
	}
}
