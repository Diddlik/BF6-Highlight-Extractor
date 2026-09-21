using Bf6Highlights.Ui;
using Xunit;

namespace Bf6Highlights.Tests;

public sealed class RegionMathTests
{
    [Theory]
    [InlineData(1920, 1080, 1.0)]
    [InlineData(1920, 1080, 1.5)]
    [InlineData(1920, 1080, 2.0)]
    [InlineData(1280, 720, 1.0)]
    [InlineData(1280, 720, 1.5)]
    [InlineData(1280, 720, 2.0)]
    public void SurfaceRoundTripIsIndependentOfDipScale(int width, int height, double scale)
    {
        var expected = new RegionSettings { X = 173, Y = 91, Width = 511, Height = 223 };
        var surfaceWidth = width / scale;
        var surfaceHeight = height / scale;

        var displayed = RegionMath.ToSurface(expected, width, height, surfaceWidth, surfaceHeight);
        var actual = RegionMath.ToVideo(displayed.Left, displayed.Top, displayed.Right,
            displayed.Bottom, width, height, surfaceWidth, surfaceHeight);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FullFrameAccountsForLetterboxing()
    {
        var displayed = RegionMath.ToSurface(
            new RegionSettings { X = 0, Y = 0, Width = 1920, Height = 1080 },
            1920, 1080, 1000, 1000);

        Assert.Equal(0, displayed.X, 6);
        Assert.Equal(218.75, displayed.Y, 6);
        Assert.Equal(1000, displayed.Width, 6);
        Assert.Equal(562.5, displayed.Height, 6);
    }

    [Fact]
    public void FullFrameAccountsForPillarboxing()
    {
        var displayed = RegionMath.ToSurface(
            new RegionSettings { X = 0, Y = 0, Width = 1920, Height = 1080 },
            1920, 1080, 2000, 500);

        Assert.Equal(555.555556, displayed.X, 6);
        Assert.Equal(0, displayed.Y, 6);
        Assert.Equal(888.888889, displayed.Width, 6);
        Assert.Equal(500, displayed.Height, 6);
    }

    [Fact]
    public void DragOutsideTheDisplayedFrameIsClampedToTheVideo()
    {
        var region = RegionMath.ToVideo(-500, -300, 2500, 1500,
            1920, 1080, 1920, 1080);

        Assert.Equal(new RegionSettings { X = 0, Y = 0, Width = 1920, Height = 1080 }, region);
    }

    [Fact]
    public void DragEntirelyOutsideStaysInsideTheLastVideoPixel()
    {
        var region = RegionMath.ToVideo(3000, 2000, 4000, 3000,
            1920, 1080, 1920, 1080);

        Assert.Equal(new RegionSettings { X = 1919, Y = 1079, Width = 1, Height = 1 }, region);
    }

    [Theory]
    [InlineData(-10_000, 0, 0, 200)]
    [InlineData(10_000, 0, 1620, 200)]
    [InlineData(0, -10_000, 100, 0)]
    [InlineData(0, 10_000, 100, 680)]
    public void MovingARegionIsClampedAtEveryVideoEdge(
        int dx, int dy, int expectedX, int expectedY)
    {
        var source = new RegionSettings { X = 100, Y = 200, Width = 300, Height = 400 };

        var moved = RegionMath.Move(source, dx, dy, 1920, 1080);

        Assert.Equal(source.Width, moved.Width);
        Assert.Equal(source.Height, moved.Height);
        Assert.Equal(expectedX, moved.X);
        Assert.Equal(expectedY, moved.Y);
    }
}
