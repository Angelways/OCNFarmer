# OCNFarmer

这是一个 Dalamud 插件项目，已替代原来的 SomethingNeedDoing Lua 脚本。

- 项目：`NorthIslandChestPlugin.csproj`
- 清单：`NorthIslandChestPlugin.json`
- 入口命令：`/ocnchest`
- 中文 ImGui 界面：开始/停止脚本、显示当前领地、宝箱数量和运行状态
- 使用 `IChatGui.ChatMessage` 监听 `XivChatType.SystemMessage`，读取魔寻宝输出
- 当前插件版本：`1.0.0`；蜃景幻界新月岛 北征之章自动寻宝

插件当前流程：不在领地 `1346` 时执行 `/pdrfe ocn`；进入蜃景幻界新月岛 北征之章后切换辅助白魔法师并开启 BOCCHI；通过 BOCCHI 同款原生通用动作调用释放魔寻宝；收到系统消息后解析银/铜宝箱；达到上限后执行自动寻宝流程。

构建前需安装 Dalamud 开发环境，并设置 `DalamudLibPath` 指向 Dalamud 的 `latest` 目录，然后运行：

```powershell
dotnet restore
dotnet build -c Release
```

当前版本尚未实现达到上限后的自动移动、开箱和奖励处理流程。
