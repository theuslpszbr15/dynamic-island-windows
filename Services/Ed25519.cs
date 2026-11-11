using System.Numerics;
using System.Security.Cryptography;

namespace DynamicIsland.Services;

/// <summary>
/// Ed25519 signatures (RFC 8032), only what the relay needs: a public key from a 32-byte seed and a signature.
/// .NET has no Ed25519 and the relay's challenge is signed once per connection, so this is the RFC's reference
/// algorithm on BigInteger (a few milliseconds per signature) instead of a new dependency. It is checked against the
/// RFC 8032 test vectors (tests) and against the relay's WebCrypto verify. The key only proves to the relay that this
/// PC owns its room; the link itself is protected by TLS and the pairing token.
/// </summary>
public static class Ed25519
{
    static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    static readonly BigInteger D = Mod(-121665 * Inverse(121666));
    static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);
    static readonly Point G = BasePoint();

    readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    /// <summary>The 32-byte public key for a 32-byte seed (the private key).</summary>
    public static byte[] PublicKey(ReadOnlySpan<byte> seed)
    {
        var (a, _) = Expand(seed);
        return Encode(Multiply(a, G));
    }

    /// <summary>The 64-byte signature of <paramref name="message"/> with the key from <paramref name="seed"/>.</summary>
    public static byte[] Sign(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> message)
    {
        var (a, prefix) = Expand(seed);
        var publicKey = Encode(Multiply(a, G));
        var r = HashToScalar(prefix, message);
        var encodedR = Encode(Multiply(r, G));
        var h = HashToScalar(encodedR, publicKey, message);
        var s = (r + h * a) % L;
        var signature = new byte[64];
        encodedR.CopyTo(signature, 0);
        ToBytes(s).CopyTo(signature, 32);
        return signature;
    }

    public static byte[] NewSeed() => RandomNumberGenerator.GetBytes(32);

    static (BigInteger A, byte[] Prefix) Expand(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != 32) throw new ArgumentException("An Ed25519 seed is 32 bytes.", nameof(seed));
        var h = SHA512.HashData(seed);
        var a = FromBytes(h.AsSpan(0, 32));
        a &= (BigInteger.One << 254) - 8;
        a |= BigInteger.One << 254;
        return (a, h[32..]);
    }

    static BigInteger HashToScalar(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        hash.AppendData(first);
        hash.AppendData(second);
        return FromBytes(hash.GetHashAndReset()) % L;
    }

    static BigInteger HashToScalar(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second, ReadOnlySpan<byte> third)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        hash.AppendData(first);
        hash.AppendData(second);
        hash.AppendData(third);
        return FromBytes(hash.GetHashAndReset()) % L;
    }

    static Point Add(Point p, Point q)
    {
        var a = Mod((p.Y - p.X) * (q.Y - q.X));
        var b = Mod((p.Y + p.X) * (q.Y + q.X));
        var c = Mod(2 * p.T * q.T * D);
        var d = Mod(2 * p.Z * q.Z);
        BigInteger e = b - a, f = d - c, g = d + c, h = b + a;
        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    static Point Multiply(BigInteger s, Point p)
    {
        var q = new Point(0, 1, 1, 0);
        while (s > 0)
        {
            if (!s.IsEven) q = Add(q, p);
            p = Add(p, p);
            s >>= 1;
        }
        return q;
    }

    static byte[] Encode(Point p)
    {
        var zInverse = Inverse(p.Z);
        var x = Mod(p.X * zInverse);
        var y = Mod(p.Y * zInverse);
        var bytes = ToBytes(y);
        if (!x.IsEven) bytes[31] |= 0x80;
        return bytes;
    }

    static Point BasePoint()
    {
        var y = Mod(4 * Inverse(5));
        var x2 = Mod((y * y - 1) * Inverse(D * y * y + 1));
        var x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (Mod(x * x - x2) != 0) x = Mod(x * SqrtM1);
        if (!x.IsEven) x = P - x; // the base point's x is even
        return new Point(x, y, 1, Mod(x * y));
    }

    static BigInteger Mod(BigInteger v)
    {
        var r = v % P;
        return r.Sign < 0 ? r + P : r;
    }

    static BigInteger Inverse(BigInteger v) => BigInteger.ModPow(Mod(v), P - 2, P);

    static BigInteger FromBytes(ReadOnlySpan<byte> littleEndian) => new(littleEndian, isUnsigned: true, isBigEndian: false);

    static byte[] ToBytes(BigInteger v)
    {
        var bytes = new byte[32];
        v.TryWriteBytes(bytes, out _, isUnsigned: true, isBigEndian: false);
        return bytes;
    }
}
