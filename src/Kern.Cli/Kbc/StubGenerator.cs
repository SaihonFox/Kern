using System.Text;
using Kern.Cli.Build;
using Kern.Compiler.Syntax;

namespace Kern.Cli.Kbc;

/// <summary>
/// Интерфейсный исходник библиотеки <c>.kbc</c> (spec round 25): копия файла, где тела
/// функций/операторов/конструкторов/аксессоров заменены на <c>panic("kbc")</c>, а
/// <c>main</c> вырезан. Такой файл компилируется потребителем как обычный модуль и даёт
/// типы/перегрузки для binder'а; настоящий код подставляет <see cref="KbcLinker"/>.
/// </summary>
internal static class StubGenerator
{
	private const string PanicBody = "{ panic(\"kbc\") }";
	private const string PanicExpression = "panic(\"kbc\")";

	public static string Generate(string text, string path)
	{
		var parsed = Parser.Parse(text);
		if (parsed.Diagnostics.Any(diagnostic => diagnostic.IsError))
		{
			throw new BuildException(
				$"{path}: не удалось построить интерфейс библиотеки: " +
				string.Join("; ", parsed.Diagnostics.Where(item => item.IsError).Select(item => item.Message)));
		}

		var edits = new List<(int Start, int Length, string Replacement)>();
		foreach (DeclarationNode declaration in parsed.Tree.Declarations)
			VisitTopLevel(declaration, text, edits);
		if (edits.Count == 0)
			return text;

		var builder = new StringBuilder(text);
		foreach (var (start, length, replacement) in edits.OrderByDescending(edit => edit.Start))
			builder.Remove(start, length).Insert(start, replacement);
		return builder.ToString();
	}

	private static void VisitTopLevel(
		DeclarationNode declaration,
		string text,
		List<(int Start, int Length, string Replacement)> edits)
	{
		switch (declaration)
		{
			case FunctionDeclaration function:
				if (function.Name == "main")
				{
					edits.Add((function.Span.Start, function.Span.Length, string.Empty));
					return;
				}

				ReplaceFunctionBody(function, edits);
				break;
			case TypeDeclaration type:
				foreach (var member in type.DeclaredMembers)
					VisitMember(member, text, edits);
				break;
			case UnitDeclaration unit:
				foreach (var member in unit.DeclaredMembers)
					VisitMember(member, text, edits);
				break;
			case ExtensionOperatorDeclaration extensionOperator:
				if (extensionOperator.ReturnType is not null && extensionOperator.Body is { } extensionBody)
					ReplaceBody(extensionBody, edits);
				break;
			case GlobalFieldDeclaration global when !global.IsRequired:
				StripInitializer(global.Initializer, text, edits);
				break;
		}
	}

	private static void VisitMember(
		SyntaxNode member,
		string text,
		List<(int Start, int Length, string Replacement)> edits)
	{
		switch (member)
		{
			case FunctionDeclaration function:
				ReplaceFunctionBody(function, edits);
				break;
			case ConstructorMemberNode constructor:
				if (constructor.Body is { } constructorBody)
					ReplaceBody(constructorBody, edits);
				break;
			case PropertyMemberNode property:
				if (property.ExpressionBody is { } expression)
					edits.Add((expression.Span.Start, expression.Span.Length, PanicExpression));
				if (property.IsStatic && property.Initializer is { } propertyInitializer)
					StripInitializer(propertyInitializer, text, edits);
				foreach (PropertyAccessorNode accessor in property.Accessors)
					if (accessor.Body is { } accessorBody)
						ReplaceBody(accessorBody, edits);
				break;
			case FieldMemberNode field when field.IsStatic && !field.IsRequired:
				StripInitializer(field.Initializer, text, edits);
				break;
			case OperatorMemberNode operatorMember:
				if (operatorMember.ReturnType is not null && operatorMember.Body is { } operatorBody)
					ReplaceBody(operatorBody, edits);
				break;
		}
	}

	/// <summary>
	/// Тело функции с объявленным результатом заменяется: без <c>-&gt; T</c> сигнатура
	/// выводится из тела, поэтому такие функции остаются как есть (spec round 25).
	/// </summary>
	private static void ReplaceFunctionBody(
		FunctionDeclaration function,
		List<(int Start, int Length, string Replacement)> edits)
	{
		if (function.IsAbstract || function.ReturnType is null)
			return;
		ReplaceBody(function.Body, edits);
	}

	private static void ReplaceBody(
		SyntaxNode body,
		List<(int Start, int Length, string Replacement)> edits)
	{
		if (body.Span.Length == 0)
			return;
		edits.Add((body.Span.Start, body.Span.Length, body is BlockStatement ? PanicBody : PanicExpression));
	}

	/// <summary>
	/// Убирает <c>= expr</c> у статического поля/глобала: инициализаторы — реализация
	/// библиотеки, их выполнит связанный static-init, а не заглушка потребителя.
	/// </summary>
	private static void StripInitializer(
		ExpressionNode? initializer,
		string text,
		List<(int Start, int Length, string Replacement)> edits)
	{
		if (initializer is null)
			return;
		int index = initializer.Span.Start - 1;
		while (index >= 0 && char.IsWhiteSpace(text[index]))
			index--;
		if (index < 0 || text[index] != '=')
			return;
		edits.Add((index, initializer.Span.End - index, string.Empty));
	}
}
