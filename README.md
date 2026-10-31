# GlassDotClock

GlassDotClock 是一款适用于 Windows 的透明点阵时钟，同时可作为屏幕保护程序使用。界面采用全透明窗口与轻微中性灰蒙层，让桌面内容保持清晰可见，并以冰川蓝白点阵显示时间、日期和实时天气。

当前版本：**1.0**

## 功能

- 点阵时间显示，支持 12/24 小时制
- 可选秒钟、日期和呼吸动画
- 全透明桌面背景，仅叠加轻微灰度
- 根据公网 IP 自动定位城市并显示当前天气
- 点击天气区域可查看当日逐小时气温曲线
- 支持普通窗口、全屏屏保和 Windows 屏保预览
- 支持多显示器、托盘菜单和开机自启

## 系统要求

- Windows 10 或 Windows 11
- .NET Framework 4.x
- 天气功能需要网络连接；时钟本身可离线运行

## 使用方法

### 桌面时钟

运行 `GlassDotClock.exe`。主要快捷键：

- `F11`：进入全屏屏保
- `Esc`：退出全屏屏保
- 双击托盘图标：重新显示主窗口

### 安装为 Windows 屏幕保护程序

1. 右键单击 `GlassDotClock.scr`。
2. 选择“安装”。
3. 在 Windows 屏幕保护程序设置中选择等待时间，并按需启用“在恢复时显示登录屏幕”。

## 从源码构建

在项目目录打开 PowerShell，运行：

```powershell
.\build.ps1
```

构建完成后会在项目根目录生成：

- `GlassDotClock.exe`
- `GlassDotClock.scr`

项目直接使用 Windows 自带的 .NET Framework C# 编译器，无需安装额外依赖。

## 天气数据与隐私

GlassDotClock 使用 [ipwho.is](https://ipwho.is/) 根据公网 IPv4 地址估算城市和坐标，并使用 [Open-Meteo](https://open-meteo.com/) 获取天气预报。使用天气功能时，相关网络服务会收到完成请求所需的 IP 地址或坐标信息。天气数据署名：Open-Meteo.com（CC BY 4.0）。

## 版本

### 1.0

- 首个正式版本
- 提供透明灰度点阵时钟、日期、实时天气与气温曲线
- 支持 Windows 屏幕保护程序、托盘和开机自启
