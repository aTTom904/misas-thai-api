using System;
using System.Threading.Tasks;
using System.Net;
using System.IO;
using System.Collections.Generic;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Dapper;
using System.Data;
using Newtonsoft.Json;

namespace misas_thai_api
{
    public static class MenuItemsApi
    {
        [Function("GetMenuItems")]
        public static async Task<HttpResponseData> GetMenuItems(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "menuitems")] HttpRequestData req,
            FunctionContext ctx)
        {
            var log = ctx.GetLogger("GetMenuItems");
            try
            {
                using var conn = Database.GetOpenConnection();
                var rows = await conn.QueryAsync<MenuItem>("SELECT uuid, item_name AS ItemName, category, price, quantity, created_dttm AS CreatedDttm, updated_dttm AS UpdatedDttm FROM dbo.menu_items ORDER BY created_dttm DESC");
                var res = req.CreateResponse(HttpStatusCode.OK);
                res.Headers.Add("Content-Type", "application/json");
                await res.WriteStringAsync(JsonConvert.SerializeObject(rows));
                return res;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error fetching menu items");
                var res = req.CreateResponse(HttpStatusCode.InternalServerError);
                await res.WriteStringAsync("{\"error\":\"Server error\"}");
                return res;
            }
        }

        [Function("GetMenuItemByUuid")]
        public static async Task<HttpResponseData> GetMenuItemByUuid(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "menuitems/{uuid}")] HttpRequestData req,
            string uuid,
            FunctionContext ctx)
        {
            var log = ctx.GetLogger("GetMenuItemByUuid");
            try
            {
                if (!Guid.TryParse(uuid, out var guid))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteStringAsync("{\"error\":\"Invalid uuid\"}");
                    return bad;
                }
                using var conn = Database.GetOpenConnection();
                var row = await conn.QueryFirstOrDefaultAsync<MenuItem>("SELECT uuid, item_name AS ItemName, category, price, quantity, created_dttm AS CreatedDttm, updated_dttm AS UpdatedDttm FROM dbo.menu_items WHERE uuid = @Uuid", new { Uuid = guid });
                if (row == null)
                {
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteStringAsync("{\"error\":\"Not found\"}");
                    return notFound;
                }
                var res = req.CreateResponse(HttpStatusCode.OK);
                res.Headers.Add("Content-Type", "application/json");
                await res.WriteStringAsync(JsonConvert.SerializeObject(row));
                return res;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error fetching menu item");
                var res = req.CreateResponse(HttpStatusCode.InternalServerError);
                await res.WriteStringAsync("{\"error\":\"Server error\"}");
                return res;
            }
        }

        [Function("CreateMenuItem")]
        public static async Task<HttpResponseData> CreateMenuItem(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "menuitems")] HttpRequestData req,
            FunctionContext ctx)
        {
            var log = ctx.GetLogger("CreateMenuItem");
            try
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var input = JsonConvert.DeserializeObject<CreateMenuItemInput>(body);
                if (input == null || string.IsNullOrWhiteSpace(input.ItemName))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteStringAsync("{\"error\":\"Invalid input\"}");
                    return bad;
                }
                var newUuid = Guid.NewGuid();
                using var conn = Database.GetOpenConnection();
                var sql = @"INSERT INTO dbo.menu_items (uuid, item_name, category, price, quantity, created_dttm, updated_dttm) VALUES (@Uuid, @ItemName, @Category, @Price, @Quantity, SYSUTCDATETIME(), SYSUTCDATETIME());";
                await conn.ExecuteAsync(sql, new { Uuid = newUuid, ItemName = input.ItemName, Category = input.Category, Price = input.Price, Quantity = input.Quantity });
                var res = req.CreateResponse(HttpStatusCode.Created);
                res.Headers.Add("Content-Type", "application/json");
                await res.WriteStringAsync(JsonConvert.SerializeObject(new { uuid = newUuid }));
                return res;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error creating menu item");
                var res = req.CreateResponse(HttpStatusCode.InternalServerError);
                await res.WriteStringAsync("{\"error\":\"Server error\"}");
                return res;
            }
        }

        private class CreateMenuItemInput
        {
            public string ItemName { get; set; } = string.Empty;
            public string Category { get; set; } = string.Empty;
            public decimal Price { get; set; }
            public int Quantity { get; set; }
        }

        private class MenuItem
        {
            public Guid Uuid { get; set; }
            public string ItemName { get; set; } = string.Empty;
            public string Category { get; set; } = string.Empty;
            public decimal Price { get; set; }
            public int Quantity { get; set; }
            public DateTime CreatedDttm { get; set; }
            public DateTime UpdatedDttm { get; set; }
        }
    }
}
