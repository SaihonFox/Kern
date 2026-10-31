using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public interface ITypeMember
{
	TextSpan Span { get; }

	/// <summary>Модификатор доступа; у членов типа по умолчанию <see cref="Accessibility.Private"/>.</summary>
	Accessibility Accessibility { get; }

	/// <summary>Переопределение члена родителя (<c>~</c>).</summary>
	bool IsOverride { get; }
}
