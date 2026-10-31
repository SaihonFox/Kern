using System.Text;
using Kern.Compiler.Semantics.Symbols;
using Kern.Core;

namespace Kern.Compiler.Semantics.Overloads;

/// <summary>Диагностики разрешения перегрузки (коды KRN14xx и общие KRN10xx).</summary>
public static class OverloadDiagnostics
{
	/// <summary>
	/// Сообщает об ошибке разрешения (ничего не делает при успехе и при ошибочных аргументах — ошибка уже сообщена).
	/// <paramref name="name"/> — имя вызываемой функции для текста; <paramref name="span"/> — диапазон всего вызова.
	/// </summary>
	public static void Report(DiagnosticBag diagnostics, SourceFile? file, TextSpan span, string name, OverloadResult result, OverloadRequest request)
	{
		if (result.SuppressedByErrorArguments) return;
		switch (result.Status)
		{
			case OverloadStatus.NoCandidates:
				diagnostics.Error(Krn.FunctionNotFound, $"функция '{name}' не найдена", file, span);
				break;

			case OverloadStatus.NoApplicable when result.Rejected.Count == 1:
			{
				var r = result.Rejected[0];
				var at = r.ArgumentIndex is { } i && i < request.Arguments.Count ? request.Arguments[i].Span : span;
				diagnostics.Error(CodeFor(r.Reason), $"вызов '{name}': {r.Message}\n  кандидат: {r.Function.ToDisplayString()}", file, at);
				break;
			}

			case OverloadStatus.NoApplicable:
			{
				var sb = new StringBuilder();
				sb.Append($"нет перегрузки '{name}', применимой к аргументам {DescribeArguments(request)}. Кандидаты:");
				foreach (var r in result.Rejected)
					sb.Append($"\n  - {r.Function.ToDisplayString()} — {r.Message}");
				diagnostics.Error(Krn.NoApplicableOverload, sb.ToString(), file, span);
				break;
			}

			case OverloadStatus.Ambiguous:
			{
				var sb = new StringBuilder();
				sb.Append($"вызов '{name}' с аргументами {DescribeArguments(request)} неоднозначен между:");
				foreach (var c in result.Ambiguous)
					sb.Append($"\n  - {c.Function.ToDisplayString()}{Location(c.Function)}");
				diagnostics.Error(Krn.AmbiguousCall, sb.ToString(), file, span);
				break;
			}
		}
	}

	private static string CodeFor(RejectionReason reason) => reason switch
	{
		RejectionReason.GenericArity => Krn.WrongTypeArgCount,
		RejectionReason.InferenceFailed => Krn.CannotInferTypeArgs,
		RejectionReason.ConstraintViolated => Krn.ConstraintViolated,
		RejectionReason.ArgumentConversion or RejectionReason.ReceiverMismatch => Krn.ArgumentMismatch,
		_ => Krn.NoApplicableOverload,
	};

	/// <summary>`(i32, name: str)`.</summary>
	public static string DescribeArguments(OverloadRequest request) =>
		"(" + string.Join(", ", request.Arguments.Select(a => (a.Name is { } n ? n + ": " : "") + a.Type.ToDisplayString())) + ")";

	private static string Location(FunctionSymbol f)
	{
		if (f.File is not { } file) return f.IsNative ? " [нативная]" : "";
		var (line, col) = file.GetLineColumn(f.Span.Start);
		return $" [{file.Path}:{line}:{col}]";
	}
}
