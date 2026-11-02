using Kern.Binding.Values;
using Kern.Runtime.IO;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>Двоичный ввод-вывод для бутстраппинга: <c>file.read_bytes/write_bytes</c>.</summary>
public sealed class FileBytesTests
{
	[Fact]
	public void Bytes_round_trip_through_a_file()
	{
		string path = Path.Combine(Path.GetTempPath(), $"kern-bytes-{Guid.NewGuid():N}.bin");
		byte[] expected = [0, 1, 2, 127, 128, 255];
		try
		{
			var list = new KList<byte>(KValue.Obj(new Kern.Vm.Values.KList(
				null, expected.Select(KValue.U8))));
			Assert.True(FileApi.WriteBytes(path, list).IsOk);
			var read = FileApi.ReadBytes(path);
			Assert.True(read.IsOk);
			Assert.Equal(expected, read.Ok.Value.Ref is Kern.Vm.Values.KList items
				? items.Items.Select(item => item.AsU8)
				: []);
		}
		finally
		{
			if (File.Exists(path))
				File.Delete(path);
		}
	}
}
