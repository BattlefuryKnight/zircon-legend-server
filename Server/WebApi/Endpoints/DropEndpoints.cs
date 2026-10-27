using System;
using System.Collections.Generic;
using System.Linq;
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
    /// 怪物掉落表管理端点（怪物掉落配置的增删改查）
    /// </summary>
    public static class DropEndpoints
    {
        public static void Map(WebApplication app)
        {
            var group = app.MapGroup("/api/game");

            group.MapGet("/drops", GetDrops);
            group.MapGet("/drops/monsters", GetDropMonsters);
            group.MapPost("/drops", CreateDrop);
            group.MapPut("/drops/{index:int}", UpdateDrop);
            group.MapDelete("/drops/{index:int}", DeleteDrop);
            group.MapPost("/drops/batch-delete", BatchDeleteDrops);
        }

        #region Handlers

        /// <summary>
        /// 掉落表列表：monsterIndex 过滤指定怪物；itemSearch 按物品名过滤（可组合）
        /// </summary>
        private static IResult GetDrops(
            ClaimsPrincipal user,
            int page = 1,
            int pageSize = 50,
            int? monsterIndex = null,
            string? itemSearch = null, string? itemType = null)
        {
            if (!JwtHelper.HasMinimumIdentity(user, AccountIdentity.Supervisor))
                return Results.Forbid();

            var list = SEnvir.DropInfoList;
            if (list == null) return Results.Ok(new { total = 0, page, pageSize, totalPages = 0, drops = Array.Empty<object>() });

            var query = new List<DropInfo>();
            for (int i = 0; i < list.Count; i++)
            {
                var drop = list[i];
                if (drop == null) continue;

                if (monsterIndex.HasValue && drop.Monster?.Index != monsterIndex.Value) continue;

                if (!string.IsNullOrEmpty(itemSearch) &&
                    drop.Item?.ItemName?.Contains(itemSearch, StringComparison.OrdinalIgnoreCase) != true)
                    continue;


                if (!string.IsNullOrEmpty(itemType) &&
                    !string.Equals(drop.Item?.ItemType.ToString(), itemType, StringComparison.OrdinalIgnoreCase))
                    continue;                query.Add(drop);
            }

            var total = query.Count;
            var drops = query
                .OrderBy(d => d.Monster?.Index)
                .ThenBy(d => d.Index)
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
                drops
            });
        }

        /// <summary>
        /// 怪物轻量列表（下拉选择用，最多返回 50 条）
        /// </summary>
        private static IResult GetDropMonsters(ClaimsPrincipal user, string? search = null)
        {
            if (!JwtHelper.HasMinimumIdentity(user, AccountIdentity.Supervisor))
                return Results.Forbid();

            var monsters = SEnvir.MonsterInfoList;
            if (monsters == null) return Results.Ok(new { monsters = Array.Empty<object>() });

            var result = new List<object>();
            for (int i = 0; i < monsters.Count && result.Count < 50; i++)
            {
                var monster = monsters[i];
                if (monster == null) continue;

                if (!string.IsNullOrEmpty(search) &&
                    monster.MonsterName?.Contains(search, StringComparison.OrdinalIgnoreCase) != true)
                    continue;

                result.Add(new { index = monster.Index, name = monster.MonsterName ?? "", level = monster.Level });
            }

            return Results.Ok(new { monsters = result });
        }

        private static IResult CreateDrop(
            ClaimsPrincipal user,
            HttpContext context,
            DropRequest request)
        {
            if (!JwtHelper.HasMinimumIdentity(user, AccountIdentity.SuperAdmin))
                return Results.Forbid();

            var (monster, item, error) = ResolveRequest(request);
            if (error != null) return Results.BadRequest(new { message = error });

            var list = SEnvir.DropInfoList;
            if (list == null) return Results.BadRequest(new { message = "服务器数据未就绪" });

            var drop = list.CreateNewObject();
            drop.Monster = monster;
            drop.Item = item;
            ApplyRequest(drop, request);

            WebApiLogger.Audit(context, "新增掉落", $"monster={monster.MonsterName} item={item.ItemName} chance={drop.Chance}");
            return Results.Ok(new { message = "掉落配置创建成功", index = drop.Index });
        }

        private static IResult UpdateDrop(
            ClaimsPrincipal user,
            HttpContext context,
            int index,
            DropRequest request)
        {
            if (!JwtHelper.HasMinimumIdentity(user, AccountIdentity.SuperAdmin))
                return Results.Forbid();

            var drop = FindDrop(index);
            if (drop == null) return Results.NotFound(new { message = "掉落配置不存在" });

            var (monster, item, error) = ResolveRequest(request);
            if (error != null) return Results.BadRequest(new { message = error });

            drop.Monster = monster;
            drop.Item = item;
            ApplyRequest(drop, request);

            WebApiLogger.Audit(context, "修改掉落", $"index={index} monster={monster.MonsterName} item={item.ItemName} chance={drop.Chance}");
            return Results.Ok(new { message = "掉落配置更新成功" });
        }

        private static IResult DeleteDrop(
            ClaimsPrincipal user,
            HttpContext context,
            int index)
        {
            if (!JwtHelper.HasMinimumIdentity(user, AccountIdentity.SuperAdmin))
                return Results.Forbid();

            var drop = FindDrop(index);
            if (drop == null) return Results.NotFound(new { message = "掉落配置不存在" });

            string monsterName = drop.Monster?.MonsterName ?? "?";
            string itemName = drop.Item?.ItemName ?? "?";

            drop.Delete();

            WebApiLogger.Audit(context, "删除掉落", $"index={index} monster={monsterName} item={itemName}");
            return Results.Ok(new { message = $"已删除 {monsterName} 的 [{itemName}] 掉落配置" });
        }


        private static IResult BatchDeleteDrops(
            ClaimsPrincipal user,
            HttpContext context,
            BatchDeleteRequest request)
        {
            if (!JwtHelper.HasMinimumIdentity(user, AccountIdentity.SuperAdmin))
                return Results.Forbid();

            if (request?.Indexes == null || request.Indexes.Count == 0)
                return Results.BadRequest(new { message = "请选择要删除的掉落配置" });

            var list = SEnvir.DropInfoList;
            if (list == null) return Results.BadRequest(new { message = "服务器数据未就绪" });

            var set = new HashSet<int>(request.Indexes);
            int removed = 0;

            for (int i = list.Count - 1; i >= 0; i--)
            {
                var drop = list[i];
                if (drop == null || !set.Contains(drop.Index)) continue;

                drop.Delete();
                removed++;
            }

            WebApiLogger.Audit(context, "批量删除掉落", $"count={removed}");
            return Results.Ok(new { message = $"已删除 {removed} 条掉落配置" });
        }

        #region Request

        public class BatchDeleteRequest
        {
            public List<int> Indexes { get; set; }
        }
        #endregion

        #region Helpers

        private static DropInfo? FindDrop(int index)
        {
            var list = SEnvir.DropInfoList;
            if (list == null) return null;

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i]?.Index == index) return list[i];
            }

            return null;
        }

        private static (MonsterInfo?, ItemInfo?, string? error) ResolveRequest(DropRequest request)
        {
            if (request == null) return (null, null, "请求无效");

            if (request.Chance <= 0) return (null, null, "掉落概率分母必须大于 0");
            if (request.Amount <= 0) return (null, null, "掉落数量必须大于 0");

            var monsters = SEnvir.MonsterInfoList;
            MonsterInfo? monster = null;
            if (monsters != null)
            {
                for (int i = 0; i < monsters.Count; i++)
                {
                    if (monsters[i]?.Index == request.MonsterIndex) { monster = monsters[i]; break; }
                }
            }

            if (monster == null) return (null, null, "怪物不存在");

            var items = SEnvir.ItemInfoList;
            ItemInfo? item = null;
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i]?.Index == request.ItemIndex) { item = items[i]; break; }
                }
            }

            if (item == null) return (null, null, "物品不存在");

            return (monster, item, null);
        }

        private static void ApplyRequest(DropInfo drop, DropRequest request)
        {
            drop.Chance = request.Chance;
            drop.Amount = request.Amount;
            drop.DropSet = request.DropSet;
            drop.PartOnly = request.PartOnly;
            drop.EasterEvent = request.EasterEvent;
        }

        private static object ToDto(DropInfo drop)
        {
            var item = drop.Item;
            var monster = drop.Monster;

            return new
            {
                index = drop.Index,
                monsterIndex = monster?.Index ?? 0,
                monsterName = monster?.MonsterName ?? "",
                itemIndex = item?.Index ?? 0,
                itemName = item?.ItemName ?? "",
                itemType = item?.ItemType.ToString() ?? "",
                chance = drop.Chance,
                amount = drop.Amount,
                dropSet = drop.DropSet,
                partOnly = drop.PartOnly,
                easterEvent = drop.EasterEvent,
                probabilityHint = drop.Chance > 0 ? $"≈ 1/{drop.Chance}" : "-",
            };
        }

        #endregion


        public class DropRequest
        {
            public int MonsterIndex { get; set; }
            public int ItemIndex { get; set; }
            public int Chance { get; set; }
            public int Amount { get; set; } = 1;
            public int DropSet { get; set; }
            public bool PartOnly { get; set; }
            public bool EasterEvent { get; set; }
        }

        #endregion
    }
}