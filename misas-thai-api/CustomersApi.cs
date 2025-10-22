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
    public static class CustomersApi
    {
        [Function("GetCustomers")]
        public static async Task<HttpResponseData> GetCustomers(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "customers")] HttpRequestData req,
            FunctionContext ctx)
        {
            var log = ctx.GetLogger("GetCustomers");
            try
            {
                using var conn = Database.GetOpenConnection();
                var rows = await conn.QueryAsync<Person>("SELECT uuid, name, email, phone, consent_to_updates AS ConsentToUpdates, created_dttm AS CreatedDttm FROM dbo.customers ORDER BY created_dttm DESC");
                var res = req.CreateResponse(HttpStatusCode.OK);
                res.Headers.Add("Content-Type", "application/json");
                await res.WriteStringAsync(JsonConvert.SerializeObject(rows));
                return res;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error fetching customers");
                var res = req.CreateResponse(HttpStatusCode.InternalServerError);
                await res.WriteStringAsync("{\"error\":\"Server error\"}");
                return res;
            }
        }

        [Function("GetCustomerByUuid")]
        public static async Task<HttpResponseData> GetCustomerByUuid(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "customers/{uuid}")] HttpRequestData req,
            string uuid,
            FunctionContext ctx)
        {
            var log = ctx.GetLogger("GetCustomerByUuid");
            try
            {
                if (!Guid.TryParse(uuid, out var guid))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteStringAsync("{\"error\":\"Invalid uuid\"}");
                    return bad;
                }
                using var conn = Database.GetOpenConnection();
                var row = await conn.QueryFirstOrDefaultAsync<Person>("SELECT uuid, name, email, phone, consent_to_updates AS ConsentToUpdates, created_dttm AS CreatedDttm FROM dbo.customers WHERE uuid = @Uuid", new { Uuid = guid });
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
                log.LogError(ex, "Error fetching customer");
                var res = req.CreateResponse(HttpStatusCode.InternalServerError);
                await res.WriteStringAsync("{\"error\":\"Server error\"}");
                return res;
            }
        }

        [Function("CreateCustomer")]
        public static async Task<HttpResponseData> CreateCustomer(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "customers")] HttpRequestData req,
            FunctionContext ctx)
        {
            var log = ctx.GetLogger("CreateCustomer");
            try
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var input = JsonConvert.DeserializeObject<CreateCustomerInput>(body);
                if (input == null || string.IsNullOrWhiteSpace(input.Name))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteStringAsync("{\"error\":\"Invalid input\"}");
                    return bad;
                }
                var newUuid = Guid.NewGuid();
                using var conn = Database.GetOpenConnection();
                var sql = @"INSERT INTO dbo.customers (uuid, name, email, phone, consent_to_updates, created_dttm) VALUES (@Uuid, @Name, @Email, @Phone, @ConsentToUpdates, SYSUTCDATETIME());";
                await conn.ExecuteAsync(sql, new { Uuid = newUuid, Name = input.Name, Email = input.Email, Phone = input.Phone, ConsentToUpdates = input.ConsentToUpdates });
                var res = req.CreateResponse(HttpStatusCode.Created);
                res.Headers.Add("Content-Type", "application/json");
                await res.WriteStringAsync(JsonConvert.SerializeObject(new { uuid = newUuid }));
                return res;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error creating customer");
                var res = req.CreateResponse(HttpStatusCode.InternalServerError);
                await res.WriteStringAsync("{\"error\":\"Server error\"}");
                return res;
            }
        }

        private class CreateCustomerInput
        {
            public string Name { get; set; } = string.Empty;
            public string Email { get; set; } = string.Empty;
            public string Phone { get; set; } = string.Empty;
            public bool ConsentToUpdates { get; set; }
        }

        private class Person
        {
            public Guid Uuid { get; set; }
            public string Name { get; set; } = string.Empty;
            public string Email { get; set; } = string.Empty;
            public string Phone { get; set; } = string.Empty;
            public bool ConsentToUpdates { get; set; }
            public DateTime CreatedDttm { get; set; }
        }
    }
}
