using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Library;
using Library.SystemModels;
using Server.DBModels;
using Server.Envir;
using Zircon.Server.Models;

namespace Server.Envir
{
    /// <summary>
    /// 暗黑风格装备词条系统：
    /// - 怪物掉落装备时按权重概率 roll 词条（RollAffixes）
    /// - 词条配置删除/更新时级联重建受影响装备的词条属性行（DeleteAffix / RefreshAffixInstances）
    /// - 条件词条在玩家 RefreshStats 时运行时评估（ApplyConditionalAffixStats）
    /// </summary>
    public static class AffixSystem
    {
        #region 基础定义

        /// <summary>允许附加词条的装备类型</summary>
        private static readonly HashSet<ItemType> EquipmentTypes = new HashSet<ItemType>
        {
            ItemType.Weapon,
            ItemType.Armour,
            ItemType.Helmet,
            ItemType.Necklace,
            ItemType.Bracelet,
            ItemType.Ring,
            ItemType.Shoes,
            ItemType.Shield,
            ItemType.Torch,
            ItemType.Fabao,
        };

        public static bool IsEquipmentType(ItemType type)
        {
            return EquipmentTypes.Contains(type);
        }

        public class AffixParamDef
        {
            public int Min { get; set; }
            public int Max { get; set; }
            /// <summary>true=按百分比生效（运行时按角色基础属性比例加成），false=按数值生效（直接加固定值）；旧配置默认百分比</summary>
            public bool IsPercent { get; set; } = true;
        }

        public class AffixEffectDef
        {
            public string Stat { get; set; }
            /// <summary>true = 数值引用占位符{ParamIndex}；false = 使用固定值 FixedAmount</summary>
            public bool UseParam { get; set; }
            public int FixedAmount { get; set; }
            public int ParamIndex { get; set; }
        }

        #endregion

        #region JSON 解析

        public static List<AffixParamDef> ParseParams(string json)
        {
            var result = new List<AffixParamDef>();
            if (string.IsNullOrWhiteSpace(json)) return result;

            try
            {
                var parsed = JsonSerializer.Deserialize<List<AffixParamDef>>(json);
                if (parsed != null) result.AddRange(parsed);
            }
            catch (Exception)
            {
                // 配置损坏时按无参数处理
            }

            return result;
        }

        public static List<AffixEffectDef> ParseEffects(string json)
        {
            var result = new List<AffixEffectDef>();
            if (string.IsNullOrWhiteSpace(json)) return result;

            try
            {
                var parsed = JsonSerializer.Deserialize<List<AffixEffectDef>>(json);
                if (parsed != null) result.AddRange(parsed);
            }
            catch (Exception)
            {
                // 配置损坏时按无效果处理
            }

            return result;
        }

        public static int[] ParseValues(string values)
        {
            if (string.IsNullOrWhiteSpace(values)) return Array.Empty<int>();

            var parts = values.Split(',');
            var result = new List<int>();
            foreach (var part in parts)
            {
                if (int.TryParse(part.Trim(), out int v)) result.Add(v);
                else result.Add(0);
            }

            return result.ToArray();
        }

        private static int[] RollValues(ItemAffixInfo def)
        {
            var paramDefs = ParseParams(def.ParamsJson);
            var values = new int[paramDefs.Count];
            for (int i = 0; i < paramDefs.Count; i++)
            {
                int min = Math.Min(paramDefs[i].Min, paramDefs[i].Max);
                int max = Math.Max(paramDefs[i].Min, paramDefs[i].Max);
                values[i] = min == max ? min : SEnvir.Random.Next(min, max + 1);
            }

            return values;
        }

        public static string RenderText(ItemAffixInfo def, int[] values)
        {
            if (def == null || string.IsNullOrEmpty(def.Template)) return string.Empty;

            // 手动替换 {n} 占位符：越界或不匹配时保留原样，避免 string.Format 抛异常导致占位符直接显示
            return System.Text.RegularExpressions.Regex.Replace(
                def.Template,
                @"\{(\d+)\}",
                match =>
                {
                    if (!int.TryParse(match.Groups[1].Value, out int index)) return match.Value;
                    return index >= 0 && index < values.Length ? values[index].ToString() : match.Value;
                });
        }

        #endregion

        #region 掉落 roll

        /// <summary>
        /// 掉落生成装备时按概率添加词条（在 SEnvir.CreateDropItem 之后调用）
        /// </summary>
        public static void RollAffixes(UserItem item)
        {
            if (item?.Info == null) return;

            var list = SEnvir.ItemAffixList;
            if (list == null || list.Count == 0) return;

            if (!IsEquipmentType(item.Info.ItemType)) return;

            // 词条概率与数量（按稀有度，设置页「词条概率配置」可调）
            int affixChance, minCount, maxCount;
            switch (item.Rarity)
            {
                case Rarity.Heavenly:
                    affixChance = Config.词条概率天工; minCount = Config.词条数量天工最小; maxCount = Config.词条数量天工最大;
                    break;
                case Rarity.Divine:
                    affixChance = Config.词条概率神兵; minCount = Config.词条数量神兵最小; maxCount = Config.词条数量神兵最大;
                    break;
                case Rarity.Famous:
                    affixChance = Config.词条概率名器; minCount = Config.词条数量名器最小; maxCount = Config.词条数量名器最大;
                    break;
                case Rarity.Elite:
                    affixChance = Config.词条概率稀世; minCount = Config.词条数量稀世最小; maxCount = Config.词条数量稀世最大;
                    break;
                case Rarity.Superior:
                    affixChance = Config.词条概率精炼; minCount = Config.词条数量精炼最小; maxCount = Config.词条数量精炼最大;
                    break;
                default:
                    affixChance = Config.词条概率凡兵; minCount = Config.词条数量凡兵最小; maxCount = Config.词条数量凡兵最大;
                    break;
            }

            if (affixChance <= 0 || SEnvir.Random.Next(100) >= affixChance) return;

            minCount = Math.Max(0, minCount);
            maxCount = Math.Max(minCount, maxCount);
            if (maxCount <= 0) return;
            // 构建候选池
            var candidates = new List<ItemAffixInfo>();
            long totalWeight = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var def = list[i];
                if (def == null || !def.Enabled) continue;
                if (!MatchItem(def, item.Info)) continue;

                candidates.Add(def);
                totalWeight += Math.Max(1, def.Weight);
            }

            if (candidates.Count == 0) return;

            int count = Math.Min(SEnvir.Random.Next(minCount, maxCount + 1), candidates.Count);
            int applied = 0;

            var used = new HashSet<ItemAffixInfo>();
            for (int n = 0; n < count; n++)
            {
                if (totalWeight <= 0) break;

                long roll = SEnvir.Random.Next((int)Math.Min(int.MaxValue, totalWeight));
                ItemAffixInfo chosen = null;

                foreach (var def in candidates)
                {
                    if (used.Contains(def)) continue;

                    roll -= Math.Max(1, def.Weight);
                    if (roll < 0)
                    {
                        chosen = def;
                        break;
                    }
                }

                if (chosen == null) break;

                used.Add(chosen);
                totalWeight -= Math.Max(1, chosen.Weight);

                ApplyAffix(item, chosen);
                applied++;
            }

            if (applied > 0)
                item.StatsChanged();
        }

        private static bool MatchItem(ItemAffixInfo def, ItemInfo info)
        {
            // 类型过滤
            if (!string.IsNullOrWhiteSpace(def.ApplicableTypes))
            {
                bool matched = false;
                foreach (var part in def.ApplicableTypes.Split(','))
                {
                    if (Enum.TryParse(part.Trim(), out ItemType type) && type == info.ItemType)
                    {
                        matched = true;
                        break;
                    }
                }

                if (!matched) return false;
            }
            else if (!IsEquipmentType(info.ItemType))
            {
                return false;
            }

            // 等级过滤（装备需求等级 RequiredAmount）
            int level = info.RequiredAmount;
            if (def.MinLevel >= 0 && level < def.MinLevel) return false;
            if (def.MaxLevel >= 0 && level > def.MaxLevel) return false;

            return true;
        }

        private static void ApplyAffix(UserItem item, ItemAffixInfo def)
        {
            int[] values = RollValues(def);

            UserItemAffix instance = SEnvir.UserItemAffixList.CreateNewObject();
            instance.Item = item;
            instance.Affix = def;
            instance.Values = string.Join(",", values);
            instance.DisplayText = RenderText(def, values);

            UpdateAffixItemName(item);

            // 静态词条直接写属性行；条件词条在玩家 RefreshStats 时运行时评估
            if (def.ConditionType == AffixConditionType.None)
                ApplyStaticEffects(item, def, values);
        }

        private static void ApplyStaticEffects(UserItem item, ItemAffixInfo def, int[] values)
        {
            foreach (var effect in ParseEffects(def.EffectsJson))
            {
                if (IsEffectPercent(def, effect)) continue;   // 百分比效果由 ApplyPercentAffixStats 运行时按角色基础属性计算
                int amount = ResolveEffectAmount(effect, values);
                if (amount == 0) continue;
                if (!Enum.TryParse(effect.Stat, out Stat stat)) continue;

                item.AddStat(stat, amount, StatSource.Affix);
            }
        }

        private static int ResolveEffectAmount(AffixEffectDef effect, int[] values)
        {
            if (effect == null) return 0;

            if (effect.UseParam)
                return effect.ParamIndex >= 0 && effect.ParamIndex < values.Length ? values[effect.ParamIndex] : 0;

            return effect.FixedAmount;
        }

        #endregion

        #region 属性重建与级联

        /// <summary>
        /// 清空装备的词条属性行并按剩余词条实例重算（删除/修改词条配置后调用）
        /// </summary>
        /// <summary>
        /// 词条命名：以第一条词条名 + "之" + 原始物品名（如"嗜血之屠龙刀"）；无词条时恢复原名
        /// </summary>
        public static void UpdateAffixItemName(UserItem item)
        {
            if (item?.Info == null) return;

            string firstName = null;
            if (item.Affixes != null)
            {
                foreach (UserItemAffix instance in item.Affixes)
                {
                    if (instance?.Affix == null || string.IsNullOrWhiteSpace(instance.Affix.AffixName)) continue;

                    firstName = instance.Affix.AffixName.Trim();
                    break;
                }
            }

            item.AffixName = string.IsNullOrEmpty(firstName) ? null : $"{firstName}之{item.Info.ItemName}";
        }

        public static void RebuildItemAffixStats(UserItem item)
        {
            if (item == null) return;

            for (int i = item.AddedStats.Count - 1; i >= 0; i--)
            {
                if (item.AddedStats[i].StatSource != StatSource.Affix) continue;
                item.AddedStats[i].Delete();
            }

            foreach (UserItemAffix instance in item.Affixes)
            {
                var def = instance.Affix;
                if (def == null || def.ConditionType != AffixConditionType.None) continue;

                ApplyStaticEffects(item, def, ParseValues(instance.Values));
            }

            UpdateAffixItemName(item);

            item.StatsChanged();
        }

        /// <summary>
        /// 配置更新后刷新引用该配置的词条实例（重新渲染文本）并重建受影响装备属性
        /// </summary>
        /// <summary>
        /// 启动自愈：重渲染全部词条文本（修复旧版本渲染缺陷）、数值缺失时重新 roll、
        /// 清理配置已删除的孤儿实例，并重建受影响装备的属性行。在 LoadDatabase 末尾调用。
        /// </summary>
        public static int RepairAllInstances()
        {
            var all = SEnvir.UserItemAffixList;
            if (all == null) return 0;

            int repaired = 0;
            var affectedItems = new List<UserItem>();

            for (int i = all.Count - 1; i >= 0; i--)
            {
                var instance = all[i];
                if (instance == null) continue;

                // 孤儿实例（配置已被删除）
                if (instance.Affix == null)
                {
                    instance.Delete();
                    repaired++;
                    continue;
                }

                var def = instance.Affix;
                var paramDefs = ParseParams(def.ParamsJson);
                var values = ParseValues(instance.Values);

                // 数值缺失或数量与配置不符（旧数据/配置变更）→ 重新 roll
                if (values.Length != paramDefs.Count)
                {
                    values = RollValues(def);
                    instance.Values = string.Join(",", values);
                }

                // 文本重渲染（当前代码渲染，占位符按当前数值代入）
                string text = RenderText(def, values);
                if (instance.DisplayText != text)
                {
                    instance.DisplayText = text;
                    repaired++;
                }

                if (instance.Item != null && !affectedItems.Contains(instance.Item))
                    affectedItems.Add(instance.Item);
            }

            foreach (var item in affectedItems)
            {
                UpdateAffixItemName(item);
                RebuildItemAffixStats(item);
            }

            return repaired;
        }

        public static List<UserItem> RefreshAffixInstances(ItemAffixInfo def)
        {
            var affected = new List<UserItem>();
            var all = SEnvir.UserItemAffixList;
            if (def == null || all == null) return affected;

            for (int i = all.Count - 1; i >= 0; i--)
            {
                var instance = all[i];
                if (instance == null || instance.Affix != def) continue;

                var values = ParseValues(instance.Values);
                if (values.Length != ParseParams(def.ParamsJson).Count)
                {
                    // 配置参数数与已存数值不符（如编辑时新增了参数）→ 重新 roll，保证占位符可完整代入
                    values = RollValues(def);
                    instance.Values = string.Join(",", values);
                }

                instance.DisplayText = RenderText(def, values);

                if (instance.Item != null && !affected.Contains(instance.Item))
                    affected.Add(instance.Item);
            }

            foreach (var item in affected)
                RebuildItemAffixStats(item);

            return affected;
        }

        /// <summary>
        /// 删除词条配置：先从所有已有装备中移除对应词条实例与属性行，再删除配置本体
        /// </summary>
        public static string DeleteAffix(ItemAffixInfo def)
        {
            if (def == null) return "词条不存在";

            var all = SEnvir.UserItemAffixList;
            if (all == null) return "服务器数据未就绪";

            var instances = new List<UserItemAffix>();
            var affectedItems = new List<UserItem>();

            for (int i = all.Count - 1; i >= 0; i--)
            {
                var instance = all[i];
                if (instance == null || instance.Affix != def) continue;

                instances.Add(instance);
                if (instance.Item != null && !affectedItems.Contains(instance.Item))
                    affectedItems.Add(instance.Item);
            }

            foreach (var instance in instances)
                instance.Delete();

            foreach (var item in affectedItems)
                RebuildItemAffixStats(item);

            def.Delete();

            RefreshOnlinePlayers();

            return $"删除成功：已从 {affectedItems.Count} 件装备中移除该词条";
        }

        /// <summary>
        /// 词条属性行变更后刷新在线玩家属性
        /// </summary>
        public static void RefreshOnlinePlayers()
        {
            var players = SEnvir.Players;
            if (players == null) return;

            foreach (var player in players)
            {
                try
                {
                    player?.RefreshStats();
                }
                catch (Exception)
                {
                    // 忽略单个玩家刷新失败
                }
            }
        }

        #endregion

        #region 条件词条运行时评估

        /// <summary>
        /// 评估条件是否满足
        /// </summary>
        public static bool EvaluateCondition(ItemAffixInfo def, int[] values, PlayerObject player)
        {
            if (def == null || player == null || def.ConditionType == AffixConditionType.None) return false;

            switch (def.ConditionType)
            {
                case AffixConditionType.HealthPercentBelow:
                {
                    int threshold = GetConditionValue(def, values);
                    if (IsConditionPercent(def))
                    {
                        int maxHP = player.Stats[Stat.Health];
                        if (maxHP <= 0) return false;
                        return player.CurrentHP * 100 < threshold * maxHP;
                    }
                    return player.CurrentHP < threshold;
                }
                case AffixConditionType.MagicPercentBelow:
                {
                    int threshold = GetConditionValue(def, values);
                    if (IsConditionPercent(def))
                    {
                        int maxMP = player.Stats[Stat.Mana];
                        if (maxMP <= 0) return false;
                        return player.CurrentMP * 100 < threshold * maxMP;
                    }
                    return player.CurrentMP < threshold;
                }
                case AffixConditionType.NightTime:
                    return SEnvir.Now.Hour >= 19 || SEnvir.Now.Hour < 6;
                default:
                    return false;
            }
        }

        private static int GetConditionValue(ItemAffixInfo def, int[] values)
        {
            if (def.ConditionParamIndex < 0 || def.ConditionParamIndex >= values.Length) return 0;
            return values[def.ConditionParamIndex];
        }

        /// <summary>
        /// 把满足条件的词条效果加入角色运行时属性（不落库）。在 RefreshStats 末尾调用。
        /// </summary>
        public static bool ApplyConditionalAffixStats(PlayerObject player)
        {
            bool hasConditional = false;

            if (player?.Equipment == null) return false;

            foreach (UserItem item in player.Equipment)
            {
                if (item == null) continue;

                foreach (UserItemAffix instance in item.Affixes)
                {
                    var def = instance.Affix;
                    if (def == null || def.ConditionType == AffixConditionType.None) continue;

                    hasConditional = true;

                    if (!EvaluateCondition(def, ParseValues(instance.Values), player)) continue;

                    var values = ParseValues(instance.Values);
                    foreach (var effect in ParseEffects(def.EffectsJson))
                    {
                        if (IsEffectPercent(def, effect)) continue;   // 百分比效果由 ApplyPercentAffixStats 处理
                        int amount = ResolveEffectAmount(effect, values);
                        if (amount == 0) continue;
                        if (!Enum.TryParse(effect.Stat, out Stat stat)) continue;

                        player.Stats[stat] += amount;
                    }
                }
            }

            return hasConditional;
        }
        /// <summary>效果是否按百分比生效（引用参数的 IsPercent 决定）</summary>
        private static bool IsEffectPercent(ItemAffixInfo def, AffixEffectDef effect)
        {
            if (effect == null || !effect.UseParam) return false;

            var paramDefs = ParseParams(def?.ParamsJson);
            return effect.ParamIndex >= 0 && effect.ParamIndex < paramDefs.Count && paramDefs[effect.ParamIndex].IsPercent;
        }

        /// <summary>条件阈值是否按百分比（条件参数引用的 IsPercent）</summary>
        private static bool IsConditionPercent(ItemAffixInfo def)
        {
            if (def == null) return false;

            var paramDefs = ParseParams(def.ParamsJson);
            return def.ConditionParamIndex >= 0 && def.ConditionParamIndex < paramDefs.Count && paramDefs[def.ConditionParamIndex].IsPercent;
        }

        /// <summary>
        /// 百分比词条效果运行时应用：以角色当前基础属性为基数，按百分比加成（多件装备同属性百分比累加后一次生效，避免累乘）。
        /// 条件词条的百分比效果同样只在条件满足时计入。在 RefreshStats 末尾调用。
        /// </summary>
        public static void ApplyPercentAffixStats(PlayerObject player)
        {
            if (player?.Equipment == null) return;

            var percentMap = new Dictionary<Stat, int>();

            foreach (UserItem item in player.Equipment)
            {
                if (item == null) continue;

                foreach (UserItemAffix instance in item.Affixes)
                {
                    var def = instance.Affix;
                    if (def == null) continue;

                    var values = ParseValues(instance.Values);

                    // 条件词条需条件满足才计入
                    if (def.ConditionType != AffixConditionType.None && !EvaluateCondition(def, values, player)) continue;

                    foreach (var effect in ParseEffects(def.EffectsJson))
                    {
                        if (!IsEffectPercent(def, effect)) continue;

                        int amount = ResolveEffectAmount(effect, values);
                        if (amount == 0) continue;
                        if (!Enum.TryParse(effect.Stat, out Stat stat)) continue;

                        percentMap[stat] += amount;
                    }
                }
            }

            foreach (KeyValuePair<Stat, int> pair in percentMap)
                player.Stats[pair.Key] += player.Stats[pair.Key] * pair.Value / 100;
        }


        #endregion
    }
}