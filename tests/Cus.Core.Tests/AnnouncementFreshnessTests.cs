using Cus.Core.Net;

namespace Cus.Core.Tests;

/// <summary>Contatore degli annunci: un annuncio vecchio ripetuto non passa.</summary>
public class AnnouncementFreshnessTests
{
    private const string A = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string B = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Fact]
    public void AcceptsOnlyIncreasingSequences_PerDevice()
    {
        var freshness = new AnnouncementFreshness();

        Assert.True(freshness.TryAccept(A, 10, nowTicks: 0));
        Assert.False(freshness.TryAccept(A, 10, nowTicks: 1)); // ripetuto
        Assert.False(freshness.TryAccept(A, 5, nowTicks: 2));  // più vecchio
        Assert.True(freshness.TryAccept(A, 11, nowTicks: 3));
        Assert.True(freshness.TryAccept(B, 1, nowTicks: 4));   // ogni dispositivo ha il suo contatore
    }

    [Fact]
    public void ForgetsDevices_NotHeardForAWhile()
    {
        var freshness = new AnnouncementFreshness();
        var later = (long)AnnouncementFreshness.ForgetAfter.TotalMilliseconds;

        Assert.True(freshness.TryAccept(A, 10, nowTicks: 0));
        Assert.True(freshness.TryAccept(A, 3, nowTicks: later));
    }

    [Fact]
    public void IsBounded_AndMakesRoomByForgettingStaleDevices()
    {
        var freshness = new AnnouncementFreshness(capacity: 1);
        var later = (long)AnnouncementFreshness.ForgetAfter.TotalMilliseconds;

        Assert.True(freshness.TryAccept(A, 1, nowTicks: 0));
        Assert.False(freshness.TryAccept(B, 1, nowTicks: 1));
        Assert.True(freshness.TryAccept(A, 2, nowTicks: 2)); // chi è già ricordato passa sempre
        Assert.True(freshness.TryAccept(B, 1, nowTicks: 2 + later));
    }
}
