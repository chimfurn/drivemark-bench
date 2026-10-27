# DriveMark — 第三方组件与来源说明 / Third-party notices

## 基于 FIO / Built on fio

DriveMark 是一个基于开源项目 **fio（Flexible I/O Tester）** 开发的图形界面（GUI）。
DriveMark 只负责界面、参数校验、调用 fio、结果解析与绘图、自动化与安全护栏；
所有磁盘 I/O 测试均由 fio 完成。

- **内置 fio 版本**：fio-3.43（本软件随包分发，包含一个最小补丁：
  带宽/IOPS 日志最小 100ms 平滑窗口，见 `FIO/patches/fio-3.43-drivemark-min-log-window.patch`）
- **fio 作者**：Jens Axboe 及贡献者
- **fio 项目主页**：https://github.com/axboe/fio
- **fio 许可证**：GNU General Public License v2.0（GPL-2.0）；
  完整许可证文本与对应源码位于 `FIO/` 目录（`FIO/COPYING` 为许可证，`FIO/init.c` 等为源码）。
- 本软件对 fio 的修改仅限上述补丁；fio 其余部分未修改。

## 其他

- 本项目的界面/图表代码（`src/`、`tests/`、`tools/`）由本项目维护。
- 第三方组件均保留其原始版权与许可证声明。