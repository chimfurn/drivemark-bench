# DriveMark 内置 fio 说明

- 版本：**fio-3.43**（上游：<https://github.com/axboe/fio>，tag `fio-3.43`）
- 构建：MSYS2 mingw-w64 gcc（`./configure --cc=gcc && make -j8`），仅依赖 Windows 系统 DLL
- 可选补丁：`patches/fio-3.43-drivemark-min-log-window.patch`

## 补丁作用（默认不启用）

Windows 下用 fio 测试**大块 + 高队列**（例如 1MB / QD32）时，完成事件会成批到达。
如果日志窗口很小（1ms/10ms），窗口内的"字节数 / 时间"会被整批完成瞬间填满，产生异常尖峰；
fio 的 JSON 汇总平均仍然正确。

本补丁给带宽/IOPS 日志增加一个**可选的 100ms 最小窗口**：

- **默认不生效**：不设置任何环境变量时，fio 保持上游原始行为；
- 只有软件在高级设置中勾选「启用 fio 日志平滑」时，DriveMark 才会给 fio 子进程设置
  `DRIVEMARK_FIO_MIN_LOG_MS=100`，此时带宽/IOPS 日志窗口 ≥ 100ms；
- **延迟日志不受影响**，仍按 `log_avg_msec` 保持高频。

## 验证方式

- 关闭平滑：bw/iops 日志按采样间隔原样记录（可观察到完成突发造成的尖峰）；
- 开启平滑：bw/iops 日志行数约为“时长 / 100ms”，尖峰被抑制；延迟日志点数不受影响。

## 许可证

fio 为 GPL-2.0；完整源码与许可证见 `FIO/`（`FIO/COPYING`）。
