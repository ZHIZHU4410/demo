using dc;
using dc.en;
using dc.pow;
using dc.tool;
using dc.tool.weap;
using Hashlink.Proxy.Objects;
using HaxeProxy.Runtime;
using ModCore.Events.Interfaces;
using ModCore.Events.Interfaces.Game;
using ModCore.Mods;
using ModCore.Modules;
using System;
using System.IO;

namespace BumpBootsOverhaul
{
    /// <summary>
    /// BumpBoots（斯巴达凉鞋）强化模组：
    ///   0. 删除第一、二段平a —— data.cdb 数据补丁：strikeChain 只保留第三段
    ///      （每次平a都是第三段大踹飞；武器只有一段连击，isLastCycle 恒为 true）
    ///   1. 攻击范围变大五倍 —— data.cdb 数据补丁：该段判定框 area.width 3→15、area.height 2→10
    ///      （见 patch_bumpboots_cdb.py，随 res.pak 启动时合并）
    ///   2. 无视墙体 —— Hook Weapon.canHit：命中判定跳过墙/地形阻挡检查，
    ///      只要目标落在英雄周围 5 倍范围圈（半径约 19 格）内即可命中（含墙后、身后、上下层）
    ///   3. 踹的距离更远 —— data.cdb 该段 bump 2→10（水平冲量 ×5）
    ///      + 运行时把击飞的上抛(竖直)分量放大 3 倍，怪物呈抛物线远远踹飞
    ///   4. 怪物密度增大到 400% —— Hook User.br_getExtraMobDensity 返回 4.0（刷怪密度 ×4，
    ///      与刷怪器 MobsGen 叠加，常规关卡与 Boss Rush 通用）
    ///
    /// 说明：连击段与判定框放大在数据层（这样游戏的攻击框/动画表现与判定一致）；
    /// 代码层只补“无视墙体”的命中判定与上抛高度，二者叠加即实现 5 倍大范围穿墙踹飞。
    /// </summary>
    public class BumpBootsOverhaulMain : ModBase, IOnGameExit, IOnAfterLoadingAssets
    {
        // ===== 可调参数 =====

        /// <summary>怪物密度倍率（400% = 4.0）</summary>
        private const double MOB_DENSITY = 4.0;

        /// <summary>第三段击飞上抛(竖直)分量倍率 → 踹得更高、飞得更远（水平冲量已在 data.cdb 中 ×5）</summary>
        private const double KICK_DY_MULT = 3.0;

        /// <summary>
        /// 无视墙体的兜底命中半径（格）。
        /// 原版第三段判定：宽3格、高2格、offsetCaseX 0.8 → 最远触及英雄前方 0.8+3 = 3.8 格；
        /// 5 倍范围 = 3.8 × 5 ≈ 19 格。此半径内的敌人无视墙体/地形一律判定可命中。
        /// </summary>
        private const double WALL_IGNORE_REACH_TILES = 19.0;

        /// <summary>怪物密度开关（true = 400%）</summary>
        private bool _mobDensityEnabled = true;

        /// <summary>第三段正待被踹飞的目标（原生稳定 id，用于放大该目标的上抛分量）</summary>
        private int _kickTargetUid = int.MinValue;

        public BumpBootsOverhaulMain(ModInfo info) : base(info) { }

        public override void Initialize()
        {
            base.Initialize();
            Hook_Weapon.canHit += OnWeaponCanHit;
            Hook_Entity.bump += OnEntityBump;
            Hook_User.br_getExtraMobDensity += OnBrGetExtraMobDensity;
            System.Console.WriteLine("[BumpBootsOverhaul] 已加载: 第三下平a 范围x5(数据) + 无视墙体 + 踹飞更远 + 怪物密度400%");
        }

        void IOnGameExit.OnGameExit()
        {
            Hook_Weapon.canHit -= OnWeaponCanHit;
            Hook_Entity.bump -= OnEntityBump;
            Hook_User.br_getExtraMobDensity -= OnBrGetExtraMobDensity;
            System.Console.WriteLine("[BumpBootsOverhaul] 已卸载");
        }

        /// <summary>
        /// 资源加载完成后：把本模组 res.pak（含 data.cdb 补丁：BumpBoots 第三段 area/bump ×5）
        /// 挂载进 FsPak，游戏的 CDBManager 会在关卡生成/资源重载时合并 data.cdb_ 补丁。
        /// </summary>
        void IOnAfterLoadingAssets.OnAfterLoadingAssets()
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(typeof(BumpBootsOverhaulMain).Assembly.Location) ?? "";
                string pakPath = System.IO.Path.Combine(dir, "res.pak");
                if (System.IO.File.Exists(pakPath))
                {
                    FsPak.Instance.FileSystem.loadPak(ToHaxeString(pakPath));
                    System.Console.WriteLine($"[BumpBootsOverhaul] res.pak 已加载: {pakPath}");
                }
                else
                {
                    System.Console.WriteLine($"[BumpBootsOverhaul] 未找到 res.pak: {pakPath}");
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[BumpBootsOverhaul] res.pak 加载失败: {ex.Message}");
            }
        }

        private static dc.String ToHaxeString(string s)
        {
            return new HashlinkString(s).AsHaxe<dc.String>();
        }

        // ---------- 范围x5 + 无视墙体：只影响 BumpBoots 第三段（isLastCycle） ----------
        private bool OnWeaponCanHit(Hook_Weapon.orig_canHit orig, Weapon self, Entity e, Area area)
        {
            // 非 BumpBoots：保持原版判定
            if (!(self is BumpBoots bb))
            {
                return orig(self, e, area);
            }

            // 只放大第三段；前两段保持原版手感
            bool isLast = false;
            try { isLast = bb.isLastCycle(); }
            catch { isLast = false; }
            if (!isLast)
            {
                return orig(self, e, area);
            }

            try
            {
                // 1) 先走原版判定（data.cdb 里第三段的 area 已放大成 15×10，命中框本身已是 5 倍大）
                if (orig(self, e, area))
                {
                    MarkKickTarget(e);
                    return true;
                }

                // 2) 无视墙体兜底：只要在 5 倍范围圈内，一律判定命中（墙后 / 身后 / 上下层都能被踹）
                if (InFiveXRange(bb, e))
                {
                    MarkKickTarget(e);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[BumpBootsOverhaul] canHit err: {ex.Message}");
                try { return orig(self, e, area); }
                catch { return false; }
            }
        }

        /// <summary>目标是否落在英雄周围 5 倍范围圈内（无视墙体）。距离按格子换算为像素(1格=24px)。</summary>
        private static bool InFiveXRange(BumpBoots bb, Entity e)
        {
            Hero h = bb.owner;
            if (h == null || h.destroyed || h.life <= 0) return false;
            if (e == null || e.destroyed || e.life <= 0) return false;
            try { if (!e.canBeHit()) return false; }
            catch { return false; }

            double hx = ((double)h.cx + h.xr) * 24.0;
            double hy = ((double)h.cy + h.yr) * 24.0;
            double ex = ((double)e.cx + e.xr) * 24.0;
            double ey = ((double)e.cy + e.yr) * 24.0;
            double dx = ex - hx;
            double dy = ey - hy;
            double reachPx = WALL_IGNORE_REACH_TILES * 24.0;
            double r = reachPx + e.radius;
            return (dx * dx + dy * dy) <= (r * r);
        }

        private void MarkKickTarget(Entity e)
        {
            try { _kickTargetUid = e.__uid; }
            catch { _kickTargetUid = int.MinValue; }
        }

        // ---------- 踹得更远：第三段击飞的上抛分量 ×KICK_DY_MULT ----------
        private void OnEntityBump(Hook_Entity.orig_bump orig, Entity self, double dx, double dy, bool? ignoreResist)
        {
            // 只放大“向上抛”（dy<0，屏幕坐标系上为负），避免误伤其它水平击退
            if (_kickTargetUid != int.MinValue && dy < 0.0)
            {
                int uid = int.MinValue;
                try { uid = self.__uid; }
                catch { uid = int.MinValue; }
                if (uid == _kickTargetUid)
                {
                    // 一次性：只放大这一次上抛
                    _kickTargetUid = int.MinValue;
                    try
                    {
                        orig(self, dx, dy * KICK_DY_MULT, ignoreResist);
                        return;
                    }
                    catch { /* 放大失败时回落到默认逻辑 */ }
                }
            }
            orig(self, dx, dy, ignoreResist);
        }

        // ---------- 怪物密度 400% ----------
        private double OnBrGetExtraMobDensity(Hook_User.orig_br_getExtraMobDensity orig, dc.User self)
        {
            try
            {
                if (_mobDensityEnabled)
                {
                    return MOB_DENSITY;   // 400%
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[BumpBootsOverhaul] density hook err: {ex.Message}");
            }
            return orig(self);
        }
    }
}
