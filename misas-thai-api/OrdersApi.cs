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
    public static class OrdersApi
    {
        [Function("GetOrders")]
        public static async Task<HttpResponseData> GetOrders(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "orders")] HttpRequestData req,
            FunctionContext ctx)
        {
            var log = ctx.GetLogger("GetOrders");
            try
            {
                using var conn = Database.GetOpenConnection();
                var rows = await conn.QueryAsync<Order>("SELECT uuid, order_number AS OrderNumber, customer_name AS CustomerName, customer_email AS CustomerEmail, customer_phone AS CustomerPhone, consent_to_updates AS ConsentToUpdates, total, order_date AS OrderDate, payment_token AS PaymentToken, status, created_dttm AS CreatedDttm, updated_dttm AS UpdatedDttm, customer_id AS CustomerId FROM dbo.orders ORDER BY created_dttm DESC");
                var res = req.CreateResponse(HttpStatusCode.OK);
                res.Headers.Add("Content-Type", "application/json");
                await res.WriteStringAsync(JsonConvert.SerializeObject(rows));
                return res;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error fetching orders");
                var res = req.CreateResponse(HttpStatusCode.InternalServerError);
                await res.WriteStringAsync("{\"error\":\"Server error\"}");
                return res;
            }
        }

        [Function("GetOrderByUuid")]
        public static async Task<HttpResponseData> GetOrderByUuid(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "orders/{uuid}")] HttpRequestData req,
            string uuid,
            FunctionContext ctx)
        {
            var log = ctx.GetLogger("GetOrderByUuid");
            try
            {
                if (!Guid.TryParse(uuid, out var guid))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteStringAsync("{\"error\":\"Invalid uuid\"}");
                    return bad;
                }
                using var conn = Database.GetOpenConnection();
                var row = await conn.QueryFirstOrDefaultAsync<Order>("SELECT uuid, order_number AS OrderNumber, customer_name AS CustomerName, customer_email AS CustomerEmail, customer_phone AS CustomerPhone, consent_to_updates AS ConsentToUpdates, total, order_date AS OrderDate, payment_token AS PaymentToken, status, created_dttm AS CreatedDttm, updated_dttm AS UpdatedDttm, customer_id AS CustomerId FROM dbo.orders WHERE uuid = @Uuid", new { Uuid = guid });
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
                log.LogError(ex, "Error fetching order");
                var res = req.CreateResponse(HttpStatusCode.InternalServerError);
                await res.WriteStringAsync("{\"error\":\"Server error\"}");
                return res;
            }
        }

        [Function("CreateOrder")]
        public static async Task<HttpResponseData> CreateOrder(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "orders")] HttpRequestData req,
            FunctionContext ctx)
        {
            var log = ctx.GetLogger("CreateOrder");
            try
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var input = JsonConvert.DeserializeObject<CreateOrderInput>(body);
                if (input == null || string.IsNullOrWhiteSpace(input.OrderNumber))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteStringAsync("{\"error\":\"Invalid input\"}");
                    return bad;
                }
                var newUuid = Guid.NewGuid();
                using var conn = Database.GetOpenConnection();
                var sql = @"INSERT INTO dbo.orders (uuid, order_number, customer_name, customer_email, customer_phone, consent_to_updates, total, order_date, payment_token, status, created_dttm, updated_dttm, customer_id) VALUES (@Uuid, @OrderNumber, @CustomerName, @CustomerEmail, @CustomerPhone, @ConsentToUpdates, @Total, @OrderDate, @PaymentToken, @Status, SYSUTCDATETIME(), SYSUTCDATETIME(), @CustomerId);";
                await conn.ExecuteAsync(sql, new { Uuid = newUuid, OrderNumber = input.OrderNumber, CustomerName = input.CustomerName, CustomerEmail = input.CustomerEmail, CustomerPhone = input.CustomerPhone, ConsentToUpdates = input.ConsentToUpdates, Total = input.Total, OrderDate = input.OrderDate, PaymentToken = input.PaymentToken, Status = input.Status, CustomerId = input.CustomerId });
                var res = req.CreateResponse(HttpStatusCode.Created);
                res.Headers.Add("Content-Type", "application/json");
                await res.WriteStringAsync(JsonConvert.SerializeObject(new { uuid = newUuid }));
                return res;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error creating order");
                var res = req.CreateResponse(HttpStatusCode.InternalServerError);
                await res.WriteStringAsync("{\"error\":\"Server error\"}");
                return res;
            }
        }

        private class CreateOrderInput
        {
            public string OrderNumber { get; set; } = string.Empty;
            public string CustomerName { get; set; } = string.Empty;
            public string CustomerEmail { get; set; } = string.Empty;
            public string CustomerPhone { get; set; } = string.Empty;
            public bool ConsentToUpdates { get; set; }
            public decimal Total { get; set; }
            public DateTime OrderDate { get; set; }
            public string PaymentToken { get; set; } = string.Empty;
            public string Status { get; set; } = string.Empty;
            public int CustomerId { get; set; }
        }

        private class Order
        {
            public Guid Uuid { get; set; }
            public string OrderNumber { get; set; } = string.Empty;
            public string CustomerName { get; set; } = string.Empty;
            public string CustomerEmail { get; set; } = string.Empty;
            public string CustomerPhone { get; set; } = string.Empty;
            public bool ConsentToUpdates { get; set; }
            public decimal Total { get; set; }
            public DateTime OrderDate { get; set; }
            public string PaymentToken { get; set; } = string.Empty;
            public string Status { get; set; } = string.Empty;
            public DateTime CreatedDttm { get; set; }
            public DateTime UpdatedDttm { get; set; }
            public int CustomerId { get; set; }
        }
    }
}
