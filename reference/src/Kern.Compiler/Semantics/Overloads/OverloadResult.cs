using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.Semantics.Overloads;

public enum OverloadStatus
{
	/// <summary>Единственный лучший кандидат: <see cref="OverloadResult.Selected"/>.</summary>
	Success,
	/// <summary>Список кандидатов пуст.</summary>
	NoCandidates,
	/// <summary>Кандидаты есть, но ни один не применим: причины в <see cref="OverloadResult.Rejected"/>.</summary>
	NoApplicable,
	/// <summary>Несколько применимых кандидатов без лучшего: они в <see cref="OverloadResult.Ambiguous"/>.</summary>
	Ambiguous,
}

/// <summary>Почему кандидат не применим.</summary>
public enum RejectionReason
{
	GenericArity,
	TooManyArguments,
	MissingArgument,
	UnknownNamedArgument,
	DuplicateArgument,
	PositionalAfterNamed,
	/// <summary>Расширение вызвано без получателя либо получатель не приводится к расширяемому типу.</summary>
	ReceiverMismatch,
	ArgumentConversion,
	/// <summary>Не удалось вывести generic-аргументы (нет информации или конфликт границ).</summary>
	InferenceFailed,
	ConstraintViolated,
}

/// <summary>Неприменимый кандидат с причиной. <see cref="ArgumentIndex"/> — индекс аргумента (с 0), если причина в нём.</summary>
public sealed record RejectedCandidate(FunctionSymbol Function, RejectionReason Reason, string Message, int? ArgumentIndex = null);

/// <summary>Использованное неявное преобразование аргумента; <see cref="IsConstant"/> — константное приведение числового литерала.</summary>
public sealed record ArgumentConversion(ConversionKind Kind, bool IsConstant)
{
	public static readonly ArgumentConversion Identity = new(ConversionKind.Identity, false);
	public bool NeedsNode => Kind != ConversionKind.Identity;
}

/// <summary>Чем заполняется параметр.</summary>
public enum ParameterSlotKind
{
	/// <summary>Одним аргументом.</summary>
	Argument,
	/// <summary>Значением по умолчанию (аргумент пропущен).</summary>
	Default,
	/// <summary>Variadic `..args` в расширенной форме: все аргументы упаковываются в list&lt;T&gt; (возможно, пустой).</summary>
	VariadicPack,
}

/// <summary>
/// Что подставить на место параметра <see cref="Parameter"/>. <see cref="ParameterType"/> — тип, к которому приводятся аргументы
/// (после подстановки generic-аргументов): для <see cref="ParameterSlotKind.VariadicPack"/> — тип ЭЛЕМЕНТА, иначе тип параметра
/// (для variadic в нормальной форме — list&lt;T&gt;). <see cref="ArgumentIndices"/> — индексы аргументов запроса (для Default пусто),
/// <see cref="Conversions"/> — преобразование каждого из них.
/// </summary>
public sealed record ParameterSlot(
	ParameterSymbol Parameter,
	ParameterSlotKind Kind,
	TypeSymbol ParameterType,
	IReadOnlyList<int> ArgumentIndices,
	IReadOnlyList<ArgumentConversion> Conversions);

/// <summary>Применимый кандидат: всё, что нужно биндеру для построения <c>BoundCall</c>.</summary>
public sealed class ResolvedCall
{
	public required FunctionSymbol Function { get; init; }

	/// <summary>Generic-аргументы самой функции в порядке <see cref="FunctionSymbol.TypeParams"/> (явные или выведенные).</summary>
	public required IReadOnlyList<TypeSymbol> TypeArgs { get; init; }

	/// <summary>Generic-аргументы типа-владельца (по порядку его параметров; пусто, если владелец не generic или неизвестен).</summary>
	public required IReadOnlyList<TypeSymbol> OwnerTypeArgs { get; init; }

	/// <summary>Полная подстановка: владелец + параметры функции + параметры расширяемого типа.</summary>
	public required IReadOnlyDictionary<TypeParamSymbol, TypeSymbol> Substitution { get; init; }

	/// <summary>По одному слоту на параметр функции в позиционном порядке.</summary>
	public required IReadOnlyList<ParameterSlot> Slots { get; init; }

	/// <summary>Для каждого аргумента запроса — индекс параметра, которому он достался.</summary>
	public required IReadOnlyList<int> ArgumentToParameter { get; init; }

	/// <summary>Индексы параметров в порядке вычисления аргументов (по исходнику); null — совпадает с позиционным.</summary>
	public IReadOnlyList<int>? EvaluationOrder { get; init; }

	/// <summary>Variadic-параметр заполнен расширенной формой (`f(1, 2, 3)`), а не готовым списком.</summary>
	public bool IsExpandedForm { get; init; }

	/// <summary>Приведение получателя к расширяемому типу (только для расширений).</summary>
	public ArgumentConversion? ReceiverConversion { get; init; }

	/// <summary>Возвращаемый тип с подстановкой (void, если не указан).</summary>
	public required TypeSymbol ReturnType { get; init; }

	public int DefaultsUsed => Slots.Count(s => s.Kind == ParameterSlotKind.Default);

	/// <summary>Были ли выводимые/явные generic-параметры (для правила «не-generic лучше generic»).</summary>
	public bool IsGenericInstantiation { get; init; }

	// ---- служебное для сравнения кандидатов

	/// <summary>Целевой тип каждого аргумента запроса (после подстановки).</summary>
	public required IReadOnlyList<TypeSymbol> ArgumentTargets { get; init; }
	/// <summary>То же до подстановки (для сравнения «специфичности»).</summary>
	public required IReadOnlyList<TypeSymbol> DeclaredTargets { get; init; }
	public TypeSymbol? ReceiverTarget { get; init; }
	public TypeSymbol? DeclaredReceiverTarget { get; init; }

	public override string ToString() => Function.ToDisplayString();
}

/// <summary>Результат разрешения перегрузки.</summary>
public sealed class OverloadResult
{
	public required OverloadStatus Status { get; init; }
	public ResolvedCall? Selected { get; init; }
	/// <summary>Равноценные лучшие кандидаты при <see cref="OverloadStatus.Ambiguous"/>.</summary>
	public IReadOnlyList<ResolvedCall> Ambiguous { get; init; } = [];
	/// <summary>Все применимые кандидаты (после отсева переопределённых базовых).</summary>
	public IReadOnlyList<ResolvedCall> Applicable { get; init; } = [];
	public IReadOnlyList<RejectedCandidate> Rejected { get; init; } = [];
	/// <summary>
	/// В аргументах есть выражения с ошибкой. Тогда неоднозначность/неприменимость не порождает диагностик
	/// (ошибка уже сообщена); при неоднозначности статус — <see cref="OverloadStatus.Success"/> и <see cref="Selected"/> —
	/// первый из лучших, чтобы привязка шла дальше.
	/// </summary>
	public bool SuppressedByErrorArguments { get; init; }

	public bool IsSuccess => Status == OverloadStatus.Success;
}
