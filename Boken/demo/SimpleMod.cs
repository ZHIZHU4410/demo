﻿using dc;
using dc.en;
using dc.en.active;
using dc.en.dookuInteractions;
using dc.en.hero;
using dc.en.inter;
using dc.en.mob;
using dc.hl;
using dc.hl.types;
using dc.level;
using dc.libs.heaps.slib;
using dc.pow;
using dc.pr;
using dc.pr.infection;
using dc.tool;
using dc.tool.atk;
using dc.tool.hero.activeSkills;
using dc.tool.mainSkills;
using dc.tool.mod.script;
using dc.tool.weap;
using dc.ui.sel;
using dc._Data;
using Hashlink;
using Hashlink.Proxy;
using Hashlink.Proxy.Clousre;
using Hashlink.Proxy.DynamicAccess;
using Hashlink.Proxy.Objects;
using Hashlink.Proxy.Values;
using Hashlink.Virtuals;
using HaxeProxy.Runtime;
using ModCore.Events.Interfaces.Game;
using ModCore.Events.Interfaces.Game.Hero;
using ModCore.Mods;
using ModCore.Modules;
using ModCore.Utitities;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

using SystemIOFile = System.IO.File;
using SystemIOPath = System.IO.Path;
using SystemMath = System.Math;

namespace SampleSimple
{
    public class SimpleMod : ModBase, IOnHeroUpdate
    {
        public SimpleMod(ModInfo info) : base(info) { }
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vkey);
        private bool _isTKeyPressed = false;
        private bool _isPreparing = false;
        private double _prepareTimer = 0.0;
        private const double PREPARE_DURATION = 1.0;          // 虚化准备期 1 秒
        private double _invincibleTimer = 0.0;
        private const double INVINCIBLE_DURATION = 10.0;      // 无敌持续 10 秒
        private double _cdTimer = 0.0;
        private const double CD_DURATION = 1.0;               // 冷却 1 秒（可按需修改）
        private double _damageBuffTimer = 0.0;
        private double _damageMultiplier = 1.0;
        private const double DAMAGE_BUFF_DURATION = 10.0;     // 增伤持续 10 秒
        private const double DAMAGE_MULTIPLIER_VALUE = 10.0;
        private readonly Random _random = new Random();
        private double _trailAccumulator = 0.0;
        private const double TRAIL_INTERVAL = 0.07;
        private const double TRAIL_ALPHA = 0.5;
        private const double TRAIL_DURATION = 0.3;

        // 巨人口哨连发控制
        private double _whistleTimer = 0.0;
        private const double WHISTLE_INTERVAL = 0.4;           // 每 1 秒触发一次（每次释放十个口哨）

        public override void Initialize()
        {
            base.Logger.Information("虚化 Mod 已加载");
            Hook_Entity.applyAttackResult += Hook_Entity_applyAttackResult;
            Hook_Hero.applyAttackResult += Hook_Hero_applyAttackResult;
        }

        private void Hook_Entity_applyAttackResult(Hook_Entity.orig_applyAttackResult orig, Entity self, AttackData attack)
        {
            // 增伤效果
            if (attack?.source is Hero heroSource && _damageMultiplier > 1.0)
            {
                int originalDmg = attack.finalDmg;
                attack.finalDmg = (int)(originalDmg * _damageMultiplier);
            }

            // 判断受击者是否为玩家英雄
            Hero targetHero = self as Hero;
            if (targetHero == null && attack?.lastHitTarget is Hero hitHero)
                targetHero = hitHero;

            if (targetHero != null)
            {
                // 正在虚化准备中
                if (_isPreparing)
                {
                    double elapsed = PREPARE_DURATION - _prepareTimer;
                    // 完美虚化判定：受击时刻在 1 秒内
                    bool isPerfect = elapsed <= PREPARE_DURATION;
                    if (isPerfect)
                    {
                        // 重置虚化 CD
                        _cdTimer = 0.0;

                        // 激活各种 buff（持续 10 秒）
                        StartInvincible(targetHero);
                        ActivateDamageBuff(targetHero);
                        StartAngelHalo(targetHero);
                        StartSpeedBoost(targetHero);

                        base.Logger.Information($"完美虚化！受击时刻 {elapsed:F2}s，增伤/天使/加速/十倍口哨连发已激活（持续10秒），虚化CD已重置");
                        _isPreparing = false;
                        _prepareTimer = 0.0;
                        return;
                    }
                    else
                    {
                        _isPreparing = false;
                        _prepareTimer = 0.0;
                        base.Logger.Information($"虚化失败（受击时刻 {elapsed:F2}s），CD 继续");
                    }
                }

                // 无敌时不应用伤害
                if (_invincibleTimer > 0)
                {
                    return;
                }
            }

            orig(self, attack);
        }

        private void StartAngelHalo(Hero hero)
        {
            if (hero == null) return;
            double duration = 10.0;
            var durationRef = new Ref<double>(ref duration);
            hero.setAffectS(79, duration, durationRef, null);
            base.Logger.Information("天使头环已激活 (10秒)");
        }

        private void StartSpeedBoost(Hero hero)
        {
            if (hero == null) return;
            double duration = 10.0;
            var durationRef = new Ref<double>(ref duration);
            hero.setAffectS(69, duration, durationRef, null);
            base.Logger.Information("加速 buff 已激活 (10秒)");
        }

        private void Hook_Hero_applyAttackResult(Hook_Hero.orig_applyAttackResult orig, Hero self, AttackData attack)
        {
            if (self != null && _invincibleTimer > 0)
                return;
            orig(self, attack);
        }

        private void StartInvincible(Hero hero)
        {
            if (hero == null) return;
            _invincibleTimer = INVINCIBLE_DURATION;
            double ignore = 0;
            var ignoreRef = new Ref<double>(ref ignore);
            hero.setAffectS(48, INVINCIBLE_DURATION, ignoreRef, null);
            _trailAccumulator = 0;
            _whistleTimer = 0.0;
        }

        private void ActivateDamageBuff(Hero hero)
        {
            _damageMultiplier = DAMAGE_MULTIPLIER_VALUE;
            _damageBuffTimer = DAMAGE_BUFF_DURATION;
        }

        private void PreparePhantom(Hero hero)
        {
            if (hero == null) return;
            if (_cdTimer > 0)
            {
                base.Logger.Information($"虚化冷却中，剩余 {_cdTimer:F1}s");
                return;
            }
            if (_isPreparing) return;

            _isPreparing = true;
            _prepareTimer = PREPARE_DURATION;
            _cdTimer = CD_DURATION;
            base.Logger.Information($"虚化准备，持续{PREPARE_DURATION}秒，CD 开始计时");
        }

        private void CreateRandomTrails(Hero hero)
        {
            if (hero == null) return;
            int trailCount = _random.Next(2, 5);
            for (int i = 0; i < trailCount; i++)
            {
                int randomColor = (255 << 24) | (_random.Next(256) << 16) | (_random.Next(256) << 8) | _random.Next(256);
                double offsetX = (_random.NextDouble() * 120.0) - 60.0;
                double offsetY = (_random.NextDouble() * 40.0) - 30.0;

                double alpha = TRAIL_ALPHA;
                double duration = TRAIL_DURATION;

#pragma warning disable CS0612
                var trail = OnionSkin.Class.fromEntity(
                    hero,
                    null,
                    randomColor,
                    Ref<double>.In(alpha),
                    Ref<double>.In(duration),
                    Ref<bool>.Null,
                    Ref<bool>.Null,
                    Ref<double>.Null
                );
#pragma warning restore CS0612

                if (trail != null)
                    trail.offset(offsetX, offsetY);
            }
        }

        /// <summary>
        /// 每次调用释放十个巨人口哨（十倍效果）
        /// </summary>
        private void TryUseGiantWhistleTenTimes(Hero hero)
        {
            if (hero == null) return;
            try
            {
                // 从玩家已装备的主动技能中找到巨人口哨物品
                InventItem whistleItem = null;
                var inventory = hero.inventory;
                for (int i = 0; i < 2; i++)
                {
                    var item = inventory.getActiveOn(i);
                    if (item != null && item._itemData != null)
                    {
                        var id = item._itemData.id;
                        if (id.ToString() == "GiantWhistle")
                        {
                            whistleItem = item;
                            break;
                        }
                    }
                }

                if (whistleItem == null)
                {
                    base.Logger.Warning("未装备巨人口哨，无法释放");
                    return;
                }

                // 释放十个口哨
                for (int i = 0; i < 10; i++)
                {
                    GiantWhistle whistle = new GiantWhistle(hero, whistleItem);
                    dc.en.Mob target = whistle.lockTarget();
                    if (target != null)
                    {
                        whistle.shoryuken(target);
                    }
                    if (!whistle.destroyed)
                        whistle.destroy();
                }
            }
            catch (Exception ex)
            {
                base.Logger.Error($"强制释放口哨失败: {ex.Message}");
            }
        }

        void IOnHeroUpdate.OnHeroUpdate(double dt)
        {
            Hero hero = ModCore.Modules.Game.Instance.HeroInstance;
            if (hero == null) return;

            bool isTPressed = GetAsyncKeyState(84) < 0;
            if (isTPressed && !_isTKeyPressed)
            {
                PreparePhantom(hero);
            }
            _isTKeyPressed = isTPressed;

            if (_invincibleTimer > 0)
            {
                _invincibleTimer -= dt;
                if (_invincibleTimer < 0) _invincibleTimer = 0;
            }

            if (_prepareTimer > 0)
            {
                _prepareTimer -= dt;
                if (_prepareTimer <= 0 && _isPreparing)
                {
                    _isPreparing = false;
                    base.Logger.Information($"虚化准备超时，进入{CD_DURATION}秒CD（保持中）");
                }
            }

            if (_cdTimer > 0)
            {
                _cdTimer -= dt;
                if (_cdTimer < 0) _cdTimer = 0;
            }

            if (_damageBuffTimer > 0)
            {
                _damageBuffTimer -= dt;
                if (_damageBuffTimer <= 0)
                {
                    _damageMultiplier = 1.0;
                    base.Logger.Information("增伤buff结束");
                }
            }

            // 无敌期间每 0.2 秒释放十个巨人口哨
            if (_invincibleTimer > 0)
            {
                _whistleTimer += dt;
                while (_whistleTimer >= WHISTLE_INTERVAL)
                {
                    TryUseGiantWhistleTenTimes(hero);
                    _whistleTimer -= WHISTLE_INTERVAL;
                }
            }
            else
            {
                _whistleTimer = 0.0;
            }

            if (_invincibleTimer > 0)
            {
                _trailAccumulator += dt;
                if (_trailAccumulator >= TRAIL_INTERVAL)
                {
                    _trailAccumulator -= TRAIL_INTERVAL;
                    CreateRandomTrails(hero);
                }
            }
            else
            {
                _trailAccumulator = 0;
            }
        }
    }
}