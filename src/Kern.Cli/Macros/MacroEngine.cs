using Kern.Binding.Catalog;
using Kern.Bytecode;
using Kern.Cli.Infrastructure;
using Kern.Compiler.Syntax;
using Kern.Runtime.Macros;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Cli.Macros;

/// <summary>Ошибка/предупреждение макроса с местом применения.</summary>
internal sealed record MacroMessage(string Path, int Line, int Column, bool IsError, string Text);

/// <summary>Результат раскрытия макросов: исходники и диагностика.</summary>
internal sealed record MacroExpansion(
	IReadOnlyList<(string Path, string Text)> Files,
	IReadOnlyList<MacroMessage> Messages);

/// <summary>
/// Текстовое раскрытие макросов (spec round 22): файлы с <c>#[macro]</c> компилируются
/// отдельным мини-проектом и исполняются в VM до разбора основного проекта. Атрибутные
/// макросы добавляют члены/верхнеуровневый код, макросы-выражения <c>имя!(…)</c>
/// раскрываются проходами (до 16). Сгенерированный атрибутами код повторно не раскрывается.
/// </summary>
internal static class MacroEngine
{
	private const int MaxPasses = 16;

	private sealed record MacroDefinition(string Name, bool IsExpression, FunctionDef Function);

	public static MacroExpansion Expand(
		IReadOnlyList<(string Path, string Text)> files,
		string moduleName,
		Func<IReadOnlyList<(string Path, string Text)>, CompileResult> compileMacros)
	{
		var parsed = files
			.Select(file => (file.Path, file.Text, Tree: Parser.Parse(file.Text)))
			.ToList();
		var macroFiles = parsed
			.Where(file => HasMacroDefinitions(file.Tree.Tree))
			.Select(file => (file.Path, file.Text))
			.ToList();
		if (macroFiles.Count == 0)
			return new MacroExpansion(files, []);

		CompileResult compiled = compileMacros(macroFiles);
		if (!compiled.Success)
		{
			var errors = compiled.ProjectMessages.Count > 0
				? compiled.ProjectMessages
				: [compiled.FailureMessage ?? "не удалось скомпилировать файлы макросов"];
			return new MacroExpansion(files, [.. errors.Select(message =>
				new MacroMessage("macros", 0, 0, true, message))]);
		}

		var interpreter = new Interpreter(compiled.Program!.Module, new Kern.Binding.Resolution.NativeResolver(compiled.Program.Catalog));
		var definitions = CollectDefinitions(compiled.Program.Module);
		var attributeMacros = definitions.Where(pair => !pair.Value.IsExpression).ToDictionary(pair => pair.Key, pair => pair.Value);
		var expressionMacros = definitions.Where(pair => pair.Value.IsExpression).ToDictionary(pair => pair.Key, pair => pair.Value);
		var messages = new List<MacroMessage>();
		var result = new List<(string Path, string Text)>(files.Count);

		foreach (var (path, original, tree) in parsed)
		{
			string text = original;
			if (expressionMacros.Count > 0)
				text = ExpandExpressions(text, path, expressionMacros, interpreter, messages);

			var treeAfter = Parser.Parse(text);
			var members = new Dictionary<int, List<string>>();
			var tops = new List<string>();
			ApplyAttributeMacros(text, path, treeAfter.Tree, attributeMacros, interpreter, members, tops, messages);

			var edits = new List<(int Start, int Length, string Replacement)>();
			edits.AddRange(FilterAttributes(text, attributeMacros.Keys.ToHashSet(StringComparer.Ordinal)));
			foreach (var (attributeStart, generated) in members)
			{
				var node = TypeAt(treeAfter.Tree, attributeStart);
				if (node is null)
					continue;
				string body = string.Join("\n", generated);
				string substring = text.Substring(node.Span.Start, node.Span.Length);
				int close = substring.LastIndexOf('}');
				if (close >= 0)
				{
					int position = node.Span.Start + close;
					edits.Add((position, 0, "\n" + body + "\n"));
				}
				else
				{
					edits.Add((node.Span.End, 0, " {\n" + body + "\n}"));
				}
			}

			if (tops.Count > 0)
				edits.Add((text.Length, 0, "\n\n" + string.Join("\n", tops) + "\n"));

			string expanded = Apply(text, edits);
			result.Add((path, expanded));
		}

		return new MacroExpansion(result, messages);
	}

	private static bool HasMacroDefinitions(CompilationUnit tree) =>
		tree.Declarations.OfType<FunctionDeclaration>().Any(function =>
			function.DeclaredAttributes.Any(attribute => attribute.Name == "macro"));

	private static Dictionary<string, MacroDefinition> CollectDefinitions(KbcModule module)
	{
		var result = new Dictionary<string, MacroDefinition>(StringComparer.Ordinal);
		foreach (FunctionDef function in module.FunctionPool)
		{
			if (function.OwnerType >= 0 || function.ParamCount != 1 || function.IsNative)
				continue;
			AttributeUse? macro = function.Attributes.FirstOrDefault(attribute => attribute.Name == "macro");
			if (macro is null)
				continue;
			bool isExpression = macro.Args.Count > 0 && macro.Args[0].AsStr == "expr";
			result[function.Name] = new MacroDefinition(function.Name, isExpression, function);
		}

		return result;
	}

	private static void ApplyAttributeMacros(
		string text,
		string path,
		CompilationUnit tree,
		IReadOnlyDictionary<string, MacroDefinition> macros,
		Interpreter interpreter,
		Dictionary<int, List<string>> members,
		List<string> tops,
		List<MacroMessage> messages)
	{
		foreach (DeclarationNode declaration in tree.Declarations)
		{
			IReadOnlyList<AttributeNode> attributes = declaration switch
			{
				TypeDeclaration type => type.DeclaredAttributes,
				UnitDeclaration unit => unit.DeclaredAttributes,
				_ => [],
			};
			if (attributes.Count == 0)
				continue;

			KernMacroDecl declarationInfo = BuildDeclaration(text, declaration);
			foreach (AttributeNode attribute in attributes)
			{
				if (attribute.Name == "derive")
				{
					foreach (string argument in attribute.Arguments)
					{
						if (macros.TryGetValue(argument, out MacroDefinition? derived))
							Invoke(derived, declarationInfo, [], text, path, declaration.Span.Start, interpreter, members, tops, messages);
					}

					continue;
				}

				if (macros.TryGetValue(attribute.Name, out MacroDefinition? macro))
					Invoke(macro, declarationInfo, attribute.Arguments, text, path, declaration.Span.Start, interpreter, members, tops, messages);
			}
		}
	}

	private static void Invoke(
		MacroDefinition macro,
		KernMacroDecl declaration,
		IReadOnlyList<string> arguments,
		string text,
		string path,
		int offset,
		Interpreter interpreter,
		Dictionary<int, List<string>> members,
		List<string> tops,
		List<MacroMessage> messages)
	{
		(int line, int column) = LineColumn(text, offset);
		var context = new KernMacroContext(declaration, macro.Name, path, line);
		context.Arguments.AddRange(arguments);
		try
		{
			KValue result = interpreter.RunValue(KValue.Obj(macro.Function), [KValue.Obj(new KNativeBox(context))]);
			if (result.Kind == ValueKind.Str && result.AsStr.Length > 0)
				context.Member(result.AsStr);
			if (!members.TryGetValue(offset, out List<string>? generated))
				members[offset] = generated = [];
			generated.AddRange(context.Members);
			tops.AddRange(context.Tops);
			foreach (MacroDiagnostic diagnostic in context.Diagnostics)
				messages.Add(new MacroMessage(path, line, column, diagnostic.IsError, diagnostic.Message));
		}
		catch (KernPanic panic)
		{
			messages.Add(new MacroMessage(path, line, column, true, panic.Message));
		}
	}

	private static string ExpandExpressions(
		string text,
		string path,
		IReadOnlyDictionary<string, MacroDefinition> macros,
		Interpreter interpreter,
		List<MacroMessage> messages)
	{
		for (var pass = 0; pass < MaxPasses; pass++)
		{
			var calls = FindMacroCalls(text, [.. macros.Keys]);
			if (calls.Count == 0)
				return text;
			var edits = new List<(int Start, int Length, string Replacement)>();
			foreach (var call in calls)
			{
				(int line, int column) = LineColumn(text, call.Start);
				var declaration = new KernMacroDecl("", "expr", [], [], [], [], [], "");
				var context = new KernMacroContext(declaration, call.Name, path, line);
				context.Arguments.AddRange(SplitArguments(call.Arguments));
				string replacement;
				try
				{
					KValue result = interpreter.RunValue(KValue.Obj(macros[call.Name].Function), [KValue.Obj(new KNativeBox(context))]);
					replacement = result.Kind == ValueKind.Str
						? result.AsStr
						: context.Expressions.Count > 0
							? context.Expressions[^1]
							: throw new KernPanic($"макрос '{call.Name}' не вернул выражение");
					foreach (MacroDiagnostic diagnostic in context.Diagnostics)
						messages.Add(new MacroMessage(path, line, column, diagnostic.IsError, diagnostic.Message));
				}
				catch (KernPanic panic)
				{
					messages.Add(new MacroMessage(path, line, column, true, panic.Message));
					replacement = call.Text;
				}

				edits.Add((call.Start, call.Length, replacement));
			}

			string next = Apply(text, edits);
			if (next == text)
				return next;
			text = next;
		}

		messages.Add(new MacroMessage(path, 0, 0, true, "превышено число раскрытий макросов (16)"));
		return text;
	}

	private sealed record MacroCall(string Name, int Start, int Length, string Arguments, string Text);

	private static List<MacroCall> FindMacroCalls(string text, IReadOnlyCollection<string> names)
	{
		var calls = new List<MacroCall>();
		int index = 0;
		while (index < text.Length)
		{
			if (SkipTriviaOrString(text, ref index))
				continue;
			if (!IsIdentifierStart(text[index]))
			{
				index++;
				continue;
			}

			int start = index;
			while (index < text.Length && IsIdentifierPart(text[index]))
				index++;
			string name = text[start..index];
			if (!names.Contains(name))
				continue;
			if (index + 1 >= text.Length || text[index] != '!' || text[index + 1] != '(')
				continue;
			int open = index + 1;
			int close = FindClosing(text, open, '(', ')');
			if (close < 0)
				continue;
			string arguments = text[(open + 1)..close];
			calls.Add(new MacroCall(name, start, close - start + 1, arguments, text[start..(close + 1)]));
			index = close + 1;
		}

		return calls;
	}

	private static DeclarationNode? TypeAt(CompilationUnit tree, int start) =>
		tree.Declarations.FirstOrDefault(declaration =>
			declaration is TypeDeclaration or UnitDeclaration &&
			declaration.Span.Start == start);

	private static KernMacroDecl BuildDeclaration(string text, DeclarationNode declaration)
	{
		string name;
		string kind;
		IReadOnlyList<string> generics;
		IReadOnlyList<ParameterNode> parameters;
		IReadOnlyList<SyntaxNode> members;
		IReadOnlyList<string> parents;
		IReadOnlyList<AttributeNode> attributes;
		switch (declaration)
		{
			case TypeDeclaration type:
				name = type.Name;
				kind = "type";
				generics = [.. type.GenericParameters.Select(parameter => parameter.Name)];
				parameters = type.PrimaryParameters;
				members = type.DeclaredMembers;
				parents = [.. type.Parents.Select(parent => parent.ToString())];
				attributes = type.DeclaredAttributes;
				break;
			case UnitDeclaration unit:
				name = unit.Name;
				kind = unit.IsImmutable ? "unit!" : "unit";
				generics = [.. unit.GenericParameters.Select(parameter => parameter.Name)];
				parameters = unit.PrimaryParameters;
				members = unit.DeclaredMembers;
				parents = [];
				attributes = unit.DeclaredAttributes;
				break;
			default:
				name = "";
				kind = "";
				generics = [];
				parameters = [];
				members = [];
				parents = [];
				attributes = [];
				break;
		}

		var fields = new List<KernMacroField>();
		foreach (ParameterNode parameter in parameters)
			fields.Add(new KernMacroField(
				parameter.Name,
				parameter.Type?.ToString() ?? "?",
				"param",
				parameter.DefaultValue is null ? null : Slice(text, parameter.DefaultValue.Span),
				true));
		foreach (SyntaxNode member in members)
		{
			switch (member)
			{
				case FieldMemberNode field:
					fields.Add(new KernMacroField(
						field.Name,
						field.Type.ToString(),
						"field",
						field.Initializer is null ? null : Slice(text, field.Initializer.Span),
						field.Accessibility == Accessibility.Public));
					break;
				case PropertyMemberNode property:
					fields.Add(new KernMacroField(
						property.Name,
						property.Type.ToString(),
						"property",
						property.ExpressionBody is null ? null : Slice(text, property.ExpressionBody.Span),
						property.Accessibility == Accessibility.Public));
					break;
			}
		}

		var methods = new List<KernMacroMethod>();
		foreach (SyntaxNode member in members)
		{
			if (member is not FunctionDeclaration function)
				continue;
			var methodParameters = function.Parameters
				.Select(parameter => new KernMacroParam(
					parameter.Name,
					parameter.Type?.ToString() ?? "?",
					parameter.DefaultValue is null ? null : Slice(text, parameter.DefaultValue.Span)))
				.ToArray();
			methods.Add(new KernMacroMethod(function.Name, function.ReturnType?.ToString() ?? "void", methodParameters));
		}

		var attrs = attributes.Select(attribute => new KernMacroAttr(attribute.Name, attribute.Arguments)).ToArray();
		return new KernMacroDecl(
			name,
			kind,
			generics,
			fields,
			methods,
			attrs,
			parents,
			Slice(text, declaration.Span));
	}

	private static string Slice(string text, Kern.Core.Diagnostics.TextSpan span) =>
		span.Start >= 0 && span.Start + span.Length <= text.Length
			? text.Substring(span.Start, span.Length)
			: "";

	private static List<(int Start, int Length, string Replacement)> FilterAttributes(
		string text,
		IReadOnlySet<string> macros)
	{
		var edits = new List<(int Start, int Length, string Replacement)>();
		int index = 0;
		while (index < text.Length)
		{
			if (SkipTriviaOrString(text, ref index))
				continue;
			if (index + 1 >= text.Length || text[index] != '#' || text[index + 1] != '[')
			{
				index++;
				continue;
			}

			int start = index;
			int close = FindClosing(text, index + 1, '[', ']');
			if (close < 0)
				break;
			string content = text[(index + 2)..close];
			var items = SplitArguments(content);
			var kept = new List<string>();
			foreach (string item in items)
			{
				string trimmed = item.Trim();
				string name = AttributeName(trimmed);
				if (macros.Contains(name))
					continue;
				if (name == "derive")
					trimmed = FilterDerive(trimmed, macros);
				if (trimmed.Length > 0)
					kept.Add(trimmed);
			}

			string replacement = kept.Count == 0 ? "" : "#[" + string.Join(", ", kept) + "]";
			if (!string.Equals(replacement, text[start..(close + 1)], StringComparison.Ordinal))
				edits.Add((start, close - start + 1, replacement));
			index = close + 1;
		}

		return edits;
	}

	private static string FilterDerive(string item, IReadOnlySet<string> macros)
	{
		int open = item.IndexOf('(');
		int close = item.LastIndexOf(')');
		if (open < 0 || close <= open)
			return item;
		var args = SplitArguments(item[(open + 1)..close]).Select(argument => argument.Trim()).Where(arg =>
			arg.Length > 0 && !macros.Contains(arg)).ToArray();
		return args.Length == 0 ? "" : "derive(" + string.Join(", ", args) + ")";
	}

	private static string AttributeName(string item)
	{
		int end = 0;
		while (end < item.Length && (char.IsLetterOrDigit(item[end]) || item[end] == '_'))
			end++;
		return item[..end];
	}

	private static List<string> SplitArguments(string text)
	{
		var result = new List<string>();
		int depth = 0;
		int start = 0;
		var quote = '\0';
		for (var index = 0; index < text.Length; index++)
		{
			char symbol = text[index];
			if (quote != '\0')
			{
				if (symbol == '\\')
					index++;
				else if (symbol == quote)
					quote = '\0';
				continue;
			}

			if (symbol is '"' or '\'')
				quote = symbol;
			else if (symbol is '(' or '[' or '{')
				depth++;
			else if (symbol is ')' or ']' or '}')
				depth--;
			else if (symbol == ',' && depth == 0)
			{
				result.Add(text[start..index]);
				start = index + 1;
			}
		}

		if (start < text.Length)
			result.Add(text[start..]);
		return result;
	}

	private static string Apply(string text, IReadOnlyList<(int Start, int Length, string Replacement)> edits)
	{
		if (edits.Count == 0)
			return text;
		var builder = new System.Text.StringBuilder(text);
		foreach (var (start, length, replacement) in edits.OrderByDescending(edit => edit.Start))
			builder.Remove(start, length).Insert(start, replacement);
		return builder.ToString();
	}

	private static bool SkipTriviaOrString(string text, ref int index)
	{
		if (index + 1 < text.Length && text[index] == '/' && text[index + 1] == '/')
		{
			index = text.IndexOf('\n', index) is int newline && newline >= 0 ? newline + 1 : text.Length;
			return true;
		}

		if (index + 1 < text.Length && text[index] == '/' && text[index + 1] == '*')
		{
			int end = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
			index = end < 0 ? text.Length : end + 2;
			return true;
		}

		if (text[index] == '"')
		{
			int end = SkipString(text, index);
			index = end;
			return true;
		}

		return false;
	}

	private static int SkipString(string text, int start)
	{
		bool triple = start + 2 < text.Length && text[start] == '"' && text[start + 1] == '"' && text[start + 2] == '"';
		int index = start + (triple ? 3 : 1);
		while (index < text.Length)
		{
			if (triple && index + 2 < text.Length && text[index] == '"' && text[index + 1] == '"' && text[index + 2] == '"')
				return index + 3;
			if (!triple && text[index] == '"')
				return index + 1;
			if (text[index] == '\\')
				index += 2;
			else
				index++;
		}

		return text.Length;
	}

	private static int FindClosing(string text, int open, char opening, char closing)
	{
		int depth = 0;
		var quote = '\0';
		for (var index = open; index < text.Length; index++)
		{
			char symbol = text[index];
			if (quote != '\0')
			{
				if (symbol == '\\')
					index++;
				else if (symbol == quote)
					quote = '\0';
				continue;
			}

			if (symbol is '"' or '\'')
				quote = symbol;
			else if (symbol == opening)
				depth++;
			else if (symbol == closing && --depth == 0)
				return index;
		}

		return -1;
	}

	private static (int Line, int Column) LineColumn(string text, int offset)
	{
		int line = 1;
		int column = 1;
		for (var index = 0; index < offset && index < text.Length; index++)
		{
			if (text[index] == '\n')
			{
				line++;
				column = 1;
			}
			else
			{
				column++;
			}
		}

		return (line, column);
	}

	private static bool IsIdentifierStart(char symbol) => char.IsLetter(symbol) || symbol == '_';
	private static bool IsIdentifierPart(char symbol) => char.IsLetterOrDigit(symbol) || symbol == '_';
}
