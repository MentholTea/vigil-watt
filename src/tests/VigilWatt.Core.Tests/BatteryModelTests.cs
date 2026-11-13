using VigilWatt.Core.Models;
using VigilWatt.Core.Plugins.Logitech;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 官方电池模型（库仑放电表 / 充电电压表 / 恒压段时间表）与充电状态机。
/// 2026/11/13 19:20:00 fred1
/// 期望值来源：G HUB settings.db 实存 (mV,pct) 配对与 battery.xml 原始行，见 docs/hardware/logitech.md §电池模型。
/// </summary>
public class BatteryModelTests
{
    private static BatteryModel SampleModel()
    {
        // 缩小版：形状与官方一致，便于手算断言。
        var discharge = new[]
        {
            new DischargePoint(0, 4186, 1.34),
            new DischargePoint(100, 4026, 137.38),
            new DischargePoint(300, 3837, 398.48),
            new DischargePoint(706, 3479, 902.34),
        };
        var charging = new[]
        {
            new ChargePoint(3271, 2),
            new ChargePoint(3929, 10),
            new ChargePoint(4098, 45),
            new ChargePoint(4200, 68),
        };
        var slots = new ChargingTimeSlots
        {
            StableStateDetectionVoltageMv = 4100,
            StableStateMinimumVoltageChangeMv = 3,
            StableStateCheckPeriodInMinute = 1,
            Slots = new[] { new SlotPoint(1, 66), new SlotPoint(40, 90), new SlotPoint(88, 99) },
        };
        return new BatteryModel { Discharge = discharge, Charging = charging, TimeSlots = slots };
    }

    [Fact]
    public void M1_放电表顶与底分别夹到100和0()
    {
        var m = SampleModel();
        Assert.Equal(100, m.PercentFromDischarge(4186));
        Assert.Equal(100, m.PercentFromDischarge(4300));
        Assert.Equal(0, m.PercentFromDischarge(3479));
        Assert.Equal(0, m.PercentFromDischarge(3000));
    }

    [Fact]
    public void M2_放电是库仑累计不是电压直接映射()
    {
        var m = SampleModel();
        // 4026mV 位于表中第二行，已放出 137.38 / 902.34 → 剩余 84.77% → 85
        Assert.Equal(85, m.PercentFromDischarge(4026));
        // 3837mV → 已放出 398.48 / 902.34 → 55.84% → 56
        Assert.Equal(56, m.PercentFromDischarge(3837));
    }

    [Fact]
    public void M3_放电线性插值()
    {
        var m = SampleModel();
        // 4186(1.34) 与 4026(137.38) 的中点 → 已放出 69.36 → 剩余 92.31% → 92
        Assert.Equal(92, m.PercentFromDischarge(4106));
    }

    [Fact]
    public void M4_充电表插值与端点()
    {
        var m = SampleModel();
        Assert.Equal(2, m.PercentFromCharging(3271));
        Assert.Equal(2, m.PercentFromCharging(3000));
        // 到达平台电压：返回平台首行 68，**不是**表尾 100 —— 平台段交给时间表
        Assert.Equal(68, m.PercentFromCharging(4200));
        Assert.Equal(68, m.PercentFromCharging(4300));
        // 4098→45 与 4200→68 之间
        Assert.Equal(56, m.PercentFromCharging(4149));
    }

    [Fact]
    public void M5_同一电压下充电表显著低于放电表_这就是充电虚高的根因()
    {
        var m = SampleModel();
        // 3.93V 附近：放电约 70%，充电只有 ~10%
        int byDischarge = m.PercentFromDischarge(3929);
        int byCharging = m.PercentFromCharging(3929);
        Assert.True(byCharging < byDischarge, $"充电 {byCharging}% 应低于放电 {byDischarge}%");
        Assert.True(byDischarge - byCharging > 50, "两者差距应超过 50 点（真机 20%→69% 的来源）");
    }

    [Fact]
    public void M6_时间表按分钟插值且超尾返回末格()
    {
        var m = SampleModel().TimeSlots;
        Assert.Equal(66, m.PercentAtMinute(0.5));
        Assert.Equal(66, m.PercentAtMinute(1));
        Assert.Equal(90, m.PercentAtMinute(40));
        Assert.Equal(99, m.PercentAtMinute(200));
        // 1→40 分钟之间中点 (20.5min) → 66 + 0.5*(90-66) = 78
        Assert.Equal(78, m.PercentAtMinute(20.5));
    }

    [Fact]
    public void M7_充电也用放电表_与GHUB一致()
    {
        // G HUB 四条实测记录全部等于放电表：4084→92、4088→92、4170→100、4201→100。
        var solver = new ChargingSolver(SampleModel());
        Assert.Equal(solver.Feed(3929, 0), SampleModel().PercentFromDischarge(3929));
    }

    [Fact]
    public void M8_插线锚点优先于当前放电表()
    {
        // 真机 19:22：插线 4084mV，放电表 91.8%，但显示 92%（插线前读数）。
        var solver = new ChargingSolver(SampleModel());
        int pct = solver.Feed(4084, 92);
        Assert.Equal(92, pct);
        Assert.Equal(92, solver.Anchor);
    }

    [Fact]
    public void M9_电压升高后用放电表推进_真机1946场景()
    {
        // 19:22 插线 92% -> 19:46 电压 4201mV，放电表 100% -> 显示 100%
        var solver = new ChargingSolver(SampleModel());
        solver.Feed(4084, 92);
        int later = solver.Feed(4201, 92);
        Assert.Equal(100, later);
    }

    [Fact]
    public void M10_电压回落不回退_真机1947场景()
    {
        // 19:47 电压回落到 4170mV，放电表 99.6%→100，显示仍 100%
        var solver = new ChargingSolver(SampleModel());
        solver.Feed(4084, 92);
        solver.Feed(4201, 92);
        int after = solver.Feed(4170, 92);
        Assert.Equal(100, after);
    }

    [Fact]
    public void M11_Reset后重新锚定()
    {
        var solver = new ChargingSolver(SampleModel());
        solver.Feed(4084, 92);
        Assert.True(solver.CurrentPercent > 0);

        solver.Reset();
        Assert.Equal(-1, solver.CurrentPercent);
        Assert.Equal(-1, solver.Anchor);
    }
    [Fact]
    public void M12_恒压起点百分比取最高电压平台首行而非门限处()
    {
        var m = SampleModel();
        // 表顶 4200mV 首行 68（门限 4100 落在 4098(45) 与 4200(68) 之间，不能拿 45 当恒压起点）
        Assert.Equal(68, m.PlateauStartPercent);
    }

    [Fact]
    public void M13_无历史时用当前放电表兜底()
    {
        // 首次上线就是充电态、历史库也没记录：只能信当前放电表（不是充电表）。
        var solver = new ChargingSolver(SampleModel());
        int pct = solver.Feed(3929, 0);
        Assert.Equal(SampleModel().PercentFromDischarge(3929), pct);
        Assert.True(pct > 60, "放电表在 3.929V 应给出 60% 以上，而不是充电表的 10%");
    }
}