using VigilWatt.Core.Models;

namespace VigilWatt.Core.Plugins.Logitech;

/// <summary>
/// 功 能： 与 G HUB 一致的充电读数：锚点托底 + 充电表推进 + 恒压段时间表。
/// 2026/11/13 20:12:00 fred1
/// 2026/11/17 12:20:00 fred1 改用充电表并接入 charging_time_slots，见 docs/hardware/logitech.md §充电读数。
/// </summary>
internal sealed class ChargingSolver
{
    private readonly BatteryModel _model;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>插线瞬间锁定的放电读数：起点，也是充电期间的下界。</summary>
    private int _anchor = -1;

    /// <summary>进入恒压稳定态的时刻；null 表示还没进入。</summary>
    private DateTimeOffset? _stableSince;

    /// <summary>上一帧电压，用来判断相邻采样是否已稳定。</summary>
    private int _lastMillivolts = -1;

    /// <summary>上一帧的时刻。</summary>
    private DateTimeOffset? _lastSeenAt;

    public ChargingSolver(BatteryModel model, Func<DateTimeOffset>? now = null)
    {
        _model = model;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>当前显示值；-1 表示尚未建立基准。</summary>
    public int CurrentPercent { get; private set; } = -1;

    /// <summary>插线瞬间的锚点；-1 表示尚未锚定。</summary>
    public int Anchor => _anchor;

    /// <summary>进入恒压段时间表的时刻；null 表示还没进入稳定态。</summary>
    public DateTimeOffset? StableSince => _stableSince;

    /// <summary>拔线时清空：下次插线重新锚定，恒压段计时作废。</summary>
    public void Reset()
    {
        _anchor = -1;
        CurrentPercent = -1;
        _stableSince = null;
        _lastMillivolts = -1;
        _lastSeenAt = null;
    }

    /// <summary>
    /// 充电中给一帧电压。<paramref name="lastDischargePercent"/> 是插线前最后的放电读数。
    /// 低电量走充电表，进恒压段后交时间表，到 4.2V 表顶判满，见 docs/hardware/logitech.md §充电读数。
    /// </summary>
    public int Feed(int millivolts, int lastDischargePercent)
    {
        var now = _now();
        int target = _model.PercentFromCharging(millivolts);

        int byTable = TablePercent(millivolts, now);
        if (byTable > target) target = byTable;

        // 表顶在充电表里只给平台首行，物理上到顶就是满，时间表也分辨不出那 43 行同电压。
        if (_model.Charging.Count > 0 && millivolts >= _model.Charging[^1].Millivolts) target = 100;

        if (_anchor < 0)
        {
            // 插线前读数优先；冷启动就插着线（无历史）才用当前结果兜底。
            _anchor = lastDischargePercent > 0 ? lastDischargePercent : target;
            CurrentPercent = _anchor;
        }

        // 只升不降：电压回落时不回退，也不允许锚点被低值拉下去。
        if (target > CurrentPercent) CurrentPercent = target;

        CurrentPercent = Math.Clamp(CurrentPercent, 0, 100);
        return CurrentPercent;
    }

    /// <summary>恒压段按时间表推进；未进入稳定态返回 -1（不参与取大）。</summary>
    private int TablePercent(int millivolts, DateTimeOffset now)
    {
        var slots = _model.TimeSlots;
        if (slots.Slots.Count == 0 || slots.StableStateDetectionVoltageMv <= 0) return -1;

        if (millivolts < slots.StableStateDetectionVoltageMv)
        {
            // 掉出门限：刷新期间电压没稳住，计时作废，回到充电表。
            _stableSince = null;
        }
        else if (_stableSince is null)
        {
            bool sway = _lastMillivolts >= 0
                && Math.Abs(millivolts - _lastMillivolts) <= slots.StableStateMinimumVoltageChangeMv;
            if (sway) _stableSince = _lastSeenAt ?? now;
        }

        _lastMillivolts = millivolts;
        _lastSeenAt = now;

        if (_stableSince is null) return -1;
        return slots.PercentAtMinute((now - _stableSince.Value).TotalMinutes);
    }
}