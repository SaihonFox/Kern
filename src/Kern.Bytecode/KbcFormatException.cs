namespace Kern.Bytecode;

/// <summary>
/// Ошибка формата .kbc: неверная сигнатура/версия, обрезанные данные, битые индексы.
/// Сообщения — по-русски; это не Kern-panic, а повреждённый модуль.
/// </summary>
public sealed class KbcFormatException : Exception
{
	public KbcFormatException(string message) : base(message)
	{
	}

	public KbcFormatException(string message, Exception? inner) : base(message, inner)
	{
	}
}
