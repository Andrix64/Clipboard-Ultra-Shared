using System.Security.Cryptography;

namespace Cus.Core.Crypto;

/// <summary>Ruolo nel flusso initiator-responder di CPace (A = Initiator, B = Responder).</summary>
public enum CPaceRole
{
    Initiator,
    Responder,
}

/// <summary>Abort del protocollo CPace: messaggio del peer non valido o segreto K uguale a G.I.</summary>
public sealed class CPaceException : Exception
{
    public CPaceException()
    {
    }

    public CPaceException(string message)
        : base(message)
    {
    }

    public CPaceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// CPace-ristretto255-SHA512 secondo draft-irtf-cfrg-cpace-21, modalità initiator-responder
/// (transcript_ir). Un'istanza vale per una sola esecuzione: dopo <see cref="DeriveIsk"/> lo
/// scalare segreto viene azzerato.
/// </summary>
public sealed class CPace
{
    /// <summary>G_Ristretto255.DSI.</summary>
    internal static ReadOnlySpan<byte> Dsi => "CPaceRistretto255"u8;

    /// <summary>G.DSI || b"_ISK".</summary>
    internal static ReadOnlySpan<byte> DsiIsk => "CPaceRistretto255_ISK"u8;

    /// <summary>Dimensione del blocco di input di SHA-512 (H.s_in_bytes).</summary>
    internal const int HashBlockBytes = 128;

    /// <summary>Lunghezza di ISK: H.b_in_bytes di SHA-512.</summary>
    public const int IskBytes = 64;

    /// <summary>Lunghezza del messaggio Y (codifica ristretto255).</summary>
    public const int MessageBytes = Sodium.Ristretto255Bytes;

    private readonly CPaceRole _role;
    private readonly byte[] _sid;
    private readonly byte[] _ownAd;
    private readonly byte[] _scalar;
    private readonly byte[] _message;
    private bool _used;

    private CPace(CPaceRole role, ReadOnlySpan<byte> sid, ReadOnlySpan<byte> ownAd, byte[] scalar, byte[] message)
    {
        _role = role;
        _sid = sid.ToArray();
        _ownAd = ownAd.ToArray();
        _scalar = scalar;
        _message = message;
    }

    /// <summary>Messaggio pubblico Y di questa parte (32 byte), da inviare al peer insieme a ownAd.</summary>
    public byte[] Message => (byte[])_message.Clone();

    /// <summary>Calcola il generatore, campiona lo scalare e prepara il messaggio Y.</summary>
    public static CPace Create(CPaceRole role, ReadOnlySpan<byte> prs, ReadOnlySpan<byte> ci, ReadOnlySpan<byte> sid, ReadOnlySpan<byte> ownAd)
    {
        var scalar = SampleScalar();
        try
        {
            return CreateWithScalar(role, prs, ci, sid, ownAd, scalar);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scalar);
        }
    }

    /// <summary>Come <see cref="Create"/>, ma con lo scalare fornito (per i test vector).</summary>
    internal static CPace CreateWithScalar(CPaceRole role, ReadOnlySpan<byte> prs, ReadOnlySpan<byte> ci, ReadOnlySpan<byte> sid, ReadOnlySpan<byte> ownAd, ReadOnlySpan<byte> scalar)
    {
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role));
        if (scalar.Length != Sodium.Ristretto255ScalarBytes)
            throw new ArgumentException($"Servono {Sodium.Ristretto255ScalarBytes} byte.", nameof(scalar));

        var generator = CalculateGenerator(prs, ci, sid);
        var message = ScalarMult(scalar, generator);
        return new CPace(role, sid, ownAd, scalar.ToArray(), message);
    }

    /// <summary>
    /// Calcola ISK = H.hash(lv_cat(DSI || "_ISK", sid, K) || transcript_ir(Ya, ADa, Yb, ADb)).
    /// Lancia <see cref="CPaceException"/> se il messaggio del peer non decodifica o se K = G.I.
    /// </summary>
    public byte[] DeriveIsk(ReadOnlySpan<byte> peerMessage, ReadOnlySpan<byte> peerAd)
    {
        if (_used)
            throw new InvalidOperationException("Istanza CPace già usata.");
        _used = true;

        var k = ScalarMultVfy(_scalar, peerMessage);
        CryptographicOperations.ZeroMemory(_scalar);
        try
        {
            if (IsIdentity(k))
                throw new CPaceException("Messaggio CPace del peer non valido.");

            var transcript = _role == CPaceRole.Initiator
                ? TranscriptIr(_message, _ownAd, peerMessage, peerAd)
                : TranscriptIr(peerMessage, peerAd, _message, _ownAd);
            return ComputeIsk(_sid, k, transcript);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(k);
        }
    }

    // --- Gruppo G_Ristretto255 (sezione 8.3) ---

    /// <summary>
    /// G.sample_scalar(): 32 byte casuali con i bit oltre il 252° azzerati, letti little-endian.
    /// Lo zero (probabilità 2^-252) viene scartato.
    /// </summary>
    internal static byte[] SampleScalar()
    {
        var scalar = new byte[Sodium.Ristretto255ScalarBytes];
        do
        {
            RandomNumberGenerator.Fill(scalar);
            scalar[^1] &= 0x0F;
        }
        while (IsIdentity(scalar));

        return scalar;
    }

    /// <summary>G.calculate_generator: element_derivation(SHA-512(generator_string)), codificato.</summary>
    internal static byte[] CalculateGenerator(ReadOnlySpan<byte> prs, ReadOnlySpan<byte> ci, ReadOnlySpan<byte> sid)
    {
        var genStr = GeneratorString(Dsi, prs, ci, sid, HashBlockBytes);
        Span<byte> hash = stackalloc byte[Sodium.Ristretto255HashBytes];
        try
        {
            SHA512.HashData(genStr, hash);
            return Sodium.Ristretto255FromHash(hash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(genStr);
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    /// <summary>G.scalar_mult(y, g): lancia se il risultato non è calcolabile (g non valido o y*g = I).</summary>
    internal static byte[] ScalarMult(ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> point)
    {
        var result = new byte[Sodium.Ristretto255Bytes];
        if (!Sodium.Ristretto255ScalarMult(result, scalar, point))
            throw new CPaceException("Moltiplicazione scalare ristretto255 fallita.");
        return result;
    }

    /// <summary>G.scalar_mult_vfy(y, X): restituisce G.I se X non decodifica o se y*X è il neutro.</summary>
    internal static byte[] ScalarMultVfy(ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> point)
    {
        var result = new byte[Sodium.Ristretto255Bytes];
        if (!Sodium.Ristretto255ScalarMult(result, scalar, point))
            CryptographicOperations.ZeroMemory(result);
        return result;
    }

    /// <summary>Confronto con G.I (32 byte a zero) a tempo costante.</summary>
    internal static bool IsIdentity(ReadOnlySpan<byte> point)
    {
        var acc = 0;
        foreach (var b in point)
            acc |= b;
        return acc == 0;
    }

    /// <summary>ISK = SHA-512(lv_cat(G.DSI || b"_ISK", sid, K) || transcript).</summary>
    internal static byte[] ComputeIsk(ReadOnlySpan<byte> sid, ReadOnlySpan<byte> k, ReadOnlySpan<byte> transcript)
    {
        var prefix = LvCat(DsiIsk, sid, k);
        var input = new byte[prefix.Length + transcript.Length];
        try
        {
            prefix.CopyTo(input, 0);
            transcript.CopyTo(input.AsSpan(prefix.Length));
            return SHA512.HashData(input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(prefix);
            CryptographicOperations.ZeroMemory(input);
        }
    }

    /// <summary>Session id opzionale (sezione 10.6): H.hash(b"CPaceSidOutput" || transcript).</summary>
    internal static byte[] SidOutput(ReadOnlySpan<byte> transcript) =>
        SHA512.HashData(Concat("CPaceSidOutput"u8, transcript));

    // --- Funzioni sulle stringhe (appendice A) ---

    /// <summary>generator_string(DSI, PRS, CI, sid, s_in_bytes) dell'appendice A.2.</summary>
    internal static byte[] GeneratorString(ReadOnlySpan<byte> dsi, ReadOnlySpan<byte> prs, ReadOnlySpan<byte> ci, ReadOnlySpan<byte> sid, int sInBytes)
    {
        var lenZpad = Math.Max(0, sInBytes - 1 - PrependLenSize(prs.Length) - PrependLenSize(dsi.Length));
        var zpad = new byte[lenZpad];
        return LvCat(dsi, prs, zpad, ci, sid);
    }

    /// <summary>prepend_len: lunghezza in LEB128 seguita dai dati.</summary>
    internal static byte[] PrependLen(ReadOnlySpan<byte> data)
    {
        var result = new byte[PrependLenSize(data.Length)];
        WritePrependLen(data, result);
        return result;
    }

    /// <summary>lv_cat(a0, a1, ...) = prepend_len(a0) || prepend_len(a1) || ...</summary>
    internal static byte[] LvCat(params ReadOnlySpan<byte[]> args)
    {
        var total = 0;
        foreach (var a in args)
            total += PrependLenSize(a.Length);

        var result = new byte[total];
        var offset = 0;
        foreach (var a in args)
            offset += WritePrependLen(a, result.AsSpan(offset));
        return result;
    }

    private static byte[] LvCat(ReadOnlySpan<byte> a0, ReadOnlySpan<byte> a1, ReadOnlySpan<byte> a2)
    {
        var result = new byte[PrependLenSize(a0.Length) + PrependLenSize(a1.Length) + PrependLenSize(a2.Length)];
        var offset = WritePrependLen(a0, result);
        offset += WritePrependLen(a1, result.AsSpan(offset));
        WritePrependLen(a2, result.AsSpan(offset));
        return result;
    }

    private static byte[] LvCat(ReadOnlySpan<byte> a0, ReadOnlySpan<byte> a1, ReadOnlySpan<byte> a2, ReadOnlySpan<byte> a3, ReadOnlySpan<byte> a4)
    {
        var result = new byte[PrependLenSize(a0.Length) + PrependLenSize(a1.Length) + PrependLenSize(a2.Length)
            + PrependLenSize(a3.Length) + PrependLenSize(a4.Length)];
        var offset = WritePrependLen(a0, result);
        offset += WritePrependLen(a1, result.AsSpan(offset));
        offset += WritePrependLen(a2, result.AsSpan(offset));
        offset += WritePrependLen(a3, result.AsSpan(offset));
        WritePrependLen(a4, result.AsSpan(offset));
        return result;
    }

    /// <summary>transcript_ir(Ya, ADa, Yb, ADb) = lv_cat(Ya, ADa) || lv_cat(Yb, ADb).</summary>
    internal static byte[] TranscriptIr(ReadOnlySpan<byte> ya, ReadOnlySpan<byte> adA, ReadOnlySpan<byte> yb, ReadOnlySpan<byte> adB)
    {
        var result = new byte[PrependLenSize(ya.Length) + PrependLenSize(adA.Length) + PrependLenSize(yb.Length) + PrependLenSize(adB.Length)];
        var offset = WritePrependLen(ya, result);
        offset += WritePrependLen(adA, result.AsSpan(offset));
        offset += WritePrependLen(yb, result.AsSpan(offset));
        WritePrependLen(adB, result.AsSpan(offset));
        return result;
    }

    /// <summary>transcript_oc(Ya, ADa, Yb, ADb) = o_cat(lv_cat(Ya, ADa), lv_cat(Yb, ADb)). Solo per i test vector.</summary>
    internal static byte[] TranscriptOc(ReadOnlySpan<byte> ya, ReadOnlySpan<byte> adA, ReadOnlySpan<byte> yb, ReadOnlySpan<byte> adB) =>
        OCat(LvCat([ya.ToArray(), adA.ToArray()]), LvCat([yb.ToArray(), adB.ToArray()]));

    /// <summary>o_cat: b"oc" seguito dalla stringa lessicograficamente maggiore e poi dall'altra.</summary>
    internal static byte[] OCat(ReadOnlySpan<byte> bytes1, ReadOnlySpan<byte> bytes2) =>
        LexicographicallyLarger(bytes1, bytes2)
            ? Concat("oc"u8, bytes1, bytes2)
            : Concat("oc"u8, bytes2, bytes1);

    /// <summary>lexiographically_larger dell'appendice A.3.1.</summary>
    internal static bool LexicographicallyLarger(ReadOnlySpan<byte> bytes1, ReadOnlySpan<byte> bytes2)
    {
        var minLen = Math.Min(bytes1.Length, bytes2.Length);
        for (var m = 0; m < minLen; m++)
        {
            if (bytes1[m] > bytes2[m])
                return true;
            if (bytes1[m] < bytes2[m])
                return false;
        }

        return bytes1.Length > bytes2.Length;
    }

    private static int PrependLenSize(int length)
    {
        var size = 1;
        for (var l = (uint)length >> 7; l != 0; l >>= 7)
            size++;
        return size + length;
    }

    /// <summary>Scrive prepend_len(data) in destination e restituisce i byte scritti.</summary>
    private static int WritePrependLen(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        var offset = 0;
        var length = (uint)data.Length;
        while (true)
        {
            if (length < 128)
                destination[offset++] = (byte)length;
            else
                destination[offset++] = (byte)((length & 0x7F) | 0x80);
            length >>= 7;
            if (length == 0)
                break;
        }

        data.CopyTo(destination[offset..]);
        return offset + data.Length;
    }

    private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => Concat(a, b, []);

    private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c)
    {
        var result = new byte[a.Length + b.Length + c.Length];
        a.CopyTo(result);
        b.CopyTo(result.AsSpan(a.Length));
        c.CopyTo(result.AsSpan(a.Length + b.Length));
        return result;
    }
}
