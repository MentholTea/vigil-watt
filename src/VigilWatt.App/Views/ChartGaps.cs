namespace VigilWatt.App.Views;

/// <summary>
/// 功 能： 曲线断档的分类与分段结果：短间隔实线、长间隔虚线，一律相连不留裂口。
/// 2026/11/14 11:25:00 fred1
/// 为什么不再有"断开"档：断口会把走势切碎，看不出"睡前 50%、早上 48%"的连续关系，
/// 见 docs/project/code-flow.md §6。
/// </summary>
public static class ChartGaps
{
    /// <summary>默认实线阈值：间隔不超过它视为连续（采样抖动、短暂离线）。</summary>
    public static readonly TimeSpan SolidLimit = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 超过它就画虚线。保留这个常量只为让调用方有个统一说法，
    /// 现在**不再有"完全断开"**——整夜空档也是虚线。
    /// </summary>
    public static readonly TimeSpan DashedFrom = SolidLimit;

    /// <summary>按采样间隔推算实线阈值：间隔 ×3 但不少于 30 分钟，避免把正常采样抖动判成断档。</summary>
    public static TimeSpan SolidLimitFor(int sampleIntervalMinutes)
        => TimeSpan.FromMinutes(Math.Max(SolidLimit.TotalMinutes, Math.Max(1, sampleIntervalMinutes) * 3.0));

    /// <summary>一段相邻采样之间的连接方式。</summary>
    public enum Link
    {
        /// <summary>实线：采样正常。</summary>
        Solid,

        /// <summary>虚线：间隔偏大，弱化表示"这段是推测"（含整夜关机）。</summary>
        Dashed,

        /// <summary>已废弃的"断开"档，仅为兼容旧调用保留；绘制时按虚线处理。</summary>
        Break,
    }

    /// <summary>
    /// 一条绘制段：闭区间 [Start, End]。<see cref="Incoming"/> 表示"从上一段末尾连到本段首点"这条线的画法，
    /// 首段恒为 Solid（没有上一段）。
    /// </summary>
    public readonly record struct Segment(int Start, int End, Link Incoming);

    /// <summary>判定两个相邻采样之间的连接方式：超过实线阈值就是虚线，永不判断开。</summary>
    public static Link Classify(TimeSpan gap, TimeSpan solidLimit, TimeSpan breakLimit = default)
        => gap <= solidLimit ? Link.Solid : Link.Dashed;

    /// <summary>
    /// 把采样点拆成绘制段。规则：实线间隔并入当前段；超过阈值的间隔断开新段，
    /// 并标记为虚线起头（绘制时虚线把两段接上，视觉连续）。
    /// 段之间不共享点，避免面积图重复描边、悬停反查重复命中。
    /// </summary>
    public static List<Segment> Split<T>(
        IReadOnlyList<T> points,
        Func<T, DateTimeOffset> timeOf,
        TimeSpan solidLimit,
        TimeSpan breakLimit = default)
    {
        var result = new List<Segment>();
        if (points.Count == 0) return result;
        if (points.Count == 1)
        {
            result.Add(new Segment(0, 0, Link.Solid));
            return result;
        }

        int segStart = 0;
        for (int i = 1; i < points.Count; i++)
        {
            var gap = timeOf(points[i]) - timeOf(points[i - 1]);
            if (Classify(gap, solidLimit) == Link.Solid) continue;

            // 当前段在 i-1 结束（含），从 i 起新段；两段之间怎么连由第二遍回填
            result.Add(new Segment(segStart, i - 1, Link.Solid));
            segStart = i;
        }

        result.Add(new Segment(segStart, points.Count - 1, Link.Solid));

        // 第二遍：按"相邻两段之间的间隔"回填每段的 Incoming
        for (int k = 1; k < result.Count; k++)
        {
            var prev = points[result[k - 1].End];
            var cur = points[result[k].Start];
            var link = Classify(timeOf(cur) - timeOf(prev), solidLimit);
            result[k] = result[k] with { Incoming = link };
        }

        return result;
    }
}
