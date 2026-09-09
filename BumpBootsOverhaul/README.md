# BumpBootsOverhaul

死亡细胞《BumpBoots（斯巴达凉鞋）》强化模组。

## 功能

0. **删除第一、二段平a，只保留第三段** —— 通过 **data.cdb 数据补丁**把连击段 `strikeChain` 从 3 段精简为 1 段（只留原第三段），武器只有一段连击，**每次平a都是第三段大踹飞**（原版第三段的机制/动画/判定全部保留并强化）。
1. **攻击范围变大五倍** —— 第三段（唯一一段）的攻击判定框从「宽 3 格 × 高 2 格」放大到「宽 15 格 × 高 10 格」（offsetCaseX 0.8 不变，贴脸也能踢到）。补丁以 `res.pak` 随模组安装，启动时合并。
2. **无视墙体** —— 命中判定跳过墙/地形阻挡检查：墙后、身后、上下层的敌人，只要落在英雄周围 5 倍范围圈（半径 ≈ 19 格）内都会被这一脚踹到。
3. **踹的距离更远** —— 击退冲量 `props.bump` 2 → 10（水平 ×5），运行时再把击飞的上抛分量放大 3 倍，怪物呈抛物线远远飞出。
4. **怪物密度增大到 400%** —— Hook `User.br_getExtraMobDensity` 返回 4.0（刷怪密度 ×4，刷怪器 MobsGen 叠加，常规关卡与 Boss Rush 通用）。到处都是怪，一脚一大片。

## 数据补丁（data.cdb / res.pak）

- `patch_bumpboots_cdb.py`：基于 MDK v35 模板生成 `data.cdb`（仅改 `weapon` 表 BumpBoots 第三段的 area / bump）。
- 构建时（仿照 DiverseDeckOverhaul）：
  `DCCMTool cdb diff`（对比游戏模板）→ diff.pak → `pak unpack` → `Assets/data.cdb_/weapon/BumpBoots.json` → MDK `PackAssetsIntoPak` 打成 `res.pak` 自动安装。
- **启动时加载**：模组在 `IOnAfterLoadingAssets` 中把 `res.pak` 挂载进 `FsPak`（`FsPak.Instance.FileSystem.loadPak`），
  游戏的 `CDBManager` 在首次关卡生成时合并 `data.cdb_` 补丁。若未加载 res.pak，范围/冲量数据不会生效。

修改数值时：编辑 `patch_bumpboots_cdb.py` 顶部常量 → 重跑 `python patch_bumpboots_cdb.py` → 重新 `dotnet build`。
注意：连击精简与判定框放大在**数据层**；"无视墙体"的命中判定与上抛放大在**代码层**，两层叠加缺一不可。

## 可调参数

| 位置 | 参数 | 默认 | 说明 |
|---|---|---|---|
| `patch_bumpboots_cdb.py` | `KEEP_STRIKE_INDEX` | 2 | 只保留第几段（删掉其余段；改成精简前逻辑可恢复三段连击） |
| `patch_bumpboots_cdb.py` | `AREA_W_MULT` / `AREA_H_MULT` | 5.0 | 判定框宽/高倍率 |
| `patch_bumpboots_cdb.py` | `BUMP_MULT` | 5.0 | 水平击退冲量倍率 |
| `BumpBootsOverhaulMain.cs` | `KICK_DY_MULT` | 3.0 | 上抛（竖直）分量倍率，踹得更高更远 |
| `BumpBootsOverhaulMain.cs` | `WALL_IGNORE_REACH_TILES` | 19.0 | 无视墙体的兜底命中半径（格） |
| `BumpBootsOverhaulMain.cs` | `MOB_DENSITY` | 4.0 | 怪物密度（400%） |

## 数据改动对照

原版 `weapon` 表 `BumpBoots`：

| 改动 | 原版 | 改动后 |
|---|---|---|
| `strikeChain` 段数 | 3 段（前两段 + 第三段） | 1 段（删除前两段，只保留第三段） |
| `strikeChain[0].props.bump` | 2 | 10（×5） |
| `strikeChain[0].area.width` | 3 | 15（×5） |
| `strikeChain[0].area.height` | 2 | 10（×5） |
| `strikeChain[0].area.offsetCaseX` | 0.8 | 0.8（不变，保持贴脸判定） |

（保留段即原第三段，其余两段的 `power/coolDown/charge` 等数据一并删除。）

## 实现说明

- 武器只剩一段连击后，`isLastCycle()` 恒为 true：`Weapon.canHit` 的放大判定与上抛放大对**每一次平a**生效。
- 命中后游戏原逻辑照常执行（伤害、眩晕、DOT、以及 `bump(冲量, -0.37)` 踹飞），模组只把上抛分量临时放大，不会重复触发。
- 怪物密度为全局刷怪加成，与武器是否在手无关。
- 若想恢复前两段，把 `patch_bumpboots_cdb.py` 里 `KEEP_STRIKE_INDEX` 改回不精简的逻辑即可（删除 `line["strikeChain"] = [strike]` 一行并重跑）。

## 构建 / 安装

```bat
dotnet build        :: Debug 下自动安装到 coremod/mods/BumpBootsOverhaul（含 res.pak）
```

手动安装：把 `bin\Debug\net10.0\output\BumpBootsOverhaul\` 下的文件（dll + modinfo.json + res.pak）复制到
`<游戏根目录>\coremod\mods\BumpBootsOverhaul\`，启动游戏使用
`<游戏根目录>\coremod\core\host\startup\DeadCellsModding.exe`。
