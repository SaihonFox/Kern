using System.Text.RegularExpressions;
using Kern.Binding.Catalog;

namespace Kern.Cli.Interop;

/// <summary>
/// Разбор <c>use dotnet.System.IO[File, Path]</c> (spec §12.10): находит .NET-типы и
/// расширяет нативный каталог статическими членами. Сами <c>use dotnet.*</c> не
/// участвуют в разрешении модулей (типы и функции уже глобальны).
/// </summary>
internal static class DotnetImport
{
	private static readonly Regex Use = new(
		@"(?:^|\n)\s*use\s+dotnet\.(?<ns>[A-Za-z_][A-Za-z0-9_.]*)\s*\[\s*(?<items>[^\]]*?)\s*\]",
		RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

	public static NativeCatalog Apply(NativeCatalog catalog, IEnumerable<string> sources)
	{
		ArgumentNullException.ThrowIfNull(catalog);
		ArgumentNullException.ThrowIfNull(sources);

		var types = new List<Type>();
		foreach (string source in sources)
		{
			foreach (Match match in Use.Matches(source))
			{
				string ns = match.Groups["ns"].Value;
				foreach (string item in match.Groups["items"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
				{
					string name = item.Trim();
					if (name.Length == 0)
						continue;
					Type? type = DotnetCatalog.FindType(ns + "." + name);
					if (type is not null)
						types.Add(type);
				}
			}
		}

		return types.Count == 0 ? catalog : DotnetCatalog.Import(catalog, types);
	}
}
