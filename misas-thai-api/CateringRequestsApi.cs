using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using Newtonsoft.Json;
using System.Data.SqlClient;
using System.Linq;
using System.Collections.Generic;
using Dapper;

namespace misas_thai_api
{
    public static class CateringRequestsApi
    {
            // HTML email body styled like TakePayment.cs
            private static string CreateCateringRequestEmailHtml(CateringRequestDto request)
            {
                var itemsHtml = string.Empty;
                foreach (var item in request.CartItems)
                {
                    var servesText = item.SelectedServes.HasValue ? $" (Serves {item.SelectedServes.Value})" : "";
                    var sizeText = !string.IsNullOrEmpty(item.SelectedSize) ? $" ({item.SelectedSize} Tray)" : "";
                    itemsHtml += $"<tr><td>{item.ItemName}{servesText}{sizeText}</td><td>{item.Quantity}</td><td>${item.Price:F2}</td><td>${(item.Price * item.Quantity):F2}</td></tr>";
                    if (item.UpgradePhadThai48Qty > 0)
                    {
                        var upgrade48Price = 24m;
                        var upgrade48Total = item.UpgradePhadThai48Qty * upgrade48Price;
                        itemsHtml += $"<tr><td>Upgrade: Pad Thai (48 oz)</td><td>{item.UpgradePhadThai48Qty}</td><td>${upgrade48Price:F2}</td><td>${upgrade48Total:F2}</td></tr>";
                    }
                    if (item.UpgradePhadThai24Qty > 0)
                    {
                        var upgrade24Price = 12m;
                        var upgrade24Total = item.UpgradePhadThai24Qty * upgrade24Price;
                        itemsHtml += $"<tr><td>Upgrade: Pad Thai (24 oz)</td><td>{item.UpgradePhadThai24Qty}</td><td>${upgrade24Price:F2}</td><td>${upgrade24Total:F2}</td></tr>";
                    }
                    if (item.AddOnQty > 0)
                    {
                        var addOnPrice = item.SelectedSize == "Half" ? 15m : 25m;
                        var addOnSize = item.SelectedSize == "Half" ? "16oz" : "32oz";
                        var addOnName = item.ItemName == "Sai Ua Sausage" ? "Prik Noom Sauce" : "Jao Sauce";
                        var addOnTotal = item.AddOnQty * addOnPrice;
                        itemsHtml += $"<tr><td>Add-on: {addOnName} ({addOnSize})</td><td>{item.AddOnQty}</td><td>${addOnPrice:F2}</td><td>${addOnTotal:F2}</td></tr>";
                    }
                }

                var grandTotal = request.TotalPrice;

                return $@"
        <html>
        <body style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;'>
            <div style='font-size: 2.2em; color: #ee6900; font-weight: bold; text-align: center; margin-top: 30px; margin-bottom: 20px;'>
                Thank you for your catering request!
            </div>
            <p>Hi {request.CustomerName},</p>
            <p>Thank you for submitting your catering request! We have received your details and will contact you soon to confirm availability and finalize your order.</p>
            <h3>📅 Event Details</h3>
            <p><strong>Event Address:</strong> {request.StreetAddress}</p>
            <p><strong>Requested Date:</strong> {request.RequestedDate:MMMM dd, yyyy}</p>
            <p><strong>Event Info:</strong> {request.EventDetails}</p>
            <h3>📦 Order Summary</h3>
            <table style='width: 100%; border-collapse: collapse; margin: 20px 0;'>
                <thead>
                    <tr style='background-color: #f8f9fa;'>
                        <th style='border: 1px solid #ddd; padding: 8px; text-align: left;'>Item</th>
                        <th style='border: 1px solid #ddd; padding: 8px; text-align: center;'>Qty</th>
                        <th style='border: 1px solid #ddd; padding: 8px; text-align: right;'>Price</th>
                        <th style='border: 1px solid #ddd; padding: 8px; text-align: right;'>Total</th>
                    </tr>
                </thead>
                <tbody>
                    {itemsHtml}
                    <tr style='background-color: #f8f9fa;'>
                        <td colspan='3' style='text-align: right; font-weight: bold;'>Total: </td>
                        <td style='font-weight: bold;'>${grandTotal:F2}</td>
                    </tr>
                </tbody>
            </table>
            {(string.IsNullOrEmpty(request.SpecialInstructions) ? "" : $"<h3>Special Instructions</h3><p style='background-color: #f8f9fa; padding: 15px; border-left: 4px solid #ee6900; margin: 20px 0;'>{request.SpecialInstructions}</p>")}
            <h3>👤 Customer Info</h3>
            <p><strong>Name:</strong> {request.CustomerName}<br/>
            <strong>Email:</strong> {request.CustomerEmail}<br/>
            <strong>Phone:</strong> {request.CustomerPhone}</p>
            <h3>💬 Questions?</h3>
            <p>We’re happy to help! Just email us at msthaistreetcuisine@gmail.com or text us at 904-315-4884.</p>
            <p>We can’t wait to help make your event special with our Thai cuisine! 🧡</p>
            <p>Warmly,<br/>
            Misa’s Thai Street Cuisine<br/>
            1301 N Orange Ave, Suite 102<br/>
            Green Cove Springs, FL 32043<br/>
            msthaistreetcuisine@gmail.com<br/>
            904-315-4884</p>
        </body>
        </html>";
            }

            // Plain text email body styled like TakePayment.cs
            private static string CreateCateringRequestEmailText(CateringRequestDto request)
            {
                var itemsLines = new System.Collections.Generic.List<string>();
                foreach (var item in request.CartItems)
                {
                    var servesText = item.SelectedServes.HasValue ? $" (Serves {item.SelectedServes.Value})" : "";
                    var sizeText = !string.IsNullOrEmpty(item.SelectedSize) ? $" ({item.SelectedSize} Tray)" : "";
                    itemsLines.Add($"{item.ItemName}{servesText}{sizeText}\t{item.Quantity}\t${item.Price:F2}\t${(item.Price * item.Quantity):F2}");
                    if (item.UpgradePhadThai48Qty > 0)
                    {
                        var upgrade48Price = 18m;
                        var upgrade48Total = item.UpgradePhadThai48Qty * upgrade48Price;
                        itemsLines.Add($"Upgrade: Pad Thai (48 oz)\t{item.UpgradePhadThai48Qty}\t${upgrade48Price:F2}\t${upgrade48Total:F2}");
                    }
                    if (item.UpgradePhadThai24Qty > 0)
                    {
                        var upgrade24Price = 9m;
                        var upgrade24Total = item.UpgradePhadThai24Qty * upgrade24Price;
                        itemsLines.Add($"Upgrade: Pad Thai (24 oz)\t{item.UpgradePhadThai24Qty}\t${upgrade24Price:F2}\t${upgrade24Total:F2}");
                    }
                    if (item.AddOnQty > 0)
                    {
                        var addOnPrice = item.SelectedSize == "Half" ? 15m : 25m;
                        var addOnSize = item.SelectedSize == "Half" ? "16oz" : "32oz";
                        var addOnName = item.ItemName == "Sai Ua Sausage" ? "Prik Noom Sauce" : "Jao Sauce";
                        var addOnTotal = item.AddOnQty * addOnPrice;
                        itemsLines.Add($"Add-on: {addOnName} ({addOnSize})\t{item.AddOnQty}\t${addOnPrice:F2}\t${addOnTotal:F2}");
                    }
                }

                var grandTotal = request.TotalPrice;
                var itemsText = string.Join("\n", itemsLines);

                return $@"
        ==============================
        THANK YOU FOR YOUR CATERING REQUEST
        ==============================

        Hi {request.CustomerName},

        Thank you for submitting your catering request! We have received your details and will contact you soon to confirm availability and finalize your order.

        📅 Event Details
        Event Address: {request.StreetAddress}
        Requested Date: {request.RequestedDate:MMMM dd, yyyy}
        Event Info: {request.EventDetails}

        📦 Order Summary
        Item\tQty\tPrice\tTotal
        {itemsText}
        Total: ${grandTotal:F2}

        {(string.IsNullOrEmpty(request.SpecialInstructions) ? "" :
            "------------------------------\n" +
            "SPECIAL INSTRUCTIONS PROVIDED BY YOU:\n" +
            request.SpecialInstructions +
            "\n------------------------------\n")}

        👤 Customer Info
        Name: {request.CustomerName}
        Email: {request.CustomerEmail}
        Phone: {request.CustomerPhone}

        💬 Questions?
        We’re happy to help! Just email us at msthaistreetcuisine@gmail.com or text us at 904-315-4884.

        We can’t wait to help make your event special with our Thai cuisine! 🧡

        Warmly,
        Misa’s Thai Street Cuisine
        1301 N Orange Ave, Suite 102
        Green Cove Springs, FL 32043
        msthaistreetcuisine@gmail.com
        904-315-4884
        ";
            }
        [Function("CateringRequestsApi")]
        public static async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestData req,
            FunctionContext executionContext)
        {
            var log = executionContext.GetLogger("CateringRequestsApi");
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var cateringRequest = JsonConvert.DeserializeObject<CateringRequestDto>(requestBody);
            if (cateringRequest == null)
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("{\"error\":\"Invalid request\"}");
                return bad;
            }

            using var conn = Database.GetOpenConnection();
            using var transaction = conn.BeginTransaction();

            // Upsert customer (inline, similar to OrdersApi)
            var customer = (await conn.QueryAsync<dynamic>(
                @"SELECT id FROM dbo.customers WHERE email = @Email AND phone = @Phone",
                new { Email = cateringRequest.CustomerEmail, Phone = cateringRequest.CustomerPhone }, transaction)).FirstOrDefault();
            if (customer == null)
            {
                customer = (await conn.QueryAsync<dynamic>(
                    @"SELECT id FROM dbo.customers WHERE email = @Email",
                    new { Email = cateringRequest.CustomerEmail }, transaction)).FirstOrDefault();
            }
            if (customer == null)
            {
                customer = (await conn.QueryAsync<dynamic>(
                    @"SELECT id FROM dbo.customers WHERE phone = @Phone",
                    new { Phone = cateringRequest.CustomerPhone }, transaction)).FirstOrDefault();
            }

            int customerId;
            if (customer != null)
            {
                customerId = customer.id;
                // Read and update data column
                var customerData = await conn.QueryFirstOrDefaultAsync<string>(
                    "SELECT data FROM dbo.customers WHERE id = @Id",
                    new { Id = customerId }, transaction);
                dynamic dataObj = null;
                if (!string.IsNullOrWhiteSpace(customerData))
                {
                    try { dataObj = JsonConvert.DeserializeObject<dynamic>(customerData); } catch { }
                }
                if (dataObj == null) dataObj = new System.Dynamic.ExpandoObject();
                // Add/increment number_of_catering_requests
                if (dataObj.number_of_catering_requests == null)
                    dataObj.number_of_catering_requests = 1;
                else
                    dataObj.number_of_catering_requests = (int)dataObj.number_of_catering_requests + 1;
                // Add to total_spent
                if (dataObj.total_spent == null)
                    dataObj.total_spent = cateringRequest.TotalPrice;
                else
                    dataObj.total_spent = (decimal)dataObj.total_spent + cateringRequest.TotalPrice;
                var newDataJson = JsonConvert.SerializeObject(dataObj);
                await conn.ExecuteAsync(
                    @"UPDATE dbo.customers SET name = @Name, email = @Email, phone = @Phone, data = @Data, updated_dttm = SYSUTCDATETIME() WHERE id = @Id",
                    new { Id = customerId, Name = cateringRequest.CustomerName, Email = cateringRequest.CustomerEmail, Phone = cateringRequest.CustomerPhone, Data = newDataJson }, transaction);
            }
            else
            {
                var uuid = Guid.NewGuid();
                // Create initial data JSON
                var dataObj = new System.Dynamic.ExpandoObject() as IDictionary<string, object>;
                dataObj["number_of_orders"] = 0;
                dataObj["total_spent"] = cateringRequest.TotalPrice;
                dataObj["loyalty_reward_available"] = false;
                dataObj["number_of_catering_requests"] = 1;
                var newDataJson = JsonConvert.SerializeObject(dataObj);
                await conn.ExecuteAsync(
                    @"INSERT INTO dbo.customers (uuid, name, email, phone, data, created_dttm, updated_dttm) VALUES (@Uuid, @Name, @Email, @Phone, @Data, SYSUTCDATETIME(), SYSUTCDATETIME())",
                    new { Uuid = uuid, Name = cateringRequest.CustomerName, Email = cateringRequest.CustomerEmail, Phone = cateringRequest.CustomerPhone, Data = newDataJson }, transaction);
                customerId = await conn.QuerySingleAsync<int>(
                    "SELECT id FROM dbo.customers WHERE uuid = @Uuid",
                    new { Uuid = uuid }, transaction);
            }

            // Insert catering request
            var cateringUuid = Guid.NewGuid();
            var dataJson = JsonConvert.SerializeObject(new {
                eventDetails = cateringRequest.EventDetails,
                specialInstructions = cateringRequest.SpecialInstructions,
                cart = cateringRequest.CartItems
            });
            await conn.ExecuteAsync(
                @"INSERT INTO dbo.catering_requests
                    (uuid, customer_id, customer_name, customer_email, customer_phone, delivery_address, delivery_date, order_total, data, created_dttm, updated_dttm)
                  SELECT @Uuid, c.id, c.name, c.email, c.phone, @DeliveryAddress, @DeliveryDate, @OrderTotal, @Data, SYSUTCDATETIME(), SYSUTCDATETIME()
                  FROM dbo.customers c WHERE c.id = @CustomerId",
                new {
                    Uuid = cateringUuid,
                    CustomerId = customerId,
                    DeliveryAddress = cateringRequest.StreetAddress,
                    DeliveryDate = cateringRequest.RequestedDate,
                    OrderTotal = cateringRequest.TotalPrice,
                    Data = dataJson
                }, transaction);
            var cateringRequestId = await conn.QuerySingleAsync<int>(
                "SELECT id FROM dbo.catering_requests WHERE uuid = @Uuid",
                new { Uuid = cateringUuid }, transaction);

            transaction.Commit();

            // Prepare email data

            // Build email subject and bodies using TakePayment.cs style
            string subject = $"Misa's Thai Street Cuisine Catering Request Confirmation";
            string htmlBody = CreateCateringRequestEmailHtml(cateringRequest);
            string plainTextBody = CreateCateringRequestEmailText(cateringRequest);

            var sendEmailPayloadCustomer = new {
                To = cateringRequest.CustomerEmail,
                Subject = subject,
                HtmlBody = htmlBody,
                PlainTextBody = plainTextBody
            };
            var sendEmailPayloadBusiness = new {
                To = Environment.GetEnvironmentVariable("Restaurant__NotificationEmail") ?? "info@misasthai.com",
                Subject = $"New Catering Request from {cateringRequest.CustomerName}",
                HtmlBody = $"<h2>New Catering Request Received</h2><p>From: {cateringRequest.CustomerName}</p>" + htmlBody,
                PlainTextBody = $"New catering request received from {cateringRequest.CustomerName}\n\n" + plainTextBody
            };

            // Use HttpClient to call SendEmail function
            var httpClient = new System.Net.Http.HttpClient();
            var baseUrl = Environment.GetEnvironmentVariable("ApiBaseUrl") ?? "http://localhost:7071/api";
            var sendEmailUrl = baseUrl.TrimEnd('/') + "/SendEmail";
            try
            {
                await httpClient.PostAsync(sendEmailUrl, new System.Net.Http.StringContent(
                    Newtonsoft.Json.JsonConvert.SerializeObject(sendEmailPayloadCustomer),
                    System.Text.Encoding.UTF8, "application/json"));
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to send customer confirmation email");
            }
            try
            {
                await httpClient.PostAsync(sendEmailUrl, new System.Net.Http.StringContent(
                    Newtonsoft.Json.JsonConvert.SerializeObject(sendEmailPayloadBusiness),
                    System.Text.Encoding.UTF8, "application/json"));
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to send business notification email");
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteStringAsync($"{{\"success\":true,\"cateringRequestId\":{cateringRequestId}}}");
            return response;
        }
    }

    public class CateringRequestDto
    {
        public string CustomerName { get; set; } = string.Empty;
        public string CustomerEmail { get; set; } = string.Empty;
        public string CustomerPhone { get; set; } = string.Empty;
        public string StreetAddress { get; set; } = string.Empty;
        public DateTime RequestedDate { get; set; }
        public string EventDetails { get; set; } = string.Empty;
        public string SpecialInstructions { get; set; } = string.Empty;
        public List<CartItemDto> CartItems { get; set; } = new();
        public decimal TotalPrice { get; set; }
    }

    public class CartItemDto
    {
        public string ItemName { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public int? SelectedServes { get; set; }
        public string? SelectedSize { get; set; }
        public int UpgradePhadThai24Qty { get; set; }
        public int UpgradePhadThai48Qty { get; set; }
        public int AddOnQty { get; set; }
        public decimal Price { get; set; }
    }
}
