using Kern.Compiler.Semantics.Symbols;

namespace Kern.Compiler.Semantics.Overloads;

/// <summary>
/// Разрешение перегрузки «как в C#»:
/// <list type="number">
/// <item>применимость каждого кандидата (<see cref="CandidateEvaluator"/>): generic-арность, число аргументов,
/// именованные, значения по умолчанию, variadic `..args` (нормальная/расширенная форма), вывод generic-аргументов, ограничения;</item>
/// <item>отсев базовых перегрузок, перекрытых переопределением в потомке;</item>
/// <item>выбор лучшего через <see cref="Conversions"/> (<see cref="CandidateComparer"/>); если лучшего нет — ошибка неоднозначности
/// с перечислением кандидатов (<see cref="OverloadDiagnostics"/>).</item>
/// </list>
/// Возвращаемый тип в выборе НЕ участвует (он лишь подставляется в <see cref="ResolvedCall.ReturnType"/>).
/// </summary>
public static class OverloadResolver
{
	public static OverloadResult Resolve(OverloadSet set, OverloadRequest request) => Resolve(set.Functions, request);

	public static OverloadResult Resolve(IEnumerable<FunctionSymbol> candidates, OverloadRequest request)
	{
		var list = candidates.Distinct().ToList();
		if (list.Count == 0) return new OverloadResult { Status = OverloadStatus.NoCandidates };

		var applicable = new List<ResolvedCall>();
		var rejected = new List<RejectedCandidate>();
		foreach (var f in list)
		{
			var (call, rejection) = CandidateEvaluator.Evaluate(f, request);
			if (call is not null) applicable.Add(call);
			else rejected.Add(rejection!);
		}

		bool errorArgs = request.HasErrorArguments;
		if (applicable.Count == 0)
			return new OverloadResult { Status = OverloadStatus.NoApplicable, Rejected = rejected, SuppressedByErrorArguments = errorArgs };

		applicable = CandidateComparer.PruneOverridden(applicable);
		if (applicable.Count == 1)
			return new OverloadResult { Status = OverloadStatus.Success, Selected = applicable[0], Applicable = applicable, Rejected = rejected };

		var best = applicable.FirstOrDefault(c => applicable.All(o => ReferenceEquals(o, c) || CandidateComparer.Compare(c, o, request) > 0));
		if (best is not null)
			return new OverloadResult { Status = OverloadStatus.Success, Selected = best, Applicable = applicable, Rejected = rejected };

		var maximal = applicable
			.Where(c => !applicable.Any(o => !ReferenceEquals(o, c) && CandidateComparer.Compare(o, c, request) > 0))
			.ToList();
		if (maximal.Count < 2) maximal = applicable; // нетранзитивное сравнение: перечисляем всех

		// Ошибочные аргументы: ошибка уже сообщена, не плодим каскад — берём первого из лучших.
		if (errorArgs)
			return new OverloadResult
			{
				Status = OverloadStatus.Success, Selected = maximal[0], Applicable = applicable, Rejected = rejected, SuppressedByErrorArguments = true,
			};

		return new OverloadResult { Status = OverloadStatus.Ambiguous, Ambiguous = maximal, Applicable = applicable, Rejected = rejected };
	}

	/// <summary>
	/// Кандидаты по уровням приоритета (например: члены типа → расширения → функции внешней области): берётся первый уровень,
	/// где есть хотя бы один применимый кандидат (как поиск extension-методов в C#). Если применимых нет нигде —
	/// <see cref="OverloadStatus.NoApplicable"/> со всеми причинами отказа.
	/// </summary>
	public static OverloadResult ResolveTiers(IEnumerable<IEnumerable<FunctionSymbol>> tiers, OverloadRequest request)
	{
		var rejected = new List<RejectedCandidate>();
		bool anyCandidates = false;
		bool suppressed = false;
		foreach (var tier in tiers)
		{
			var result = Resolve(tier, request);
			if (result.Status is OverloadStatus.Success or OverloadStatus.Ambiguous) return result;
			if (result.Status == OverloadStatus.NoCandidates) continue;
			anyCandidates = true;
			suppressed |= result.SuppressedByErrorArguments;
			rejected.AddRange(result.Rejected);
		}
		return anyCandidates
			? new OverloadResult { Status = OverloadStatus.NoApplicable, Rejected = rejected, SuppressedByErrorArguments = suppressed }
			: new OverloadResult { Status = OverloadStatus.NoCandidates };
	}
}
