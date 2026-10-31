using Kern.Compiler.Semantics.Binding.Flow;
using Kern.Compiler.Semantics.Binding.Members;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Project;
using Kern.Compiler.Semantics.Scopes;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Core;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Binding.Core;

/// <summary>
/// Привязчик тел функций одного файла. Разбит на partial-классы по темам (Expressions/, Patterns/).
/// Один экземпляр на файл; на каждое тело вызывается <see cref="BeginFunction"/>.
/// </summary>
public sealed partial class Binder
{
	private readonly Dictionary<Symbol, FunctionFrame> _owner = [];

	public Binder(ProjectModel project, ExtensionIndex extensions, FileModel file)
	{
		Project = project;
		Extensions = extensions;
		File = file;
		Flow = new FlowAnalysis(this);
		Members = new MemberLookup(project, extensions);
		Frame = new FunctionFrame(null, false);
		Scope = file.Scope;
	}

	/// <summary>Оркестратор тел: нужен для вывода типов «по требованию» (возвращаемый тип функции, тип поля с `var`).</summary>
	public Bodies.BodyBinder? Owner { get; init; }

	public ProjectModel Project { get; }
	public ExtensionIndex Extensions { get; }
	public FileModel File { get; }
	public MemberLookup Members { get; }
	public FlowAnalysis Flow { get; private set; }
	public FunctionFrame Frame { get; private set; }
	public Scope Scope { get; private set; }

	/// <summary>Тип, чьё тело привязывается (null — функция верхнего уровня).</summary>
	public TypeDeclSymbol? CurrentType { get; private set; }
	public FunctionSymbol? CurrentFunction { get; private set; }
	/// <summary>Тип `$` (self): тип объявления либо получатель расширения; null — недоступен.</summary>
	public TypeSymbol? SelfType { get; private set; }

	private DiagnosticBag Diagnostics => Project.Diagnostics;
	private SourceFile SourceFile => File.File;

	// ------------------------------------------------------------ диагностики

	public void Error(string code, string message, TextSpan span) => Diagnostics.Error(code, message, SourceFile, span);
	public void Warn(string code, string message, TextSpan span) => Diagnostics.Warning(code, message, SourceFile, span);

	public BoundErrorExpr ErrorExpr(string code, string message, TextSpan span)
	{
		Error(code, message, span);
		return new BoundErrorExpr(span, message);
	}

	// ------------------------------------------------------------ области и локальные

	public void PushScope(string description = "block") => Scope = new Scope(Scope, description);

	public void PopScope() => Scope = Scope.Parent ?? Scope;

	public LocalSymbol DeclareLocal(string name, TypeSymbol? type, TextSpan span, bool singleAssign = false)
	{
		var local = new LocalSymbol(name, type) { File = SourceFile, Span = span, IsSingleAssign = singleAssign };
		if (name != "_")
		{
			if (Scope.LookupLocal(name) is not null)
				Error(Krn.DuplicateLocal, $"имя '{name}' уже объявлено в этой области", span);
			else
				Scope.Declare(local);
		}
		_owner[local] = Frame;
		return local;
	}

	public void DeclareParameter(ParameterSymbol p)
	{
		if (Scope.LookupLocal(p.Name) is null) Scope.Declare(p);
		_owner[p] = Frame;
	}

	/// <summary>Фиксирует использование переменной: если она принадлежит внешнему кадру — захват во всех промежуточных лямбдах.</summary>
	public void NoteUse(Symbol variable, TypeSymbol type, TextSpan span, bool written)
	{
		if (!_owner.TryGetValue(variable, out var owner) || ReferenceEquals(owner, Frame)) return;
		for (var f = Frame; f is not null && !ReferenceEquals(f, owner); f = f.Parent)
		{
			if (!f.Captures.TryGetValue(variable, out var info))
				f.Captures[variable] = info = new CaptureInfo(variable, type, span);
			if (written) info.WrittenInside = true;
		}
	}

	// ------------------------------------------------------------ контекст функции

	/// <summary>Сбрасывает состояние для нового тела. Создаёт корневую область с параметрами.</summary>
	public void BeginFunction(
		TypeDeclSymbol? type, FunctionSymbol? function, TypeSymbol? selfType, IReadOnlyList<ParameterSymbol> parameters,
		TypeSymbol? returnType, bool isAsync, Scope? baseScope = null)
	{
		CurrentType = type;
		CurrentFunction = function;
		SelfType = selfType;
		Flow = new FlowAnalysis(this);
		Frame = new FunctionFrame(null, false) { IsAsync = isAsync, ReturnType = returnType, Description = function?.Name ?? "функция" };
		_owner.Clear();
		Scope = new Scope(baseScope ?? type?.MemberScope ?? FileScopeOf(function), "function");
		Frame.RootScope = Scope;
		if (function is not null)
			foreach (var tp in function.TypeParams) Scope.Declare(tp);
		foreach (var p in parameters) DeclareParameter(p);
	}

	private Scope FileScopeOf(FunctionSymbol? function) => File.Scope;

	/// <summary>`$` и неявный this доступны.</summary>
	public bool HasThis => SelfType is not null && !(CurrentFunction is { IsStatic: true, ExtensionOf: null });

	// ------------------------------------------------------------ вспомогательные

	public static bool IsVoid(TypeSymbol t) => t is PrimitiveType { Kind: Kern.Bytecode.PrimKind.Void };

	public TypeSymbol ResolveType(Syn.TypeRef? type) => Project.Resolver.Resolve(type, Scope, SourceFile);
}
