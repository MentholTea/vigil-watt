using VigilWatt.Core.Models;

namespace VigilWatt.Core.Plugins.Logitech;

/// <summary>
/// 功 能： 与 G HUB 一致的充电读数：以插线前放电值为起点，用充电电压表只升不降地推进。
/// 2026/11/13 20:12:00 fred1
/// 2026/11/17 12:20:00 fred1 改用充电表（放电表把极化电压读高半个表），见 docs/hardware/logitech.md §充电读数。
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
    /// 充电表读极化电压，锚点托住下限；到 4.2V 表顶才判满，见 docs/hardware/logitech.md §充电读数。
    /// </summary>
    public int Feed(int millivolts, int lastDischargePercent)
    {
        int byCharging = _model.PercentFromCharging(millivolts);

        // 表顶（4.2V）在充电表里只给平台首行 68，物理上到顶就是满，时间表那 43 行同电压分辨不出。
        if (_model.Charging.Count > 0 && millivolts >= _model.Charging[^1].Millivolts) byCharging = 100;

        if (_anchor < 0)
        {
            // 插线前读数优先；冷启动就插着线（无历史）才用充电表结果兜底。
            _anchor = lastDischargePercent > 0 ? lastDischargePercent : byCharging;
            CurrentPercent = _anchor;
        }

        // 只升不降：电压回落时不回退，也不允许锚点被低值拉下去。
        if (byCharging > CurrentPercent) CurrentPercent = byCharging;

        CurrentPercent = Math.Clamp(CurrentPercent, 0, 100);
        return CurrentPercent;
    }
}
