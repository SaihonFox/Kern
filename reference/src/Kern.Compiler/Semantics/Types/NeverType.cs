using Kern.Bytecode;

namespace Kern.Compiler.Semantics.Types;

/// <summary>
/// Тип выражения, которое не возвращает управление: `ret`, `panic(...)`, ветка if/match, целиком состоящая из них.
/// Неявно приводится к любому типу (<see cref="Conversions.Classify"/> даёт Subtype) и не влияет на общий тип ветвей.
/// Также служит «неизвестной» половиной результата: `Ok(5)` без ожидаемого типа имеет тип <c>result&lt;i32, never&gt;</c>,
/// который приводится к любому <c>result&lt;i32, E&gt;</c>. В пуле типов стирается до object.
/// </summary>
public sealed class NeverType : TypeSymbol
{
	public static readonly NeverType Instance = new();
	private NeverType() { }
	public override TypeSig ToTypeSig() => TypeSig.Object;
	public override string ToDisplayString() => "never";
	public override bool Equals(TypeSymbol? other) => ReferenceEquals(this, other);
	public override int GetHashCode() => 4;
}
