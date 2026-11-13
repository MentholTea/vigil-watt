namespace VigilWatt.App.Views;

/// <summary>
/// 功 能： 曲线断档的三档判定与分段结果（纯逻辑，可单测）。
/// 2026/11/13 22:30:00 fred1
/// 为什么分三档：采样抖动不该画成裂口，真关机又不能连成斜坡，见 docs/hardware/logitech.md §电池模型。
/// </summary>
public static class ChartGaps
{
    /// <summary>默认实线阈值：间隔不超过它视为连续（采样抖动、短暂离线）。</summary>
    public static readonly TimeSpan SolidLimit = TimeSpan.FromMinutes(30);

    /// <summary>断档阈值：超过它完全断开（关机/拔出）。介于两者之间画虚线。</summary>
    public static readonly TimeSpan BreakLimit = TimeSpan.FromHours(4);

    /// <summary>按采样间隔推算实线阈值：间隔 ×3 但不少于 30 分钟，避免把正常采样抖动判成断档。</summary>
    public static TimeSpan SolidLimitFor(int sampleIntervalMinutes)
        => TimeSpan.FromMinutes(Math.Max(SolidLimit.TotalMinutes, Math.Max(1, sampleIntervalMinutes) * 3.0));

    /// <summary>一段相邻采样之间的连接方式。</summary>
    public enum Link
    {
        /// <summary>实线：采样正常。</summary>
        Solid,

        /// <summary>虚线：间隔偏大但不足以判关机，弱化表示"这段是推测"。</summary>
        Dashed,

        /// <summary>断开：关机/拔出，不连线。</summary>
        Break,
    }

    /// <summary>
    /// 一条绘制段：闭区间 [Start, End]。<see cref="Incoming"/> 表示"从上一段末尾连到本段首点"这条线的画法，
    /// 首段恒为空（没有上一段）。
    /// </summary>
    public readonly record struct Segment(int Start, int End, Link Incoming);

    /// <summary>判定两个相邻采样之间的连接方式。</summary>
    public static Link Classify(TimeSpan gap, TimeSpan solidLimit, TimeSpan breakLimit)
    {
        if (gap <= solidLimit) return Link.Solid;
        if (gap > breakLimit) return Link.Break;
        return Link.Dashed;
    }

    /// <summary>
    /// 把采样点拆成绘制段。规则：
    /// <list type="bullet">
    /// <item>实线间隔：并入当前段（段内恒为实线）。</item>
    /// <item>虚线间隔：当前段到此为止，下一个点起新段，且新段的 <c>Incoming = Dashed</c>，
    /// 绘制时用虚线把两段接上 —— 视觉连续、语义上仍标明"这段是推测"。</item>
    /// <item>断开间隔：当前段到此为止，下一个点起新段，<c>Incoming = Break</c>，两段之间不画任何线。</item>
    /// </list>
    /// 段之间不共享点，避免面积图重复描边、悬停反查重复命中。
    /// </summary>
    public static List<Segment> Split<T>(
        IReadOnlyList<T> points,
        Func<T, DateTimeOffset> timeOf,
        TimeSpan solidLimit,
        TimeSpan breakLimit)
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
            if (Classify(gap, solidLimit, breakLimit) == Link.Solid) continue;

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
            var link = Classify(timeOf(cur) - timeOf(prev), solidLimit, breakLimit);
            result[k] = result[k] with { Incoming = link };
        }

        return result;
    }
}
