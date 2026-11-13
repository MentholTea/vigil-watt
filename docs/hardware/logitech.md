# 罗技 Logitech (VID_046D) —— ✅ Bolt 真机验证（2026-10-08）；协议驱动 + 嵌入目录

> 从 [protocols.md](protocols.md) 拆出（2026-10-21）。结论均来自真机验证或官方软件抓包；改协议代码前先读本文。


HID++ 1.0/2.0 协议发现（无型号白名单）。电量级联：
`0x1004` Unified Battery → `0x1000` Battery Status → `0x1001` Battery Voltage → `0x1F20` ADC Measurement → 1.0 寄存器 `0x07`/`0x0D`。

嵌入资源 [`logitech-hidpp.json`](../../src/VigilWatt.Core/Plugins/Logitech/logitech-hidpp.json) 提供接收器槽位/报文偏好与毫伏曲线；**目录是补充不是白名单**——未知 PID 仍打开 HID++ 接口（默认最多 6 槽、有长则跳短）。

| 新设备类型 | 怎么扩展 |
|---|---|
| 新的 HID++ 鼠标 / 键盘 / 演示器（已有特征） | **零代码**——ping + GetFeature 自动发现 |
| 新的接收器族（确认 PID / 槽位 / 长短偏好后） | 在 JSON `receivers` **加一行** |
| 某 WPID 电压曲线与默认锂电差很大 | JSON `wpidCurves` + `voltageCurves` |
| 全新电量特征 ID，或根本不是 HID++ | **改 C#**（耳机 / Centurion / 仅 G HUB 等） |

蓝牙罗技走标准 GATT `0x180F`（`BleBatteryWatcher`），不在此插件。

**本机 Logi Bolt 接收器 `046D:C548` + MX Master 3S（2026-10-08）：**
- 必须走 20 字节长接口（usage `FF00/0002`，report **0x11**）；旁边 7 字节短接口（`0x10`）对 Bolt 鼠标不应答，应跳过（目录 `reportPreference=long`）。
- Unifying（`C52B`/`C532`）常只在短接口应答 → 目录 `reportPreference=short`，有短同伴时跳过长接口。
- 把 `0x10` 补零写进长接口 → 驱动 `ERROR_INVALID_FUNCTION`（与北通硬编码错 report ID 同类）。
- `Request()` 返回缓冲从 feature 字节起，**参数在下标 2**：GetFeature 下标 / 电量% / 充电态分别是 `resp[2]` / `resp[2]` / `resp[4]`（旧代码读 `resp[0]` 会把 feature 下标当成电量）。
- 发现 ping 醒着约 1s 才回，超时宜 ≥1.5s；设备名走 `GetFeature(0x0005)` 再 GetCount/GetChar；设备类型优先读接收器配对寄存器 `0xB5`（Unifying `0x2N` / Bolt `0x5N`），读不到再 `DeviceKindGuesser`。
- Unified Battery `0x1004`：**func 1 = GetStatus**（电量% + 充电态），**func 0 = GetCapabilities**（不能当电量）。组装请求必须 `(feature<<8)|(func<<4)`，写成 `|func` 会被 SwId 补丁抹成 func 0（真机曾把 Capabilities 的 15 误当电量，充着电也读不到 charging）。
- **实时充电**：插拔电时设备会发 HID++ 自发通知（`sw` 低 4 位=0，payload 与 GetStatus 同布局）。插件后台 `PumpNotifications` 只读不写，经 `StatusPushed` 即时 Upsert——不必等 60 秒轮询（与 Logi Battery Dashboard 同源）。未知 `devIdx` 的通知会当场补 ping 发现。
- 每次 `Attach` 按目录槽位数补扫尚未发现的索引（接收器上第二台不必等重启）。

**HID++ 报文层（字节级；实现见 `Plugins/Logitech/HidppIO.cs` / `HidppCodec.cs`）：**

| 报文 | 布局 |
|---|---|
| 短（7 字节） | `[0x10, devIdx, feature, (func<<4)\|sw, p0, p1, p2]` |
| 长（20 字节） | `[0x11, devIdx, feature, (func<<4)\|sw, p0..p15]` |
| 错误响应 | `data[0]` = `0x8F`（1.0）/ `0xFF`（2.0）；`data[1..2]` 回显请求前两字节；`data[3]` = 错误码 |
| 设备自发通知 | `sw` 低 4 位 = 0（本应用请求应答用 SwId `0x0B`，据此区分） |

**各特征的电量 / 充电编码（实现见 `HidppCodec.cs`）：**

| 特征 | 编码 |
|---|---|
| Battery Voltage `0x1001` | 大端毫伏 + flags；`bit7`=外部供电、低 3 位 `0`=充电中 / `1`=充满 / `2`=未充电、`bit5`=危急 |
| ADC Measurement `0x1F20` | 大端毫伏 + flags；`0x01`=放电 / `0x03`=充电 / `0x07`=充满 |
| 统一充电状态码（`0x1004` / `0x1000` 共用） | `1`/`2`/`4`=充电中、`3`=充满、其余=放电 |
| 配对信息 `0xB5` 字节布局 | Unifying `0x2N`：参数区 `[3..4]`=WPID、`[7]`=kind；Bolt `0x5N`：`[1]`=kind、`[2..3]`=WPID（小端，交换后成大端） |

## 电池模型（官方 powermodel，2026-11-13 逆向）

> G502 LIGHTSPEED 只有 `0x1001` 电压通道，**没有** `0x1000`/`0x1004` 百分比通道（真机 `GetFeature` 两者 idx 均为 0），
> 百分比完全由本地算法从毫伏换算。G HUB 用一套 XML 模型算，本应用现在复刻同一套。

**数据来源**：`C:\ProgramData\LGHUB\depots\<build>\<modelId>\battery.xml`（`pid` 即设备 WPID，G502 是 `0x407F`）。
仓库内已转成 [`battery-models.json`](../../src/VigilWatt.Core/Plugins/Logitech/battery-models.json) 并作为嵌入资源，
加载器 `BatteryModelCatalog`（按 WPID 索引），算法 `BatteryModel` + `ChargingSolver`。

**三段表各管什么：**

| 段 | 行数（G502） | 语义 |
|---|---|---|
| `<discharge>` | 707 | `时间(分), CCV(V), 累计已放出(mWh)`。**不是电压→百分比**，而是库仑计 |
| `<charging>` | 83 | `CCV(V), SoC(%)`。充电态专用曲线，同电压下远低于放电曲线 |
| `<charging_time_slots>` | 26 | `分钟, SoC(%)`。4.2V 恒压段按时间推进 |

**放电换算**：电压插值出"已放出 mWh"，再 `百分比 = (1 − 已放出/总容量) × 100`，总容量 902.34 mWh（截止 3.479V）。

**充电也走放电表**——这是对照 G HUB 实际行为得出的，见下节。

## 充电读数：与 G HUB 完全一致的实现

> **结论先行**：G HUB 充电时**也用放电表**，不是充电表，也不是"冻结"。它只是**更新很慢**（4~24 分钟一次），
> 所以插线后头几分钟看着像卡住不动。

**证据**：本机 `%LOCALAPPDATA%\LGHUB\settings.db-WAL` 里 G HUB 自己写下的四条记录，全部等于放电表结果：

| 时间 | 电压 | G HUB | 放电表 | 状态 | 吻合 |
|---|---|---|---|---|---|
| 18:53 | 4088 mV | 92% | 92.2% | 放电 | ✓ |
| 19:22 | 4084 mV | 92% | 91.8% | 插线瞬间 | ✓ |
| 19:46 | 4201 mV | **100%** | 100.0% | 充电中 | ✓ |
| 19:47 | 4170 mV | 100% | 99.6% | 充电中 | ✓ |

G HUB 的更新间隔实测为 13 / 4 / 11 / 17 / 24 / 1 分钟，没有固定节拍。

**实现**（`ChargingSolver`）：

1. **锚点** = 插线前最后一次放电读数（可回填历史库，见下）。起点就用它，不从电压重算。
2. 每帧用**放电表**算 `byDischarge`，只升不降：`if (byDischarge > Current)` 才推进。
   —— 充电时端电压被极化抬高（3.9V 就能读到 4.2V 表顶），放电表会把值推高；电压回落时不回退。
3. 拔线调 `Reset()`，清空锚点，下次插线重新锚定。

**为什么不用 `<charging>` 表和 `<charging_time_slots>`**：

`<charging>` 表描述的是"**从空电开始充到该电压时的 SoC**"，不是"当前电量的 SoC"。
同一块 92% 的电池插线后电压立刻被抬到 4199mV，用充电表算只有 67% —— 与 G HUB 的 92% 差 25 点。
`<charging_time_slots>` 是恒压段的经验时间表，G HUB 实测并没有用它（19:46 电压到 4201mV 就直接 100%，
没有按 66%→99% 的 88 分钟曲线走）。两段表仍保留在 JSON 里供对照，但**不参与显示值计算**。

**锚点回填**：冷启动时就插着线（没有"上一次放电"）时，从 `history.db` 查最近的放电采样当锚点
（`IBatteryPlugin.LastLogitechDischargeLookup`，由 `DeviceManager` 注入）。找不到才退回当前放电表结果。

**为什么不叠 `BatteryReadingFilter`**：有官方模型时放电表本身单调、`ChargingSolver` 又保证只升不降，
再叠一层跳变过滤会变成"锚点值 + 3 分钟延迟"，反而偏离 G HUB。无模型的旧曲线才需要过滤兜底。

**`flags` 低 3 位取值**（按 OpenLogi 对 `0x1001` 的逆向）：`0`=充电中、`1`=充满、`2`=接电但未充电、`3~7`=未定义。
旧代码把非 `1` 一律当"充电中"，会把 `2`（接电不充）误报成充电。

**验证**：

- `LogitechBatteryModelGoldenTests` 用 G HUB 自己存的 `(millivolts, percentage)` 配对逐点回归放电段
  （4195→100 … 4096→93 共 11 组），全部精确命中。
- `BatteryModelTests.M7~M11` 覆盖充电四条真机场景：插线锚点优先、电压升高后推进、电压回落不回退、Reset 重锚。


## 模型怎么选中：WPID 三条来路

| 场景 | 怎么拿 WPID | 备注 |
|---|---|---|
| 接收器 + 配对寄存器可用 | `0x83B5` 读配对信息 | Unifying/Bolt 走这条 |
| 接收器但 `0x83B5` 回错误码 06 | 目录 `receiverModels`：接收器 PID → 型号 WPID | **本机 C539 就是这条**（G502 单配接收器不支持配对寄存器） |
| 有线模式直连 | 目录 `directModels`：本体 PID → 型号 WPID | C08D → 407F，与接收器共用同一份模型 |

三条都没有 → `BatteryModelCatalog.Find` 返回 null，退回旧 `defaultLithium` 曲线（13 点，精度差 1~2 点）。

## `0x0005` 设备名长度上限

真机有线模式自报 **37 字符**（`G502 LIGHTSPEED Wireless Gaming Mouse`）。
旧代码 `resp[2] is 0 or > 32` 把 37 判无效，设备名退化成「罗技设备 255」。上限已放宽到 64。

## 直连设备多台（2026-11-06 真机事故）

插数据线后鼠标本体以有线 PID（G502 是 `C08D`）出现，**不走接收器**。本体对 `devIdx` 1~6 **全部应答**，
而旧逻辑是"先扫 1..6，全空才试 `0xFF`"，于是同一只鼠标被登记成 6 台（名字全是"罗技设备 N"，电量充电态完全相同）。

**修法**：首次扫描**先 ping `0xFF`**；通了立刻置 `DirectDevice`、只建一台、跳过槽位扫描。
直连设备无接收器，`0x83B5` 配对寄存器必然超时，已跳过；WPID 走 `directModels`。

## 设备 ID：无线与有线必须是同一台

**问题**：同一只鼠标两条通道的 HID 路径 PID 不同，按路径哈希算出两个 ID，列表里就是两台。

| 通道 | HID 路径片段 | 旧 ID |
|---|---|---|
| 2.4G 接收器 | `VID_046D&PID_**C539**&MI_02&Col02` | `logitech:E0F7218A1EE236AC:1` |
| 有线本体 | `VID_046D&PID_**C08D**&MI_02&Col02` | `logitech:5F2FE7EBCCE3B227:255` |

**修法**：能定型号时 ID 用 WPID，两条通道算出同一个 ID：

```
logitech:model:407F
```

`DeviceIdFor` 负责选择；定不出型号（`0x83B5` 读不到且目录没登记）才退回旧的 `路径哈希:槽位`。
好处是 `DeviceManager.Upsert` 天然合并、历史曲线连续、自定义名与黑名单共用一份。

**黑名单要判两套 ID**：发现阶段（`DiscoverDevices`）还不知道型号，只有槽位式 ID；
建立后用的是型号式 ID。`IsAnyIdBlocked` 两套都判，否则"永久移除"在另一种写法下会失效。

**ID 迁移**（1.3.2 起）：旧版本产生的 `logitech:<hash>:<槽位>` 需要一次性并入 `logitech:model:<WPID>`，
否则历史曲线会断成两段。迁移脚本见本节末尾，执行前先备份 `history.db`。

```sql
-- 先备份 history.db，再执行
UPDATE samples SET device_id = 'logitech:model:407F'
WHERE device_id LIKE 'logitech:%' AND device_id != 'logitech:model:407F';
```

`settings.json` 里的 `DeviceCustomizations` / `IgnoredDevices` / `BlockedDevices` 键名也要同步替换。


