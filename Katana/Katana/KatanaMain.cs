using dc;
using dc.cine;
using dc.en;
using dc.en.active;
using dc.en.bu;
using dc.en.inter;
using dc.en.mob;
using dc.en.mob.boss;
using dc.en.mob.boss.giant;
using dc.en.pet;
using dc.h2d;
using dc.h2d.col;
using dc.h3d.impl;
using dc.h3d.mat;
using dc.h3d.pass;
using dc.haxe.io;
using dc.hl;
using dc.hl.types;
using dc.hxbit.enumSer;
using dc.hxd;
using dc.hxd.fs;
using dc.hxd.res;
using dc.hxd.snd;
using dc.hxsl;
using dc.level;
using dc.light;
using dc.pow;
using dc.shader;
using dc.tool;
using dc.tool.atk;
using dc.tool.hero;
using dc.tool.hero.activeSkills;
using dc.tool.weap;
using dc.ui;
using Hashlink.Proxy.Objects;
using HaxeProxy.Runtime;
using HaxeProxy.Runtime.Internals;
using HaxeProxy.Runtime.Internals.Cache;
using ModCore.Events.Interfaces;
using ModCore.Events.Interfaces.Game;
using ModCore.Events.Interfaces.Game.Hero;
using ModCore.Mods;
using ModCore.Modules;
using ModCore.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Katana
{
    /// <summary>
    /// Katana 居合流：
    ///   1. 删除常规平砍 —— 每次攻击执行都会被改写成居合（普通三段挥砍不再出现/不再造成伤害）
    ///   2. 只能使用居合 —— 走原版 nextIsChargeAtk 冲刺斩逻辑：向前瞬移 + 斩击路径上所有敌人
    ///   3. 不需要蓄力 —— 按下即满蓄力执行（无需长按/读完条）
    ///   4. 距离变长 —— data.cdb 把居合段 props.range 6 -> 14（满蓄力冲刺 ≈ 21 格）
    ///   （data.cdb 同时把前三段平砍 animId 换成 AtkKatanaA、hitFrame 改为 1，
    ///    使按下的表现直接是居合前冲而非小挥砍；见 patch_katana_cdb.py + res.pak）
    ///   另保留旧版功能：居合/攻击期间短暂无敌帧。
    /// </summary>
    public class KatanaMain : ModBase, IOnHeroUpdate, IOnGameExit, IOnAfterLoadingAssets
    {
        public KatanaMain(ModInfo info) : base(info) { }

        // ---------- 居合化 ----------
        // 满蓄力判定: 蓄力比例 = katanaChargeF / 30 / props.threshold(0.5)，>=1 即满
        // 60 => 比例 4.0，原生会按满蓄力取 1.0：满距离(1.5x) + 满伤害
        private const int FULL_CHARGE_F = 60;

        // ---------- 无敌帧相关（旧版保留：攻击期间短暂无敌） ----------
        private double _invincibleTimer = 0.0;
        // 无敌帧持续时间：覆盖 Katana 攻击动画
        private const double INVINCIBLE_DURATION = 0.5;

        public override void Initialize()
        {
            base.Initialize();

            // 居合化：拦截 Katana 每次攻击执行，一律改成“满蓄力居合”
            Hook_Katana.onExecute += OnKatanaExecute;
            // 清理：非居合执行时清掉原版按住攒的蓄力帧（避免出现原版读条蓄力）
            Hook_Katana.fixedUpdate += OnKatanaFixedUpdate;

            // 无敌帧：钩子检测 Katana 武器使用
            Hook_HeroWeaponsManager.onWeaponUse += OnWeaponUseHook;
            // 钩子：阻止无敌帧期间的伤害
            Hook_Entity.applyAttackResult += Hook_Entity_applyAttackResult;
            Hook_Hero.applyAttackResult += Hook_Hero_applyAttackResult;

            global::System.Console.WriteLine("[Katana] 居合流已加载：无平砍 / 一键居合 / 免蓄力 / 距离加长(data.cdb range 6->14)");
        }

        /// <summary>
        /// Katana 每次攻击执行（原版 onExecute：平砍或居合都经过这里）时调用。
        /// 无论当前是平砍段还是蓄力段，都改写为“满蓄力居合”再交给原版执行：
        /// 原生 onExecute 看到 nextIsChargeAtk=true 会走冲刺斩分支（瞬移 + 路径群伤 + 无敌帧动画）。
        /// 居合结束后立即解除原生自带的 0.7s 控制锁 → 无后摇，可马上再次行动。
        /// </summary>
        private bool OnKatanaExecute(Hook_Katana.orig_onExecute orig, dc.tool.weap.Katana self)
        {
            try
            {
                if (self != null && !self.destroyed && self.owner != null && !self.owner.destroyed)
                {
                    // 使用第 4 段（居合冲刺）的技能数据：power 220 / 加长 range / duration 等
                    self.set_cycle(3);
                    // 标记为蓄力居合执行 → 原生走冲刺斩
                    self.nextIsChargeAtk = true;
                    // 满蓄力 → 满距离(1.5x range)、满伤害
                    self.katanaChargeF = FULL_CHARGE_F;
                    // 表现：直接播放居合前冲动作（对齐原版满蓄力自动释放时的动画）
                    TryPlayDashAnim(self);
                }
            }
            catch (Exception ex)
            {
                global::System.Console.WriteLine($"[Katana] onExecute 改写失败: {ex.Message}");
            }
            bool result = orig(self);
            // 删除后摇：解除原生居合自带的 lockControlsS(0.7) 控制锁，结束后立即恢复操控
            try
            {
                if (self != null && !self.destroyed
                    && self.owner != null && !self.owner.destroyed && self.owner.life > 0)
                {
                    self.owner.unlockControls();
                }
            }
            catch
            {
                // 解锁失败不影响居合
            }
            return result;
        }

        /// <summary>
        /// 清理原版“按住蓄力”逻辑：不在居合冲刺中时，把攒下的蓄力帧清零，
        /// 避免出现原版 LoadAtkKatanaA 读条等待（本模组不依赖蓄力）。
        /// </summary>
        private void OnKatanaFixedUpdate(Hook_Katana.orig_fixedUpdate orig, dc.tool.weap.Katana self)
        {
            orig(self);
            try
            {
                if (self == null || self.destroyed) return;
                // 居合冲刺执行中 nextIsChargeAtk 为 true（原生执行完会自行复位），
                // 非执行状态时不允许原版攒蓄力
                if (!self.nextIsChargeAtk && self.katanaChargeF != 0)
                {
                    self.katanaChargeF = 0;
                }
            }
            catch
            {
                // 单帧清理失败忽略，下帧重试
            }
        }

        /// <summary>播放居合前冲动画 AtkKatanaA（对齐原版满蓄力释放的表现）。</summary>
        private static void TryPlayDashAnim(dc.tool.weap.Katana self)
        {
            try
            {
                if (self.owner?.spr == null) return;
                var anim = self.owner.spr.get_anim();
                if (anim == null) return;
                anim.play(ToHaxeString("AtkKatanaA"), null, null);
            }
            catch
            {
                // 动画播放失败不影响居合逻辑
            }
        }

        private static dc.String ToHaxeString(string s)
        {
            return new HashlinkString(s).AsHaxe<dc.String>();
        }

        /// <summary>
        /// 武器使用时触发。检测是否为 Katana（居合命中帧同样触发），激活短暂无敌帧。
        /// </summary>
        private void OnWeaponUseHook(Hook_HeroWeaponsManager.orig_onWeaponUse orig, HeroWeaponsManager self, Weapon w, int slot)
        {
            orig(self, w, slot);

            if (self.hero == null) return;

            // 通过类型判断是否为 Katana 武器
            bool isKatana = w is dc.tool.weap.Katana;
            // 备选：通过物品 ID 判断
            if (!isKatana && w?.item?._itemData?.id != null)
            {
                isKatana = w.item._itemData.id.ToString() == "Katana";
            }

            if (!isKatana) return;

            Hero hero = self.hero;

            // 居合（原平砍已被改写）激活无敌帧
            _invincibleTimer = INVINCIBLE_DURATION;

            double ignore = 0;
            var ignoreRef = new Ref<double>(ref ignore);
            // affectS id 48 = 无敌
            hero.setAffectS(48, INVINCIBLE_DURATION, ignoreRef, null);
        }

        /// <summary>
        /// 实体受到攻击结果时触发。若玩家处于无敌帧中，阻止伤害应用。
        /// </summary>
        private void Hook_Entity_applyAttackResult(Hook_Entity.orig_applyAttackResult orig, Entity self, AttackData attack)
        {
            // 判断受击者是否为玩家英雄
            Hero? targetHero = self as Hero;
            if (targetHero == null && attack?.lastHitTarget is Hero hitHero)
                targetHero = hitHero;

            if (targetHero != null && _invincibleTimer > 0)
            {
                // 无敌帧中，不应用伤害
                return;
            }

            orig(self, attack);
        }

        /// <summary>
        /// 英雄受到攻击结果时触发。若处于无敌帧中，阻止伤害应用。
        /// </summary>
        private void Hook_Hero_applyAttackResult(Hook_Hero.orig_applyAttackResult orig, Hero self, AttackData attack)
        {
            if (self != null && _invincibleTimer > 0)
                return;
            orig(self, attack);
        }

        // ---------- 资源加载 ----------
        /// <summary>
        /// 资源加载完成后：把本模组 res.pak（含 data.cdb_ 补丁：Katana 居合数据）挂载进 FsPak，
        /// 游戏的 CDBManager 会在首次关卡生成/重载资源时合并 data.cdb_ 补丁。
        /// </summary>
        void IOnAfterLoadingAssets.OnAfterLoadingAssets()
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(typeof(KatanaMain).Assembly.Location) ?? "";
                string pakPath = System.IO.Path.Combine(dir, "res.pak");
                if (System.IO.File.Exists(pakPath))
                {
                    FsPak.Instance.FileSystem.loadPak(ToHaxeString(pakPath));
                    global::System.Console.WriteLine($"[Katana] res.pak 已加载: {pakPath}");
                }
                else
                {
                    global::System.Console.WriteLine($"[Katana] 未找到 res.pak: {pakPath}");
                }
            }
            catch (Exception ex)
            {
                global::System.Console.WriteLine($"[Katana] res.pak 加载失败: {ex.Message}");
            }
        }

        void IOnHeroUpdate.OnHeroUpdate(double dt)
        {
            // 无敌帧倒计时
            if (_invincibleTimer > 0)
            {
                _invincibleTimer -= dt;
                if (_invincibleTimer < 0) _invincibleTimer = 0;

                // 免疫眩晕：清除 stun affect（ID 8）
                Hero? hero = ModCore.Modules.Game.Instance.HeroInstance;
                if (hero != null && hero.life > 0)
                {
                    hero.removeAllAffects(8);
                }
            }
        }

        void IOnGameExit.OnGameExit()
        {
            Hook_Katana.onExecute -= OnKatanaExecute;
            Hook_Katana.fixedUpdate -= OnKatanaFixedUpdate;
            Hook_HeroWeaponsManager.onWeaponUse -= OnWeaponUseHook;
            Hook_Entity.applyAttackResult -= Hook_Entity_applyAttackResult;
            Hook_Hero.applyAttackResult -= Hook_Hero_applyAttackResult;
            global::System.Console.WriteLine("[Katana] 游戏退出，资源清理完成");
        }
    }
}
