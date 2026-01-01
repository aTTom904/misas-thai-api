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
                
                if (input == null || string.IsNullOrWhiteSpace(input.CustomerEmail) || string.IsNullOrWhiteSpace(input.CustomerPhone))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteStringAsync("{\"error\":\"Invalid input - email and phone required\"}");
                    return bad;
                }

                using var conn = Database.GetOpenConnection();
                using var transaction = conn.BeginTransaction();
                
                try
                {
                    // Step 1: Find existing customer with hierarchical lookup
                    CustomerRecord existingCustomer = null;
                    
                    // Try email + phone first
                    existingCustomer = await conn.QueryFirstOrDefaultAsync<CustomerRecord>(
                        @"SELECT id, uuid, name, email, phone, consent_to_updates AS ConsentToUpdates, data 
                          FROM dbo.customers 
                          WHERE email = @Email AND phone = @Phone",
                        new { Email = input.CustomerEmail, Phone = input.CustomerPhone },
                        transaction);
                    
                    // Fallback to email only
                    if (existingCustomer == null)
                    {
                        existingCustomer = await conn.QueryFirstOrDefaultAsync<CustomerRecord>(
                            @"SELECT id, uuid, name, email, phone, consent_to_updates AS ConsentToUpdates, data 
                              FROM dbo.customers 
                              WHERE email = @Email",
                            new { Email = input.CustomerEmail },
                            transaction);
                    }
                    
                    // Fallback to phone only
                    if (existingCustomer == null)
                    {
                        existingCustomer = await conn.QueryFirstOrDefaultAsync<CustomerRecord>(
                            @"SELECT id, uuid, name, email, phone, consent_to_updates AS ConsentToUpdates, data 
                              FROM dbo.customers 
                              WHERE phone = @Phone",
                            new { Phone = input.CustomerPhone },
                            transaction);
                    }

                    int customerId;
                    Guid customerUuid;

                    if (existingCustomer != null)
                    {
                        // Customer exists - update it
                        customerId = existingCustomer.Id;
                        customerUuid = existingCustomer.Uuid;
                        
                        // Parse existing data or start fresh
                        CustomerStats currentStats = new CustomerStats { NumberOfOrders = 0, TotalSpent = 0 };
                        if (!string.IsNullOrWhiteSpace(existingCustomer.Data))
                        {
                            try
                            {
                                currentStats = JsonConvert.DeserializeObject<CustomerStats>(existingCustomer.Data) ?? currentStats;
                            }
                            catch (Exception ex)
                            {
                                log.LogWarning(ex, $"Could not parse existing customer data for customer {customerId}, starting fresh");
                            }
                        }
                        
                        // Increment with current order
                        var updatedStats = new CustomerStats
                        {
                            NumberOfOrders = currentStats.NumberOfOrders + 1,
                            TotalSpent = currentStats.TotalSpent + input.OrderTotal
                        };
                        
                        var dataJson = JsonConvert.SerializeObject(updatedStats);
                        
                        // Update customer with latest info and incremented stats
                        await conn.ExecuteAsync(
                            @"UPDATE dbo.customers 
                              SET name = @Name, 
                                  email = @Email, 
                                  phone = @Phone, 
                                  consent_to_updates = @ConsentToUpdates, 
                                  data = @Data, 
                                  updated_dttm = SYSUTCDATETIME() 
                              WHERE id = @Id",
                            new { 
                                Id = customerId,
                                Name = input.CustomerName,
                                Email = input.CustomerEmail,
                                Phone = input.CustomerPhone,
                                ConsentToUpdates = input.ConsentToUpdates,
                                Data = dataJson
                            },
                            transaction);
                        
                        log.LogInformation($"Updated existing customer {customerId}. Orders: {updatedStats.NumberOfOrders}, Total spent: ${updatedStats.TotalSpent}");
                    }
                    else
                    {
                        // New customer - insert with first order stats
                        customerUuid = Guid.NewGuid();
                        
                        var initialStats = new CustomerStats
                        {
                            NumberOfOrders = 1,
                            TotalSpent = input.OrderTotal
                        };
                        
                        var dataJson = JsonConvert.SerializeObject(initialStats);
                        
                        await conn.ExecuteAsync(
                            @"INSERT INTO dbo.customers (uuid, name, email, phone, consent_to_updates, data, created_dttm, updated_dttm) 
                              VALUES (@Uuid, @Name, @Email, @Phone, @ConsentToUpdates, @Data, SYSUTCDATETIME(), SYSUTCDATETIME())",
                            new { 
                                Uuid = customerUuid,
                                Name = input.CustomerName,
                                Email = input.CustomerEmail,
                                Phone = input.CustomerPhone,
                                ConsentToUpdates = input.ConsentToUpdates,
                                Data = dataJson
                            },
                            transaction);
                        
                        // Get the inserted customer ID
                        customerId = await conn.QuerySingleAsync<int>(
                            "SELECT id FROM dbo.customers WHERE uuid = @Uuid",
                            new { Uuid = customerUuid },
                            transaction);
                        
                        log.LogInformation($"Created new customer {customerId}. First order: ${input.OrderTotal}");
                    }

                    // Step 2: Create the order
                    var orderUuid = Guid.NewGuid();
                    
                    // Build order data JSON with items and additional info
                    var orderData = new
                    {
                        items = input.Items,
                        additionalInfo = input.AdditionalInformation ?? "",
                        paymentToken = input.PaymentToken ?? "",
                        salesTax = input.SalesTax,
                        discountCode = input.DiscountCode ?? ""
                    };
                    
                    var orderDataJson = JsonConvert.SerializeObject(orderData);
                    
                    // Parse delivery date - remove day of week in parentheses if present
                    var deliveryDateStr = input.DeliveryDate;
                    var parenIndex = deliveryDateStr.IndexOf('(');
                    if (parenIndex > 0)
                    {
                        deliveryDateStr = deliveryDateStr.Substring(0, parenIndex).Trim();
                    }
                    
                    await conn.ExecuteAsync(
                        @"INSERT INTO dbo.orders 
                          (uuid, customer_id, customer_name, customer_email, customer_phone, 
                           delivery_address, delivery_date, order_total, tip, discount, data, 
                           created_dttm, updated_dttm) 
                          VALUES 
                          (@Uuid, @CustomerId, @CustomerName, @CustomerEmail, @CustomerPhone, 
                           @DeliveryAddress, @DeliveryDate, @OrderTotal, @Tip, @Discount, @Data, 
                           SYSUTCDATETIME(), SYSUTCDATETIME())",
                        new { 
                            Uuid = orderUuid,
                            CustomerId = customerId,
                            CustomerName = input.CustomerName,
                            CustomerEmail = input.CustomerEmail,
                            CustomerPhone = input.CustomerPhone,
                            DeliveryAddress = input.DeliveryAddress,
                            DeliveryDate = DateTime.Parse(deliveryDateStr),
                            OrderTotal = input.OrderTotal,
                            Tip = input.TipAmount,
                            Discount = input.DiscountAmount,
                            Data = orderDataJson
                        },
                        transaction);
                    
                    transaction.Commit();
                    
                    log.LogInformation($"Successfully created order {orderUuid} for customer {customerId}");
                    
                    var res = req.CreateResponse(HttpStatusCode.Created);
                    res.Headers.Add("Content-Type", "application/json");
                    await res.WriteStringAsync(JsonConvert.SerializeObject(new { 
                        orderUuid = orderUuid,
                        customerId = customerId,
                        customerUuid = customerUuid,
                        success = true
                    }));
                    return res;
                }
                catch (Exception)
                {
                    transaction.Rollback();
                    throw;
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error creating order and upserting customer");
                var res = req.CreateResponse(HttpStatusCode.InternalServerError);
                await res.WriteStringAsync("{\"error\":\"Server error\"}");
                return res;
            }
        }

        private class CreateOrderInput
        {
            // Customer fields
            public string CustomerName { get; set; } = string.Empty;
            public string CustomerEmail { get; set; } = string.Empty;
            public string CustomerPhone { get; set; } = string.Empty;
            public bool ConsentToUpdates { get; set; }
            
            // Order fields
            public string DeliveryAddress { get; set; } = string.Empty;
            public string DeliveryDate { get; set; } = string.Empty;
            public decimal OrderTotal { get; set; }
            public decimal TipAmount { get; set; }
            public decimal DiscountAmount { get; set; }
            public decimal SalesTax { get; set; }
            public string? AdditionalInformation { get; set; }
            public string? PaymentToken { get; set; }
            public string? DiscountCode { get; set; }
            public List<OrderItemData> Items { get; set; } = new();
        }

        private class OrderItemData
        {
            public string ItemName { get; set; } = string.Empty;
            public string Category { get; set; } = string.Empty;
            public decimal Price { get; set; }
            public int Quantity { get; set; }
            public string? SelectedServes { get; set; }
            public string? SelectedSize { get; set; }
            public int? UpgradePhadThai24Qty { get; set; }
            public int? UpgradePhadThai48Qty { get; set; }
            public int? AddOnQty { get; set; }
        }

        private class CustomerRecord
        {
            public int Id { get; set; }
            public Guid Uuid { get; set; }
            public string Name { get; set; } = string.Empty;
            public string Email { get; set; } = string.Empty;
            public string Phone { get; set; } = string.Empty;
            public bool ConsentToUpdates { get; set; }
            public string? Data { get; set; }
        }

        private class CustomerStats
        {
            [JsonProperty("number_of_orders")]
            public int NumberOfOrders { get; set; }
            
            [JsonProperty("total_spent")]
            public decimal TotalSpent { get; set; }
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
