using Kern.Binding.Catalog;
using Kern.Compiler.Semantics.Project;
using Kern.Compiler.Semantics.Scopes;
using Kern.Compiler.Semantics.Symbols;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Declare.Collect;

/// <summary>
/// Фаза 2: разрешение `use`. Виды путей: пользовательские/std-модули (`use a.b[X]`, `use a.b.*`, `use a.b`),
/// нативные Kern-модули (`kern.text`), .NET (`use dotnet.System.IO[File]`).
/// Функции импортируются «вживую» через <see cref="ImportedOverloadSet"/> — группы, заполняемые позже, видны сразу.
/// </summary>
public sealed class ImportResolver(CollectContext ctx)
{
	private INativeSymbolSource Natives => ctx.Binder.Source;

	// ------------------------------------------------------------ prelude

	/// <summary>Неявные импорты: все публичные имена перечисленных модулей попадают в prelude (первый побеждает, молча).</summary>
	public void PopulatePrelude(IEnumerable<string> modules)
	{
		foreach (var path in modules)
		{
			var user = ctx.Modules.Find(path);
			var native = Natives.FindModule(path);
			ImportAll(ctx.Prelude, user, native, file: null, own: null, default);
		}
	}

	// ------------------------------------------------------------ use

	public void ResolveFile(FileModel file)
	{
		foreach (var use in file.Unit.Uses)
			ResolveUse(file, use);
	}

	private void ResolveUse(FileModel file, Syn.UseDecl use)
	{
		string path = string.Join('.', use.Path);
		if (use.IsDotnet) ResolveDotnet(file, use, path);
		else ResolveKern(file, use, path);
	}

	private void ResolveDotnet(FileModel file, Syn.UseDecl use, string path)
	{
		if (use.Path.Count < 2)
		{
			ctx.Error(Krn.ImportNotFound, "после `dotnet` ожидается пространство имён .NET (`use dotnet.System.IO[File]`)", file.File, use.Span);
			return;
		}
		if (use.Wildcard)
		{
			ctx.Error(Krn.ImportWildcardUnsupported, "`use dotnet.X.*` не поддерживается: перечислите типы в скобках", file.File, use.Span);
			return;
		}

		string ns = string.Join('.', use.Path.Skip(1));
		if (use.Items.Count > 0)
		{
			foreach (var item in use.Items)
			{
				if (Natives.FindDotnetType(ns, item) is { } type)
					Bind(file.Scope, new NativeTypeBinding(item, type), file, null, use);
				else
					ctx.Error(Krn.ImportItemNotFound, $"в .NET-пространстве '{ns}' нет публичного типа '{item}'", file.File, use.Span);
			}
			return;
		}

		if (!Natives.DotnetNamespaceExists(ns))
		{
			ctx.Error(Krn.ImportNotFound, $"пространство имён .NET '{ns}' не найдено", file.File, use.Span);
			return;
		}
		BindAlias(file, new ModuleAliasSymbol(use.Path[^1], path), use);
	}

	private void ResolveKern(FileModel file, Syn.UseDecl use, string path)
	{
		var user = ctx.Modules.Find(path);
		var native = Natives.FindModule(path);
		if (ReferenceEquals(user, file.Module)) return; // импорт собственного модуля — ничего не делает

		if (user is null && native is null)
		{
			// `use kern` / `use a` — префикс существующих модулей: даёт только алиас для `a.b.Type`.
			bool prefix = ctx.Modules.All.Any(m => m.Name.StartsWith(path + ".", StringComparison.Ordinal));
			if (prefix && use.Items.Count == 0 && !use.Wildcard)
				BindAlias(file, new ModuleAliasSymbol(use.Path[^1], path), use);
			else
				ctx.Error(Krn.ImportNotFound, $"модуль '{path}' не найден" + MissingLibraryHint(path), file.File, use.Span);
			return;
		}

		if (use.Wildcard)
		{
			ImportAll(file.Scope, user, native, file, file.Module, use.Span);
			if (user is not null && !file.ImportedModules.Contains(user)) file.ImportedModules.Add(user);
			return;
		}

		if (use.Items.Count == 0)
		{
			BindAlias(file, new ModuleAliasSymbol(use.Path[^1], path) { Module = user, NativeModule = native }, use);
			if (user is not null && !file.ImportedModules.Contains(user)) file.ImportedModules.Add(user);
			return;
		}

		foreach (var item in use.Items)
			ImportItem(file, use, path, user, native, item);
	}

	private void ImportItem(FileModel file, Syn.UseDecl use, string path, ModuleSymbol? user, NativeModule? native, string item)
	{
		bool found = false;
		bool hiddenPrivate = false;

		if (user is not null)
		{
			if (user.Scope.LookupLocal(item) is { } sym)
			{
				if (sym.Access == Syn.Access.Priv) hiddenPrivate = true;
				else { Bind(file.Scope, sym, file, file.Module, use); found = true; }
			}
			foreach (var p in user.ExtensionProperties.Where(p => p.Name == item && p.Access != Syn.Access.Priv))
			{
				file.ImportedExtensionProperties.Add(p);
				found = true;
			}
		}

		if (native is not null)
		{
			if (native.Types.TryGetValue(item, out var nt))
			{
				Bind(file.Scope, new NativeTypeBinding(item, nt), file, file.Module, use);
				found = true;
			}
			var fns = native.Functions.Where(f => f.Name == item && !IsValueMember(f)).ToList();
			if (fns.Count > 0)
			{
				Bind(file.Scope, ctx.Binder.BindGroup(item, fns), file, file.Module, use);
				found = true;
			}
			// статические свойства/поля/константы модуля
			if (native.Functions.FirstOrDefault(f => f.Name == item && IsValueMember(f)) is { } valueMember)
			{
				Bind(file.Scope, ctx.Binder.Bind(valueMember), file, file.Module, use);
				found = true;
			}
		}

		if (!found)
		{
			// `use a[b]` где `a.b` — модуль: даёт алиас.
			string sub = path + "." + item;
			var subUser = ctx.Modules.Find(sub);
			var subNative = Natives.FindModule(sub);
			if (subUser is not null || subNative is not null)
			{
				BindAlias(file, new ModuleAliasSymbol(item, sub) { Module = subUser, NativeModule = subNative }, use);
				if (subUser is not null && !file.ImportedModules.Contains(subUser)) file.ImportedModules.Add(subUser);
				return;
			}
		}

		if (!found)
		{
			ctx.Error(Krn.ImportItemNotFound,
				hiddenPrivate ? $"'{item}' в модуле '{path}' приватный" : $"модуль '{path}' не содержит '{item}'",
				file.File, use.Span);
		}
	}

	// ------------------------------------------------------------ привязка

	private void BindAlias(FileModel file, ModuleAliasSymbol alias, Syn.UseDecl use)
	{
		if (file.Scope.LookupLocal(alias.Name) is ModuleAliasSymbol same && same.Path == alias.Path) return;
		if (file.Module.Scope.LookupLocal(alias.Name) is { } own)
		{
			ctx.Error(Krn.ImportConflict, $"имя '{alias.Name}' конфликтует с объявлением в модуле '{file.Module.Name}' ({own.Kind})", file.File, use.Span);
			return;
		}
		if (file.Scope.Declare(alias) is { } existing)
			ctx.Error(Krn.ImportConflict, $"имя '{alias.Name}' уже импортировано ({existing.FullName})", file.File, use.Span);
	}

	private void ImportAll(Scope target, ModuleSymbol? user, NativeModule? native, FileModel? file, ModuleSymbol? own, Kern.Core.TextSpan span)
	{
		if (user is not null)
			foreach (var sym in user.Scope.Symbols.Values.Where(s => s.Access != Syn.Access.Priv).ToList())
				Bind(target, sym, file, own, null, span);

		if (native is not null)
		{
			foreach (var (name, nt) in native.Types)
				Bind(target, new NativeTypeBinding(name, nt), file, own, null, span);
			foreach (var group in native.Functions.Where(f => !IsValueMember(f)).GroupBy(f => f.Name))
				Bind(target, ctx.Binder.BindGroup(group.Key, group), file, own, null, span);
			foreach (var value in native.Functions.Where(IsValueMember).DistinctBy(f => f.Name))
				Bind(target, ctx.Binder.Bind(value), file, own, null, span);
		}
	}

	private static bool IsValueMember(NativeMember m) => m.Kind is NativeMemberKind.Property or NativeMemberKind.Field;

	private void Bind(Scope target, Symbol sym, FileModel? file, ModuleSymbol? own, Syn.UseDecl? use) =>
		Bind(target, sym, file, own, use, use?.Span ?? default);

	/// <summary>
	/// Кладёт символ в область. file == null — молчаливый режим (prelude): конфликты игнорируются, первый побеждает.
	/// own — модуль файла: его объявления с тем же именем конфликтуют с импортом (функции — объединяются).
	/// </summary>
	private void Bind(Scope target, Symbol sym, FileModel? file, ModuleSymbol? own, Syn.UseDecl? use, Kern.Core.TextSpan span)
	{
		string name = sym.Name;
		var existing = target.LookupLocal(name);
		var ownSym = own?.Scope.LookupLocal(name);

		if (sym is OverloadSet group)
		{
			if (ownSym is not null and not OverloadSet) { Conflict(file, name, span, ownSym); return; }
			switch (existing)
			{
				case null:
					var sources = new List<OverloadSet>();
					if (ownSym is OverloadSet ownGroup && !ReferenceEquals(ownGroup, group)) sources.Add(ownGroup);
					sources.Add(group);
					target.Set(new ImportedOverloadSet(name, sources) { Container = group.Container });
					return;
				case ImportedOverloadSet merged:
					merged.AddSource(group);
					return;
				case OverloadSet single when ReferenceEquals(single, group):
					return;
				default:
					Conflict(file, name, span, existing);
					return;
			}
		}

		if (ownSym is not null && !ReferenceEquals(ownSym, sym)) { Conflict(file, name, span, ownSym); return; }
		if (existing is null) { target.Set(sym); return; }
		if (ReferenceEquals(existing, sym)) return;
		if (existing is NativeTypeBinding a && sym is NativeTypeBinding b && ReferenceEquals(a.Native, b.Native)) return;
		Conflict(file, name, span, existing);
	}

	private void Conflict(FileModel? file, string name, Kern.Core.TextSpan span, Symbol existing)
	{
		if (file is null) return;
		ctx.Error(Krn.ImportConflict, $"импорт '{name}' конфликтует с уже известным именем ({existing.FullName})", file.File, span);
	}

	/// <summary>Модули внешних библиотек (libs/): если их нет в каталоге — подсказать, как собрать и где kernc ищет DLL.</summary>
	private static string MissingLibraryHint(string module)
	{
		string? lib =
			module == "kern.db" || module.StartsWith("kern.db.", StringComparison.Ordinal) ? "Kern.Lib.Db"
			: module == "kern.ui" || module.StartsWith("kern.ui.", StringComparison.Ordinal) ? "Kern.Lib.Ui"
			: module == "kern.ffi" ? "Kern.Lib.Ffi"
			: module == "kern.transpile" ? "Kern.Lib.Transpile"
			: module == "kern.di" ? "Kern.Lib.Di"
			: module == "kern.backend" || module.StartsWith("kern.backend.", StringComparison.Ordinal) ? "Kern.Lib.Backend"
			: null;
		return lib is null ? "" :
			$". Это внешняя библиотека {lib}: соберите её (`dotnet build libs/Kern.Libs.slnx`) — kernc ищет DLL в libs/_out репозитория, "
			+ "в папке libs рядом с kernc, в ~/.kern/libs, в KERN_LIBS и в `libs = [...]` из kern.toml";
	}
}
