#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using dc;
using dc.cdb;
using dc.en;
using dc.en.inter.npc;
using dc.h2d;
using dc.hl.types;
using dc.tool;
using dc.ui;
using Hashlink;
using Hashlink.Marshaling;
using Hashlink.Proxy.Objects;
using Hashlink.Virtuals;
using HaxeProxy.Runtime;
using ModCore.Events.Interfaces.Game;
using ModCore.Events.Interfaces.Game.Hero;
using ModCore.Menu;
using ModCore.Mods;
using ModCore.Modules;
using ModCore.Storage;
using ModCore.Utilities;
using Serilog;

namespace OnePerkOnly
{
    /// <summary>
    /// 单一变异（Perk）模式。
    /// 在选项里选定唯一变异后：所有变异选择界面只允许选它，且可在每次遇到变异商人时重复拿（直到槽满）。
    /// 叠加：随卷轴成长类变异经 getRelevantPerkTier 等效放大；固定阈值类（如处决 P_Execute_LowHealth）经
    /// 运行时改写物品定义 props.prct = 基础值 × 持有份数 实现。
    /// </summary>
    public class OnePerkOnlyMain : ModBase, IOnGameExit, IOnGameInit, IModMenu, IOnHeroUpdate
    {
        public static Config<Configs> config { get; } = new Config<Configs>("OnePerkOnly");

        /// <summary>静态日志（Initialize 里从实例 Logger 缓存，写入 coremod\logs）。</summary>
        private static ILogger _log;

        /// <summary>处决类变异（固定百分比阈值，不走卷轴 tier）的 id。</summary>
        private const string EXEC_ID = "P_Execute_LowHealth";
        /// <summary>处决阈值叠加上限（防 100% 秒杀）。</summary>
        private const double EXEC_CAP = 0.9;
        private static double? _execBasePrct;   // 处决基础阈值（首次读取并缓存）
        private static int _execLastN = -1;     // 上次写入对应的持有份数
        private static bool _execModified;      // 是否改写过 def.props.prct

        private static bool ModEnabled
        {
            get => config.Value.enabled;
            set { config.Value.enabled = value; config.Save(); }
        }

        private static string PerkId
        {
            get => config.Value.perkId ?? "";
            set { config.Value.perkId = value ?? ""; config.Save(); }
        }

        /// <summary>叠加开关：持有 N 份同一变异时，把该变异的随卷轴成长效果按 N 倍放大（经等效 tier 实现）。</summary>
        private static bool StackEnabled
        {
            get => config.Value.stack;
            set { config.Value.stack = value; config.Save(); }
        }

        /// <summary>组12 且 droppable=true（变异商人可提供）的变异 id。</summary>
        private static readonly List<string> _perkIds = new List<string>();
        private static bool _listLoaded;

        public OnePerkOnlyMain(ModInfo info) : base(info) { }

        #region Lifecycle

        public override void Initialize()
        {
            base.Initialize();
            _log = Logger;
            Hook_PerkSelect.alreadyKnown += OnPerkSelectAlreadyKnown;
            Hook_PerkSelect.requirementsOk += OnPerkSelectRequirementsOk;
            Hook_PerkSelect.isVisible += OnPerkSelectIsVisible;
            Hook_PerkSelect.addPerk += OnPerkSelectAddPerk;
            Hook_PerkSelect.onChoose += OnPerkSelectOnChoose;
            Hook_PerkMaster.getAvailablePerks += OnPerkMasterGetAvailablePerks;
            Hook_Inventory.add += OnInventoryAdd;
            Hook_Hero.applyItemPickEffect += OnHeroApplyItemPickEffect;
            Hook_Entity.getRelevantPerkTier += OnEntityGetRelevantPerkTier;
            _log.Information($"[OnePerkOnly] 模组已加载. enabled={ModEnabled}, stack={StackEnabled}, perkId='{PerkId}'");
            TryLoadPerkList();
        }

        void IOnGameInit.OnGameInit()
        {
            _listLoaded = false;
            TryLoadPerkList(force: true);
            _log.Information("[OnePerkOnly] 进局：可提供变异(" + _perkIds.Count + "): " + string.Join(", ", _perkIds));
        }

        void IOnGameExit.OnGameExit()
        {
            Hook_PerkSelect.alreadyKnown -= OnPerkSelectAlreadyKnown;
            Hook_PerkSelect.requirementsOk -= OnPerkSelectRequirementsOk;
            Hook_PerkSelect.isVisible -= OnPerkSelectIsVisible;
            Hook_PerkSelect.addPerk -= OnPerkSelectAddPerk;
            Hook_PerkSelect.onChoose -= OnPerkSelectOnChoose;
            Hook_PerkMaster.getAvailablePerks -= OnPerkMasterGetAvailablePerks;
            Hook_Inventory.add -= OnInventoryAdd;
            Hook_Hero.applyItemPickEffect -= OnHeroApplyItemPickEffect;
            Hook_Entity.getRelevantPerkTier -= OnEntityGetRelevantPerkTier;
            RestoreExecuteDef();
            _log.Information("[OnePerkOnly] 游戏退出，模组已卸载");
        }

        /// <summary>每帧同步：处决类变异把 def.props.prct 保持为 基础值×持有份数（拿 N 份 = 处决线 N 倍）。</summary>
        void IOnHeroUpdate.OnHeroUpdate(double dt)
        {
            try { SyncExecuteThreshold(); }
            catch { }
        }

        private static void SyncExecuteThreshold()
        {
            string pid = PerkId;
            bool active = RestrictActive() && StackEnabled
                          && pid != null && pid.Trim().Equals(EXEC_ID, StringComparison.OrdinalIgnoreCase);
            if (!active)
            {
                RestoreExecuteDef();   // 关掉/换变异时还原基础值
                return;
            }

            dc.en.Hero hero = ModCore.Modules.Game.Instance?.HeroInstance;
            if (hero == null || hero.inventory == null) { RestoreExecuteDef(); return; }

            int n = 0;
            try { n = hero.inventory.countItemKind(H(EXEC_ID)); } catch { n = 0; }
            if (n <= 1) { RestoreExecuteDef(); return; }   // 1 份 = 原版基础值

            if (!_execBasePrct.HasValue)
            {
                try
                {
                    dynamic def = Data.Class.item.byId.get(H(EXEC_ID));
                    if (def == null) return;
                    object raw = def.props.prct;
                    if (raw == null) { _execBasePrct = 0.0; return; }
                    _execBasePrct = Convert.ToDouble(raw);
                }
                catch { return; }
            }

            if (n == _execLastN) return;   // 份数没变不用重复写
            double v = _execBasePrct.Value * n;
            if (v > EXEC_CAP) v = EXEC_CAP;
            if (WriteExecutePrct(v))
            {
                _execLastN = n;
                _execModified = true;
                _log.Information($"[OnePerkOnly] 处决叠加: P_Execute_LowHealth x{n} prct {_execBasePrct.Value:F3} -> {v:F3}");
            }
        }

        /// <summary>把处决变异的 props.prct 写为指定值；失败返回 false。</summary>
        private static bool WriteExecutePrct(double v)
        {
            try
            {
                dynamic def = Data.Class.item.byId.get(H(EXEC_ID));
                if (def == null) return false;
                dynamic props = def.props;
                props.prct = v;
                return true;
            }
            catch (Exception ex)
            {
                _log.Warning($"[OnePerkOnly] 写处决 prct 失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>还原处决变异的 props.prct 为基础值（退出/关闭/份数<=1 时）。</summary>
        private static void RestoreExecuteDef()
        {
            if (!_execModified) return;
            if (_execBasePrct.HasValue && WriteExecutePrct(_execBasePrct.Value))
            {
                _log.Information($"[OnePerkOnly] 处决 prct 已还原为 {_execBasePrct.Value:F3}");
            }
            _execModified = false;
            _execLastN = -1;
        }

        #endregion

        #region Options menu

        public string GetName() => "OnePerkOnly";

        public void BuildMenu(dc.ui.Options options)
        {
            try
            {
                OptionsBase ob = (OptionsBase)options;
                ((dc.ui.Text)ob.title).set_text(H("ONE PERK ONLY"));
                ob.createScroller(0.0);

                bool enabled = ModEnabled;
                ob.addToggleWidget(
                    H("Enable single-perk mode"),
                    H("Only the perk chosen below can ever be taken. It can be picked multiple times (fills every mutation slot)."),
                    (HlFunc<bool>)delegate
                    {
                        ModEnabled = !ModEnabled;
                        _log.Information($"[OnePerkOnly] 开关 -> {ModEnabled}");
                        return ModEnabled;
                    },
                    new Ref<bool>(ref enabled),
                    ob.scrollerFlow);

                bool stack = StackEnabled;
                ob.addToggleWidget(
                    H("Stack effects xN (same perk xN)"),
                    H("When you hold N copies of the chosen perk, its tier-scaled effects (bonus = prct + customScaling x (tier-1)) are multiplied by N. Perks with flat/custom effects cannot stack."),
                    (HlFunc<bool>)delegate
                    {
                        StackEnabled = !StackEnabled;
                        _log.Information($"[OnePerkOnly] 叠加开关 -> {StackEnabled}");
                        return StackEnabled;
                    },
                    new Ref<bool>(ref stack),
                    ob.scrollerFlow);

                TryLoadPerkList();
                if (_perkIds.Count == 0)
                {
                    ob.addSimpleWidget(
                        H("Perk list unavailable"),
                        H("Item database not loaded yet in this menu. Open this menu once during a run (pause), or write the perk id directly into coremod\\config\\OnePerkOnly.json (perk ids are printed to the loader console when a run starts)."),
                        (HlAction)delegate { },
                        new Ref<int>(ref _dummyOffset),
                        ob.scrollerFlow);
                }
                else
                {
                    string current = PerkId;
                    int cur = IndexOfId(current);
                    if (cur < 0) cur = 0;

                    ArrayObj texts = new ArrayObj
                    {
                        array = new HashlinkArray(HashlinkMarshal.Module.KnownTypes.Dynamic, 0)
                    };
                    foreach (string id in _perkIds)
                    {
                        texts.push(H(id));
                    }

                    int offset = 0;
                    ob.addListWidget(
                        H("Only perk (mutation)"),
                        H("Cycle to choose the single mutation allowed in runs. Empty = vanilla behaviour."),
                        (HlAction<int>)delegate (int idx)
                        {
                            if (idx >= 0 && idx < _perkIds.Count)
                            {
                                PerkId = _perkIds[idx];
                                _log.Information($"[OnePerkOnly] 已锁定变异: {_perkIds[idx]}");
                            }
                        },
                        cur,
                        _perkIds.Count,
                        texts,
                        new Ref<int>(ref offset),
                        ob.scrollerFlow);
                }

                ob.updateScroller();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[OnePerkOnly] BuildMenu error");
            }
        }

        private static int _dummyOffset;

        #endregion

        #region Perk list (runtime enumeration of item db, group 12 + droppable = offerable mutations)

        private static void TryLoadPerkList(bool force = false)
        {
            if (_listLoaded && !force) return;

            IndexId itemDb = null;
            ArrayDyn all = null;
            try { itemDb = Data.Class.item; }
            catch (Exception ex)
            {
                _log.Warning($"[OnePerkOnly] Data.Class.item 读取失败(稍后重试): {ex.Message}");
                _listLoaded = false;
                return;
            }
            if (itemDb == null) { _log.Warning("[OnePerkOnly] Data.Class.item == null（数据未加载）"); _listLoaded = false; return; }

            try { all = itemDb.all; }
            catch (Exception ex) { _log.Warning($"[OnePerkOnly] item.all 读取失败: {ex.Message}"); _listLoaded = false; return; }
            if (all == null) { _log.Warning("[OnePerkOnly] item.all == null"); _listLoaded = false; return; }

            try
            {
                List<string> found = new List<string>();
                List<string> notDroppable = new List<string>();
                int n = all.get_length();
                bool elemErrLogged = false;
                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        dynamic def = all.getDyn(i);
                        if (def == null) continue;
                        if ((int)def.group == 12)
                        {
                            string id = ((object)def.id)?.ToString();
                            if (string.IsNullOrEmpty(id) || found.Contains(id) || notDroppable.Contains(id)) continue;
                            bool drop = true;
                            try { drop = (bool)def.droppable; } catch { drop = true; }
                            if (drop) found.Add(id); else notDroppable.Add(id);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!elemErrLogged)
                        {
                            elemErrLogged = true;
                            _log.Warning($"[OnePerkOnly] 元素[{i}]读取失败(已忽略同类): {ex.Message}");
                        }
                    }
                }

                found.Sort(StringComparer.Ordinal);
                notDroppable.Sort(StringComparer.Ordinal);
                _perkIds.Clear();
                _perkIds.AddRange(found);
                _listLoaded = true;
                _log.Information($"[OnePerkOnly] 扫描完成：物品库共 {n} 条，组12={found.Count + notDroppable.Count}，可提供={found.Count}");
                if (notDroppable.Count > 0)
                    _log.Information("[OnePerkOnly] 组12但droppable=false: " + string.Join(", ", notDroppable));
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[OnePerkOnly] 枚举失败");
                _listLoaded = false;
            }
        }

        #endregion

        #region Hook implementations

        private static bool RestrictActive()
        {
            if (!ModEnabled) return false;
            TryLoadPerkList();
            return IndexOfId(PerkId) >= 0;
        }

        private static int IndexOfId(string id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            for (int i = 0; i < _perkIds.Count; i++)
            {
                if (string.Equals(_perkIds[i], id.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        private static bool SamePerk(dc.String k)
        {
            if (k == null) return false;
            try { return SamePerkId(((object)k).ToString()); }
            catch { return false; }
        }

        private static bool SamePerkId(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            string p = PerkId;
            return p.Length > 0 && string.Equals(id.Trim(), p.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static int GetOwnedPerks(dc.en.Hero hero)
        {
            try { return hero?.inventory?.countItemGroup(12) ?? 0; }
            catch { return -1; }
        }

        private static bool OnPerkSelectAlreadyKnown(Hook_PerkSelect.orig_alreadyKnown orig, dc.ui.PerkSelect self, dc.String k)
        {
            try
            {
                if (RestrictActive() && SamePerk(k))
                {
                    _log.Information($"[OnePerkOnly] alreadyKnown({k}) -> false（允许重复选锁定变异）");
                    return false;
                }
            }
            catch (Exception ex) { _log.Warning($"[OnePerkOnly] alreadyKnown err: {ex.Message}"); }
            return orig(self, k);
        }

        private static bool OnPerkSelectRequirementsOk(Hook_PerkSelect.orig_requirementsOk orig, dc.ui.PerkSelect self, dc.String k)
        {
            try
            {
                if (RestrictActive())
                {
                    if (SamePerk(k))
                    {
                        int owned = GetOwnedPerks(self?.hero);
                        int max = -1;
                        try { max = self.getMaxPerksHere(); } catch { }
                        bool allow = owned >= 0 && max >= 0 && owned < max;
                        return allow;
                    }
                    return false;
                }
            }
            catch (Exception ex) { _log.Warning($"[OnePerkOnly] requirementsOk err: {ex.Message}"); }
            return orig(self, k);
        }

        private static bool OnPerkSelectIsVisible(Hook_PerkSelect.orig_isVisible orig, dc.ui.PerkSelect self, dc.String k)
        {
            try
            {
                if (RestrictActive()) return SamePerk(k);
            }
            catch (Exception ex) { _log.Warning($"[OnePerkOnly] isVisible err: {ex.Message}"); }
            return orig(self, k);
        }

        /// <summary>面板建行时观察：记录每一行对应的物品 id（null=重置行）。</summary>
        private static Hashlink.Virtuals.virtual_canBeSelected_fb_ii_ OnPerkSelectAddPerk(Hook_PerkSelect.orig_addPerk orig, dc.ui.PerkSelect self, dc.tool.InventItem ii)
        {
            try
            {
                string id = ii == null ? "<reset-row>" : ItemKindId(ii);
                string state = RestrictActive() ? "restrict" : "vanilla";
                _log.Information($"[OnePerkOnly] addPerk row [{state}]: {id}");
            }
            catch (Exception ex) { _log.Warning($"[OnePerkOnly] addPerk hook err: {ex.Message}"); }
            return orig(self, ii);
        }

        private static void OnPerkSelectOnChoose(Hook_PerkSelect.orig_onChoose orig, dc.ui.PerkSelect self)
        {
            try
            {
                int owned = GetOwnedPerks(self?.hero);
                _log.Information($"[OnePerkOnly] >>> onChoose(确认选取) owned={owned} restrict={RestrictActive()}");
            }
            catch (Exception ex) { _log.Warning($"[OnePerkOnly] onChoose hook err: {ex.Message}"); }
            orig(self);
        }

        private static void OnHeroApplyItemPickEffect(Hook_Hero.orig_applyItemPickEffect orig, dc.en.Hero self, dc.Entity from, dc.tool.InventItem i)
        {
            try
            {
                string id = i == null ? "?" : ItemKindId(i);
                _log.Information($"[OnePerkOnly] Hero.applyItemPickEffect id='{id}'");
            }
            catch (Exception ex) { _log.Warning($"[OnePerkOnly] applyItemPickEffect hook err: {ex.Message}"); }
            try
            {
                orig(self, from, i);
                int owned = GetOwnedPerks(self);
                _log.Information($"[OnePerkOnly]   applyItemPickEffect 完成，当前变异数 owned={owned}");
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[OnePerkOnly]   applyItemPickEffect 抛异常!");
                throw;
            }
        }

        /// <summary>
        /// 效果叠加核心：变异强度 = prct + customScaling × (getRelevantPerkTier(id) − 1)。
        /// 当英雄持有 N 份锁定的变异时，返回一个“等效卷轴数”tier'，使该公式结果 = N × 单份强度，
        /// 从而让同一种变异 ×N 的效果精确叠加（仅对这类“随卷轴成长”的效果有效）。
        /// </summary>
        private static int OnEntityGetRelevantPerkTier(Hook_Entity.orig_getRelevantPerkTier orig, dc.Entity self, dc.String k)
        {
            if (!StackEnabled || !(self is dc.en.Hero hero)) return orig(self, k);
            try
            {
                if (!RestrictActive() || !SamePerk(k)) return orig(self, k);

                int baseTier = orig(self, k);
                if (baseTier <= 1) return baseTier;   // tier=1 时加成仅为基础值，多份自然按下面逻辑乘

                int n = hero.inventory.countItemKind(k);   // 持有份数（含第一份）
                if (n <= 1) return baseTier;

                dynamic def = Data.Class.item.byId.get(k);
                if (def == null) return baseTier;

                double prct = 0, cs = 0;
                try { prct = (double)def.props.prct; } catch { }
                try { cs = (double)def.commonProps.customScaling; } catch { }
                if (cs <= 0)   // 无随卷轴成长（固定效果），无法经等效 tier 放大
                {
                    _log.Warning($"[OnePerkOnly] 叠加跳过：变异 {k} 无 customScaling（固定效果，无法经 tier 放大）prct={prct}");
                    return baseTier;
                }

                double bonus = prct + cs * (baseTier - 1);   // 单份强度
                double bonusN = bonus * n;                    // N 份应得强度
                double tierN = 1.0 + (bonusN - prct) / cs;    // 使得 prct + cs*(tierN-1) == bonusN
                int tier2 = (int)System.Math.Round(tierN);
                if (tier2 < baseTier) tier2 = baseTier;
                _log.Information($"[OnePerkOnly] 叠加: {k} x{n} tier {baseTier} -> {tier2} (prct={prct} cs={cs})");
                return tier2;
            }
            catch (Exception ex)
            {
                _log.Warning($"[OnePerkOnly] getRelevantPerkTier hook err: {ex.Message}");
                return orig(self, k);
            }
        }

        private static int OnPerkMasterGetAvailablePerks(Hook_PerkMaster.orig_getAvailablePerks orig, dc.en.inter.npc.PerkMaster self)
        {
            try
            {
                if (RestrictActive())
                {
                    string p = PerkId;
                    if (string.IsNullOrEmpty(p) || IndexOfId(p) < 0)
                    {
                        _log.Information($"[OnePerkOnly] getAvailablePerks: 锁定变异 '{p}' 不可提供 -> 放行(0)");
                        return 0;
                    }
                }
            }
            catch (Exception ex) { _log.Warning($"[OnePerkOnly] getAvailablePerks hook err: {ex.Message}"); }
            return orig(self);
        }

        private static dc.tool.InventItem OnInventoryAdd(Hook_Inventory.orig_add orig, dc.tool.Inventory self, dc.tool.InventItem i)
        {
            try
            {
                if (i != null && RestrictActive())
                {
                    int grp = ItemGroup(i);
                    string id = ItemKindId(i);
                    if (grp == 12)
                    {
                        if (!SamePerkId(id))
                        {
                            _log.Information($"[OnePerkOnly] Inventory.add 拦截非锁定变异: id='{id}'");
                            return i;   // 不加入背包 = 不生效
                        }
                        _log.Information($"[OnePerkOnly] Inventory.add 允许锁定变异: id='{id}'");
                    }
                }
            }
            catch (Exception ex) { _log.Warning($"[OnePerkOnly] inventory.add hook err: {ex.Message}"); }
            return orig(self, i);
        }

        private static int ItemGroup(dc.tool.InventItem i)
        {
            // 优先从物品数据(itemData)读 group —— 扫描阶段已证明动态读取有效
            try
            {
                if (i._itemData != null)
                {
                    dynamic d = i._itemData;
                    return (int)d.group;
                }
            }
            catch { }
            return -1;
        }

        /// <summary>物品真实 id：从 _itemData.id 读取（最可靠）；无数据时退回按 kind 取索引。</summary>
        private static string ItemKindId(dc.tool.InventItem i)
        {
            try
            {
                if (i._itemData != null)
                {
                    dynamic d = i._itemData;
                    object oid = d.id;
                    string sid = oid?.ToString();
                    if (!string.IsNullOrEmpty(sid)) return sid;
                }
            }
            catch { }
            try
            {
                dynamic k = i.kind;
                object idx = k.Index;
                string sid = idx?.ToString();
                if (!string.IsNullOrEmpty(sid)) return sid;
            }
            catch { }
            try { return i.kind?.GetType().Name ?? ""; }
            catch { return "?"; }
        }

        #endregion

        #region helpers

        private static dc.String H(string s)
        {
            return new HashlinkString(s).AsHaxe<dc.String>();
        }

        #endregion
    }

    /// <summary>持久化配置（coremod\config\OnePerkOnly.json）。</summary>
    public class Configs
    {
        public bool enabled = true;
        public bool stack = true;
        public string perkId = "";
    }
}
