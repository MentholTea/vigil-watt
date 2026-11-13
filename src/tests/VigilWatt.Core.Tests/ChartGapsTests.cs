using VigilWatt.App.Views;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 曲线断档三档分类与分段（实线 / 虚线 / 断开）。
/// 2026/11/13 22:35:00 fred1
/// 阈值取值依据真机采样分布，见 ChartGaps 与 docs/hardware/logitech.md。
/// </summary>
public class ChartGapsTests
{
    private static readonly TimeSpan Solid = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Break = TimeSpan.FromHours(4);

    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 8, 0, 0, TimeSpan.FromHours(8));

    private static List<(DateTimeOffset Time, int Percent)> Build(params double[] minutesFromT0)
        => minutesFromT0.Select(m => (T0.AddMinutes(m), 50)).ToList();

    [Fact]
    public void G1_阈值为实线虚线断档三档()
    {
        Assert.Equal(ChartGaps.Link.Solid, ChartGaps.Classify(TimeSpan.FromMinutes(10), Solid, Break));
        Assert.Equal(ChartGaps.Link.Solid, ChartGaps.Classify(TimeSpan.FromMinutes(30), Solid, Break));
        Assert.Equal(ChartGaps.Link.Dashed, ChartGaps.Classify(TimeSpan.FromMinutes(31), Solid, Break));
        Assert.Equal(ChartGaps.Link.Dashed, ChartGaps.Classify(TimeSpan.FromHours(4), Solid, Break));
        Assert.Equal(ChartGaps.Link.Break, ChartGaps.Classify(TimeSpan.FromHours(4.1), Solid, Break));
    }

    [Fact]
    public void G2_实线阈值随采样间隔放大但不低于30分钟()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), ChartGaps.SolidLimitFor(5));
        Assert.Equal(TimeSpan.FromMinutes(30), ChartGaps.SolidLimitFor(10));   // 10×3=30
        Assert.Equal(TimeSpan.FromMinutes(45), ChartGaps.SolidLimitFor(15));   // 15×3=45
        Assert.Equal(TimeSpan.FromMinutes(30), ChartGaps.SolidLimitFor(0));    // 非法值兜底
    }

    [Fact]
    public void G3_全部连续时只产生一段()
    {
        var pts = Build(0, 10, 20, 30);
        var segs = ChartGaps.Split(pts, p => p.Time, Solid, Break);
        Assert.Single(segs);
        Assert.Equal(0, segs[0].Start);
        Assert.Equal(3, segs[0].End);
        Assert.Equal(ChartGaps.Link.Solid, segs[0].Incoming);
    }

    [Fact]
    public void G4_单个采样点自成一段()
    {
        var segs = ChartGaps.Split(Build(0), p => p.Time, Solid, Break);
        Assert.Single(segs);
        Assert.Equal(0, segs[0].Start);
        Assert.Equal(0, segs[0].End);
    }

    [Fact]
    public void G5_空序列返回空()
    {
        Assert.Empty(ChartGaps.Split(Array.Empty<(DateTimeOffset, int)>(), p => p.Item1, Solid, Break));
    }

    [Fact]
    public void G6_虚线间隔拆段且新段标记为虚线()
    {
        // 0,10 连续；到 60 分钟（间隔 50 分）→ 虚线
        var pts = Build(0, 10, 60, 70);
        var segs = ChartGaps.Split(pts, p => p.Time, Solid, Break);

        Assert.Equal(2, segs.Count);
        Assert.Equal((0, 1), (segs[0].Start, segs[0].End));
        Assert.Equal(ChartGaps.Link.Solid, segs[0].Incoming);
        Assert.Equal((2, 3), (segs[1].Start, segs[1].End));
        Assert.Equal(ChartGaps.Link.Dashed, segs[1].Incoming);
    }

    [Fact]
    public void G7_超长间隔完全断开()
    {
        // 到 600 分钟（10 小时）→ 断开
        var pts = Build(0, 10, 600, 610);
        var segs = ChartGaps.Split(pts, p => p.Time, Solid, Break);

        Assert.Equal(2, segs.Count);
        Assert.Equal((0, 1), (segs[0].Start, segs[0].End));
        Assert.Equal((2, 3), (segs[1].Start, segs[1].End));
        Assert.Equal(ChartGaps.Link.Break, segs[1].Incoming);
    }

    [Fact]
    public void G8_段之间不共享端点()
    {
        var pts = Build(0, 10, 600, 610, 1200);
        var segs = ChartGaps.Split(pts, p => p.Time, Solid, Break);

        for (int i = 1; i < segs.Count; i++)
            Assert.True(segs[i].Start > segs[i - 1].End,
                $"第 {i} 段起点 {segs[i].Start} 应大于上一段终点 {segs[i - 1].End}");
    }

    [Fact]
    public void G9_段覆盖全部采样点且不重不漏()
    {
        var pts = Build(0, 10, 60, 70, 600, 610);
        var segs = ChartGaps.Split(pts, p => p.Time, Solid, Break);

        int covered = segs.Sum(s => s.End - s.Start + 1);
        Assert.Equal(pts.Count, covered);

        var indices = segs.SelectMany(s => Enumerable.Range(s.Start, s.End - s.Start + 1)).ToList();
        Assert.Equal(Enumerable.Range(0, pts.Count), indices);
    }

    [Fact]
    public void G10_真机夜间关机场景判为断开()
    {
        // 真机 11-13 20:43 -> 11-14 07:58（约 11 小时），必须断开而非连线
        var pts = Build(0, 10, 11 * 60 + 30);
        var segs = ChartGaps.Split(pts, p => p.Time, Solid, Break);
        Assert.Equal(ChartGaps.Link.Break, segs[^1].Incoming);
    }

    [Fact]
    public void G11_真机采样抖动场景判为虚线而非裂口()
    {
        // 真机 78 处 15~60 分钟断口、电量几乎不变 —— 归到虚线档，不再画成裂口
        var pts = Build(0, 10, 60);
        var segs = ChartGaps.Split(pts, p => p.Time, Solid, Break);
        Assert.Equal(ChartGaps.Link.Dashed, segs[^1].Incoming);
    }
}
