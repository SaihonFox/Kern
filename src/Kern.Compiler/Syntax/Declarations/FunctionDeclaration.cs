using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record FunctionDeclaration(
	string Name,
	IReadOnlyList<ParameterNode> Parameters,
	BlockStatement Body,
	TextSpan Span,
	TypeSyntax? ReturnType = null,
	IReadOnlyList<AttributeNode>? Attributes = null,
	bool IsAbstract = false,
	bool IsStatic = false,
	Accessibility Accessibility = Accessibility.Private,
	bool IsOverride = false,
	bool IsAsync = false) : DeclarationNode(Span), ITypeMember
{
	public IReadOnlyList<AttributeNode> DeclaredAttributes { get; } = Attributes ?? [];

	/// <summary>Полные generic-параметры объявления (с ограничениями); для не-generic функций пусто.</summary>
	public IReadOnlyList<GenericParameterNode> GenericParameters { get; init; } = [];

	/// <summary>Число generic-параметров; вычисляется из <see cref="GenericParameters"/>.</summary>
	public int GenericArity => GenericParameters.Count;
}
