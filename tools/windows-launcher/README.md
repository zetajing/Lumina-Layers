# Windows 本机启动器

一个基于 Windows Forms 的小型 EXE，用于控制已安装依赖的 Lumina Studio 源码版。提供启动、停止、健康状态、打开网页、折叠日志和托盘。使用 Windows 自带的 .NET Framework 4.8，不需要 Node/Electron 桌面宿主，也不替代 Python 和前端依赖安装。

## 编译

在 Windows PowerShell 中执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/windows-launcher/build.ps1
```

输出在 `tools/windows-launcher/bin/`。也可通过 `-OutputDirectory` 指定交付目录。源码只使用系统 Framework 程序集，不需要 NuGet 包或额外 .NET SDK。

将生成的 `LuminaLauncher.exe`、`LuminaLauncher.exe.config` 放在同一目录，双击 EXE。首次使用可在“设置”中指定 Lumina 项目目录、虚拟环境的 `python.exe` 和 `node.exe`。也可将示例配置复制为 EXE 旁的 `LuminaLauncher.json` 后修改；相对路径以配置文件所在目录为基准。实际配置含本机路径，不要提交。

## 使用

- “启动服务”启动后端与 Vite 网页服务，等待健康检查后再显示运行中；已有健康服务时不重复启动。
- “打开 Lumina 网页”使用默认浏览器访问 `http://localhost:4174/`。
- “停止服务”只匹配配置项目的 Python/前端进程并停止其进程树，不按端口号终止其他程序，也不修改耗材或工程文件。
- 关闭主窗口会收起到托盘并保持服务运行。双击托盘图标恢复窗口；托盘菜单“退出并停止服务”完整退出。
- 日志保存在 EXE 旁 `logs/`，主窗口也可展开查看。日志和机器配置不纳入 Git。

前后端绑定本机 `127.0.0.1`，使用固定端口 8000 和 4174。依赖缺失、端口占用和启动失败会在窗口和日志中显示。该工具用于本机，不是服务器部署管理器。

## 集成验证

`-IncludeSmokeTests` 额外编译控制器集成验证程序。它会停止当前配置项目并重新启动，再验证端口占用处理、重复启动、耗材数量和服务停止。请在没有转换任务运行时执行；测试结束会停止服务。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/windows-launcher/build.ps1 -IncludeSmokeTests
.\tools\windows-launcher\bin\LauncherSmokeTests.exe C:\path\LuminaLauncher.json C:\path\smoke-report.json
```

界面还应手动检查按钮、托盘和日志区域。集成程序不会自动代表 GUI 检查通过。
