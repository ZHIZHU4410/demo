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
    public class SimpleMod : ModBase, IOnGameExit, IOnHeroUpdate, IOnGameEndInit
    {
        // ---------- 颜色转换辅助方法 ----------
        private static int ColorFromHex(string hex)
        {
            if (hex.StartsWith("#")) hex = hex.Substring(1);
            if (hex.Length == 6) hex = "FF" + hex;
            return Convert.ToInt32(hex, 16);
        }

        // ---------- SnakeFang 穿墙传送相关常量 ----------
        private const double GridSize = 24.0;
        private const double HeightThreshold = 288.0;
        private const double HalfFactor = 0.5;
        private const double VerticalOffset = 2.4;
        private const int EffectId = 13;
        private const int EffectDuration = 2;
        private const double DefaultAlpha = 1.0;
        private const double DefaultSec = 1.0;
        private const int OffsetAmount = 5;
        private const double DirectionOffset = 1.2;
        private const double Friction = 0.87;
        private const double SlowMoTimescale = 0.1;
        private const double SlowMoDuration = 0.2;
        private const int DashParam = 2;
        private const double DistanceDivisor = 48.0;
        private const int DistanceSqBonus = 50;
        private const double One = 1.0;
        private const double DefaultVolume = 1.0;
        private const double DefaultPitch = 1.0;
        private const string ColorRed = "#ff0000";
        private const string ColorWhite = "#FFFFFF";
        private const string ColorDarkRed = "#fdfdfd";
        private const string ColorLightPink = "#FFFFFF";

        // 穿墙标志
        private bool isnowall = false;

        // ---------- 关卡深度映射（保留原有） ----------
        private Dictionary<string, int> biomeWorldDepthMap = new Dictionary<string, int>()
        {
            { "PrisonStart", 0 }, { "PrisonCourtyard", 1 }, { "SewerShort", 1 },
            { "PrisonDepths", 1 }, { "PrisonCorrupt", 1 }, { "PrisonRoof", 2 },
            { "Ossuary", 2 }, { "SewerDepths", 2 }, { "Bridge", 3 },
            { "BeholderPit", 3 }, { "StiltVillage", 4 }, { "AncientTemple", 4 },
            { "Cemetery", 4 }, { "ClockTower", 5 }, { "Crypt", 5 },
            { "TopClockTower", 6 }, { "Cavern", 5 }, { "Giant", 6 },
            { "Castle", 7 }, { "Distillery", 7 }, { "Throne", 8 },
            { "Astrolab", 9 }, { "Observatory", 10 },
            { "Greenhouse", 1 }, { "Swamp", 2 }, { "SwampHeart", 3 },
            { "Tumulus", 4 }, { "Cliff", 5 }, { "GardenerStage", 6 },
            { "Shipwreck", 7 }, { "Lighthouse", 8 }, { "QueenArena", 10 },
            { "PurpleGarden", 1 }, { "DookuCastle", 2 }, { "DookuCastleHard", 7 },
            { "DeathArena", 3 }, { "DookuArena", 8 }
        };

        private readonly Random _random = new Random();
        private int _currentLevelIndex = 0;

        // ---------- 按键状态 ----------
        private bool _isNKeyPressed = false;
        private bool _isMKeyPressed = false;
        private bool _isKKeyPressed = false;
        private bool _isTKeyPressed = false;
        private const int VK_N = 0x4E;
        private const int VK_M = 0x4D;
        private const int VK_K = 0x4B;
        private const int VK_T = 0x54;

        [DllImport("user32.dll", CharSet = CharSet.Auto, ExactSpelling = true)]
        private static extern short GetAsyncKeyState(int vkey);

        public SimpleMod(ModInfo info) : base(info) { }

        public override void Initialize()
        {
            base.Initialize();

            // 注册 SnakeFang 钩子
            Hook_SnakeFang.teleportTo += Hook_SnakeFang_teleportTo;
            Hook_TargetHelper.filterBySight += Hook_TargetHelper_filterBySight;
            Hook_SnakeFang.onExecute += Hook_SnakeFang_onExecute;

            System.Console.WriteLine("SimpleMod 初始化完成");
        }

        // ---------- SnakeFang 钩子实现 ----------
        private bool Hook_SnakeFang_onExecute(Hook_SnakeFang.orig_onExecute orig, SnakeFang self)
        {
            isnowall = true;
            return orig(self);
        }

        private void Hook_TargetHelper_filterBySight(Hook_TargetHelper.orig_filterBySight orig, TargetHelper self, Entity otherSource, Ref<bool> ignoreOneWay, int? ignoreSpotType)
        {
            if (isnowall) return; // 穿墙：忽略视线阻挡
            orig(self, otherSource, ignoreOneWay, ignoreSpotType);
        }

        private void Hook_SnakeFang_teleportTo(Hook_SnakeFang.orig_teleportTo orig, SnakeFang self, Entity e)
        {
            Hero hero = self.owner;

            double targetWorldX = ((double)e.cx + e.xr) * GridSize - (double)self.owner.dir * (e.radius + self.owner.radius);
            double targetWorldY;
            if (e.hei > HeightThreshold)
                targetWorldY = ((double)e.cy + e.yr) * GridSize - e.hei * HalfFactor - VerticalOffset;
            else
                targetWorldY = ((double)e.cy + e.yr) * GridSize - VerticalOffset;

            int gridX = (int)(targetWorldX / GridSize);
            int gridY = (int)(targetWorldY / GridSize);
            double offsetX = (targetWorldX - gridX * GridSize) / GridSize;
            double offsetY = (targetWorldY - gridY * GridSize) / GridSize;

            double oldHeadX = hero.get_headX();
            double oldHeadY = hero.get_headY();

            int newDir;
            if (e == null)
                newDir = hero.dir;
            else
            {
                double ownerCenterX = ((double)hero.cx + hero.xr) * GridSize;
                double targetCenterX = ((double)e.cx + e.xr) * GridSize;
                newDir = targetCenterX < ownerCenterX ? -1 : 1;
            }

            var map = hero._level.map;

            bool IsWalkable(int x, int y)
            {
                if (x < 0 || x >= map.wid || y < 0 || y >= map.hei) return false;
                int idx = y * map.wid + x;
                if (idx >= map.collisions.length) return false;
                int coll = map.collisions.getDyn(idx);
                return (coll & 1) == 0;
            }

            if (!IsWalkable(gridX, gridY))
            {
                if (IsWalkable(gridX, gridY - 1))
                {
                    gridY--;
                    offsetX = 0.5 + 0.4 * newDir;
                }
                else if (IsWalkable(gridX - newDir, gridY))
                {
                    gridX -= newDir;
                    offsetX = 0.5 + 0.4 * newDir;
                }
                else
                    return;
            }

            if (IsWalkable(gridX, gridY + 1))
                gridY++;

            hero.safeTpTo(gridX, gridY, offsetX, offsetY, true);
            isnowall = true;

            hero.setAffectS(EffectId, EffectDuration, Ref<double>.Null, null);

            var animId = self.get_curSkillInf().animId;
            int hitFrame = self.get_curSkillInf().hitFrame - 1;
            var tile = hero.spr.lib.getTile(animId, Ref<int>.From(ref hitFrame), Ref<double>.Null, Ref<double>.Null, null).clone();
            tile.dx = (int)-(tile.width * HalfFactor);
            tile.dy = -tile.height;

            var onionSkin = OnionSkin.Class.fromEntity(hero, null, ColorFromHex(ColorWhite), Ref<double>.In(DefaultAlpha), Ref<double>.In(DefaultSec), Ref<bool>.Null, Ref<bool>.Null, Ref<double>.Null);
            double offsetAmount = -hero.dir * OffsetAmount;
            onionSkin.offset(offsetAmount, 0.0);
            onionSkin.dx = hero.dir * DirectionOffset;
            onionSkin.ds = 0.0;
            onionSkin.frict = Friction;

            Boot.Class.ME.slowMo(SlowMoTimescale, SlowMoDuration, null, null);

            if (e != null)
            {
                int distanceSq = GetDistanceSq(self.owner, e);
                self.owner._level.fx.dash(self.owner, -self.owner.dir, ColorFromHex(ColorDarkRed), Ref<int>.In(distanceSq), Ref<double>.In(DashParam));
            }

            double startX = oldHeadX + hero.dir * GridSize;
            double startY = oldHeadY;
            double endX = targetWorldX;
            double endY = targetWorldY;
            self.owner._level.fx.entityTeleport(startX, startY, endX, endY, ColorFromHex(ColorLightPink), Ref<bool>.In(false), Ref<bool>.In(true), Ref<bool>.In(true));

            self.owner._level.lAudio.playEventOn(self.tpSfx, self.owner, DefaultVolume, DefaultPitch, null);

            int GetDistanceSq(Entity a, Entity b)
            {
                double sub = One / DistanceDivisor;
                double dx = (a.cx + a.xr) - (b.cx + b.xr);
                double dy = (a.cy + a.yr - a.hei * sub) - (b.cy + b.yr - b.hei * sub);
                return (int)(dx * dx + dy * dy) + DistanceSqBonus;
            }
        }

        // ---------- 资源加载（移除了 CDBManager 相关代码） ----------
        void IOnGameEndInit.OnGameEndInit()
        {
            var res = Info.ModRoot!.GetFilePath("res.pak");
            if (System.IO.File.Exists(res))
                FsPak.Instance.FileSystem.loadPak(res.AsHaxeString());
        }

        // ---------- 按键功能（保留原有） ----------
        void IOnHeroUpdate.OnHeroUpdate(double dt)
        {
            bool isNPressedNow = GetAsyncKeyState(VK_N) < 0;
            if (isNPressedNow && !_isNKeyPressed)
                TeleportToPrevLevel();
            _isNKeyPressed = isNPressedNow;

            bool isMPressedNow = GetAsyncKeyState(VK_M) < 0;
            if (isMPressedNow && !_isMKeyPressed)
                TeleportToNextLevel();
            _isMKeyPressed = isMPressedNow;

            bool isTPressedNow = GetAsyncKeyState(VK_T) < 0;
            if (isTPressedNow && !_isTKeyPressed)
            {
                Logger.Information("物品生成键被按下，触发Spawn！");
                TriggerSpawnEvent();
            }
            _isTKeyPressed = isTPressedNow;
        }

        private void TeleportToPrevLevel()
        {
            try
            {
                Hero me = ModCore.Modules.Game.Instance.HeroInstance;
                if (me == null) return;
                string currentMapId = me._level.map.id.ToString();
                if (string.IsNullOrEmpty(currentMapId)) return;
                if (!biomeWorldDepthMap.TryGetValue(currentMapId, out int currentWorldDepth)) return;
                int targetWorldDepth = currentWorldDepth - 1;
                if (targetWorldDepth <= 0) return;
                var targetMapKeys = biomeWorldDepthMap.Where(kvp => kvp.Value == targetWorldDepth).Select(kvp => kvp.Key).ToList();
                if (targetMapKeys.Count == 0) return;
                string targetMapKey = targetMapKeys[_random.Next(targetMapKeys.Count)];
                LevelTransition.Class.@goto(targetMapKey.AsHaxeString());
                System.Console.WriteLine($"[传送] 从 {currentMapId} (深度{currentWorldDepth}) 传送至 {targetMapKey} (深度{targetWorldDepth})");
            }
            catch (Exception ex) { System.Console.WriteLine($"[传送-上一级] 错误：{ex.Message}"); }
        }

        private void TeleportToNextLevel()
        {
            try
            {
                Hero me = ModCore.Modules.Game.Instance.HeroInstance;
                if (me == null) return;
                string currentMapId = me._level.map.id.ToString();
                if (string.IsNullOrEmpty(currentMapId)) return;
                if (!biomeWorldDepthMap.TryGetValue(currentMapId, out int currentWorldDepth)) return;
                int targetWorldDepth = currentWorldDepth + 1;
                int maxWorldDepth = biomeWorldDepthMap.Values.Max();
                if (targetWorldDepth > maxWorldDepth) return;
                var targetMapKeys = biomeWorldDepthMap.Where(kvp => kvp.Value == targetWorldDepth).Select(kvp => kvp.Key).ToList();
                if (targetMapKeys.Count == 0) return;
                string targetMapKey = targetMapKeys[_random.Next(targetMapKeys.Count)];
                LevelTransition.Class.@goto(targetMapKey.AsHaxeString());
                System.Console.WriteLine($"[传送] 从 {currentMapId} (深度{currentWorldDepth}) 传送至 {targetMapKey} (深度{targetWorldDepth})");
            }
            catch (Exception ex) { System.Console.WriteLine($"[传送-下一级] 错误：{ex.Message}"); }
        }

        private void TriggerSpawnEvent()
        {
            var hero = ModCore.Modules.Game.Instance.HeroInstance;
            if (hero == null) return;

            var itemPool = new (string id,InventItemKind kind)[]
            {
                ("AllUp",new InventItemKind.Consumable("AllUp".AsHaxeString()))
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

        private void ToggleInvertMovement()
        {
            try
            {
                var options = Main.Class.ME.options;
                if (options == null) return;
                options.invertPlayerMovements = !options.invertPlayerMovements;
                System.Console.WriteLine($"[方向反转] 已切换至：{(options.invertPlayerMovements ? "启用" : "禁用")}");
            }
            catch (Exception ex) { System.Console.WriteLine($"[方向反转] 错误：{ex.Message}"); }
        }

        void IOnGameExit.OnGameExit()
        {
            System.Console.WriteLine("游戏退出，SimpleMod 资源清理");
        }
    }
}