namespace Kern.Lib.Backend.Routing;

/// <summary>Шаблон маршрута: литералы, <c>{name}</c> и хвост <c>{*rest}</c>.</summary>
internal sealed class RoutePattern
{
	private readonly string[] _segments;

	public RoutePattern(string pattern) =>
		_segments = pattern.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

	/// <summary>Чем больше литералов, тем специфичнее маршрут.</summary>
	public int Specificity =>
		_segments.Count(segment => !IsParam(segment)) * 2 +
		(_segments.Any(segment => segment.StartsWith("{*", StringComparison.Ordinal)) ? 0 : 1);

	private static bool IsParam(string segment) =>
		segment.Length > 2 && segment[0] == '{' && segment[^1] == '}';

	public bool Match(string path, out Dictionary<string, string> values)
	{
		values = [];
		string[] actual = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
		for (var index = 0; index < _segments.Length; index++)
		{
			string segment = _segments[index];
			if (segment.StartsWith("{*", StringComparison.Ordinal) && segment.EndsWith('}'))
			{
				values[segment[2..^1]] = string.Join('/', actual.Skip(index).Select(Uri.UnescapeDataString));
				return true;
			}

			if (index >= actual.Length)
				return false;
			if (IsParam(segment))
				values[segment[1..^1]] = Uri.UnescapeDataString(actual[index]);
			else if (!string.Equals(segment, actual[index], StringComparison.Ordinal))
				return false;
		}

		return _segments.Length == actual.Length;
	}

	public static string Join(string prefix, string pattern)
	{
		string left = prefix.Trim('/');
		string right = pattern.Trim('/');
		return "/" + (left.Length == 0 ? right : right.Length == 0 ? left : left + "/" + right);
	}
}
