# DriveMark

**DriveMark** 是一个面向 Windows 的磁盘测速图形界面，基于开源项目
**[fio (Flexible I/O Tester)](https://github.com/axboe/fio)** 开发。

> 实际磁盘 I/O 测试由 **fio** 完成；DriveMark 负责界面、参数校验、调用 fio、结果解析与绘图、安全护栏。
> DriveMark is a Windows disk benchmark GUI built on fio.

## 功能 / Features

- 简单模式：测试项目、块大小、文件大小、测试时长、采样间隔、队列深度、队列数
- 实时绘图 / 完成后绘图；折线图 / 散点图；X 轴时间或累计传输量；Y 轴吞吐量 / IOPS / 延迟
- 长测试支持横向滑动查看；结果可保存到 `results\`（job / JSON / 日志 / 摘要）
- 中文 / English 双语界面，可在顶栏切换
- 写测试安全护栏：只写入目标盘的 `DriveMark-test` 目录，测试后自动清理
- RAW / 未格式化磁盘测试：默认只读；写入需显式确认（会覆盖数据），整盘测试需要管理员权限
- 内置 **fio-3.43**（含一个小补丁：带宽/IOPS 日志最小 100ms 平滑窗口，避免大块+高队列的假点；
  延迟日志仍保持高频）

## 下载 / Download

- 从 [Releases](https://github.com/chimfurn/drivemark-bench/releases) 下载 `DriveMark-v*-win64.zip`
- 系统要求：**Windows 10/11 x64**，已安装 **.NET 9 或 .NET 10 桌面运行时**
- 解压后双击根目录的 **`DriveMark.exe`**
- 整盘 RAW 测试：右键 `DriveMark.exe` →「以管理员身份运行」

## 使用 / Usage

1. 选择目标磁盘；
2. 选择测试项目（随机读 / 顺序写等）、块大小、文件大小、测试时长、采样间隔；
3. 点击「开始测试」，右侧显示实时进度与曲线；
4. 测试结束后结果保存在 `results\<运行号>\`，可点「打开结果目录」查看。

> 写测试会在目标盘创建 `DriveMark-test` 目录，测试完成后自动删除；不会格式化磁盘。
> RAW 写入会直接覆盖设备数据且无法恢复，仅用于可清空的盘。

## 构建 / Build

需要 **.NET 9 SDK**（Windows）：

```powershell
dotnet build DriveMark.slnx
dotnet publish src\DriveMark.App\DriveMark.App.csproj -c Release -o app
```

fio 可执行文件与源码位于 `FIO/` 目录；程序运行时会自动定位 `FIO\fio.exe`。

## 目录结构 / Repository layout

```
src/            C# / WPF 源码（App / Chart / Core）
tests/          测试（129 项）
tools/          命令行工具与辅助脚本
FIO/            fio-3.43 源码、许可证与构建产物
docs/           示例数据（合成日志）
DriveMark.exe   已编译程序（本地构建产物，不提交）
```

## 基于 fio / Built on fio

- fio：**fio-3.43**，作者 **Jens Axboe** 及贡献者
- fio 主页：<https://github.com/axboe/fio>
- fio 许可证：**GPL-2.0**；完整源码与许可证见 `FIO/`（`FIO/COPYING`）
- DriveMark 对 fio 的修改仅限 `FIO/patches/` 中的最小补丁；其他组件未修改

## 许可证 / License

本项目采用 **GPL-2.0** 许可证，见 [LICENSE](LICENSE)；
第三方组件声明见 [NOTICE.md](NOTICE.md)。
