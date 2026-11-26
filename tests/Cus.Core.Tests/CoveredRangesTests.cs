using Cus.Core.Node;

namespace Cus.Core.Tests;

public class CoveredRangesTests
{
    [Fact]
    public void SequentialReads_CountEachByteOnce()
    {
        var ranges = new CusNode.CoveredRanges();

        Assert.Equal(100, ranges.Add(0, 100));
        Assert.Equal(100, ranges.Add(100, 200));
        Assert.Equal(0, ranges.Add(0, 200));
    }

    [Fact]
    public void ReadingOnlyTheTail_CountsOnlyTheTail()
    {
        var ranges = new CusNode.CoveredRanges();

        Assert.Equal(10, ranges.Add(990, 1000));
        Assert.Equal(990, ranges.Add(0, 1000));
    }

    [Fact]
    public void OverlappingAndOutOfOrderReads_CountOnlyNewBytes()
    {
        var ranges = new CusNode.CoveredRanges();

        Assert.Equal(10, ranges.Add(50, 60));
        Assert.Equal(10, ranges.Add(10, 20));
        Assert.Equal(30, ranges.Add(15, 55)); // 20..50 nuovi
        Assert.Equal(5, ranges.Add(5, 60));   // 5..10 nuovi
        Assert.Equal(0, ranges.Add(30, 30));
    }
}
