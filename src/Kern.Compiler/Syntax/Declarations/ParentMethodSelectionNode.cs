using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record ParentMethodSelectionNode(
	string ParentType,
	string Name,
	IReadOnlyList<ParameterNode> Parameters,
	TypeSyntax? ReturnType,
	TextSpan Span,
	Accessibility Accessibility = Accessibility.Private,
	bool IsOverride = false) : SyntaxNode(Span), ITypeMember;
