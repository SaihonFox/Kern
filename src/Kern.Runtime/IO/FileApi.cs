using System.Text;
using Kern.Binding.Attributes;
using Kern.Binding.Values;
using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.IO;

/// <summary>
/// <c>file.*</c> (spec §16, раунд 4): файловые операции возвращают
/// <c>result&lt;T, str&gt;</c> — ошибка ввода-вывода не паника, а <c>Err</c> с русским
/// текстом. Паника остаётся только у ошибок программиста (неверный тип аргумента).
/// <c>exists</c> — простой предикат: спрашивать «есть ли файл» через result неудобно.
/// </summary>
[KernType("file")]
public static class FileApi
{
	private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

	/// <summary><c>file.read_text(path)</c>: весь файл строкой.</summary>
	[KernStatic]
	[KernCapability("fs.read")]
	[KernMember]
	public static KResult<string, string> ReadText(string path) =>
		Try(path, () => System.IO.File.ReadAllText(path, Utf8));

	/// <summary><c>file.write_text(path, text)</c>: перезаписать файл (создать при отсутствии).</summary>
	[KernStatic]
	[KernCapability("fs.write")]
	[KernMember]
	public static KResult<bool, string> WriteText(string path, string text) =>
		Try(path, () =>
		{
			System.IO.File.WriteAllText(path, text, Utf8);
			return true;
		});

	/// <summary><c>file.append_text(path, text)</c>: дописать строку в конец файла.</summary>
	[KernStatic]
	[KernCapability("fs.write")]
	[KernMember]
	public static KResult<bool, string> AppendText(string path, string text) =>
		Try(path, () =>
		{
			System.IO.File.AppendAllText(path, text, Utf8);
			return true;
		});

	/// <summary><c>file.exists(path)</c>: существует ли файл или папка.</summary>
	[KernStatic]
	[KernCapability("fs.read")]
	[KernMember]
	public static bool Exists(string path) =>
		System.IO.File.Exists(path) || System.IO.Directory.Exists(path);

	/// <summary><c>file.delete(path)</c>: удалить файл; отсутствие файла — не ошибка.</summary>
	[KernStatic]
	[KernCapability("fs.write")]
	[KernMember]
	public static KResult<bool, string> Delete(string path) =>
		Try(path, () =>
		{
			if (System.IO.File.Exists(path))
				System.IO.File.Delete(path);
			return true;
		});

	/// <summary><c>file.list_dir(path)</c>: имена файлов и папок каталога (сортировка по имени).</summary>
	[KernStatic]
	[KernName("list_dir")]
	[KernCapability("fs.read")]
	[KernMember]
	public static KResult<KList<string>, string> ListDir(string path) =>
		Try(path, () =>
		{
			if (!System.IO.Directory.Exists(path))
				throw new DirectoryNotFoundException();
			IEnumerable<string> names = System.IO.Directory
				.EnumerateFileSystemEntries(path)
				.Select(entry => System.IO.Path.GetFileName(entry) ?? entry)
				.OrderBy(name => name, StringComparer.Ordinal);
			return Strings(names);
		});

	/// <summary><c>file.copy(from, to)</c>: копия файла; существующий приёмник — <c>Err</c>.</summary>
	[KernStatic]
	[KernCapability("fs.read")]
	[KernCapability("fs.write")]
	[KernMember]
	public static KResult<bool, string> Copy(string from, string to) => Copy(from, to, false);

	/// <summary><c>file.copy(from, to, overwrite)</c>: копия файла с явной перезаписью.</summary>
	[KernStatic]
	[KernCapability("fs.read")]
	[KernCapability("fs.write")]
	[KernMember]
	public static KResult<bool, string> Copy(string from, string to, bool overwrite) =>
		Try(from, () =>
		{
			System.IO.File.Copy(from, to, overwrite);
			return true;
		});

	/// <summary><c>file.move(from, to)</c>: перенос/переименование; существующий приёмник — <c>Err</c>.</summary>
	[KernStatic]
	[KernCapability("fs.write")]
	[KernMember]
	public static KResult<bool, string> Move(string from, string to) =>
		Try(from, () =>
		{
			System.IO.File.Move(from, to);
			return true;
		});

	/// <summary><c>file.create_dir(path)</c>: создать папку со всеми родителями.</summary>
	[KernStatic]
	[KernName("create_dir")]
	[KernCapability("fs.write")]
	[KernMember]
	public static KResult<bool, string> CreateDir(string path) =>
		Try(path, () =>
		{
			System.IO.Directory.CreateDirectory(path);
			return true;
		});

	/// <summary><c>file.delete_dir(path, recursive)</c>: удалить папку.</summary>
	[KernStatic]
	[KernName("delete_dir")]
	[KernCapability("fs.write")]
	[KernMember]
	public static KResult<bool, string> DeleteDir(string path, bool recursive) =>
		Try(path, () =>
		{
			System.IO.Directory.Delete(path, recursive);
			return true;
		});

	/// <summary><c>file.read_lines(path)</c>: строки файла (переводы строк не входят).</summary>
	[KernStatic]
	[KernName("read_lines")]
	[KernCapability("fs.read")]
	[KernMember]
	public static KResult<KList<string>, string> ReadLines(string path) =>
		Try(path, () => Strings(System.IO.File.ReadLines(path, Utf8)));

	/// <summary><c>file.write_lines(path, lines)</c>: записать строки через <c>\n</c>.</summary>
	[KernStatic]
	[KernName("write_lines")]
	[KernCapability("fs.write")]
	[KernMember]
	public static KResult<bool, string> WriteLines(string path, KList<string> lines) =>
		Try(path, () =>
		{
			if (lines.Value.Ref is not Kern.Vm.Values.KList list)
				throw new KernPanic($"file.write_lines: ожидался list<str>, получено {lines.Value.TypeName}");
			var builder = new StringBuilder();
			foreach (Kern.Vm.Values.KValue item in list.Items)
			{
				if (item.Kind != Kern.Vm.Values.ValueKind.Str)
					throw new KernPanic($"file.write_lines: ожидался list<str>, элемент — {item.TypeName}");
				builder.Append(item.AsStr).Append('\n');
			}

			System.IO.File.WriteAllText(path, builder.ToString(), Utf8);
			return true;
		});

	/// <summary><c>file.read_bytes(path)</c>: двоичное содержимое файла.</summary>
	[KernStatic]
	[KernName("read_bytes")]
	[KernCapability("fs.read")]
	[KernMember]
	public static KResult<KList<byte>, string> ReadBytes(string path) =>
		Try(path, () =>
		{
			byte[] bytes = System.IO.File.ReadAllBytes(path);
			return new KList<byte>(KValue.Obj(new Kern.Vm.Values.KList(
				null, bytes.Select(KValue.U8))));
		});

	/// <summary><c>file.write_bytes(path, bytes)</c>: записать двоичные данные.</summary>
	[KernStatic]
	[KernName("write_bytes")]
	[KernCapability("fs.write")]
	[KernMember]
	public static KResult<bool, string> WriteBytes(string path, KList<byte> bytes) =>
		Try(path, () =>
		{
			if (bytes.Value.Ref is not Kern.Vm.Values.KList list)
				throw new KernPanic($"file.write_bytes: ожидался list<u8>, получено {bytes.Value.TypeName}");
			var data = new byte[list.Count];
			for (int index = 0; index < data.Length; index++)
			{
				if (list[index].Kind != Kern.Vm.Values.ValueKind.U8)
					throw new KernPanic($"file.write_bytes: элемент {index} — {list[index].TypeName}, ожидался u8");
				data[index] = list[index].AsU8;
			}
			System.IO.File.WriteAllBytes(path, data);
			return true;
		});

	/// <summary>Список строк в VM-представлении <c>list&lt;str&gt;</c>.</summary>
	private static KList<string> Strings(IEnumerable<string> items) =>
		new(Kern.Vm.Values.KValue.Obj(
			new Kern.Vm.Values.KList(
				null,
				[.. items.Select(Kern.Vm.Values.KValue.Str)])));

	/// <summary>Выполнить операцию ввода-вывода, превратив исключение в <c>Err</c>.</summary>
	private static KResult<T, string> Try<T>(string path, Func<T> body)
	{
		try
		{
			return KResult<T, string>.Success(body());
		}
		catch (Exception error) when (error is IOException
			or UnauthorizedAccessException
			or ArgumentException
			or NotSupportedException
			or System.Security.SecurityException)
		{
			return KResult<T, string>.Failure(Describe(path, error));
		}
	}

	/// <summary>Русский текст ошибки ввода-вывода для `Err` (spec: сообщения — по-русски).</summary>
	private static string Describe(string path, Exception error) => error switch
	{
		FileNotFoundException => $"файл '{path}' не найден",
		DirectoryNotFoundException => $"папка для '{path}' не найдена",
		UnauthorizedAccessException => $"нет доступа к '{path}'",
		PathTooLongException => $"слишком длинный путь '{path}'",
		ArgumentException => $"некорректный путь '{path}'",
		_ => $"ошибка при работе с '{path}': {error.Message}",
	};
}
