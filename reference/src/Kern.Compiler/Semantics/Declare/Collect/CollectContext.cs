using Kern.Compiler.Semantics.Project;
using Kern.Compiler.Semantics.Scopes;
using Kern.Compiler.Semantics.Symbols;
using Kern.Core;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Declare.Collect;

/// <summary>Отложенное применение атрибутов: разрешается после того, как видны все типы и импорты.</summary>
public sealed record PendingAttributes(Symbol Target, IReadOnlyList<Syn.AttributeUse> Uses, Scope Scope, SourceFile File);

/// <summary>Общее состояние фаз сбора объявлений.</summary>
public sealed class CollectContext(
	ModuleTable modules, NativeBinder binder, TypeResolver resolver, DiagnosticBag diagnostics, Scope prelude)
{
	public ModuleTable Modules { get; } = modules;
	public NativeBinder Binder { get; } = binder;
	public TypeResolver Resolver { get; } = resolver;
	public DiagnosticBag Diagnostics { get; } = diagnostics;
	public Scope Prelude { get; } = prelude;

	public List<FileModel> Files { get; } = [];
	/// <summary>Все типы, включая вложенные, в порядке объявления (родитель раньше вложенных).</summary>
	public List<TypeDeclSymbol> Types { get; } = [];
	public List<PendingAttributes> PendingAttributes { get; } = [];

	public void Error(string code, string message, SourceFile file, TextSpan span) => Diagnostics.Error(code, message, file, span);
	public void Warning(string code, string message, SourceFile file, TextSpan span) => Diagnostics.Warning(code, message, file, span);

	public void QueueAttributes(Symbol target, IReadOnlyList<Syn.AttributeUse> uses, Scope scope, SourceFile file)
	{
		if (uses.Count > 0) PendingAttributes.Add(new PendingAttributes(target, uses, scope, file));
	}

	/// <summary>Файл (FileModel) по SourceFile — для фаз, работающих от символов.</summary>
	public FileModel FileOf(SourceFile file) => Files.First(f => ReferenceEquals(f.File, file));
}
