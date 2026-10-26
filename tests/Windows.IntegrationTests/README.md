# Native gate fixture project

T00 建立本项目骨架。材质阶段已有 `MaterialTests.cs` 的 16 项组件/像素测试，与 55 项核心业务用例分别计数；不表示发布/硬件原生验收通过。阶段命令：`dotnet test tests/Windows.IntegrationTests/Windows.IntegrationTests.csproj -c Release`。

T02 独立进程探针在 `spikes/NativeHost.Spike`，有 48 项断言、Win+D 候选对比，以及一次经用户许可的真实 Explorer 重启报告，详见 `docs/validation/G2-host.md`。物理多屏/DPI、拖动/动画尚缺，G2 不标通过。后续正式产品用例需迁入此项目。

实际 WPF 桌面主程序的独立进程冒烟由 `scripts/smoke-desktop.ps1 -MaterialStage` 执行，当前 49 项检查通过，覆盖原生拖动、侧边裁切、四态、四热区/间隙、单实例/退出以及材质切换、采样参数、停渲染、释放和持久化。见 `docs/validation/desktop-stage.md` / `G3-spike.md`。完整球动效、物理多屏、G3 预算和 WPF Explorer 恢复尚未闭环。
