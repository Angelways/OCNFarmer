# OCNFarmer 贡献：自动购买固定剂

面向上游仓库：[Angelways/OCNFarmer](https://github.com/Angelways/OCNFarmer)

## 功能摘要

在北征之章（地图 1346）初始营地，按背包白银币 / 白金币数量自动购买「终极固定剂」：

- 设置项：**自动购买固定剂**（默认关闭）
- 可配置白银币 / 白金币阈值（默认 1200 / 1920，与商店单价一致）
- 仅在初始营地水晶附近（约 60 码）触发
- 购买时**内部暂停**挂机（关 BOCCHI、停寻宝），**不**调用 `/ocnstop` / `/ocnstart`
- 用户 `/ocnstop` 会中止购买并停止全部自动流程
- 购买结束后内部恢复挂机（续宝箱检测或重新开 BOCCHI）
- 触发时机：亚返回后的营地检测流程中（在宝箱扫描前）

## 变更文件

| 文件 | 说明 |
|------|------|
| `FixativeBuyer.cs` | **新增** 购买状态机（交互 NPC、切「其他」、下单、确认、校验） |
| `Plugin.cs` | 接入状态机、UI/配置、内部暂停/恢复、亚返回后尝试购买；`/ocnstop` 中止购买 |
| `NorthIslandChestPlugin.csproj` | 版本 1.3.0；描述补充；`DalamudLibPath` 可用 MSBuild 覆盖 |
| `NorthIslandChestPlugin.json` | 描述补充 |
| `.gitignore` | 忽略 `bin/` `obj/` 等 |

不包含任何 Daily Routines 本地模块或其它仓库文件。

## 如何提交给原作者

### 方式 A：GitHub Pull Request（推荐）

1. Fork <https://github.com/Angelways/OCNFarmer>
2. 将本仓库分支 `feature/auto-buy-fixative` 推到你的 fork，或应用下方 patch
3. 向 `Angelways/OCNFarmer` 的 `main` 开 PR，正文可直接粘贴文末「PR 说明」

本地已有分支时：

```bash
cd tools/OCNFarmer
git push -u <你的fork远程> feature/auto-buy-fixative
```

### 方式 B：应用 patch

```bash
cd OCNFarmer   # 干净的 upstream main
git apply path/to/0001-auto-buy-fixative.patch
# 或
git am path/to/0001-auto-buy-fixative.patch
```

Patch 文件位置（本工作区）：

- `e:\ff14acr\tools\OCNFarmer-contrib\0001-auto-buy-fixative.patch`
- 同目录另有 `CONTRIBUTION.md`（本说明副本）与文件清单

### 方式 C：打包 zip

将下列文件发给原作者即可（相对仓库根目录）：

- `FixativeBuyer.cs`（新文件）
- `Plugin.cs`
- `NorthIslandChestPlugin.csproj`
- `NorthIslandChestPlugin.json`
- `.gitignore`（可选）

## 本地编译

默认 `DalamudLibPath` 与上游一致（`D:\XIVLauncherCN\addon\Hooks\dev`）。若本机路径不同：

```bash
dotnet build NorthIslandChestPlugin.csproj -c Release ^
  -p:DalamudLibPath="%AppData%\XIVLauncherCN\addon\Hooks\dev"
```

## 测试建议

1. 启用「自动购买固定剂」，阈值设为当前背包可触发的值
2. `/ocnstart` 进入北岛并刷 FT，亚返回到初始营地
3. 确认：购买时 BOCCHI/寻宝被内部暂停，买完后自动恢复（日志中无 `/ocnstop`/`/ocnstart`）
4. 购买进行中执行 `/ocnstop`，确认购买被中止且不会自行恢复挂机
5. 关闭开关后不再触发购买
6. 不在营地 / 不在北岛时不应购买

## PR 说明（可直接粘贴）

```markdown
## 摘要
- 新增可选功能：北岛初始营地按背包金银币阈值自动购买终极固定剂
- 设置中增加「自动购买固定剂」开关及白银/白金阈值（默认关闭）
- 购买时内部暂停挂机（关 BOCCHI），结束后内部恢复；不调用 `/ocnstop`/`/ocnstart`
- 用户 `/ocnstop` 会中止购买并停止全部自动流程

## 动机
北岛挂机时金银币容易积压；在返回初始营地时自动兑换固定剂，可减少手动操作。购买与挂机同属本插件，暂停应走内部状态，避免用停止命令自打断。

## 实现要点
- 新文件 `FixativeBuyer.cs`：营地检测 → 交互古钱鉴定师 → 选择商店对话 → 切「其他」→ 按物品 ID 下单 → 确认 → 校验扣币
- `Plugin.PauseForBuy` / `OnFixativeBuyFinished`：内部暂停与恢复；`Stop`/`/ocnstop` 调用 `FixativeBuyer.Abort`
- 仅在 Territory 1346 且距起始水晶约 60 码内购买

## 测试计划
- [ ] 开关关闭时不购买
- [ ] 开关开启且币量达阈值、在初始营地时会购买
- [ ] 购买过程中挂机暂停，结束后内部恢复（无 /ocnstop|/ocnstart）
- [ ] 购买中执行 /ocnstop 会中止购买且不恢复挂机
- [ ] 离开北岛或不在营地时不误触发
```
