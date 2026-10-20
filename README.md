不要下载这个页面的内容，工具zip的下载地址：https://github.com/TSO4/PCL-DownloadSwitchPlugin/releases

# PCL DownloadSwitchPlugin

PCL 第三方下载开关与独立备用安装器。

作者 / 维护者：**TSO4**。插件内署名及现有许可证署名为 **Real Knife — The Origin of 404**。

这是独立的第三方工具，不代表 PCL、Mojang、Microsoft 或相关加载器项目官方。

## 下载与安装

普通用户请到本仓库右侧的 **Releases（发行版）**，或者点击这个链接：https://github.com/TSO4/PCL-DownloadSwitchPlugin/releases
下载对应链接里面的"PCL-DownloadSwitch-1.3.2.zip" 。
GitHub 自动提供的 `Source code (zip)` 是源码，需要自行编译。

1. 找到平时使用的 PCL 主程序，在它旁边打开 `PCL` 文件夹。
2. 完整解压下载的 ZIP，把里面的 `DownloadSwitchPlugin` 文件夹放进去。
3. 正常启动 PCL，再运行 `DownloadSwitchPlugin` 中的 `PCL下载开关.exe`。
4. 在浮动面板上打开“恢复 MC 下载”，回到 PCL 下载页面选择版本。

目录示例：

```text
你的启动器文件夹/
├─ PCL 主程序.exe
└─ PCL/
   └─ DownloadSwitchPlugin/
      ├─ PCL下载开关.exe
      ├─ PCL备用下载.exe
      ├─ PCL.DownloadSwitch.dll
      ├─ PCL.DownloadSwitch.Native.dll
      ├─ Mono.Cecil.dll
      └─ source/
```

不要直接从 ZIP 内运行。更新前结束下载任务并关闭 PCL、备用窗口及后台助手，
使用完整新版插件文件夹更新；保留旧 `data` 可以保留设置。
不需要替换 PCL 主程序。

详细步骤：[通用安装教程](docs/通用安装教程.txt)。

## 功能

- 为仍保留原有下载接口的 PCL 提供运行时下载开关。
- 独立备用下载窗口，支持原版、Forge、Fabric、NeoForge、Quilt 的相应可用版本。
- 读取当前 PCL 游戏目录及已添加的目录，也可手动选择或新建游戏目录。
- 按 MC 元数据检测 Java，版本名称默认包含 MC 与加载器版本，可自行修改。
- 官方源与镜像（BMCLAPI）选择、失败回退、本地缓存复用和文件哈希校验。
- 下载任务保护、错误日志、只读源码查看、SHA-256 计算与比较。
- 可选自动接入与启用下载；新安装默认关闭，需在设置中主动开启。

## 1.3.2 测试版

这版修复两类“操作失败”：同名资源索引内容不同，以及 Forge 的安装中间文件
与已有 mappings 文件冲突。已有文件保留，新版本使用经过校验的独立索引；
加载器只发布运行所需的库。其他不同内容的文件冲突仍会停止并报告。

另有目录列表选择、自动版本名称、最多 16 个文件并行处理、失败来源冷却，
以及浅色下拉框黑色文字。实际速度受网络、缓存和下载节点影响。

本次在独立目录完成 **MC 1.21.11 原版**与 **Forge 61.2.4** 的真实安装；
没有启动游戏游玩。PCL 2.10.8.0 与 2.99.0.0 的目录字段已检查；
1.3.2 没有重新进行全部真实 PCL 接入测试。

此前版本的接入及其他加载器安装记录列在[验证记录](docs/验证记录.txt)。
结构兼容检测不等于所有历史版本、未来版本或第三方修改版都能使用。

## 源码与构建

源码在 `source/` 中。该目录与发布包所附源码一致，保留 `source/vendor/`。
插件源码页显示编译时内嵌的文本；只读展示，不执行页面里的代码。

构建环境：

- 64 位 Windows；
- .NET Framework 4.8；
- Visual Studio / Build Tools 的 MSVC C++ 工具、Windows SDK、NETFX SDK 4.8。

在仓库根目录打开 PowerShell：

```powershell
.\source\build.ps1
```

构建输出在 `source/build/`。普通用户运行 Releases 的预编译版本不需要安装编译工具。
不承诺不同环境编译出的文件逐字节相同。

## 反馈问题

请在 **Issues** 中填写插件版本、PCL 版本、MC/加载器版本、复现步骤、错误时间及相关日志。
日志位于插件的 `data/plugin.log`；官方加载器日志位于
`data/installer/jobs/任务编号/loader-install.log`。
转发前检查日志里的本机路径，不要上传个人 `data` 整包或账号资料。

## 许可与来源

本项目自有源码采用 [MIT License](LICENSE)。第三方 Mono.Cecil 0.11.6 使用其原有
[MIT 许可证](source/vendor/Mono.Cecil.LICENSE.txt)。

- [PCL](https://github.com/Meloong-Git/PCL)：原启动器及公开接口参考，PCL 自身有独立许可。
- [BMCLAPI](https://bmclapidoc.bangbang93.com/)：镜像下载接口。
- [Mono.Cecil](https://github.com/jbevain/cecil/tree/0.11.6)：程序集元数据读取。
- Mojang、Fabric、Quilt、Forge、NeoForge 的元数据与官方安装器接口，详见[使用说明](docs/使用说明.txt)。

本项目不包含 Minecraft 游戏文件，不重新授权 PCL 或其他第三方项目。
第三方修改、重新编译或重新打包的版本不代表本仓库作者发布或认可。
许可证及完整说明见随包文档；SHA 相同用于比较文件内容，不等于安全认证。

有帮助的话麻烦给项目点个star
