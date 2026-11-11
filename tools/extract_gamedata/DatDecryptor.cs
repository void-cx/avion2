using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExtractGameData.Core;
using K4os.Compression.LZ4;

namespace ExtractGameData;

/// <summary>
/// Removes AION2's own protection layer from the <c>.dat</c> files inside the (already decrypted) pak files. Written
/// from the format as documented by CUE4Parse (Apache-2.0) and SkeeveAN's aion2-dat notes (MIT); own code.
/// </summary>
/// <remarks>
/// <para><c>key_manifest.dat</c>: an 8-byte header, then entries of 0x30 bytes, AES-256-ECB with BLAKE3(the manifest
/// constant) as key: <c>u64 seed, byte[32] content key, byte[8] padding</c>.</para>
/// <para>A file's seed is the first 8 bytes (LE) of BLAKE3 of its name: the table name, or
/// <c>L10NString_&lt;locale&gt;</c>. Its header keys are BLAKE3(<c>u64 root, u64 seed, i32 kind, 4 zero bytes</c>),
/// root = the first 8 bytes of BLAKE3(the header constant); kind 1 compressed table, 2 stream table, 3 text.</para>
/// <para>Tables start with <c>i32 13</c>. XOR bytes 4..11 with headerKey(1)[0..8]: <c>i32 packed, i32 type</c>. Type 2:
/// XOR 12..19 with headerKey(1)[8..16] (<c>i32 aligned, i32 raw</c>), AES-256-ECB-decrypt <c>aligned</c> bytes from
/// offset 20, skip 0x20 bytes, LZ4 block of <c>packed</c> bytes → <c>raw</c>. Otherwise XOR bytes 4..11 with
/// headerKey(2)[0..8] instead: <c>i32 size, i32 3</c>, an AES counter stream (block n = AES-ECB(key,
/// <c>u64 nonce | u64 n</c>), nonce = the first 8 bytes of BLAKE3(content key | "nonc")).</para>
/// <para>Text files start with <c>i32 2</c>; XOR 4..19 with headerKey(3)[0..16]: <c>packed, 2, aligned, raw</c>, then as
/// type 2.</para>
/// <para>Since client 2.0.11 the keys depend on the client's app version ("2.0.11"), which the manifest does not name:
/// context = BLAKE3("AION2.TableKey.AppVersion" + version), manifest key = BLAKE3(manifest constant | context); the
/// version is found by trying X.Y.Z until every record's padding is zero. The header root becomes the first 8 bytes of
/// BLAKE3(<c>u64 shuffle root</c>), shuffle root = the first 8 bytes of BLAKE3(<c>u64 header constant</c> | context), and
/// a compressed table's and a text file's four header fields (XOR 4..19 with the kind's header key) are serialized in an
/// order drawn from BLAKE3(<c>u64 shuffle root, i32 0x51</c>) (CUE4Parse pull request 471 for the texts). A stream table
/// is as before, with the new root.</para>
/// The two constants are public but are not stored in this repository: they come from a local file
/// (<c>AION2_DAT_CONSTANTS</c>).
/// </remarks>
public sealed class DatDecryptor
{
    private readonly byte[] _manifestConstant;
    private readonly ulong _headerConstant;
    private ulong _headerKeyRoot;
    /// <summary>Versioned clients: where each serialized header field goes (0 type, 1 raw, 2 packed, 3 aligned).</summary>
    private int[]? _fieldOrder;
    private readonly Dictionary<ulong, byte[]> _keys = [];

    public DatDecryptor(byte[] manifestConstant, ulong headerConstant)
    {
        _manifestConstant = manifestConstant;
        _headerConstant = headerConstant;
        _headerKeyRoot = Hash64(U64(headerConstant));
    }

    /// <summary>The client's app version the keys were derived from ("2.0.11"), or null for clients before that scheme.</summary>
    public string? AppVersion { get; private set; }

    /// <summary>Reads <c>{"manifest_hash_key_hex": "...", "header_xor_constant": "0x..."}</c>.</summary>
    public static DatDecryptor FromConstantsFile(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var key = Convert.FromHexString(doc.RootElement.GetProperty("manifest_hash_key_hex").GetString()!);
        string xor = doc.RootElement.GetProperty("header_xor_constant").GetString()!;
        return new DatDecryptor(key, Convert.ToUInt64(xor, 16));
    }

    public int KeyCount => _keys.Count;

    public static ulong Seed(string name) =>
        BinaryPrimitives.ReadUInt64LittleEndian(Blake3.Hasher.Hash(Encoding.UTF8.GetBytes(name)).AsSpan());

    public void LoadManifest(byte[] data)
    {
        if (data.Length < 8 || (data.Length - 8) % 0x30 != 0)
        {
            throw new GameDataFormatException($"key_manifest.dat has {data.Length} bytes");
        }

        var plain = AesEcb(data.AsSpan(8), Blake3.Hasher.Hash(_manifestConstant).AsSpan().ToArray(), decrypt: true);
        if (!ZeroPadding(plain))
        {
            (plain, var context) = VersionedManifest(data)
                ?? throw new GameDataFormatException("key_manifest.dat: no app version 0.0.0 to 9.9.999 decrypts it (a new key scheme?)");
            ulong shuffleRoot = Hash64([.. U64(_headerConstant), .. context]);
            _headerKeyRoot = Hash64(U64(shuffleRoot));
            _fieldOrder = FieldOrder(shuffleRoot);
        }

        for (int o = 0; o < plain.Length; o += 0x30)
        {
            _keys[BinaryPrimitives.ReadUInt64LittleEndian(plain.AsSpan(o))] = plain.AsSpan(o + 8, 32).ToArray();
        }
    }

    private (byte[] Plain, byte[] Context)? VersionedManifest(byte[] data)
    {
        var prefix = "AION2.TableKey.AppVersion"u8.ToArray();
        for (int major = 0; major < 10; major++)
        for (int minor = 0; minor < 10; minor++)
        for (int patch = 0; patch < 1000; patch++)
        {
            string version = $"{major}.{minor}.{patch}";
            byte[] context = Blake3.Hasher.Hash([.. prefix, .. Encoding.ASCII.GetBytes(version)]).AsSpan().ToArray();
            byte[] key = Blake3.Hasher.Hash([.. _manifestConstant, .. context]).AsSpan().ToArray();
            if (!ZeroPadding(AesEcb(data.AsSpan(8, 0x30), key, decrypt: true)))
            {
                continue;
            }

            var plain = AesEcb(data.AsSpan(8), key, decrypt: true);
            if (ZeroPadding(plain))
            {
                AppVersion = version;
                return (plain, context);
            }
        }

        return null;
    }

    /// <summary>Every 0x30-byte record ends in 8 zero bytes: the manifest was decrypted with the right key.</summary>
    private static bool ZeroPadding(ReadOnlySpan<byte> plain)
    {
        for (int o = 40; o + 8 <= plain.Length; o += 0x30)
        {
            if (BinaryPrimitives.ReadUInt64LittleEndian(plain[o..]) != 0)
            {
                return false;
            }
        }

        return plain.Length >= 0x30;
    }

    private static int[] FieldOrder(ulong shuffleRoot)
    {
        Span<byte> input = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(input, shuffleRoot);
        BinaryPrimitives.WriteInt32LittleEndian(input[8..], 0x51);
        var shuffle = Blake3.Hasher.Hash(input).AsSpan().ToArray();
        int[] order = [0, 1, 2, 3];
        for (int i = 3; i > 0; i--)
        {
            int other = shuffle[3 - i] % (i + 1);
            (order[i], order[other]) = (order[other], order[i]);
        }

        return order;
    }

    /// <summary>Versioned header: 16 bytes at offset 4 XORed with <paramref name="key"/>, in the shuffled order.</summary>
    private (int Type, int Raw, int Packed, int Aligned) ShuffledHeader(byte[] dat, byte[] key)
    {
        var head = Xored(dat.AsSpan(4, 16), key.AsSpan(0, 16));
        var fields = new int[4];
        for (int i = 0; i < 4; i++)
        {
            fields[_fieldOrder![i]] = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(i * 4));
        }

        return (fields[0], fields[1], fields[2], fields[3]);
    }

    private static byte[] U64(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return bytes;
    }

    private static ulong Hash64(byte[] input) => BinaryPrimitives.ReadUInt64LittleEndian(Blake3.Hasher.Hash(input).AsSpan());

    /// <summary>A data table (<c>Data/Table/&lt;name&gt;.dat</c>) without its protection layer.</summary>
    public byte[] Table(byte[] dat, string name)
    {
        if (dat.Length < 20 || BinaryPrimitives.ReadInt32LittleEndian(dat) != 13)
        {
            throw new GameDataFormatException($"{name}.dat is not a table container");
        }

        ulong seed = Seed(name);
        var key = ContentKey(seed, name);
        var k1 = HeaderKey(seed, 1);
        if (_fieldOrder is not null)
        {
            var shuffled = ShuffledHeader(dat, k1);
            if (shuffled.Type == 2)
            {
                return AesLz4(dat, shuffled.Aligned, key, shuffled.Packed, shuffled.Raw, name);
            }
        }

        var head = Xored(dat.AsSpan(4, 8), k1.AsSpan(0, 8));
        int packed = BinaryPrimitives.ReadInt32LittleEndian(head);
        int type = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4));
        if (type == 2 && _fieldOrder is null)
        {
            var sizes = Xored(dat.AsSpan(12, 8), k1.AsSpan(8, 8));
            int aligned = BinaryPrimitives.ReadInt32LittleEndian(sizes);
            int raw = BinaryPrimitives.ReadInt32LittleEndian(sizes.AsSpan(4));
            return AesLz4(dat, aligned, key, packed, raw, name);
        }

        head = Xored(dat.AsSpan(4, 8), HeaderKey(seed, 2).AsSpan(0, 8));
        int size = BinaryPrimitives.ReadInt32LittleEndian(head);
        type = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4));
        if (type != 3 || size != dat.Length - 12)
        {
            throw new GameDataFormatException($"{name}.dat: unknown container type {type}");
        }

        return AesCounter(dat.AsSpan(12, size), key);
    }

    /// <summary>A text file (<c>L10N/Text/&lt;locale&gt;/L10NString.dat</c>) without its protection layer.</summary>
    public byte[] Text(byte[] dat, string locale)
    {
        if (dat.Length < 20 || BinaryPrimitives.ReadInt32LittleEndian(dat) != 2)
        {
            throw new GameDataFormatException($"L10NString.dat ({locale}) is not a text container");
        }

        ulong seed = Seed("L10NString_" + locale);
        var key = ContentKey(seed, "L10NString_" + locale);
        if (_fieldOrder is not null)
        {
            var shuffled = ShuffledHeader(dat, HeaderKey(seed, 3));
            return shuffled.Type == 2
                ? AesLz4(dat, shuffled.Aligned, key, shuffled.Packed, shuffled.Raw, "L10NString " + locale)
                : throw new GameDataFormatException($"L10NString.dat ({locale}): container type {shuffled.Type}");
        }

        var head = Xored(dat.AsSpan(4, 16), HeaderKey(seed, 3).AsSpan(0, 16));
        int packed = BinaryPrimitives.ReadInt32LittleEndian(head);
        int type = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4));
        int aligned = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(8));
        int raw = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(12));
        return type == 2
            ? AesLz4(dat, aligned, key, packed, raw, "L10NString " + locale)
            : throw new GameDataFormatException($"L10NString.dat ({locale}): container type {type}");
    }

    private byte[] ContentKey(ulong seed, string name) =>
        _keys.TryGetValue(seed, out var key) ? key : throw new GameDataFormatException($"no content key for {name} in key_manifest.dat");

    private byte[] HeaderKey(ulong seed, int kind)
    {
        Span<byte> input = stackalloc byte[24];
        input.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(input, _headerKeyRoot);
        BinaryPrimitives.WriteUInt64LittleEndian(input[8..], seed);
        BinaryPrimitives.WriteInt32LittleEndian(input[16..], kind);
        return Blake3.Hasher.Hash(input).AsSpan().ToArray();
    }

    private static byte[] Xored(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key)
    {
        var result = data.ToArray();
        for (int i = 0; i < result.Length; i++)
        {
            result[i] ^= key[i % key.Length];
        }

        return result;
    }

    private static byte[] AesLz4(byte[] dat, int aligned, byte[] key, int packed, int raw, string name)
    {
        if (aligned < 0x20 || 20 + aligned > dat.Length || packed > aligned - 0x20 || raw < 0)
        {
            throw new GameDataFormatException($"{name}: bad sizes (aligned {aligned}, packed {packed}, raw {raw})");
        }

        var plain = AesEcb(dat.AsSpan(20, aligned), key, decrypt: true);
        var output = new byte[raw];
        int n = LZ4Codec.Decode(plain.AsSpan(0x20, packed), output);
        return n == raw ? output : throw new GameDataFormatException($"{name}: LZ4 gave {n} of {raw} bytes");
    }

    private static byte[] AesCounter(ReadOnlySpan<byte> data, byte[] key)
    {
        var nonceInput = new byte[key.Length + 4];
        key.CopyTo(nonceInput, 0);
        "nonc"u8.CopyTo(nonceInput.AsSpan(key.Length));
        ulong nonce = BinaryPrimitives.ReadUInt64LittleEndian(Blake3.Hasher.Hash(nonceInput).AsSpan());

        int blocks = (data.Length + 15) / 16;
        var counters = new byte[blocks * 16];
        for (int i = 0; i < blocks; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(counters.AsSpan(i * 16), nonce);
            BinaryPrimitives.WriteUInt64LittleEndian(counters.AsSpan(i * 16 + 8), (ulong) i);
        }

        var stream = AesEcb(counters, key, decrypt: false);
        var output = data.ToArray();
        for (int i = 0; i < output.Length; i++)
        {
            output[i] ^= stream[i];
        }

        return output;
    }

    private static byte[] AesEcb(ReadOnlySpan<byte> data, byte[] key, bool decrypt)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        int length = data.Length & ~15;
        return decrypt ? aes.DecryptEcb(data[..length], PaddingMode.None) : aes.EncryptEcb(data[..length], PaddingMode.None);
    }
}
