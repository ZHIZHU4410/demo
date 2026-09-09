# OnePerkOnly —— 单一变异（Perk）模式

## 功能
在模组选项里先选定【唯一允许】的一个变异（游戏代码里叫 Perk / 变异，物品组 12 且 droppable=true，即变异商人确实会提供的那些）。

开启后，游戏内所有变异选择界面（PerkSelect，过关遇到“变异商人 Guillain”的界面）：
- 选择列表里只出现你选定的那一个变异；
- 其它变异不可选、也不会进包；
- 节奏与原版一致：每次遇到 Guillain 拿一个（槽位剩余>0 时），后面每次遇到再拿一个相同的，直到变异槽满（默认 3 个 = 同一个变异 ×3）。Guillain 的门在你拿满当前可拿数量后打开。

**效果叠加（默认开启）**：
- 同一变异拿 N 份时，“随卷轴成长”类效果（公式 `prct + customScaling×(tier−1)`）会被放大 N 倍 —— 通过 Hook `Entity.getRelevantPerkTier` 返回等效卷轴数实现（对按卷轴缩放属性的变异是精确 ×N）。
- **固定阈值类变异（如处决 `P_Execute_LowHealth`）**：不走卷轴 tier，改为每帧把该物品定义的 `props.prct` 写成 `基础值 × 持有份数`（拿 1/2/3 份 → 处决血量线 1/2/3 倍，上限 90%），关闭/退出/份数回到 1 时自动还原基础值。

> 限制说明：游戏引擎的变异效果都是“按 id 布尔判断 + 按卷轴缩放”，本身不读持有份数；因此只有走 `getRelevantPerkTier` 的随卷轴变异和本模组特判的固定阈值类变异能叠加。其它固定数值/一次性（如 YOLO、Wish）效果没有通用放大途径（需要逐条改游戏内每个效果公式）。

关闭开关或清空变异选择即恢复原版。

## 使用
1. 模组自动安装到 `Dead Cells\coremod\mods\OnePerkOnly`（Debug 构建自动安装）。
2. 进游戏 → 选项（Options）→ 模组设置里找到 **OnePerkOnly**。
3. “Enable single-perk mode” 默认开启。
4. “Stack effects xN (same perk xN)” 默认开启（叠加）。
5. “Only perk (mutation)” 列表：左右切换选择要锁定的变异，选完即保存。
6. 启动游戏进一局：开局的变异选择（以及后续每个关卡间的变异选择）只会让你选这一个变异，且可重复选到满。

> 变异显示的是物品 id（例如 P_AttackSpeed_Combo 等英文 id）。每局开始时模组会把全部可用的组 12 变异 id 打印到加载器控制台，方便对照选择。

> 注意：主菜单阶段物品数据库（data.cdb）可能尚未加载，若选项里显示“Perk list unavailable / 特长列表不可用”，请
> 1) 进一局游戏后在暂停菜单里再打开本模组设置（此时列表已就绪并会自动刷新）；或
> 2) 直接编辑配置文件 `Dead Cells\coremod\config\OnePerkOnly.json`，把 `perkId` 填成控制台日志里打印的某个变异 id。

## 配置文件
配置持久化在 `Config<Configs>("OnePerkOnly")`（同 AutoParry 等模组机制），即 `Dead Cells\coremod\config\OnePerkOnly.json`：
```json
{ "enabled": true, "stack": true, "perkId": "P_AttackSpeed_Combo" }
```
- `enabled`：总开关；`stack`：是否把锁定的变异按持有份数 ×N 放大效果；
- `perkId` 为空 = 不限制（原版）。

## 实现原理（简）
- Hook `dc.ui.PerkSelect.alreadyKnown`：对锁定变异永远返回 false（已拥有也当作“可再选”）；
- Hook `dc.ui.PerkSelect.requirementsOk`：仅锁定变异且还有剩余变异槽时返回 true，其它一律 false；
- Hook `dc.ui.PerkSelect.isVisible`：限制时只显示锁定变异（面板行过滤）；
- Hook `dc.tool.Inventory.add`（兜底）：限制时非锁定变异物品禁止入包（按 `_itemData.id` 判定真实变异 id）；
- Hook `dc.en.inter.npc.PerkMaster.getAvailablePerks`：锁定变异不可提供时放行（防卡门）；
- Hook `dc.Entity.getRelevantPerkTier`（叠加）：锁定的变异持有 N 份时返回等效卷轴数，使 `prct + customScaling×(tier−1)` 的效果 = N × 单份；
- 变异列表运行时从 `Data.Class.item`（组 12 + droppable）枚举。
