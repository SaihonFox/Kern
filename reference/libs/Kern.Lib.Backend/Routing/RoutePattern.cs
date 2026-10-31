namespace Kern.Lib.Backend.Routing;

/// <summary>Шаблон пути: литералы, `{name}` — один сегмент, `{*rest}` — остаток пути.</summary>
internal sealed class RoutePattern
{
	private readonly string[] _segments;

	public RoutePattern(string pattern) =>
		_segments = pattern.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

	/// <summary>Чем больше литералов, тем выше приоритет.</summary>
	public int Specificity => _segments.Count(s => !IsParam(s)) * 2 + (_segments.Any(s => s.StartsWith("{*")) ? 0 : 1);

	private static bool IsParam(string s) => s.Length > 2 && s[0] == '{' && s[^1] == '}';

	public bool Match(string path, out Dictionary<string, string> values)
	{
		values = [];
		var actual = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
		for (int i = 0; i < _segments.Length; i++)
		{
			var seg = _segments[i];
			if (seg.StartsWith("{*") && seg.EndsWith('}'))
			{
				values[seg[2..^1]] = string.Join('/', actual.Skip(i).Select(Uri.UnescapeDataString));
				return true;
			}
			if (i >= actual.Length) return false;
			if (IsParam(seg)) values[seg[1..^1]] = Uri.UnescapeDataString(actual[i]);
			else if (!string.Equals(seg, actual[i], StringComparison.Ordinal)) return false;
		}
		return _segments.Length == actual.Length;
	}

	public static string Join(string prefix, string pattern)
	{
		var a = prefix.Trim('/');
		var b = pattern.Trim('/');
		return "/" + (a.Length == 0 ? b : b.Length == 0 ? a : a + "/" + b);
	}
}
