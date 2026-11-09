# Windows 本机启动器

一个基于 Windows Forms 的小型 EXE，用于控制已安装依赖的 Lumina Studio 源码版。提供启动、停止、健康状态、检查更新、一键更新、打开网页、折叠日志和托盘。使用 Windows 自带的 .NET Framework 4.8，不需要 Node/Electron 桌面宿主，也不替代 Python 和前端依赖安装。

## 编译

在 Windows PowerShell 中执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/windows-launcher/build.ps1
```

输出在 `tools/windows-launcher/bin/`。也可通过 `-OutputDirectory` 指定交付目录。源码只使用系统 Framework 程序集，不需要 NuGet 包或额外 .NET SDK。

将生成的 `LuminaLauncher.exe`、`LuminaLauncher.exe.config`、`ApplyLauncherUpdate.ps1` 放在同一目录，双击 EXE。首次使用可在“设置”中指定 Lumina 项目目录、虚拟环境的 `python.exe` 和 `node.exe`。也可将示例配置复制为 EXE 旁的 `LuminaLauncher.json` 后修改；相对路径以配置文件所在目录为基准。实际配置含本机路径，不要提交。

## 使用

- “启动服务”启动后端与 Vite 网页服务，等待健康检查后再显示运行中；已有健康服务时不重复启动。
- “打开 Lumina 网页”使用默认浏览器访问 `http://localhost:4174/`。
- “停止服务”只匹配配置项目的 Python/前端进程并停止其进程树，不按端口号终止其他程序，也不修改耗材或工程文件。
- 关闭主窗口会收起到托盘并保持服务运行。双击托盘图标恢复窗口；托盘菜单“退出并停止服务”完整退出。
- 日志保存在 EXE 旁 `logs/`，主窗口也可展开查看。日志和机器配置不纳入 Git。
- 再次双击 EXE 会恢复已有窗口，包括已经收起到托盘的窗口。

前后端绑定本机 `127.0.0.1`，使用固定端口 8000 和 4174。依赖缺失、端口占用和启动失败会在窗口和日志中显示。该工具用于本机，不是服务器部署管理器。

## 检查更新与一键更新

需要可用的 Git、Python/pip、Node.js 和 Corepack。项目的 `origin/main` 作为官方 Lumina 更新来源；当前分支配置的另一条上游跟踪分支作为启动器更新来源。个人部署可以将 `origin` 指向官方仓库，个人功能分支跟踪自己的 `fork` 远端。

“检查更新”只抓取远端版本并比较提交，不停止服务，也不改变当前工作区。遇到短暂连接故障时最多自动重试两次。“一键更新”会再次检查版本；已经是最新时直接结束。请先保存网页工程，更新会重启服务，内存中的会话不属于耗材备份。

有更新时按以下顺序执行：

1. 检查未提交/未跟踪文件，在 Git 对象中预检合并。网页导入到 `lut-preset/Custom/` 的未跟踪 LUT 会保留并备份；其他未提交改动或合并冲突会拦截更新。
2. 在 EXE 旁 `backups/update-时间-编号/` 保存当前代码 ZIP、Python 依赖清单、旧启动器与配置。
3. 停止服务，备份实际使用的耗材目录（支持 `.env.local` 和环境变量覆盖）、本机设置、环境配置和 LUT。
4. 应用预检通过的合并，安装 Python 依赖，按锁文件安装并构建前端，编译新启动器，检查前后端健康。
5. 如果原来在运行，则恢复运行状态；原来已停止则保持停止。独立替换程序等待旧启动器退出后更换 EXE，再带恢复参数启动新版本。保留 `LuminaLauncher.json` 中的机器路径。

校验通过后，个人功能分支会尝试推送到其跟踪远端；不会向 `origin` 或 `main` / `dev-refactor` 推送。网络或权限导致推送失败时，本机更新保留，日志明确提示未同步。

依赖或健康检查失败时，用恢复提交还原之前的代码树，再按备份清单和锁文件恢复依赖及原有服务状态，不使用硬重置，也不丢弃更新期间出现的其他改动。恢复未完成时会显示错误及备份目录。回退记录允许后续再次尝试更新。上游若试图跟踪本地耗材/配置，或覆盖本机导入的 LUT，会阻止自动更新。

`backups/`、`launcher-update-retry.json` 和 `launcher-update-result.json` 都是本机运行数据。请把交付文件放在仓库外，或使用默认已忽略的 `bin/` 目录，不要把备份和本机配置提交到 Git。

## 集成验证

`-IncludeSmokeTests` 额外编译控制器集成验证程序。它会停止当前配置项目并重新启动，再验证端口占用处理、重复启动、耗材数量和服务停止。请在没有转换任务运行时执行；测试结束会停止服务。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/windows-launcher/build.ps1 -IncludeSmokeTests
.\tools\windows-launcher\bin\LauncherSmokeTests.exe C:\path\LuminaLauncher.json C:\path\smoke-report.json
```

界面还应手动检查按钮、托盘和日志区域。集成程序不会自动代表 GUI 检查通过。

更新器验证使用隔离的本地 Git 远端和测试工程，不更新正式 Lumina 仓库。会实际执行空依赖的 pip/pnpm 安装与前端构建，验证双来源更新、数据备份、脏工作区、冲突、数据覆盖保护和失败恢复/重试：

```powershell
.\tools\windows-launcher\bin\UpdaterIntegrationTests.exe C:\path\LuminaLauncher.json C:\path\empty-test-directory
powershell -NoProfile -ExecutionPolicy Bypass -File tools/windows-launcher/tests/SelfReplacementTests.ps1 -HelperPath tools/windows-launcher/ApplyLauncherUpdate.ps1 -TestDirectory C:\path\another-empty-directory
```

EXE 替换验证会编译无窗口的探针程序，测试正常替换、文件被锁定时的旧版本恢复，以及重启参数传递。`ApplyLauncherUpdate.ps1` 使用 UTF-8 BOM，以兼容 Windows PowerShell 5.1 的中文解析。
