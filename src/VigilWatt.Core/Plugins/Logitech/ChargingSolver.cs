using VigilWatt.Core.Models;

namespace VigilWatt.Core.Plugins.Logitech;

/// <summary>
/// 功 能： 与 G HUB 一致的充电读数：以插线前放电值为起点，用放电表只升不降地推进。
/// 2026/11/13 20:12:00 fred1
/// 四条真机记录的依据见 docs/hardware/logitech.md §电池模型。
/// </summary>
internal sealed class ChargingSolver
{
    private readonly BatteryModel _model;

    /// <summary>插线瞬间锁定的放电读数：起点，也是充电期间的下界。</summary>
    private int _anchor = -1;

    public ChargingSolver(BatteryModel model, Func<DateTimeOffset>? now = null) => _model = model;

    /// <summary>当前显示值；-1 表示尚未建立基准。</summary>
    public int CurrentPercent { get; private set; } = -1;

    /// <summary>插线瞬间的锚点；-1 表示尚未锚定。</summary>
    public int Anchor => _anchor;

    /// <summary>拔线时清空：下次插线重新锚定。</summary>
    public void Reset()
    {
        _anchor = -1;
        CurrentPercent = -1;
    }

    /// <summary>
    /// 充电中给一帧电压。<paramref name="lastDischargePercent"/> 是插线前最后的放电读数。
    ///
    /// G HUB 实测（2026-11-13，四条记录）：
    /// <list type="bullet">
    /// <item>19:22 插线，4084mV → 显示 92%（= 插线前放电值，不是此刻电压换算的 92.2 独立值，巧合一致）</item>
    /// <item>19:46 电压 4201mV → 显示 100%（放电表 100%，此时才涨）</item>
    /// <item>19:47 电压回落到 4170mV → 仍显示 100%（放电表 99.6→100，不回退）</item>
    /// </list>
    /// 也就是：**起点锁在插线前读数，之后用放电表只升不降**（G HUB 更新很慢，4~24 分钟一次，
    /// 所以插线后头几分钟看着像"冻结在 92%"）。
    /// </summary>
    public int Feed(int millivolts, int lastDischargePercent)
    {
        int byDischarge = _model.PercentFromDischarge(millivolts);

        if (_anchor < 0)
        {
            // 插线前读数优先（真机 92%）；没有历史才用当前放电表结果兜底。
            _anchor = lastDischargePercent > 0 ? lastDischargePercent : byDischarge;
            CurrentPercent = _anchor;
        }

        // 只升不降：电压被极化抬高时放电表可能已经 100%，但那不是"真的充满了"，
        // 只允许它把显示值往上推，绝不允许往下拉。
        if (byDischarge > CurrentPercent) CurrentPercent = byDischarge;

        CurrentPercent = Math.Clamp(CurrentPercent, 0, 100);
        return CurrentPercent;
    }
}
