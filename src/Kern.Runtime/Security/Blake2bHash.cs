using System.Buffers.Binary;

namespace Kern.Runtime.Security;

/// <summary>
/// BLAKE2b (RFC 7693) без секретного ключа и с ключом до 64 байт. Реализация нужна,
/// потому что BCL не предоставляет BLAKE2, а пакеты тянуть нельзя.
/// </summary>
internal static class Blake2bHash
{
	private static readonly ulong[] Iv =
	[
		0x6A09E667F3BCC908, 0xBB67AE8584CAA73B, 0x3C6EF372FE94F82B, 0xA54FF53A5F1D36F1,
		0x510E527FADE682D1, 0x9B05688C2B3E6C1F, 0x1F83D9ABFB41BD6B, 0x5BE0CD19137E2179,
	];

	private static readonly byte[,] Sigma =
	{
		{ 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
		{ 14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3 },
		{ 11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4 },
		{ 7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8 },
		{ 9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13 },
		{ 2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9 },
		{ 12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11 },
		{ 13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10 },
		{ 6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5 },
		{ 10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0 },
	};

	public static byte[] Hash(ReadOnlySpan<byte> data, int digestSize, ReadOnlySpan<byte> key)
	{
		if (digestSize is < 1 or > 64)
			throw new ArgumentOutOfRangeException(nameof(digestSize));
		if (key.Length > 64)
			throw new ArgumentOutOfRangeException(nameof(key));

		var h = (ulong[])Iv.Clone();
		h[0] ^= 0x01010000UL ^ ((ulong)key.Length << 8) ^ (ulong)digestSize;

		var input = new byte[(key.Length > 0 ? 128 : 0) + data.Length];
		if (key.Length > 0)
		{
			key.CopyTo(input);
			data.CopyTo(input.AsSpan(128));
		}
		else
		{
			data.CopyTo(input);
		}

		int blocks = input.Length / 128;
		if (input.Length > 0 && input.Length % 128 == 0)
			blocks--;
		ulong counter = 0;
		var block = new byte[128];
		for (int index = 0; index < blocks; index++)
		{
			counter += 128;
			Compress(h, input.AsSpan(index * 128, 128), counter, last: false);
		}

		int rest = input.Length - blocks * 128;
		Array.Clear(block);
		if (rest > 0)
			Array.Copy(input, blocks * 128, block, 0, rest);
		counter += (ulong)rest;
		Compress(h, block, counter, last: true);

		var full = new byte[64];
		for (int index = 0; index < 8; index++)
			BinaryPrimitives.WriteUInt64LittleEndian(full.AsSpan(index * 8), h[index]);
		return full[..digestSize];
	}

	private static void Compress(ulong[] h, ReadOnlySpan<byte> block, ulong counter, bool last)
	{
		Span<ulong> m = stackalloc ulong[16];
		for (int index = 0; index < 16; index++)
			m[index] = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(index * 8, 8));

		Span<ulong> v = stackalloc ulong[16];
		h.AsSpan().CopyTo(v[..8]);
		Iv.AsSpan().CopyTo(v[8..]);
		v[12] ^= counter;
		if (last)
			v[14] = ~v[14];

		for (int round = 0; round < 12; round++)
		{
			int row = round % 10;
			G(v, 0, 4, 8, 12, m[Sigma[row, 0]], m[Sigma[row, 1]]);
			G(v, 1, 5, 9, 13, m[Sigma[row, 2]], m[Sigma[row, 3]]);
			G(v, 2, 6, 10, 14, m[Sigma[row, 4]], m[Sigma[row, 5]]);
			G(v, 3, 7, 11, 15, m[Sigma[row, 6]], m[Sigma[row, 7]]);
			G(v, 0, 5, 10, 15, m[Sigma[row, 8]], m[Sigma[row, 9]]);
			G(v, 1, 6, 11, 12, m[Sigma[row, 10]], m[Sigma[row, 11]]);
			G(v, 2, 7, 8, 13, m[Sigma[row, 12]], m[Sigma[row, 13]]);
			G(v, 3, 4, 9, 14, m[Sigma[row, 14]], m[Sigma[row, 15]]);
		}

		for (int index = 0; index < 8; index++)
			h[index] ^= v[index] ^ v[index + 8];
	}

	private static void G(Span<ulong> v, int a, int b, int c, int d, ulong x, ulong y)
	{
		v[a] = v[a] + v[b] + x;
		v[d] = RotateRight(v[d] ^ v[a], 32);
		v[c] += v[d];
		v[b] = RotateRight(v[b] ^ v[c], 24);
		v[a] = v[a] + v[b] + y;
		v[d] = RotateRight(v[d] ^ v[a], 16);
		v[c] += v[d];
		v[b] = RotateRight(v[b] ^ v[c], 63);
	}

	private static ulong RotateRight(ulong value, int amount) =>
		(value >> amount) | (value << (64 - amount));
}
