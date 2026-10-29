# DlutTimetable

一张贴在桌面上的大工周课表：上课时看一眼，空闲时让它安静待在一边。

<p align="center">
  <img src="src/DlutTimetable.Desktop/Assets/DlutTimetable.png" alt="大工课表图标" width="128" />
</p>

## 实际效果

使用当前课表与桌面壁纸实录。

**桌面常驻**：半透明周课表融入桌面，当前日期和课程安排一眼可见。

![半透明周课表固定在桌面上的实际显示效果](docs/images/desktop-resident.png)

**收起为悬浮球**：点击收起按钮缩成悬浮球，再点击悬浮球展开，回到原来的位置。

![周课表收起为悬浮球并恢复展开的动画](docs/images/minimize-to-ball.gif)

**侧边隐藏**：课表收进屏幕边缘，鼠标移到侧边条时展开，移开后自动收回。

![鼠标移入侧边条展开课表、移出后自动收回的动画](docs/images/edge-docking.gif)

## 能做什么

- 显示当前周课程，并醒目标出今天
- 从学校页面手动读取课表，也可以离线查看已保存的数据
- 透明度、缩放、配色和窗口位置都能自己调
- 固定、拖动、贴边隐藏、悬浮球几种桌面状态随手切换
- 系统托盘、导入导出、安装修复和卸载

## 下载与打开

目前提供 **Windows 11 x64 开发预览版 0.26.0-dev**。下载 Release 里的完整 ZIP，解压后运行 `DlutTimetable.Desktop.exe`；也可以运行 `Install.cmd` 做当前用户安装。

首次打开后，在“设置 → 数据与账号”里导入自己的课表（注意点击本科课程表）。学校刷新需要 WebView2 Runtime 和学校网络；手动导入及已缓存课表不依赖实时网络。应用不会在后台定时抓取课表。

> 这是未签名的预览版本，Windows 10、长时间运行、复杂多屏/DPI 等场景正在测试中。登录启动使用当前 Windows 用户的启动项，可能被 Windows 设置或组织策略禁用。

## 自己构建

需要仓库固定版本的 .NET SDK 10.0.402。首次可运行 `scripts/bootstrap.ps1`，然后：

```powershell
dotnet build DlutTimetable.slnx -c Release
```

生成独立 Windows 包：

```powershell
./scripts/publish-windows.ps1
```

更多构建和安装说明见 [`docs/BUILD.md`](docs/BUILD.md) 与 [`docs/PORTABLE.md`](docs/PORTABLE.md)。
