using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Security.Claims;
using Library;
using Library.SystemModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Server.Envir;
using Server.WebApi.Auth;

namespace Server.WebApi.Endpoints
{
    /// <summary>
    /// 装备词条管理端点（暗黑风格词条配置的增删改查）
    /// </summary>
    public static class AffixEndpoints
    {
        public static void Map(WebApplication app)
        {
            var group = app.MapGroup("/api/game");

            group.MapGet("/affixes", GetAffixes);
            group.MapGet("/affixes/meta", GetAffixMeta);
            group.MapPost("/affixes", CreateAffix);
            group.MapPut("/affixes/{index:int}", UpdateAffix);
            group.MapDelete("/affixes/{index:int}", DeleteAffix);
            group.MapPost("/affixes/icon", UploadIcon);
        }

        #region Handlers

        private static IResult GetAffixes(
            ClaimsPrincipal user,
            int page = 1,
            int pageSize = 50,
            string? search = null)
        {
            if (!JwtHelper.HasMinimumIdentity(user, AccountIdentity.Supervisor))
                return Results.Forbid();

            var list = SEnvir.ItemAffixList;
            if (list == null) return Results.Ok(new { total = 0, page, pageSize, totalPages = 0, affixes = Array.Empty<object>() });

            var query = new List<ItemAffixInfo>();
            for (int i = 0; i < list.Count; i++)
            {
                var def = list[i];
                if (def == null) continue;

                if (!string.IsNullOrEmpty(search) &&
                    def.AffixName?.Contains(search, StringComparison.OrdinalIgnoreCase) != true)
                    continue;

                query.Add(def);
            }

            var total = query.Count;
            var affixes = query
                .OrderBy(a => a.Index)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(ToDto)
                .ToList();

            return Results.Ok(new
            {
                total,
                page,
                pageSize,
                totalPages = (int)Math.Ceiling((double)total / pageSize),
                affixes
            });
        }

        private static IResult GetAffixMeta(ClaimsPrincipal user)
        {
            if (!JwtHelper.HasMinimumIdentity(user, AccountIdentity.Supervisor))
                return Results.Forbid();

            var stats = new List<object>();
            foreach (Stat stat in Enum.GetValues(typeof(Stat)))
            {
                if (stat == Stat.None) continue;

                var field = typeof(Stat).GetField(stat.ToString());
                var desc = field?.GetCustomAttribute<StatDescription>();

                stats.Add(new { value = stat.ToString(), title = desc?.Title ?? stat.ToString(), category = GetStatCategory(stat, desc) });
            }

            var itemTypes = new List<object>();
            foreach (ItemType type in Enum.GetValues(typeof(ItemType)))
            {
                if (!AffixSystem.IsEquipmentType(type)) continue;

                var field = typeof(ItemType).GetField(type.ToString());
                var attr = field?.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();

                itemTypes.Add(new { value = type.ToString(), title = attr?.Description ?? type.ToString() });
            }

            var conditions = new List<object>();
            foreach (AffixConditionType condition in Enum.GetValues(typeof(AffixConditionType)))
            {
                conditions.Add(new { value = condition.ToString(), title = GetConditionTitle(condition) });
            }

            return Results.Ok(new { stats, itemTypes, conditions });
        }

        private static IResult CreateAffix(
            ClaimsPrincipal user,
            HttpContext context,
            AffixRequest request)
        {
            if (!JwtHelper.HasMinimumIdentity(user, AccountIdentity.SuperAdmin))
                return Results.Forbid();

            var (ok, message) = ValidateAffix(request);
            if (!ok) return Results.BadRequest(new { message });

            var list = SEnvir.ItemAffixList;
            if (list == null) return Results.BadRequest(new { message = "服务器数据未就绪" });

            var def = list.CreateNewObject();
            ApplyRequest(def, request);

            WebApiLogger.Audit(context, "新增词条", $"index={def.Index} name={def.AffixName}");
            return Results.Ok(new { message = "词条创建成功", index = def.Index });
        }

        private static IResult UpdateAffix(
            ClaimsPrincipal user,
            HttpContext context,
            int index,
            AffixRequest request)
        {
            if (!JwtHelper.HasMinimumIdentity(user, AccountIdentity.SuperAdmin))
                return Results.Forbid();

            var def = FindAffix(index);
            if (def == null) return Results.NotFound(new { message = "词条不存在" });

            var (ok, message) = ValidateAffix(request);
            if (!ok) return Results.BadRequest(new { message });

            ApplyRequest(def, request);

            // 配置变更后：重新渲染旧装备上的词条文本并重建属性行（旧装备同步生效）
            var affected = AffixSystem.RefreshAffixInstances(def);
            AffixSystem.RefreshOnlinePlayers();

            WebApiLogger.Audit(context, "修改词条", $"index={index} name={def.AffixName} affectedItems={affected.Count}");
            return Results.Ok(new { message = $"词条更新成功，同步更新了 {affected.Count} 件装备" });
        }

        private static IResult DeleteAffix(
            ClaimsPrincipal user,
            HttpContext context,
            int index)
        {
            if (!JwtHelper.HasMinimumIdentity(user, AccountIdentity.SuperAdmin))
                return Results.Forbid();

            var def = FindAffix(index);
            if (def == null) return Results.NotFound(new { message = "词条不存在" });

            var name = def.AffixName;
            var message = AffixSystem.DeleteAffix(def);

            WebApiLogger.Audit(context, "删除词条", $"index={index} name={name}");
            return Results.Ok(new { message });
        }

        #endregion

        #region Helpers

        private static ItemAffixInfo? FindAffix(int index)
        {
            var list = SEnvir.ItemAffixList;
            if (list == null) return null;

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i]?.Index == index) return list[i];
            }

            return null;
        }

        private static (bool ok, string message) ValidateAffix(AffixRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Name))
                return (false, "词条名称不能为空");

            if (string.IsNullOrWhiteSpace(request.Template))
                return (false, "词条模板不能为空");

            var paramCount = request.ParamDefs?.Count ?? 0;

            if (request.ParamDefs != null)
            {
                foreach (var param in request.ParamDefs)
                {
                    if (param.Min > param.Max)
                        return (false, "参数区间无效：Min 不能大于 Max");
                }
            }

            if (request.Effects != null)
            {
                foreach (var effect in request.Effects)
                {
                    if (string.IsNullOrWhiteSpace(effect.Stat) || !Enum.TryParse<Stat>(effect.Stat, out _))
                        return (false, $"无效的属性类型: {effect.Stat}");

                    if (effect.UseParam && (effect.ParamIndex < 0 || effect.ParamIndex >= paramCount))
                        return (false, "效果引用的占位符索引超出参数范围");
                }
            }


            // 模板占位符越界校验：{n} 的 n 必须小于参数数量，否则渲染时无法代入数值
            var placeholderIndexes = System.Text.RegularExpressions.Regex.Matches(request.Template, @"\{(\d+)\}")
                .Select(m => int.Parse(m.Groups[1].Value))
                .ToList();

            if (placeholderIndexes.Count > 0 && placeholderIndexes.Any(n => n >= paramCount))
                return (false, $"模板占位符 {{{placeholderIndexes.Max()}}} 超出参数数量（当前 {paramCount} 个参数），请补充随机参数或修改模板");
            if (!Enum.TryParse<AffixConditionType>(request.ConditionType ?? "None", out var condition))
                return (false, $"无效的条件类型: {request.ConditionType}");

            // 事件触发型条件必须引用一个占位符参数作为触发概率（如 Min=5, Max=15 表示 5%~15% 概率）
            if (condition >= AffixConditionType.OnAttackMonster &&
                (request.ConditionParamIndex == null || request.ConditionParamIndex < 0))
                return (false, "事件触发型条件（攻击/受击/死亡/复活）必须引用一个占位符参数作为触发概率，请设置“条件参数占位符索引”");

            return (true, string.Empty);
        }

        private static void ApplyRequest(ItemAffixInfo def, AffixRequest request)
        {
            def.AffixName = request.Name.Trim();
            def.Template = request.Template.Trim();
            def.ParamsJson = JsonSerializerNullable(request.ParamDefs);
            def.EffectsJson = JsonSerializerNullable(request.Effects);
            def.ConditionType = Enum.TryParse<AffixConditionType>(request.ConditionType, out var condition)
                ? condition : AffixConditionType.None;
            def.ConditionParamIndex = request.ConditionParamIndex ?? -1;
            def.ApplicableTypes = request.ApplicableTypes == null || request.ApplicableTypes.Count == 0
                ? string.Empty
                : string.Join(",", request.ApplicableTypes);
            def.MinLevel = request.MinLevel ?? -1;
            def.MaxLevel = request.MaxLevel ?? -1;
            def.Weight = request.Weight ?? 100;
            def.Enabled = request.Enabled ?? true;
            def.Duration = request.Duration ?? 0;
            def.BuffIcon = request.BuffIcon?.Trim();
        }

        private static string? JsonSerializerNullable<T>(List<T>? value)
        {
            return value == null ? null : System.Text.Json.JsonSerializer.Serialize(value);
        }

        private static object ToDto(ItemAffixInfo def)
        {
            var paramDefs = AffixSystem.ParseParams(def.ParamsJson);
            var effectDefs = AffixSystem.ParseEffects(def.EffectsJson);

            return new
            {
                index = def.Index,
                name = def.AffixName ?? "",
                template = def.Template ?? "",
                @params = paramDefs.Select(p => new { min = p.Min, max = p.Max, isPercent = p.IsPercent, isDuration = p.IsDuration }).ToList(),
                effects = effectDefs.Select(e => new
                {
                    stat = e.Stat,
                    useParam = e.UseParam,
                    fixedAmount = e.FixedAmount,
                    paramIndex = e.ParamIndex,
                }).ToList(),
                conditionType = def.ConditionType.ToString(),
                conditionParamIndex = def.ConditionParamIndex,
                applicableTypes = string.IsNullOrEmpty(def.ApplicableTypes)
                    ? new List<string>()
                    : def.ApplicableTypes.Split(',').Select(t => t.Trim()).ToList(),
                minLevel = def.MinLevel,
                maxLevel = def.MaxLevel,
                weight = def.Weight,
                enabled = def.Enabled,
                duration = def.Duration,
                buffIcon = def.BuffIcon ?? "",
            };
        }

        private static async Task<IResult> UploadIcon(ClaimsPrincipal user, IFormFile file)
        {
            if (!JwtHelper.HasMinimumIdentity(user, AccountIdentity.SuperAdmin))
                return Results.Forbid();

            if (file == null || file.Length == 0 || file.Length > 512 * 1024)
                return Results.BadRequest(new { message = "请上传有效的图标文件（≤512KB）" });

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".bmp")
                return Results.BadRequest(new { message = "仅支持 png/jpg/bmp 图标" });

            string dir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "bufficons");
            Directory.CreateDirectory(dir);

            string fileName = $"{Guid.NewGuid():N}{ext}";
            string fullPath = Path.Combine(dir, fileName);

            using (var stream = File.Create(fullPath))
            {
                await file.CopyToAsync(stream);
            }

            return Results.Ok(new { message = "图标上传成功", fileName });
        }

        private static string GetConditionTitle(AffixConditionType condition)
        {
            switch (condition)
            {
                case AffixConditionType.None: return "无（静态词条）";
                case AffixConditionType.HealthPercentBelow: return "血量低于{参数}%时";
                case AffixConditionType.MagicPercentBelow: return "魔法低于{参数}%时";
                case AffixConditionType.NightTime: return "夜间";
                case AffixConditionType.OnAttackMonster: return "攻击怪物时{参数}%概率触发";
                case AffixConditionType.OnAttackPlayer: return "攻击人物时{参数}%概率触发";
                case AffixConditionType.OnDamaged: return "受到攻击时{参数}%概率触发";
                case AffixConditionType.OnDeath: return "死亡时{参数}%概率触发";
                case AffixConditionType.OnRespawn: return "复活时{参数}%概率触发";
                default: return condition.ToString();
            }
        }

        #endregion

        /// <summary>
        /// 词条“属性效果”推荐分类（新增词条时下拉分组提示）：
        /// percent=仅建议百分比；both=百分比/数值均可；value=仅建议数值；unsuitable=不建议用于装备属性。
        /// 注意：引擎本身不强制百分比/数值（由参数 IsPercent 决定），此处为按 Stat 存储格式与语义给出的配置建议。
        /// </summary>
        private static readonly HashSet<Stat> BothCategoryStats = new HashSet<Stat>
        {
            Stat.Health, Stat.Mana,
            Stat.MinAC, Stat.MaxAC, Stat.MinMR, Stat.MaxMR,
            Stat.MinDC, Stat.MaxDC, Stat.MinMC, Stat.MaxMC, Stat.MinSC, Stat.MaxSC,
        };

        /// <summary>Percent 模式但属于全局/地图/特殊标记，不适合作为装备词条效果</summary>
        private static readonly HashSet<Stat> UnsuitableCategoryStats = new HashSet<Stat>
        {
            Stat.MonsterExperience, Stat.MonsterGold, Stat.MonsterDrop, Stat.MonsterDamage, Stat.MonsterHealth,
            Stat.MaxMonsterExperience, Stat.MaxMonsterGold, Stat.MaxMonsterDrop, Stat.MaxMonsterDamage, Stat.MaxMonsterHealth,
            Stat.BaseExperienceRate, Stat.BaseGoldRate, Stat.BaseDropRate,
            Stat.SaleBonus5, Stat.SaleBonus10, Stat.SaleBonus15, Stat.SaleBonus20,
        };

        private static string GetStatCategory(Stat stat, StatDescription desc)
        {
            if (UnsuitableCategoryStats.Contains(stat)) return "unsuitable";
            if (desc != null)
            {
                if (desc.Mode == StatType.Text || desc.Mode == StatType.None || desc.Mode == StatType.Time)
                    return "unsuitable";
            }
            if (BothCategoryStats.Contains(stat)) return "both";
            if (desc?.Mode == StatType.Percent) return "percent";
            return "value";
        }

        #region Request

        public class AffixRequest
        {
            public string Name { get; set; }
            public string Template { get; set; }
            public List<AffixParamDto>? ParamDefs { get; set; }
            public List<AffixEffectDto>? Effects { get; set; }
            public string ConditionType { get; set; }
            public int? ConditionParamIndex { get; set; }
            public List<string>? ApplicableTypes { get; set; }
            public int? MinLevel { get; set; }
            public int? MaxLevel { get; set; }
            public int? Weight { get; set; }
            public bool? Enabled { get; set; }
            public int? Duration { get; set; }
            public string? BuffIcon { get; set; }
        }

        public class AffixParamDto
        {
            public int Min { get; set; }
            public int Max { get; set; }
            /// <summary>true=百分比生效，false=数值生效（旧配置默认百分比）</summary>
            public bool IsPercent { get; set; } = true;
            /// <summary>true=该占位符的 roll 值作为词条效果持续时间（秒）</summary>
            public bool IsDuration { get; set; }
        }
        public class AffixEffectDto
        {
            public string Stat { get; set; }
            public bool UseParam { get; set; }
            public int FixedAmount { get; set; }
            public int ParamIndex { get; set; }
        }

        #endregion
    }
}