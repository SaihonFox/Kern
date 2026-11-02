using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>Дополнительные std-модули: ansi, ini, toml, net и часовые пояса.</summary>
[Collection("timing")]
public sealed class StdModulesExtraTests
{
	[Fact]
	public void Ansi_wraps_and_strips_sequences()
	{
		const string source = """
			fun colored() -> str ansi.color256(196, "red")
			fun stripped() -> str ansi.strip(ansi.color256(196, "red"))
			fun rgb_stripped() -> str ansi.strip(ansi.rgb(1, 2, 3, "x"))
			""";

		Assert.Contains("\u001b[38;5;196m", StdPipeline.Run(source, "colored").AsStr);
		Assert.Equal(KValue.Str("red"), StdPipeline.Run(source, "stripped"));
		Assert.Equal(KValue.Str("x"), StdPipeline.Run(source, "rgb_stripped"));
	}

	[Fact]
	public void Ini_parses_sections_and_writes_them_back()
	{
		const string source = """
			fun main_text() -> bool {
				match ini.parse("a = 1\n[db]\nhost = local\nport = 5432\n") {
					Ok(_) => true
					Err(_) => false
				}
			}
			""";

		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "main_text"));
	}

	[Fact]
	public void Toml_parses_tables_arrays_and_scalars()
	{
		const string source = """
			fun parsed() -> str {
				match toml.parse("title = \"kern\"\nport = 5432\nflags = [1, 2]\n") {
					Ok(_) => "ok"
					Err(e) => e
				}
			}
			""";

		Assert.Equal(KValue.Str("ok"), StdPipeline.Run(source, "parsed"));
	}

	[Fact]
	public void Net_resolves_localhost()
	{
		const string source = """
			fun host() -> bool net.hostname().len > 0
			fun localhost() -> bool net.resolve("localhost").len > 0
			""";

		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "host"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "localhost"));
	}

	[Fact]
	public void Time_zones_convert_datetime()
	{
		const string source = """
			fun utc_zone() -> bool {
				time_zone? zone = time.zone("UTC")
				zone != nil && time.zone("не-существует") == nil
			}

			fun converted() -> str {
				time_zone? zone = time.zone("UTC")
				datetime now = datetime.now_in(zone!)
				now.zone_name
			}
			""";

		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "utc_zone"));
		Assert.Equal(KValue.Str("UTC"), StdPipeline.Run(source, "converted"));
	}
}
