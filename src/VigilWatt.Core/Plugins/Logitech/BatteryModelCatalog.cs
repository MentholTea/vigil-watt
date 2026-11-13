using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VigilWatt.Core.Plugins.Logitech;

/// <summary>
/// 功 能： 嵌入的官方电池模型目录（G HUB 设备包 battery.xml 转成 JSON），按 wireless PID 索引。
/// 2026/11/13 19:12:00 fred1
/// 未收录的 WPID 返回 null，插件退回旧电压曲线；模型语义见 docs/hardware/logitech.md §电池模型。
/// </summary>
internal static class BatteryModelCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Lazy<IReadOnlyDictionary<ushort, BatteryModel>> Models = new(Load);

    /// <summary>按 wireless PID 取模型；未收录返回 null。</summary>
    public static BatteryModel? Find(ushort? wirelessPid)
        => wirelessPid is { } wpid && Models.Value.TryGetValue(wpid, out var m) ? m : null;

    /// <summary>已收录的型号数（日志/诊断用）。</summary>
    public static int Count => Models.Value.Count;

    private static IReadOnlyDictionary<ushort, BatteryModel> Load()
    {
        var asm = Assembly.GetExecutingAssembly();
        const string name = "VigilWatt.Core.Plugins.Logitech.battery-models.json";
        using var stream = asm.GetManifestResourceStream(name);
        if (stream == null) return new Dictionary<ushort, BatteryModel>();

        var dto = JsonSerializer.Deserialize<RootDto>(stream, JsonOptions);
        if (dto?.Models == null) return new Dictionary<ushort, BatteryModel>();

        var result = new Dictionary<ushort, BatteryModel>();
        foreach (var m in dto.Models)
        {
            if (!TryParseHexWpid(m.Wpid, out var wpid)) continue;
            if (m.Discharge == null || m.Discharge.Count == 0) continue;

            var discharge = m.Discharge
                .Where(p => p.Count >= 3)
                .Select(p => new DischargePoint((int)p[0], (int)Math.Round(p[1] * 1000.0), p[2]))
                .OrderByDescending(p => p.Millivolts)
                .ToArray();

            var charging = (m.Charging ?? [])
                .Where(p => p.Count >= 2)
                .Select(p => new ChargePoint((int)Math.Round(p[0] * 1000.0), (int)p[1]))
                .OrderBy(p => p.Millivolts)
                .ToArray();

            var slotsDto = m.ChargingTimeSlots;
            var slots = new ChargingTimeSlots
            {
                StableStateDetectionVoltageMv = slotsDto?.StableStateDetectionVoltageMv ?? 4900,
                StableStateMinimumVoltageChangeMv = slotsDto?.StableStateMinimumVoltageChangeMv ?? 3,
                StableStateCheckPeriodInMinute = slotsDto?.StableStateCheckPeriodInMinute ?? 1,
                Slots = (slotsDto?.Slots ?? [])
                    .Where(p => p.Count >= 2)
                    .Select(p => new SlotPoint((int)p[0], (int)p[1]))
                    .OrderBy(p => p.Minute)
                    .ToArray(),
            };

            result[wpid] = new BatteryModel { Discharge = discharge, Charging = charging, TimeSlots = slots };
        }
        return result;
    }

    private static bool TryParseHexWpid(string? text, out ushort wpid)
    {
        wpid = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return ushort.TryParse(text.Trim(), System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out wpid);
    }

    private sealed class RootDto
    {
        [JsonPropertyName("models")]
        public List<ModelDto>? Models { get; set; }
    }

    private sealed class ModelDto
    {
        [JsonPropertyName("wpid")]
        public string? Wpid { get; set; }

        [JsonPropertyName("discharge")]
        public List<List<double>>? Discharge { get; set; }

        [JsonPropertyName("charging")]
        public List<List<double>>? Charging { get; set; }

        [JsonPropertyName("chargingTimeSlots")]
        public SlotsDto? ChargingTimeSlots { get; set; }
    }

    private sealed class SlotsDto
    {
        [JsonPropertyName("stableStateDetectionVoltageMv")]
        public int StableStateDetectionVoltageMv { get; set; }

        [JsonPropertyName("stableStateMinimumVoltageChangeMv")]
        public int StableStateMinimumVoltageChangeMv { get; set; }

        [JsonPropertyName("stableStateCheckPeriodInMinute")]
        public int StableStateCheckPeriodInMinute { get; set; }

        [JsonPropertyName("slots")]
        public List<List<double>>? Slots { get; set; }
    }
}
