# DriveMark 内置 fio 说明

- 版本：**fio-3.43**（上游源码：https://github.com/axboe/fio ，tag fio-3.43）
- 构建：MSYS2 mingw-w64 gcc 16.2.0（`./configure --cc=gcc && make -j8`），仅依赖 Windows 系统 DLL。
- 补丁：`patches/fio-3.43-drivemark-min-log-window.patch`

## 补丁解决什么问题

Windows 下用 fio 测试**大块 + 高队列**（例如 1MB / QD32）时，完成事件会成批到达。
如果用户把采样间隔设为 1ms/10ms，fio 的 bw/iops 日志窗口会被整批完成"瞬间填满"，
从而记录出 **上万 MiB/s 的假点**（实测 1ms 采样出现 31,000 MiB/s）；但 fio 的 JSON
汇总平均（约 291 MiB/s）与实际磁盘速度一致，说明磁盘并没有那么快。

补丁做法：把 **带宽/IOPS 日志的最小平滑窗口固定为 100ms**（同时同步
`bw_avg_time` / `iops_avg_time`），而**延迟日志仍按 `log_avg_msec`（1ms/10ms）保持高频**。
这样曲线既能保持较密的采样（10 点/秒），又不会出现不可能的速度尖峰。

## 验证数据（E 盘 exFAT USB，1MB / QD32 / direct=1 / 10 秒）

| 配置 | bw 日志点数 | bw 最大值 | 延迟日志点数 |
|---|---|---|---|
| 原版 3.43，1ms 采样 | 1210 | 32,000 MiB/s | 1227 |
| 补丁版，1ms 采样 | 94 | 356 MiB/s | 1233 |
| 补丁版，10ms 采样 | 93 | 330 MiB/s | 434 |
| 对应 fio JSON 平均值 | - | 约 273 MiB/s | - |