namespace Kern.Compiler.Semantics;

/// <summary>Коды диагностик семантического анализа (KRN1xxx).</summary>
public static partial class Krn
{
	// 10xx — разрешение имён и типов
	public const string TypeNotFound = "KRN1001";
	public const string FunctionNotFound = "KRN1002";
	public const string WrongTypeArgCount = "KRN1003";
	public const string NotAType = "KRN1004";
	public const string ConstraintViolated = "KRN1005";
	public const string NameNotFound = "KRN1006";
	public const string TypeArgsOnNonGeneric = "KRN1007";
	public const string AmbiguousName = "KRN1008";

	// 11xx — импорты
	public const string ImportNotFound = "KRN1101";
	public const string ImportItemNotFound = "KRN1102";
	public const string ImportConflict = "KRN1103";
	public const string ImportWildcardUnsupported = "KRN1104";

	// 12xx — объявления
	public const string DuplicateType = "KRN1201";
	public const string DuplicateSignature = "KRN1202";
	public const string MemberNameConflict = "KRN1203";
	public const string InheritanceCycle = "KRN1204";
	public const string ParentNotOpen = "KRN1205";
	public const string UnknownParentSelector = "KRN1206";
	public const string InvalidParent = "KRN1207";
	public const string VariadicNotLast = "KRN1208";
	public const string NotAnAttribute = "KRN1209";
	public const string AbstractInConcreteType = "KRN1210";
	public const string DuplicateParameter = "KRN1211";
	public const string DuplicateEnumEntry = "KRN1212";
	public const string DuplicateTypeParam = "KRN1213";
	public const string InvalidExtensionReceiver = "KRN1214";
	public const string DuplicateParent = "KRN1215";
	public const string DuplicateModuleName = "KRN1216";
	public const string DuplicateAccessor = "KRN1217";
	public const string AbstractWithBody = "KRN1218";
	public const string MissingBody = "KRN1219";
	public const string InvalidMemberHere = "KRN1220";
	public const string UnknownAttribute = "KRN1221";

	// 13xx — проект и источники
	public const string SourceNotFound = "KRN1301";
	public const string InvalidProjectFile = "KRN1302";
	public const string StdUnavailable = "KRN1303";
	public const string SourceReadError = "KRN1304";

	// 14xx — вызовы и разрешение перегрузок
	public const string NoApplicableOverload = "KRN1401";
	public const string AmbiguousCall = "KRN1402";
	public const string CannotInferTypeArgs = "KRN1403";
	public const string ArgumentMismatch = "KRN1404";
	// 15xx: макросы
	public const string MacroPanic = "KRN1501";
	public const string MacroError = "KRN1502";
	public const string MacroWarning = "KRN1503";
	public const string MacroRecursion = "KRN1504";
	public const string MacroSignature = "KRN1505";
}
