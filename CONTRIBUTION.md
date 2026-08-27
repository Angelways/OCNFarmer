# OCNFarmer 贡献：远程货币兑换

面向上游仓库：[Angelways/OCNFarmer](https://github.com/Angelways/OCNFarmer)

## 功能摘要

在南征（1252）/ 北征（1346）初始魔路水晶旁（10 码），按背包货币数量自动兑换：

| 兑换目标 | 地图 | 货币 |
|---------|------|------|
| 终极固定剂 (51978) | 仅北征 1346 | 白银币 51975 / 白金币 51976 |
| 古旧的钱箱 (47740) | 南征 1252 + 北征 1346 | 北：51975/51976；南：45043/45044 |

实现采用 **Keita 同款远程无 UI 流程**（不交互 NPC、不切「其他」页签）：

1. `EventStart(玩家 EntityId, eventId)` — 使用玩家实体，非 NPC
2. `AgentShop` 查找奖励物品 → `AgentModule.ReceiveEvent` 下单
3. Addon 生命周期自动确认 `ShopExchangeCurrencyDialog` / `SelectYesno`，并隐藏商店 UI
4. `EventComplete(eventId)` — 所有退出路径（完成/中止/超时）均发送，避免 UI 锁 / OccupiedInEvent
5. 关闭并隐藏 `ShopExchangeCurrency` 等窗口

### 设置项

- **启用自动货币兑换**（默认关闭）
- **兑换目标**：终极固定剂 / 古旧的钱箱
- **触发方式**：
  - 返回初始水晶且达到阈值（亚返回后营地检测，可配银/金阈值）
  - 在初始水晶且货币满 9999（寻宝流程到达小水晶时触发）
- **每次最多兑换数量**（默认 20）

### 与挂机集成

- 兑换时**内部暂停**挂机（关 BOCCHI、停寻宝），**不**调用 `/ocnstop` / `/ocnstart`
- 用户 `/ocnstop` 会中止兑换并停止全部自动流程
- 兑换结束后内部恢复挂机（续宝箱检测或重新开 BOCCHI）

## 变更文件

| 文件 | 说明 |
|------|------|
| `CurrencyExchangeBuyer.cs` | **新增** Keita 远程兑换状态机（队列、AgentShop、EventComplete） |
| `ShopEventPackets.cs` | **新增** EventStart/EventComplete 发包（玩家实体 ID） |
| `Plugin.cs` | UI/配置、内部暂停/恢复、触发逻辑；`/ocnstop` 中止兑换 |
| `NorthIslandChestPlugin.csproj` | 版本 1.3.0+；描述补充 |
| `.gitignore` | 忽略 `bin/` `obj/` 等 |

删除：`FixativeBuyer.cs`（旧 UI 交互流程，已由远程兑换替代）

不包含任何 Daily Routines 本地模块。

## 如何提交给原作者

### 方式 A：GitHub Pull Request（推荐）

1. Fork <https://github.com/Angelways/OCNFarmer>
2. 将分支 `feature/auto-buy-fixative` 推到你的 fork
3. 向 `Angelways/OCNFarmer` 的 `main` 开 PR，正文可粘贴文末「PR 说明」

```bash
cd tools/OCNFarmer
git push -u fork feature/auto-buy-fixative
```

Fork 对比链接（xiaoxiaogugu）：

<https://github.com/xiaoxiaogugu/OCNFarmer/compare/main...feature/auto-buy-fixative>

## 本地编译

```bash
dotnet build NorthIslandChestPlugin.csproj -c Release ^
  -p:DalamudLibPath="%AppData%\XIVLauncherCN\addon\Hooks\dev"
```

## 测试建议

1. 启用「自动货币兑换」，选择目标与触发方式，阈值设为可触发值
2. 站在初始魔路水晶 10 码内，Debug 面板点「立即尝试货币兑换」
3. 确认：无 NPC 对话、商店 UI 被隐藏、背包货币减少/奖励增加
4. `/ocnstart` 北岛刷 FT，亚返回后（阈值模式）或到达小水晶满 9999（满额模式）自动兑换
5. 兑换中执行 `/ocnstop`，确认中止且不自行恢复挂机
6. 南征 1252 仅古旧钱箱可兑；北征固定剂/钱箱均可
7. 日志应出现 `EventStart player=...` 与 `EventComplete`，无 OccupiedInEvent 卡死

## PR 说明（可直接粘贴）

```markdown
## 摘要
- 新增可选功能：初始水晶旁远程兑换终极固定剂或古旧钱箱（Keita 同款无 UI 流程）
- 北征 1346：固定剂 + 钱箱；南征 1252：仅钱箱
- 触发：亚返回阈值 / 水晶旁货币满 9999
- 购买时内部暂停挂机，结束后内部恢复；`/ocnstop` 中止兑换

## 动机
北岛/南岛挂机时城邦货币容易积压；返回初始水晶时自动兑换，减少手动操作。采用 EventStart(玩家)+AgentShop+EventComplete 避免旧版 NPC 交互导致的 UI 锁死。

## 实现要点
- `CurrencyExchangeBuyer.cs`：EventStart(玩家) → AgentShop 下单 → 自动确认 → EventComplete（所有退出路径）
- `ShopEventPackets.cs`：独立发包，不依赖 OmenTools
- `Plugin.PauseForBuy` / `OnCurrencyExchangeFinished`：内部暂停与恢复

## 测试计划
- [ ] 开关关闭时不兑换
- [ ] 阈值/满 9999 模式在初始水晶旁正确触发
- [ ] 固定剂（1346）与钱箱（1252/1346）均可兑换
- [ ] 兑换中挂机暂停，结束后内部恢复（无 /ocnstop|/ocnstart）
- [ ] 兑换中 /ocnstop 中止且不恢复挂机
- [ ] EventComplete 发送，无 UI 锁 / OccupiedInEvent
```
