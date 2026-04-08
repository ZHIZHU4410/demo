﻿using dc;
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
using dc.pr;
using dc.shader;
using dc.tool;
using dc.tool.atk;
using dc.tool.hero.activeSkills;
using dc.tool.mod.script;
using dc.tool.weap;
using dc.ui;
using HaxeProxy.Runtime;
using HaxeProxy.Runtime.Internals;
using HaxeProxy.Runtime.Internals.Cache;
using ModCore.Events.Interfaces.Game;
using ModCore.Events.Interfaces.Game.Hero;
using ModCore.Mods;
using ModCore.Modules;
using ModCore.Utitities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace SampleSimple
{
    public class SimpleMod : ModBase, IOnHeroUpdate
    {
        private static SimpleMod? _instance;

        private double _damageMultiplier = 1.0;   // 伤害倍率，初始 1.0（原始伤害）

        private bool _isZPressed = false;
        private bool _isXPressed = false;
        private const int VK_Z = 0x5A;
        private const int VK_X = 0x58;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        public SimpleMod(ModInfo info) : base(info)
        {
            _instance = this;
        }

        public override void Initialize()
        {
            base.Initialize();

            // 订阅 Entity.applyAttackResult 钩子
            Hook_Entity.applyAttackResult += OnEntityApplyAttackResult;

            base.Logger.Information("伤害倍率 Mod 已加载，当前倍率: {0:F2}x (按 Z +10%，按 X -10%，上限10倍，下限0倍)", _damageMultiplier);
        }

        private void OnEntityApplyAttackResult(Hook_Entity.orig_applyAttackResult orig, Entity self, AttackData attack)
        {
            // 仅当攻击者是玩家英雄时修改伤害
            if (attack.source is Hero)
            {
                int originalDamage = attack.finalDmg;
                int newDamage = (int)(originalDamage * _damageMultiplier);
                attack.finalDmg = newDamage;

                base.Logger.Information($"⚔️ 伤害修改: {originalDamage} → {newDamage} (倍率 {_damageMultiplier:F2}x)");
            }

            orig(self, attack);
        }

        void IOnHeroUpdate.OnHeroUpdate(double dt)
        {
            // Z 键：伤害 ×1.1，上限 10
            bool zDown = GetAsyncKeyState(VK_Z) < 0;
            if (zDown && !_isZPressed)
            {
                _damageMultiplier = System.Math.Min(10.0, _damageMultiplier + 0.1);
                base.Logger.Information($"🔺 伤害倍率提升至: {_damageMultiplier:F2}x");
            }
            _isZPressed = zDown;

            // X 键：伤害 ×0.9，下限 0
            bool xDown = GetAsyncKeyState(VK_X) < 0;
            if (xDown && !_isXPressed)
            {
                _damageMultiplier = System.Math.Max(0.0, _damageMultiplier - 0.1);
                base.Logger.Information($"🔻 伤害倍率降低至: {_damageMultiplier:F2}x");
            }
            _isXPressed = xDown;
        }
    }
}