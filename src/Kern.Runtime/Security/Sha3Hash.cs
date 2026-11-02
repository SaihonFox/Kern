using System.Buffers.Binary;

namespace Kern.Runtime.Security;

/// <summary>
/// Переносимая реализация SHA3-256/SHA3-512 (Keccak-f[1600]). BCL-классы
/// <c>System.Security.Cryptography.SHA3_*</c> на macOS бросают
/// <see cref="PlatformNotSupportedException"/>, поэтому для <c>kern.security</c> хеш
/// считается здесь и одинаков на Windows/Linux/macOS.
/// </summary>
internal static class Sha3Hash
{
	private static readonly ulong[] RoundConstants =
	[
		0x0000000000000001, 0x0000000000008082, 0x800000000000808A, 0x8000000080008000,
		0x000000000000808B, 0x0000000080000001, 0x8000000080008081, 0x8000000000008009,
		0x000000000000008A, 0x0000000000000088, 0x0000000080008009, 0x000000008000000A,
		0x000000008000808B, 0x800000000000008B, 0x8000000000008089, 0x8000000000008003,
		0x8000000000008002, 0x8000000000000080, 0x000000000000800A, 0x800000008000000A,
		0x8000000080008081, 0x8000000000008080, 0x0000000080000001, 0x8000000080008008,
	];

	private static readonly int[] Rotations =
	[
		0, 1, 62, 28, 27,
		36, 44, 6, 55, 20,
		3, 10, 43, 25, 39,
		41, 45, 15, 21, 8,
		18, 2, 61, 56, 14,
	];

	public static byte[] Sha3_256(byte[] data) => Hash(data, rate: 136, output: 32);

	public static byte[] Sha3_512(byte[] data) => Hash(data, rate: 72, output: 64);

	private static byte[] Hash(byte[] data, int rate, int output)
	{
		var state = new ulong[25];
		int offset = 0;
		while (offset + rate <= data.Length)
		{
			Absorb(state, data, offset, rate);
			Permute(state);
			offset += rate;
		}

		var block = new byte[rate];
		int rest = data.Length - offset;
		Array.Copy(data, offset, block, 0, rest);
		block[rest] ^= 0x06;
		block[rate - 1] ^= 0x80;
		Absorb(state, block, 0, rate);
		Permute(state);

		var result = new byte[output];
		for (int index = 0; index < output / 8; index++)
			BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(index * 8), state[index]);
		for (int index = output / 8 * 8; index < output; index++)
			result[index] = (byte)(state[index / 8] >> (8 * (index % 8)));
		return result;
	}

	private static void Absorb(ulong[] state, byte[] block, int offset, int rate)
	{
		for (int lane = 0; lane < rate / 8; lane++)
			state[lane] ^= BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(offset + lane * 8));
	}

	private static void Permute(ulong[] state)
	{
		var c = new ulong[5];
		var b = new ulong[25];
		foreach (ulong round in RoundConstants)
		{
			for (int x = 0; x < 5; x++)
				c[x] = state[x] ^ state[x + 5] ^ state[x + 10] ^ state[x + 15] ^ state[x + 20];
			for (int x = 0; x < 5; x++)
			{
				ulong d = c[(x + 4) % 5] ^ Rotate(c[(x + 1) % 5], 1);
				for (int y = 0; y < 5; y++)
					state[x + 5 * y] ^= d;
			}

			for (int x = 0; x < 5; x++)
				for (int y = 0; y < 5; y++)
					b[y + 5 * ((2 * x + 3 * y) % 5)] = Rotate(state[x + 5 * y], Rotations[x + 5 * y]);

			for (int x = 0; x < 5; x++)
				for (int y = 0; y < 5; y++)
					state[x + 5 * y] = b[x + 5 * y] ^ (~b[(x + 1) % 5 + 5 * y] & b[(x + 2) % 5 + 5 * y]);

			state[0] ^= round;
		}
	}

	private static ulong Rotate(ulong value, int amount) =>
		amount == 0 ? value : (value << amount) | (value >> (64 - amount));
}
