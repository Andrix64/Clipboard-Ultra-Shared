using System.Security.Cryptography;
using Cus.Core.Crypto;

namespace Cus.Core.Tests;

/// <summary>Smoke test di libsodium: la libreria nativa si carica e le funzioni ristretto255 ci sono.</summary>
public class SodiumTests
{
    [Fact]
    public void Initializes()
    {
        Sodium.EnsureInitialized();
    }

    [Fact]
    public void Ristretto255FromHash_ReturnsValidDeterministicPoint()
    {
        var hash = SHA512.HashData("CPaceRistretto255"u8);

        var first = Sodium.Ristretto255FromHash(hash);
        var second = Sodium.Ristretto255FromHash(hash);

        Assert.Equal(Sodium.Ristretto255Bytes, first.Length);
        Assert.Equal(first, second);
        Assert.True(Sodium.Ristretto255IsValidPoint(first));
    }

    [Fact]
    public void Ristretto255IsValidPoint_RejectsGarbage()
    {
        Assert.False(Sodium.Ristretto255IsValidPoint(Enumerable.Repeat((byte)0xFF, 32).ToArray()));
        Assert.False(Sodium.Ristretto255IsValidPoint(new byte[31]));
    }

    [Fact]
    public void Ristretto255FromHash_RejectsWrongLength()
    {
        Assert.Throws<ArgumentException>(() => Sodium.Ristretto255FromHash(new byte[32]));
    }
}
