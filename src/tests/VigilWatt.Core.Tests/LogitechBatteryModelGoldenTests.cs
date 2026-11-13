using VigilWatt.Core.Plugins.Logitech;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 用 G HUB 真机数据回归官方电池模型：放电 13 组 (mV,pct) 与充电电压表接缝。
/// 2026/11/13 19:25:00 fred1
/// 样本取自本机 settings.db-WAL 实存配对；模型取自设备包 battery.xml，见 docs/hardware/logitech.md §电池模型。
/// </summary>
public class LogitechBatteryModelGoldenTests
{
    /// <summary>与生产环境同一份嵌入模型（707 行放电 / 83 行充电 / 26 行时间表）。</summary>
    private static BatteryModel Real => BatteryModelCatalog.Find(0x407F)
        ?? throw new InvalidOperationException("0x407F 模型未嵌入");

    [Fact]
    public void G0_真机模型已嵌入且行数对得上官方battery_xml()
    {
        var m = Real;
        Assert.Equal(707, m.Discharge.Count);
        Assert.Equal(83, m.Charging.Count);
        Assert.Equal(26, m.TimeSlots.Slots.Count);
        Assert.Equal(4100, m.TimeSlots.StableStateDetectionVoltageMv);
        Assert.Equal(3, m.TimeSlots.StableStateMinimumVoltageChangeMv);
        Assert.Equal(902.34, m.TotalMilliwattHours, 2);
    }

    [Theory]
    [InlineData(4195, 100)]
    [InlineData(4166, 100)]
    [InlineData(4158, 99)]
    [InlineData(4155, 99)]
    [InlineData(4146, 98)]
    [InlineData(4138, 97)]
    [InlineData(4131, 97)]
    [InlineData(4126, 96)]
    [InlineData(4117, 95)]
    [InlineData(4106, 94)]
    [InlineData(4096, 93)]
    public void G1_放电与GHUB实存配对逐点一致(int millivolts, int expected)
    {
        Assert.Equal(expected, Real.PercentFromDischarge(millivolts));
    }

    [Fact]
    public void G2_插线极化场景_放电曲线会虚高而充电曲线不会()
    {
        // 真机 2026-11-13 08:28 拔线 20%（3.717V OCV），插线后端电压抬到约 3.94V，
        // 旧实现按放电曲线读数 69%，与 G HUB 差 49 点。
        var m = Real;
        int wrong = m.PercentFromDischarge(3940);
        int right = m.PercentFromCharging(3940);

        Assert.True(wrong >= 60, $"放电曲线在 3940mV 应给出虚高值，实际 {wrong}");
        Assert.True(right <= 20, $"充电曲线在 3940mV 应给出接近真实的低值，实际 {right}");
    }

    [Fact]
    public void G3_充电电压表单调不减且覆盖到恒压段起点()
    {
        var m = Real;
        int prev = -1;
        foreach (var p in m.Charging)
        {
            Assert.True(p.Percent >= prev, $"{p.Millivolts}mV 处百分比回退：{prev} → {p.Percent}");
            prev = p.Percent;
        }

        // 官方表在 4200mV 处首行才是恒压段起点（约 68%），之后靠时间表推进
        Assert.Equal(68, m.PlateauStartPercent);
    }

    [Fact]
    public void G4_恒压段时间表覆盖到99_且按分钟单调()
    {
        var m = Real;
        var slots = m.TimeSlots.Slots;
        Assert.Equal(1, slots[0].Minute);
        Assert.Equal(66, slots[0].Percent);
        Assert.Equal(99, slots[^1].Percent);

        int prev = -1;
        foreach (var s in slots)
        {
            Assert.True(s.Percent >= prev, $"{s.Minute}min 处百分比回退：{prev} → {s.Percent}");
            prev = s.Percent;
        }
    }

    [Fact]
    public void G5_未收录的WPID返回null而不是瞎猜()
    {
        Assert.Null(BatteryModelCatalog.Find(0x1234));
        Assert.Null(BatteryModelCatalog.Find(null));
        Assert.NotNull(BatteryModelCatalog.Find(0x407F));
    }
}
