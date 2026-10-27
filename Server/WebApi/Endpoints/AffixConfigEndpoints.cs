using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Library;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Server.Envir;
using Server.WebApi.Auth;

namespace Server.WebApi.Endpoints
{
    /// <summary>
    /// 词条概率配置端点：稀有度掉落概率 + 各稀有度词条概率/数量
    /// </summary>
    public static class AffixConfigEndpoints
    {
        public static void Map(WebApplication app)
        {
            var group = app.MapGroup("/api/game");

            group.MapGet("/affixconfig", GetAffixConfig);
            group.MapPut("/affixconfig", UpdateAffixConfig);
        }

        #region Handlers

        private static IResult GetAffixConfig(ClaimsPrincipal user)
        {
            if (!JwtHelper.HasMinimumIdentity(user, AccountIdentity.Supervisor))
                return Results.Forbid();

            return Results.Ok(BuildDto());
        }

        private static IResult UpdateAffixConfig(
            ClaimsPrincipal user,
            HttpContext context,
            AffixConfigRequest request)
        {
            if (!JwtHelper.HasMinimumIdentity(user, AccountIdentity.SuperAdmin))
                return Results.Forbid();

            if (request == null) return Results.BadRequest(new { message = "请求无效" });

            var (ok, message) = Validate(request);
            if (!ok) return Results.BadRequest(new { message });

            // 掉落概率（累积百分比）
            Config.装备掉落精炼概率 = request.DropChances.Superior;
            Config.装备掉落稀世概率 = request.DropChances.Elite;
            Config.装备掉落名器概率 = request.DropChances.Famous;
            Config.装备掉落神兵概率 = request.DropChances.Divine;
            Config.装备掉落天工概率 = request.DropChances.Heavenly;

            // 词条概率与数量
            foreach (var row in request.AffixCounts)
            {
                switch (row.Rarity)
                {
                    case "Common": Config.词条概率凡兵 = row.Chance; Config.词条数量凡兵最小 = row.Min; Config.词条数量凡兵最大 = row.Max; break;
                    case "Superior": Config.词条概率精炼 = row.Chance; Config.词条数量精炼最小 = row.Min; Config.词条数量精炼最大 = row.Max; break;
                    case "Elite": Config.词条概率稀世 = row.Chance; Config.词条数量稀世最小 = row.Min; Config.词条数量稀世最大 = row.Max; break;
                    case "Famous": Config.词条概率名器 = row.Chance; Config.词条数量名器最小 = row.Min; Config.词条数量名器最大 = row.Max; break;
                    case "Divine": Config.词条概率神兵 = row.Chance; Config.词条数量神兵最小 = row.Min; Config.词条数量神兵最大 = row.Max; break;
                    case "Heavenly": Config.词条概率天工 = row.Chance; Config.词条数量天工最小 = row.Min; Config.词条数量天工最大 = row.Max; break;
                }
            }

            ConfigReader.Save();

            WebApiLogger.Audit(context, "修改词条概率配置", "");
            return Results.Ok(new { message = "词条概率配置已保存并即时生效" });
        }

        #endregion

        #region Helpers

        private static object BuildDto()
        {
            return new
            {
                dropChances = new
                {
                    superior = Config.装备掉落精炼概率,
                    elite = Config.装备掉落稀世概率,
                    famous = Config.装备掉落名器概率,
                    divine = Config.装备掉落神兵概率,
                    heavenly = Config.装备掉落天工概率,
                },
                affixCounts = new[]
                {
                    new { rarity = "Common", title = "凡兵", chance = Config.词条概率凡兵, min = Config.词条数量凡兵最小, max = Config.词条数量凡兵最大 },
                    new { rarity = "Superior", title = "精炼", chance = Config.词条概率精炼, min = Config.词条数量精炼最小, max = Config.词条数量精炼最大 },
                    new { rarity = "Elite", title = "稀世", chance = Config.词条概率稀世, min = Config.词条数量稀世最小, max = Config.词条数量稀世最大 },
                    new { rarity = "Famous", title = "名器", chance = Config.词条概率名器, min = Config.词条数量名器最小, max = Config.词条数量名器最大 },
                    new { rarity = "Divine", title = "神兵", chance = Config.词条概率神兵, min = Config.词条数量神兵最小, max = Config.词条数量神兵最大 },
                    new { rarity = "Heavenly", title = "天工", chance = Config.词条概率天工, min = Config.词条数量天工最小, max = Config.词条数量天工最大 },
                }
            };
        }

        private static (bool ok, string message) Validate(AffixConfigRequest request)
        {
            var drop = request.DropChances;

            if (drop.Superior < 0 || drop.Superior > 100 ||
                drop.Elite < 0 || drop.Elite > 100 ||
                drop.Famous < 0 || drop.Famous > 100 ||
                drop.Divine < 0 || drop.Divine > 100 ||
                drop.Heavenly < 0 || drop.Heavenly > 100)
                return (false, "掉落概率必须在 0~100 之间");

            // 累积概率校验：P(>=天工) <= P(>=神兵) <= P(>=名器) <= P(>=稀世) <= P(>=精炼)
            if (drop.Heavenly > drop.Divine || drop.Divine > drop.Famous ||
                drop.Famous > drop.Elite || drop.Elite > drop.Superior)
                return (false, "累积概率需满足：天工 ≤ 神兵 ≤ 名器 ≤ 稀世 ≤ 精炼（高档概率不能超过低档）");

            var titles = new Dictionary<string, string>
            {
                { "Common", "凡兵" }, { "Superior", "精炼" }, { "Elite", "稀世" },
                { "Famous", "名器" }, { "Divine", "神兵" }, { "Heavenly", "天工" },
            };

            if (request.AffixCounts == null || request.AffixCounts.Count != 6)
                return (false, "词条数量配置必须包含全部 6 个稀有度");

            foreach (var row in request.AffixCounts)
            {
                if (!titles.ContainsKey(row.Rarity))
                    return (false, $"未知稀有度: {row.Rarity}");

                if (row.Chance < 0 || row.Chance > 100)
                    return (false, $"{titles[row.Rarity]} 的词条概率必须在 0~100 之间");

                if (row.Min < 0 || row.Max < row.Min || row.Max > 10)
                    return (false, $"{titles[row.Rarity]} 的词条数量需满足 0 ≤ 最小 ≤ 最大 ≤ 10");
            }

            return (true, string.Empty);
        }

        #endregion

        #region Request

        public class AffixConfigRequest
        {
            public DropChancesDto DropChances { get; set; }
            public List<AffixCountDto> AffixCounts { get; set; }
        }

        public class DropChancesDto
        {
            public int Superior { get; set; }
            public int Elite { get; set; }
            public int Famous { get; set; }
            public int Divine { get; set; }
            public int Heavenly { get; set; }
        }

        public class AffixCountDto
        {
            public string Rarity { get; set; }
            public int Chance { get; set; }
            public int Min { get; set; }
            public int Max { get; set; }
        }

        #endregion
    }
}