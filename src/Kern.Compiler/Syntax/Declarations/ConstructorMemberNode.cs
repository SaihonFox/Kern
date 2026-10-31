using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record ConstructorMemberNode(
	IReadOnlyList<ParameterNode> Parameters,
	ConstructorChainNode? Chain,
	SyntaxNode? Body,
	TextSpan Span,
	Accessibility Accessibility = Accessibility.Private,
	bool IsOverride = false) : SyntaxNode(Span), ITypeMember;
