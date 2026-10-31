namespace Kern.Compiler.Semantics;

/// <summary>Коды диагностик привязки тел (KRN2xxx). Продолжение <see cref="Krn"/> (KRN1xxx — объявления и разрешение перегрузок).</summary>
public static partial class Krn
{
	// 20xx — типы и значения
	public const string TypeMismatch = "KRN2001";
	public const string NotCallable = "KRN2002";
	public const string MemberNotFound = "KRN2003";
	public const string InstanceMemberInStaticContext = "KRN2004";
	public const string InaccessibleMember = "KRN2005";
	public const string NotAValue = "KRN2006";
	public const string CannotInferVariableType = "KRN2007";
	public const string DuplicateLocal = "KRN2008";
	public const string NoOperator = "KRN2009";
	public const string NotAssignable = "KRN2010";
	public const string NumberLiteralOutOfRange = "KRN2011";
	public const string ThisUnavailable = "KRN2012";
	public const string AbstractInstantiation = "KRN2013";
	public const string TupleItemNotFound = "KRN2014";
	public const string IndexNotSupported = "KRN2015";
	public const string DelegateCallMismatch = "KRN2016";
	public const string AmbiguousFunctionRef = "KRN2017";
	public const string ParentMemberNotFound = "KRN2018";
	public const string InvalidTypeOf = "KRN2019";
	public const string CollectionTypeInference = "KRN2020";

	// 21xx — управление потоком
	public const string ConditionNotBool = "KRN2101";
	public const string IfWithoutElse = "KRN2102";
	public const string ReturnTypeMismatch = "KRN2103";
	public const string MissingReturn = "KRN2104";
	/// <summary>Вторичный конструктор типа с первичным конструктором не вызывает другой конструктор (`: $(...)`).</summary>
	public const string CtorMustChain = "KRN2190";
	public const string StopOutsideLoop = "KRN2105";
	public const string NextOutsideLoop = "KRN2106";
	public const string AwaitOutsideAsync = "KRN2107";
	public const string AwaitNotTask = "KRN2108";
	public const string NotIterable = "KRN2109";
	public const string AsyncLoopOutsideAsync = "KRN2308";
	public const string NotAsyncStream = "KRN2309";
	public const string LoopResultMismatch = "KRN2110";
	public const string CannotInferReturnType = "KRN2111";
	public const string BranchTypeMismatch = "KRN2112";

	// 22xx — паттерны и match
	public const string PatternTypeMismatch = "KRN2201";
	public const string RefutablePattern = "KRN2202";
	public const string DestructureCountMismatch = "KRN2203";
	public const string InvalidPattern = "KRN2204";
	public const string NonExhaustiveMatch = "KRN2205";
	public const string UnreachableArm = "KRN2206";

	// 23xx — flow-анализ
	public const string SingleAssignRepeated = "KRN2301";
	public const string UseBeforeAssign = "KRN2302";
	public const string MaybeNone = "KRN2303";
	public const string UnreachableCode = "KRN2304";
	public const string NonNullOnNonNullable = "KRN2305";
	public const string SafeCallOnNonNullable = "KRN2306";
	public const string SingleAssignInLoop = "KRN2307";
	public const string SingleAssignCaptured = "KRN2308";
	public const string ImmutableUnitMutation = "KRN2309";

	// 24xx — точка входа
	public const string InvalidMain = "KRN2401";
	public const string DuplicateMain = "KRN2402";
	public const string MissingMain = "KRN2403";
}
