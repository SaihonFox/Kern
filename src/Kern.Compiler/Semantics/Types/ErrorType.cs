namespace Kern.Compiler.Semantics.Types;

/// <summary>
/// Тип-ошибка. Синглтон: все последующие диагностики сходятся в него, чтобы не порождать каскад.
/// </summary>
public sealed record ErrorType : KernType
{
	private ErrorType() : base(TypeKind.Error) { }

	public static ErrorType Instance { get; } = new();

	public override string ToString() => "<error>";
}
