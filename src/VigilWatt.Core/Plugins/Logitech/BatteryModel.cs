namespace VigilWatt.Core.Plugins.Logitech;

/// <summary>
/// 功 能： 官方 powermodel 电池模型（放电库仑表 + 充电电压表 + 恒压段时间表），纯逻辑可单测。
/// 2026/11/13 19:10:00 fred1
/// 数据源自 G HUB 设备包 battery.xml；换算语义与接缝见 docs/hardware/logitech.md §电池模型。
/// </summary>
internal sealed class BatteryModel
{
    /// <summary>放电表：时间(分钟)、CCV(毫伏)、累计已放出(mWh)。表尾即总容量与截止电压。</summary>
    public required IReadOnlyList<DischargePoint> Discharge { get; init; }

    /// <summary>充电表：CCV(毫伏) → SoC(%)。4.2V 处首行才是恒压段起点，后续同行靠时间表区分。</summary>
    public required IReadOnlyList<ChargePoint> Charging { get; init; }

    /// <summary>恒压段时间表：进入稳定态后按分钟推进 SoC。</summary>
    public required ChargingTimeSlots TimeSlots { get; init; }

    /// <summary>总放电容量（mWh），即放电表最后一行。</summary>
    public double TotalMilliwattHours => Discharge.Count == 0 ? 0 : Discharge[^1].MilliwattHours;

    /// <summary>
    /// 放电：GHUB 的放电表不是"电压→百分比"，而是"电压→累计已放出 mWh"，再按总容量取剩余比例。
    /// 用固定电压曲线（旧 defaultLithium）会与官方差 1~2 点，见 docs/hardware/logitech.md §电池模型。
    /// </summary>
    public int PercentFromDischarge(int millivolts)
    {
        if (Discharge.Count == 0) return 0;
        double total = TotalMilliwattHours;
        if (total <= 0) return 0;

        double mv = millivolts;
        if (mv >= Discharge[0].Millivolts) return 100;
        if (mv <= Discharge[^1].Millivolts) return 0;

        for (int i = 0; i < Discharge.Count - 1; i++)
        {
            var hi = Discharge[i];
            var lo = Discharge[i + 1];
            if (mv > hi.Millivolts || mv < lo.Millivolts) continue;

            int span = hi.Millivolts - lo.Millivolts;
            double used = span <= 0
                ? lo.MilliwattHours
                : lo.MilliwattHours + (mv - lo.Millivolts) / span * (hi.MilliwattHours - lo.MilliwattHours);

            int percent = (int)Math.Round((1.0 - used / total) * 100.0);
            return Math.Clamp(percent, 0, 100);
        }
        return 0;
    }

    /// <summary>
    /// 充电：按充电电压表插值。电压到达平台电压（表尾那个电压）后该表**完全失去分辨率**——
    /// 官方在 4.200V 有 43 行同电压（68→100），靠时间表区分。所以到平台就返回 68（平台首行），
    /// 由 <see cref="ChargingTimeSlots"/> 接管推进；绝不能取表尾 100 当作"电压说满了"。
    /// </summary>
    public int PercentFromCharging(int millivolts)
    {
        if (Charging.Count == 0) return 0;

        int top = Charging[^1].Millivolts;
        if (millivolts >= top) return PlateauStartPercent;

        double mv = millivolts;
        if (mv <= Charging[0].Millivolts) return Charging[0].Percent;

        for (int i = 0; i < Charging.Count - 1; i++)
        {
            var lo = Charging[i];
            var hi = Charging[i + 1];
            if (mv < lo.Millivolts || mv > hi.Millivolts) continue;

            int span = hi.Millivolts - lo.Millivolts;
            if (span <= 0) return hi.Percent;

            double t = (mv - lo.Millivolts) / span;
            return (int)Math.Round(lo.Percent + t * (hi.Percent - lo.Percent));
        }
        return PlateauStartPercent;
    }

    /// <summary>
    /// 恒压段起点的 SoC：充电表在**最高电压平台**（4.2V）的首行百分比。
    /// 注意不是 <see cref="ChargingTimeSlots.StableStateDetectionVoltageMv"/> 门限处——那只是"开始检查稳定态"的电压，
    /// 平台真正稳住要到最高电压（官方表 4.200V 首行 68%，之后 43 行同电压靠时间表区分）。
    /// </summary>
    public int PlateauStartPercent
    {
        get
        {
            if (Charging.Count == 0) return 0;
            int top = Charging[^1].Millivolts;
            for (int i = 0; i < Charging.Count; i++)
            {
                if (Charging[i].Millivolts >= top) return Charging[i].Percent;
            }
            return Charging[^1].Percent;
        }
    }
}

/// <summary>放电表的一行。</summary>
internal readonly record struct DischargePoint(int Minute, int Millivolts, double MilliwattHours);

/// <summary>充电表的一行。</summary>
internal readonly record struct ChargePoint(int Millivolts, int Percent);

/// <summary>
/// 功 能： 恒压段（4.2V 平台）时间表：进入稳定态后每 30 秒一格，按分钟推进 SoC。
/// 2026/11/13 19:10:00 fred1
/// 字段含义来自官方 battery.xml 注释，见 docs/hardware/logitech.md §电池模型。
/// </summary>
internal sealed class ChargingTimeSlots
{
    /// <summary>计时器启动门限（毫伏）：电压到此才判定可能进入恒压段。</summary>
    public required int StableStateDetectionVoltageMv { get; init; }

    /// <summary>稳定判据：相邻采样电压变化不超过该值（毫伏）即视为稳定。</summary>
    public required int StableStateMinimumVoltageChangeMv { get; init; }

    /// <summary>稳定检查周期（分钟），官方注释缺省为 1。</summary>
    public required int StableStateCheckPeriodInMinute { get; init; }

    /// <summary>时间表：[分钟, SoC%]，分钟升序。</summary>
    public required IReadOnlyList<SlotPoint> Slots { get; init; }

    /// <summary>按已充电分钟数取 SoC；超出表尾返回最后一格。</summary>
    public int PercentAtMinute(double minutes)
    {
        if (Slots.Count == 0) return 0;
        if (minutes <= Slots[0].Minute) return Slots[0].Percent;

        for (int i = 0; i < Slots.Count - 1; i++)
        {
            var lo = Slots[i];
            var hi = Slots[i + 1];
            if (minutes < lo.Minute || minutes > hi.Minute) continue;

            int span = hi.Minute - lo.Minute;
            if (span <= 0) return hi.Percent;

            double t = (minutes - lo.Minute) / span;
            return (int)Math.Round(lo.Percent + t * (hi.Percent - lo.Percent));
        }
        return Slots[^1].Percent;
    }
}

/// <summary>时间表的一行：分钟 + SoC%。</summary>
internal readonly record struct SlotPoint(int Minute, int Percent);
