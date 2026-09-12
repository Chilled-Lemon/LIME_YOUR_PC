# LIME_YOUR_PC

一个面向 Windows 的轻量级电源计划优化工具。

LIME_YOUR_PC 会根据当前硬件平台创建并维护一个独立的高性能电源计划，主要针对游戏性能、系统响应速度以及接通电源时的处理器行为进行优化。

> 当前版本：**v0.4.0**

---

## ✨ LIME 简介

<p align="center">
  <img src="screenshots/main.png" width="900">
</p>

LIME_YOUR_PC 目前主要针对 Windows 电源计划进行优化。

程序会检测当前电脑的 CPU、设备类型和处理器拓扑，并根据不同硬件应用对应的优化规则。

目前主要优化：

- USB 选择性暂停
- USB3 Link Power Management
- CPU 性能提升阈值
- CPU 核心停放
- 处理器节流状态
- CPU Idle 策略
- CPU 性能检测时间间隔
- 最小处理器状态
- 异构线程调度策略
- 接通电源时的显示器超时设置

---

## 🧠 硬件自适应

LIME_YOUR_PC 并不是简单导入一份固定的电源计划。

程序会根据当前电脑的实际硬件环境动态应用部分参数。

### AMD CPU

异构线程调度策略：

```text
All processors
```

对应值：`0`

### Intel 非混合架构 CPU

例如：

```text
Intel Core i9-11900H
```

使用：

```text
Performant processors
```

对应值：`1`

### Intel P-Core + E-Core CPU

如果 Windows 当前检测到性能核和能效核同时启用，则使用：

```text
Prefer performant processors
```

对应值：`2`

程序会根据 **Windows 当前实际可见的 CPU 拓扑**进行判断，而不是只根据 CPU 型号进行猜测。

---

## 🔌 只修改 AC / 接通电源参数

LIME_YOUR_PC 的一个重要设计原则：

> **只修改接通电源（AC）时的参数。**

程序不会修改笔记本的 DC / Battery 电池模式参数。

因此笔记本拔掉电源之后，仍然可以继续使用原本的节能策略。

---

## 🔋 独立电源计划

程序不会直接修改或删除 Windows 原有的：

- 平衡
- 高性能
- OEM 电源计划
- 用户自己创建的电源计划

首次运行时会创建独立的：

```text
LIME_YOUR_PC
```

后续再次运行时会复用已有计划，重新应用并验证优化参数，而不会重复创建新的电源计划。

如果电脑中存在旧版本创建的：

```text
LEMON_YOUR_PC
```

新版本也会自动识别并迁移为 `LIME_YOUR_PC`。

---

## 🛡️ 白名单优化

LIME_YOUR_PC 不会直接导入一整套来源不明的电源计划参数。

程序只修改明确加入优化白名单中的设置：

> **没有明确决定修改的参数，就保持 Windows / OEM / 用户原来的设置。**

优化完成后，程序还会重新读取 Windows 当前实际的 AC 参数进行验证。

成功时可以在运行日志中看到：

```text
[PASS] USB 选择性暂停 = 0
[PASS] USB3 Link Power = 0
[PASS] 性能提高阈值 = 1
[PASS] 最小处理器状态 = 100
```

---

## 🚀 使用方法

前往 GitHub 仓库的 **Releases** 页面下载：

```text
LIME_YOUR_PC.exe
```

然后直接运行即可，无需安装。

由于程序需要创建和修改 Windows 电源计划，因此启动时会请求 **管理员权限**。

---

## 🛠️ 从源码编译

需要安装：

```text
.NET 8 SDK
```

然后运行：

```text
build.bat
```

项目使用：

```text
win-x64
Self-contained
Single-file
```

模式发布。

最终 EXE 位于：

```text
bin\Release\net8.0-windows\win-x64\publish\LIME_YOUR_PC.exe
```

Self-contained 版本不要求目标电脑额外安装 .NET 8 Runtime。

---

## 🔐 安全说明

LIME_YOUR_PC：

- 不联网
- 不上传用户数据
- 不下载额外文件
- 不安装驱动
- 不删除 Windows 原有电源计划
- 不修改 DC / Battery 参数
- 不修改优化白名单以外的电源设置

项目源码完全公开，可以自行检查程序实际执行的操作。

---

## 🛡️ 关于杀毒软件误报

LIME_YOUR_PC 属于 Windows 系统优化工具，并具有：

- 请求管理员权限
- 修改系统电源设置
- 调用 Windows 系统 API
- 使用 `powercfg`
- Self-contained 单文件 EXE
- 暂无商业代码签名

等特征。

因此部分杀毒软件或平台的启发式检测可能会将未知版本标记为：

```text
Generic / Heuristic / Suspicious / ML
```

如果不信任 Release 提供的 EXE，可以检查公开源码并自行编译。

---

## 🎮 优化目标

LIME_YOUR_PC 主要面向：

**游戏性能 / 系统响应速度 / 降低部分节能机制可能带来的响应延迟**

而不是追求最低功耗、最长续航或最低温度。

不同 CPU、主板、BIOS 和 Windows 版本的实际效果可能有所不同。

如果遇到兼容性问题，可以随时切换回 Windows 原有电源计划。

---

## 🧪 当前开发状态

目前的核心功能：

```text
Power Plan Optimization
```

未来可能增加：

- 当前值 → 推荐值对比
- 可选择的优化项目
- 一键恢复默认设置
- 更多 CPU 平台规则
- 系统优化模块
- 网络优化模块

---

## 📜 License

本项目采用 **MIT License**。

---

## ❤️ 关于项目

LIME_YOUR_PC 是一个以学习、实践和实际使用需求为基础逐步开发的 Windows 优化工具。
修改方案取自抖音 费利克斯Fx id:94195789190

> **本项目尽可能只修改明确、可解释、可验证的设置。**

如果你发现 Bug、兼容性问题，或者有优化建议，欢迎提交 Issue。
