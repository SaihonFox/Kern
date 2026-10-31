using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record ExtensionPropertyDeclaration(
	TypeSyntax ReceiverType,
	string Name,
	TypeSyntax PropertyType,
	ExpressionNode Body,
	TextSpan Span,
	Accessibility Accessibility = Accessibility.Public) : DeclarationNode(Span);
