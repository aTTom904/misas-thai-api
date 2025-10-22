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
    public static class AddressVerificationApi
    {
        [Function("GetAddressVerifications")]
        public static async Task<HttpResponseData> GetAddressVerifications(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "addressverification")] HttpRequestData req,
            FunctionContext ctx)
        {
            var log = ctx.GetLogger("GetAddressVerifications");
            try
            {
                using var conn = Database.GetOpenConnection();
                var rows = await conn.QueryAsync<AddressVerification>("SELECT uuid, address, address_verified AS AddressVerified, data, create_time AS CreateTime, update_time AS UpdateTime FROM dbo.address_verification ORDER BY create_time DESC");
                var res = req.CreateResponse(HttpStatusCode.OK);
                res.Headers.Add("Content-Type", "application/json");
                await res.WriteStringAsync(JsonConvert.SerializeObject(rows));
                return res;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error fetching address verifications");
                var res = req.CreateResponse(HttpStatusCode.InternalServerError);
                await res.WriteStringAsync("{\"error\":\"Server error\"}");
                return res;
            }
        }

        [Function("GetAddressVerificationByUuid")]
        public static async Task<HttpResponseData> GetAddressVerificationByUuid(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "addressverification/{uuid}")] HttpRequestData req,
            string uuid,
            FunctionContext ctx)
        {
            var log = ctx.GetLogger("GetAddressVerificationByUuid");
            try
            {
                if (!Guid.TryParse(uuid, out var guid))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteStringAsync("{\"error\":\"Invalid uuid\"}");
                    return bad;
                }
                using var conn = Database.GetOpenConnection();
                var row = await conn.QueryFirstOrDefaultAsync<AddressVerification>("SELECT uuid, address, address_verified AS AddressVerified, data, create_time AS CreateTime, update_time AS UpdateTime FROM dbo.address_verification WHERE uuid = @Uuid", new { Uuid = guid });
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
                log.LogError(ex, "Error fetching address verification");
                var res = req.CreateResponse(HttpStatusCode.InternalServerError);
                await res.WriteStringAsync("{\"error\":\"Server error\"}");
                return res;
            }
        }

        [Function("CreateAddressVerification")]
        public static async Task<HttpResponseData> CreateAddressVerification(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "addressverification")] HttpRequestData req,
            FunctionContext ctx)
        {
            var log = ctx.GetLogger("CreateAddressVerification");
            try
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var input = JsonConvert.DeserializeObject<CreateAddressVerificationInput>(body);
                if (input == null || string.IsNullOrWhiteSpace(input.Address))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteStringAsync("{\"error\":\"Invalid input\"}");
                    return bad;
                }
                var newUuid = Guid.NewGuid();
                using var conn = Database.GetOpenConnection();
                var sql = @"INSERT INTO dbo.address_verification (uuid, address, address_verified, data, create_time, update_time) VALUES (@Uuid, @Address, @AddressVerified, @Data, SYSUTCDATETIME(), SYSUTCDATETIME());";
                await conn.ExecuteAsync(sql, new { Uuid = newUuid, Address = input.Address, AddressVerified = input.AddressVerified, Data = input.Data });
                var res = req.CreateResponse(HttpStatusCode.Created);
                res.Headers.Add("Content-Type", "application/json");
                await res.WriteStringAsync(JsonConvert.SerializeObject(new { uuid = newUuid }));
                return res;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error creating address verification");
                var res = req.CreateResponse(HttpStatusCode.InternalServerError);
                await res.WriteStringAsync("{\"error\":\"Server error\"}");
                return res;
            }
        }

        // DTOs
        private class CreateAddressVerificationInput
        {
            public string Address { get; set; } = string.Empty;
            public bool AddressVerified { get; set; }
            public string Data { get; set; } = string.Empty;
        }

        private class AddressVerification
        {
            public Guid Uuid { get; set; }
            public string Address { get; set; } = string.Empty;
            public bool AddressVerified { get; set; }
            public string Data { get; set; } = string.Empty;
            public DateTime CreateTime { get; set; }
            public DateTime UpdateTime { get; set; }
        }
    }
}
