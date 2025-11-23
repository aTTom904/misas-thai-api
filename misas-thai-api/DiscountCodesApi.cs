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
using System.Linq;

namespace misas_thai_api
{
    public static class DiscountCodesApi
    {
        [Function("GetDiscountCodes")]
        public static async Task<HttpResponseData> GetDiscountCodes(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "discount-codes")] HttpRequestData req,
            FunctionContext ctx)
        {
            var log = ctx.GetLogger("GetDiscountCodes");
            try
            {
                using var conn = Database.GetOpenConnection();
                var rows = await conn.QueryAsync<DiscountCode>(
                    "SELECT id, uuid, code, data, is_active AS IsActive, expires_dttm AS ExpiresDttm, created_dttm AS CreatedDttm, updated_dttm AS UpdatedDttm FROM dbo.discount_codes WHERE is_active = 1 ORDER BY created_dttm DESC");
                
                // Parse the data field for each row to format it properly
                var response = rows.Select(row => new
                {
                    id = row.Id,
                    uuid = row.Uuid,
                    code = row.Code,
                    data = string.IsNullOrWhiteSpace(row.Data)
                        ? null
                        : JsonConvert.DeserializeObject<DiscountCodeData>(row.Data),
                    isActive = row.IsActive,
                    expiresDttm = row.ExpiresDttm,
                    createdDttm = row.CreatedDttm,
                    updatedDttm = row.UpdatedDttm
                }).ToList();

                var res = req.CreateResponse(HttpStatusCode.OK);
                res.Headers.Add("Content-Type", "application/json");
                await res.WriteStringAsync(JsonConvert.SerializeObject(response));
                return res;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error fetching discount codes");
                var res = req.CreateResponse(HttpStatusCode.InternalServerError);
                await res.WriteStringAsync("{\"error\":\"Server error\"}");
                return res;
            }
        }

        [Function("GetDiscountCodeByCode")]
        public static async Task<HttpResponseData> GetDiscountCodeByCode(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "discount-codes/{code}")] HttpRequestData req,
            string code,
            FunctionContext ctx)
        {
            var log = ctx.GetLogger("GetDiscountCodeByCode");
            try
            {
                if (string.IsNullOrWhiteSpace(code))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteStringAsync("{\"error\":\"Invalid code\"}");
                    return bad;
                }

                using var conn = Database.GetOpenConnection();
                var row = await conn.QueryFirstOrDefaultAsync<DiscountCode>(
                    "SELECT id, uuid, code, data, is_active AS IsActive, expires_dttm AS ExpiresDttm, created_dttm AS CreatedDttm, updated_dttm AS UpdatedDttm FROM dbo.discount_codes WHERE code = @Code AND is_active = 1",
                    new { Code = code.ToUpper() });

                if (row == null)
                {
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteStringAsync("{\"error\":\"Discount code not found\"}");
                    return notFound;
                }

                // Parse the data field to format it properly
                var data = string.IsNullOrWhiteSpace(row.Data)
                    ? null
                    : JsonConvert.DeserializeObject<DiscountCodeData>(row.Data);

                var response = new
                {
                    id = row.Id,
                    uuid = row.Uuid,
                    code = row.Code,
                    data = data,
                    isActive = row.IsActive,
                    expiresDttm = row.ExpiresDttm,
                    createdDttm = row.CreatedDttm,
                    updatedDttm = row.UpdatedDttm
                };

                var res = req.CreateResponse(HttpStatusCode.OK);
                res.Headers.Add("Content-Type", "application/json");
                await res.WriteStringAsync(JsonConvert.SerializeObject(response));
                return res;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error fetching discount code");
                var res = req.CreateResponse(HttpStatusCode.InternalServerError);
                await res.WriteStringAsync("{\"error\":\"Server error\"}");
                return res;
            }
        }

        [Function("ValidateDiscountCode")]
        public static async Task<HttpResponseData> ValidateDiscountCode(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "discount-codes/validate")] HttpRequestData req,
            FunctionContext ctx)
        {
            var log = ctx.GetLogger("ValidateDiscountCode");
            try
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var input = JsonConvert.DeserializeObject<ValidateDiscountCodeInput>(body);

                if (input == null || string.IsNullOrWhiteSpace(input.Code))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteStringAsync("{\"error\":\"Invalid input\"}");
                    return bad;
                }

                using var conn = Database.GetOpenConnection();
                var discountCode = await conn.QueryFirstOrDefaultAsync<DiscountCode>(
                    "SELECT id, uuid, code, data, is_active AS IsActive, expires_dttm AS ExpiresDttm, created_dttm AS CreatedDttm, updated_dttm AS UpdatedDttm FROM dbo.discount_codes WHERE code = @Code AND is_active = 1",
                    new { Code = input.Code.ToUpper() });

                if (discountCode == null)
                {
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteStringAsync("{\"error\":\"Invalid discount code\"}");
                    return notFound;
                }

                // Check if code is expired
                if (discountCode.ExpiresDttm <= DateTime.UtcNow)
                {
                    var expired = req.CreateResponse(HttpStatusCode.BadRequest);
                    await expired.WriteStringAsync("{\"error\":\"Discount code has expired\"}");
                    return expired;
                }

                // Parse the JSON data
                var data = string.IsNullOrWhiteSpace(discountCode.Data)
                    ? null
                    : JsonConvert.DeserializeObject<DiscountCodeData>(discountCode.Data);

                if (data == null)
                {
                    var invalid = req.CreateResponse(HttpStatusCode.BadRequest);
                    await invalid.WriteStringAsync("{\"error\":\"Invalid discount code data\"}");
                    return invalid;
                }

                // Validate minimum order amount
                if (data.MinimumOrderAmount.HasValue && input.OrderAmount < data.MinimumOrderAmount.Value)
                {
                    var minOrder = req.CreateResponse(HttpStatusCode.BadRequest);
                    await minOrder.WriteStringAsync(JsonConvert.SerializeObject(new
                    {
                        error = $"Minimum order amount of ${data.MinimumOrderAmount.Value:F2} required"
                    }));
                    return minOrder;
                }

                // Check max uses
                if (data.MaxUses.HasValue && data.CurrentUses >= data.MaxUses.Value)
                {
                    var maxUsed = req.CreateResponse(HttpStatusCode.BadRequest);
                    await maxUsed.WriteStringAsync("{\"error\":\"Discount code has reached maximum uses\"}");
                    return maxUsed;
                }

                // Calculate discount amount
                decimal discountAmount = 0;
                if (data.DiscountType.ToLower() == "percentage")
                {
                    discountAmount = Math.Round(input.OrderAmount * (data.DiscountValue / 100), 2);
                }
                else if (data.DiscountType.ToLower() == "fixed")
                {
                    discountAmount = data.DiscountValue;
                }

                var res = req.CreateResponse(HttpStatusCode.OK);
                res.Headers.Add("Content-Type", "application/json");
                await res.WriteStringAsync(JsonConvert.SerializeObject(new
                {
                    isValid = true,
                    code = discountCode.Code,
                    discountAmount = discountAmount,
                    description = data.Description,
                    discountType = data.DiscountType
                }));
                return res;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error validating discount code");
                var res = req.CreateResponse(HttpStatusCode.InternalServerError);
                await res.WriteStringAsync("{\"error\":\"Server error\"}");
                return res;
            }
        }

        [Function("CreateDiscountCode")]
        public static async Task<HttpResponseData> CreateDiscountCode(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "discount-codes")] HttpRequestData req,
            FunctionContext ctx)
        {
            var log = ctx.GetLogger("CreateDiscountCode");
            try
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var input = JsonConvert.DeserializeObject<CreateDiscountCodeInput>(body);

                if (input == null || string.IsNullOrWhiteSpace(input.Code))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteStringAsync("{\"error\":\"Invalid input\"}");
                    return bad;
                }

                if (input.ExpiresDttm <= DateTime.UtcNow)
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteStringAsync("{\"error\":\"Expiration date must be in the future\"}");
                    return bad;
                }

                var data = new DiscountCodeData
                {
                    DiscountType = input.DiscountType ?? "percentage",
                    DiscountValue = input.DiscountValue,
                    MinimumOrderAmount = input.MinimumOrderAmount,
                    MaxUses = input.MaxUses,
                    CurrentUses = 0,
                    Description = input.Description
                };

                var dataJson = JsonConvert.SerializeObject(data);
                var newUuid = Guid.NewGuid();

                using var conn = Database.GetOpenConnection();
                var sql = @"INSERT INTO dbo.discount_codes (uuid, code, data, is_active, expires_dttm, created_dttm, updated_dttm) 
                           VALUES (@Uuid, @Code, @Data, 1, @ExpiresDttm, SYSUTCDATETIME(), SYSUTCDATETIME());";

                await conn.ExecuteAsync(sql, new
                {
                    Uuid = newUuid,
                    Code = input.Code.ToUpper(),
                    Data = dataJson,
                    ExpiresDttm = input.ExpiresDttm
                });

                var res = req.CreateResponse(HttpStatusCode.Created);
                res.Headers.Add("Content-Type", "application/json");
                await res.WriteStringAsync(JsonConvert.SerializeObject(new { uuid = newUuid, code = input.Code.ToUpper() }));
                return res;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error creating discount code");
                var res = req.CreateResponse(HttpStatusCode.InternalServerError);
                await res.WriteStringAsync("{\"error\":\"Server error\"}");
                return res;
            }
        }

        [Function("UpdateDiscountCode")]
        public static async Task<HttpResponseData> UpdateDiscountCode(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "discount-codes/{code}")] HttpRequestData req,
            string code,
            FunctionContext ctx)
        {
            var log = ctx.GetLogger("UpdateDiscountCode");
            try
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var input = JsonConvert.DeserializeObject<UpdateDiscountCodeInput>(body);

                if (input == null)
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteStringAsync("{\"error\":\"Invalid input\"}");
                    return bad;
                }

                using var conn = Database.GetOpenConnection();

                // Get existing discount code
                var existing = await conn.QueryFirstOrDefaultAsync<DiscountCode>(
                    "SELECT id, uuid, code, data, is_active AS IsActive, expires_dttm AS ExpiresDttm, created_dttm AS CreatedDttm, updated_dttm AS UpdatedDttm FROM dbo.discount_codes WHERE code = @Code",
                    new { Code = code.ToUpper() });

                if (existing == null)
                {
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteStringAsync("{\"error\":\"Discount code not found\"}");
                    return notFound;
                }

                // Parse existing data
                var data = string.IsNullOrWhiteSpace(existing.Data)
                    ? new DiscountCodeData()
                    : JsonConvert.DeserializeObject<DiscountCodeData>(existing.Data);

                // Update fields if provided
                if (input.DiscountType != null) data.DiscountType = input.DiscountType;
                if (input.DiscountValue.HasValue) data.DiscountValue = input.DiscountValue.Value;
                if (input.MinimumOrderAmount.HasValue) data.MinimumOrderAmount = input.MinimumOrderAmount;
                if (input.MaxUses.HasValue) data.MaxUses = input.MaxUses;
                if (input.Description != null) data.Description = input.Description;

                var dataJson = JsonConvert.SerializeObject(data);

                var sql = "UPDATE dbo.discount_codes SET data = @Data, is_active = @IsActive, expires_dttm = @ExpiresDttm WHERE code = @Code";
                await conn.ExecuteAsync(sql, new
                {
                    Data = dataJson,
                    IsActive = input.IsActive ?? existing.IsActive,
                    ExpiresDttm = input.ExpiresDttm ?? existing.ExpiresDttm,
                    Code = code.ToUpper()
                });

                var res = req.CreateResponse(HttpStatusCode.OK);
                res.Headers.Add("Content-Type", "application/json");
                await res.WriteStringAsync("{\"message\":\"Discount code updated successfully\"}");
                return res;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error updating discount code");
                var res = req.CreateResponse(HttpStatusCode.InternalServerError);
                await res.WriteStringAsync("{\"error\":\"Server error\"}");
                return res;
            }
        }

        [Function("IncrementDiscountCodeUsage")]
        public static async Task<HttpResponseData> IncrementDiscountCodeUsage(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "discount-codes/{code}/increment")] HttpRequestData req,
            string code,
            FunctionContext ctx)
        {
            var log = ctx.GetLogger("IncrementDiscountCodeUsage");
            try
            {
                using var conn = Database.GetOpenConnection();

                var discountCode = await conn.QueryFirstOrDefaultAsync<DiscountCode>(
                    "SELECT id, uuid, code, data, is_active AS IsActive, expires_dttm AS ExpiresDttm, created_dttm AS CreatedDttm, updated_dttm AS UpdatedDttm FROM dbo.discount_codes WHERE code = @Code AND is_active = 1",
                    new { Code = code.ToUpper() });

                if (discountCode == null)
                {
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteStringAsync("{\"error\":\"Discount code not found\"}");
                    return notFound;
                }

                var data = string.IsNullOrWhiteSpace(discountCode.Data)
                    ? new DiscountCodeData()
                    : JsonConvert.DeserializeObject<DiscountCodeData>(discountCode.Data);

                data.CurrentUses++;
                var dataJson = JsonConvert.SerializeObject(data);

                await conn.ExecuteAsync(
                    "UPDATE dbo.discount_codes SET data = @Data WHERE code = @Code",
                    new { Data = dataJson, Code = code.ToUpper() });

                var res = req.CreateResponse(HttpStatusCode.OK);
                res.Headers.Add("Content-Type", "application/json");
                await res.WriteStringAsync(JsonConvert.SerializeObject(new
                {
                    message = "Usage incremented",
                    currentUses = data.CurrentUses
                }));
                return res;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error incrementing discount code usage");
                var res = req.CreateResponse(HttpStatusCode.InternalServerError);
                await res.WriteStringAsync("{\"error\":\"Server error\"}");
                return res;
            }
        }

        [Function("DeleteDiscountCode")]
        public static async Task<HttpResponseData> DeleteDiscountCode(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "discount-codes/{code}")] HttpRequestData req,
            string code,
            FunctionContext ctx)
        {
            var log = ctx.GetLogger("DeleteDiscountCode");
            try
            {
                using var conn = Database.GetOpenConnection();

                // Soft delete by setting is_active to 0
                var rowsAffected = await conn.ExecuteAsync(
                    "UPDATE dbo.discount_codes SET is_active = 0 WHERE code = @Code",
                    new { Code = code.ToUpper() });

                if (rowsAffected == 0)
                {
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteStringAsync("{\"error\":\"Discount code not found\"}");
                    return notFound;
                }

                var res = req.CreateResponse(HttpStatusCode.OK);
                await res.WriteStringAsync("{\"message\":\"Discount code deleted successfully\"}");
                return res;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error deleting discount code");
                var res = req.CreateResponse(HttpStatusCode.InternalServerError);
                await res.WriteStringAsync("{\"error\":\"Server error\"}");
                return res;
            }
        }

        private class DiscountCode
        {
            public int Id { get; set; }
            public Guid Uuid { get; set; }
            public string Code { get; set; } = string.Empty;
            public string Data { get; set; } = string.Empty;
            public bool IsActive { get; set; }
            public DateTime ExpiresDttm { get; set; }
            public DateTime CreatedDttm { get; set; }
            public DateTime UpdatedDttm { get; set; }
        }

        private class DiscountCodeData
        {
            public string DiscountType { get; set; } = "percentage";
            public decimal DiscountValue { get; set; }
            public decimal? MinimumOrderAmount { get; set; }
            public int? MaxUses { get; set; }
            public int CurrentUses { get; set; } = 0;
            public string? Description { get; set; }
        }

        private class ValidateDiscountCodeInput
        {
            public string Code { get; set; } = string.Empty;
            public decimal OrderAmount { get; set; }
        }

        private class CreateDiscountCodeInput
        {
            public string Code { get; set; } = string.Empty;
            public string? DiscountType { get; set; }
            public decimal DiscountValue { get; set; }
            public decimal? MinimumOrderAmount { get; set; }
            public int? MaxUses { get; set; }
            public string? Description { get; set; }
            public DateTime ExpiresDttm { get; set; }
        }

        private class UpdateDiscountCodeInput
        {
            public string? DiscountType { get; set; }
            public decimal? DiscountValue { get; set; }
            public decimal? MinimumOrderAmount { get; set; }
            public int? MaxUses { get; set; }
            public string? Description { get; set; }
            public DateTime? ExpiresDttm { get; set; }
            public bool? IsActive { get; set; }
        }
    }
}
