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
    public class SimpleMod : ModBase, IOnGameExit, IOnHeroUpdate
    {
        // ---------- 关卡深度映射 ----------
        private Dictionary<string, int> biomeWorldDepthMap = new Dictionary<string, int>()
        {
            // 第一组 biome 数据
            { "PrisonStart", 0 },
            { "PrisonCourtyard", 1 },
            { "SewerShort", 1 },
            { "PrisonDepths", 1 },
            { "PrisonCorrupt", 1 },
            { "PrisonRoof", 2 },
            { "Ossuary", 2 },
            { "SewerDepths", 2 },
            { "Bridge", 3 },
            { "BeholderPit", 3 },
            { "StiltVillage", 4 },
            { "AncientTemple", 4 },
            { "Cemetery", 4 },
            { "ClockTower", 5 },
            { "Crypt", 5 },
            { "TopClockTower", 6 },
            { "Cavern", 5 },
            { "Giant", 6 },
            { "Castle", 7 },
            { "Distillery", 7 },
            { "Throne", 8 },
            { "Astrolab", 9 },
            { "Observatory", 10 },
    
            // 第二组 biome 数据
            { "Greenhouse", 1 },
            { "Swamp", 2 },
            { "SwampHeart", 3 },
            { "Tumulus", 4 },
            { "Cliff", 5 },
            { "GardenerStage", 6 },
            { "Shipwreck", 7 },
            { "Lighthouse", 8 },
            { "QueenArena", 10 },
            { "PurpleGarden", 1 },
            { "DookuCastle", 2 },
            { "DookuCastleHard", 7 },
            { "DeathArena", 3 },
            { "DookuArena", 8 }
        };

        // ---------- 随机数生成器 ----------
        private readonly Random _random = new Random();
        private int _currentLevelIndex = 0;

        // ---------- 按键状态 ----------
        private bool _isNKeyPressed = false;
        private bool _isMKeyPressed = false;
        private bool _isTKeyPressed = false; // 物品生成键防抖
        private bool _isKKeyPressed = false;  // 新增：K键用于切换方向反转
        private const int VK_N = 0x4E; // N 键
        private const int VK_M = 0x4D; // M 键
        private const int VK_K = 0x4B; // K 键
        private const int VK_T = 0x54;    // T 键（用于生成物品）

        // ---------- P/Invoke ----------
        [DllImport("user32.dll", CharSet = CharSet.Auto, ExactSpelling = true)]
        private static extern short GetAsyncKeyState(int vkey);

        // ---------- 构造函数 ----------
        public SimpleMod(ModInfo info) : base(info)
        {
        }

        // ---------- 初始化 ----------
        public override void Initialize()
        {
            base.Initialize();
            System.Console.WriteLine("✅ SimpleMod 初始化完成，按 N 传送上一关，按 M 传送下一关，按 K 切换方向反转");
        }

        // ---------- 实现 IOnHeroUpdate ----------
        void IOnHeroUpdate.OnHeroUpdate(double dt)
        {
            // 检测 T 键（生成物品）
            bool isTPressedNow = GetAsyncKeyState(VK_T) < 0;
            if (isTPressedNow && !_isTKeyPressed)
            {
                Logger.Information("物品生成键被按下，触发Spawn！");
                TriggerSpawnEvent();
            }
            _isTKeyPressed = isTPressedNow;
            // 检测 N 键（上一关）
            bool isNPressedNow = GetAsyncKeyState(VK_N) < 0;
            if (isNPressedNow && !_isNKeyPressed)
            {
                TeleportToPrevLevel();
            }
            _isNKeyPressed = isNPressedNow;

            // 检测 M 键（下一关）
            bool isMPressedNow = GetAsyncKeyState(VK_M) < 0;
            if (isMPressedNow && !_isMKeyPressed)
            {
                TeleportToNextLevel();
            }
            _isMKeyPressed = isMPressedNow;

        }

        private void TriggerSpawnEvent()
        {
            var hero = ModCore.Modules.Game.Instance.HeroInstance;
            if (hero == null) return;

            var itemPool = new (string id, InventItemKind kind)[]
            {
                ("AnyUp",        new InventItemKind.Consumable("AnyUp".AsHaxeString())),
                ("AllUp",        new InventItemKind.Consumable("AllUp".AsHaxeString()))
            };

            var random = new Random();
            var selected = itemPool[random.Next(itemPool.Length)];

            InventItem testItem = new InventItem(selected.kind);
            bool test_boolean = false;

            ItemDrop itemDrop = new ItemDrop(hero._level, hero.cx, hero.cy, testItem, true, new HaxeProxy.Runtime.Ref<bool>(ref test_boolean));
            itemDrop.init();
            itemDrop.onDropAsLoot();
            itemDrop.dx = hero.dx;
        }

        // ---------- 传送至上一关（深度 -1） ----------
        private void TeleportToPrevLevel()
        {
            try
            {
                dc.en.Hero me = ModCore.Modules.Game.Instance.HeroInstance;
                if (me == null) return;

                string currentMapId = me._level.map.id.ToString();
                if (string.IsNullOrEmpty(currentMapId))
                {
                    System.Console.WriteLine("[传送] 无法获取当前地图ID");
                    return;
                }

                if (!biomeWorldDepthMap.TryGetValue(currentMapId, out int currentWorldDepth))
                {
                    System.Console.WriteLine($"[传送] 当前地图ID {currentMapId} 不在配置列表中");
                    return;
                }

                int targetWorldDepth = currentWorldDepth - 1;
                if (targetWorldDepth <= 0)
                {
                    System.Console.WriteLine("[传送] 已在最浅深度（0），无法向前传送");
                    return;
                }

                List<string> targetMapKeys = biomeWorldDepthMap
                    .Where(kvp => kvp.Value == targetWorldDepth)
                    .Select(kvp => kvp.Key)
                    .ToList();

                if (targetMapKeys.Count == 0)
                {
                    System.Console.WriteLine($"[传送] 未找到深度为 {targetWorldDepth} 的地图");
                    return;
                }

                string targetMapKey = targetMapKeys[_random.Next(targetMapKeys.Count)];
                dc.cine.LevelTransition.Class.@goto(targetMapKey.AsHaxeString());
                System.Console.WriteLine($"[传送] 成功从 {currentMapId} (深度{currentWorldDepth}) 传送至 {targetMapKey} (深度{targetWorldDepth})");

                _currentLevelIndex = targetWorldDepth;
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[传送-上一级] 发生错误：{ex.Message}");
            }
        }

        // ---------- 传送至下一关（深度 +1） ----------
        private void TeleportToNextLevel()
        {
            try
            {
                dc.en.Hero me = ModCore.Modules.Game.Instance.HeroInstance;
                if (me == null) return;

                string currentMapId = me._level.map.id.ToString();
                if (string.IsNullOrEmpty(currentMapId))
                {
                    System.Console.WriteLine("[传送] 无法获取当前地图ID");
                    return;
                }

                if (!biomeWorldDepthMap.TryGetValue(currentMapId, out int currentWorldDepth))
                {
                    System.Console.WriteLine($"[传送] 当前地图ID {currentMapId} 不在配置列表中");
                    return;
                }

                int targetWorldDepth = currentWorldDepth + 1;
                int maxWorldDepth = biomeWorldDepthMap.Values.Max();
                if (targetWorldDepth > maxWorldDepth)
                {
                    System.Console.WriteLine($"[传送] 已在最深深度（{maxWorldDepth}），无法向后传送");
                    return;
                }

                List<string> targetMapKeys = biomeWorldDepthMap
                    .Where(kvp => kvp.Value == targetWorldDepth)
                    .Select(kvp => kvp.Key)
                    .ToList();

                if (targetMapKeys.Count == 0)
                {
                    System.Console.WriteLine($"[传送] 未找到深度为 {targetWorldDepth} 的地图");
                    return;
                }

                string targetMapKey = targetMapKeys[_random.Next(targetMapKeys.Count)];
                dc.cine.LevelTransition.Class.@goto(targetMapKey.AsHaxeString());
                System.Console.WriteLine($"[传送] 成功从 {currentMapId} (深度{currentWorldDepth}) 传送至 {targetMapKey} (深度{targetWorldDepth})");

                _currentLevelIndex = targetWorldDepth;
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[传送-下一级] 发生错误：{ex.Message}");
            }
        }

        // ---------- 切换方向反转设置 ----------
        private void ToggleInvertMovement()
        {
            try
            {
                var options = Main.Class.ME.options;
                if (options == null)
                {
                    System.Console.WriteLine("[方向反转] 无法获取游戏选项");
                    return;
                }

                // 切换 invertPlayerMovements 属性
                bool current = options.invertPlayerMovements;
                options.invertPlayerMovements = !current;

                System.Console.WriteLine($"[方向反转] 已切换至：{(options.invertPlayerMovements ? "启用" : "禁用")}");
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[方向反转] 发生错误：{ex.Message}");
            }
        }

        // ---------- 游戏退出时清理 ----------
        void IOnGameExit.OnGameExit()
        {
            System.Console.WriteLine("游戏退出，SimpleMod 资源清理");
        }
    }
}