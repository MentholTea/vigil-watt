using VigilWatt.App.Views;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 曲线断档分类与分段：短间隔实线、长间隔虚线，一律相连不留裂口。
/// 2026/11/14 11:30:00 fred1
/// 语义变更原因（断口切碎走势）见 ChartGaps 与 docs/project/code-flow.md §6。
/// </summary>
public class ChartGapsTests
{
    private static readonly TimeSpan Solid = TimeSpan.FromMinutes(30);
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 8, 0, 0, TimeSpan.FromHours(8));

    private static List<(DateTimeOffset Time, int Percent)> Build(params double[] minutesFromT0)
        => minutesFromT0.Select(m => (T0.AddMinutes(m), 50)).ToList();

    [Fact]
    public void G1_只有实线与虚线两档_不存在断开()
    {
        Assert.Equal(ChartGaps.Link.Solid, ChartGaps.Classify(TimeSpan.FromMinutes(10), Solid));
        Assert.Equal(ChartGaps.Link.Solid, ChartGaps.Classify(TimeSpan.FromMinutes(30), Solid));
        Assert.Equal(ChartGaps.Link.Dashed, ChartGaps.Classify(TimeSpan.FromMinutes(31), Solid));
        // 整夜空档也是虚线，不再断开
        Assert.Equal(ChartGaps.Link.Dashed, ChartGaps.Classify(TimeSpan.FromHours(11), Solid));
        Assert.Equal(ChartGaps.Link.Dashed, ChartGaps.Classify(TimeSpan.FromDays(3), Solid));
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
        var segs = ChartGaps.Split(Build(0, 10, 20, 30), p => p.Time, Solid);
        Assert.Single(segs);
        Assert.Equal(0, segs[0].Start);
        Assert.Equal(3, segs[0].End);
        Assert.Equal(ChartGaps.Link.Solid, segs[0].Incoming);
    }

    [Fact]
    public void G4_单个采样点自成一段()
    {
        var segs = ChartGaps.Split(Build(0), p => p.Time, Solid);
        Assert.Single(segs);
        Assert.Equal(0, segs[0].Start);
        Assert.Equal(0, segs[0].End);
    }

    [Fact]
    public void G5_空序列返回空()
    {
        Assert.Empty(ChartGaps.Split(Array.Empty<(DateTimeOffset, int)>(), p => p.Item1, Solid));
    }

    [Fact]
    public void G6_长间隔拆段且新段标记为虚线()
    {
        // 0,10 连续；到 60 分钟（间隔 50 分）→ 虚线
        var segs = ChartGaps.Split(Build(0, 10, 60, 70), p => p.Time, Solid);

        Assert.Equal(2, segs.Count);
        Assert.Equal((0, 1), (segs[0].Start, segs[0].End));
        Assert.Equal(ChartGaps.Link.Solid, segs[0].Incoming);
        Assert.Equal((2, 3), (segs[1].Start, segs[1].End));
        Assert.Equal(ChartGaps.Link.Dashed, segs[1].Incoming);
    }

    [Fact]
    public void G7_整夜空档同样是虚线而不是断开()
    {
        // 真机场景：11-13 20:43 -> 11-14 07:58（约 11.25 小时）
        var segs = ChartGaps.Split(Build(0, 10, 11 * 60 + 30), p => p.Time, Solid);

        Assert.Equal(2, segs.Count);
        Assert.Equal(ChartGaps.Link.Dashed, segs[1].Incoming);
        Assert.DoesNotContain(segs, s => s.Incoming == ChartGaps.Link.Break);
    }

    [Fact]
    public void G8_多日空档也连成虚线()
    {
        // 三天没开机：仍要连线，不能留裂口
        var segs = ChartGaps.Split(Build(0, 10, 3 * 24 * 60), p => p.Time, Solid);
        Assert.Equal(ChartGaps.Link.Dashed, segs[^1].Incoming);
    }

    [Fact]
    public void G9_段之间不共享端点()
    {
        var segs = ChartGaps.Split(Build(0, 10, 600, 610, 1200), p => p.Time, Solid);
        for (int i = 1; i < segs.Count; i++)
            Assert.True(segs[i].Start > segs[i - 1].End,
                $"第 {i} 段起点 {segs[i].Start} 应大于上一段终点 {segs[i - 1].End}");
    }

    [Fact]
    public void G10_段覆盖全部采样点且不重不漏()
    {
        var pts = Build(0, 10, 60, 70, 600, 610);
        var segs = ChartGaps.Split(pts, p => p.Time, Solid);

        Assert.Equal(pts.Count, segs.Sum(s => s.End - s.Start + 1));
        var indices = segs.SelectMany(s => Enumerable.Range(s.Start, s.End - s.Start + 1)).ToList();
        Assert.Equal(Enumerable.Range(0, pts.Count), indices);
    }

    [Fact]
    public void G11_相邻段之间必有连接方式_不留空档()
    {
        // 任意拆段，第二段起 Incoming 一定是 Solid 或 Dashed（永不为 Break），
        // 才能保证绘制时每处间隙都有线
        var pts = Build(0, 10, 60, 70, 600, 610, 5000);
        var segs = ChartGaps.Split(pts, p => p.Time, Solid);
        for (int i = 1; i < segs.Count; i++)
            Assert.True(segs[i].Incoming is ChartGaps.Link.Solid or ChartGaps.Link.Dashed,
                $"第 {i} 段的 Incoming={segs[i].Incoming}，会导致图上出现裂口");
    }
}
