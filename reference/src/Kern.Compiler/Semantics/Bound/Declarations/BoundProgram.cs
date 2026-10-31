using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Project;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Core;

namespace Kern.Compiler.Semantics.Bound.Declarations;

/// <summary>
/// Функция/метод/оператор/аксессор/расширение с привязанным телом.
/// <see cref="Body"/> == null — тела нет (абстрактный метод, выбор родителя `fun $[A].foo`, нативная функция).
/// <see cref="ReturnType"/> — итоговый возвращаемый тип (выведенный, если в исходнике не указан).
/// <see cref="IsSynthesized"/> — тело построено компилятором (аксессоры автосвойства через backing-поле).
/// </summary>
public sealed record BoundFunction(
	FunctionSymbol Symbol,
	IReadOnlyList<ParameterSymbol> Parameters,
	BoundExpr? Body,
	TypeSymbol ReturnType,
	bool IsSynthesized = false,
	BoundCtorChain? Chain = null);

/// <summary>`ctor(...) : $(args)` — вызов другого конструктора того же типа; после него инициализаторы типа не выполняются повторно.</summary>
public sealed record BoundCtorChain(FunctionSymbol Constructor, IReadOnlyList<BoundExpr> Arguments, IReadOnlyList<int>? EvaluationOrder);

/// <summary>Инициализатор поля: `i32 x = 5`, поле из параметра первичного конструктора, backing-поле `{ get; set } = 5`.</summary>
public sealed record BoundFieldInitializer(FieldSymbol Field, BoundExpr Value);

/// <summary>
/// Вызов конструктора родителя из списка наследования `[A(x * 2)]`. Аргументы привязаны в области параметров
/// первичного конструктора. <see cref="Constructor"/> == null — у родителя нет объявленных конструкторов.
/// </summary>
public sealed record BoundParentInit(
	ParentInfo Parent,
	FunctionSymbol? Constructor,
	IReadOnlyList<BoundExpr> Arguments,
	IReadOnlyList<int>? EvaluationOrder);

/// <summary>Элемент enum: <see cref="Construction"/> — создание значения с аргументами (null — enum без параметров), <see cref="Value"/> — `green = 5`.</summary>
public sealed record BoundEnumEntry(FieldSymbol Field, BoundNew? Construction, BoundExpr? Value);

/// <summary>Конструктор: <see cref="Function"/> — параметры и тело (у первичного тела нет).</summary>
public sealed record BoundConstructor(FunctionSymbol Symbol, BoundFunction Function);

/// <summary>Тип со всеми привязанными членами.</summary>
public sealed record BoundTypeDeclaration(
	TypeDeclSymbol Type,
	IReadOnlyList<BoundParentInit> ParentInits,
	IReadOnlyList<BoundFieldInitializer> FieldInitializers,
	IReadOnlyList<BoundConstructor> Constructors,
	IReadOnlyList<BoundFunction> Functions,
	IReadOnlyList<BoundFunction> Accessors,
	IReadOnlyList<BoundEnumEntry> EnumEntries);

/// <summary>Глобальная переменная модуля (`i32 counter = 0` на верхнем уровне).</summary>
public sealed record BoundGlobal(FieldSymbol Field, BoundExpr? Initializer);

/// <summary>Содержимое модуля: глобальные переменные, свободные функции (включая расширения и операторы-расширения) и типы.</summary>
public sealed record BoundModuleBody(
	ModuleSymbol Module,
	IReadOnlyList<BoundGlobal> Globals,
	IReadOnlyList<BoundFunction> Functions,
	IReadOnlyList<BoundTypeDeclaration> Types);

/// <summary>Результат семантического анализа: привязанное дерево всех модулей проекта (std включён) и диагностики.</summary>
public sealed class BoundProgram(
	ProjectModel project,
	IReadOnlyList<BoundModuleBody> modules,
	BoundFunction? entryPoint,
	IReadOnlyDictionary<FunctionSymbol, BoundFunction> functions)
{
	public ProjectModel Project { get; } = project;
	public IReadOnlyList<BoundModuleBody> Modules { get; } = modules;
	/// <summary>Функция `main` проекта (null — не найдена или ошибка в её сигнатуре; диагностика уже сообщена).</summary>
	public BoundFunction? EntryPoint { get; } = entryPoint;
	/// <summary>Все привязанные функции по символу (методы, аксессоры, конструкторы, операторы, расширения).</summary>
	public IReadOnlyDictionary<FunctionSymbol, BoundFunction> Functions { get; } = functions;

	public DiagnosticBag Diagnostics => Project.Diagnostics;
	public bool HasErrors => Diagnostics.HasErrors;
}
