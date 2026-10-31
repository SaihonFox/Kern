using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record FieldMemberNode(
	TypeSyntax Type,
	string Name,
	bool IsRequired,
	ExpressionNode? Initializer,
	TextSpan Span,
	bool IsStatic = false,
	Accessibility Accessibility = Accessibility.Private,
	bool IsOverride = false) : SyntaxNode(Span), ITypeMember;
