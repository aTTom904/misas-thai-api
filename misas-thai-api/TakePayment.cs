using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using Newtonsoft.Json;
using Square;
using Square.Models;
using Square.Authentication;
using Azure.Communication.Email;
using System.Linq;

namespace misas_thai_api
{
    public static class TakePayment
    {
        [Function("TakePayment")]
        public static async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestData req,
            FunctionContext executionContext)
        {
            var log = executionContext.GetLogger("TakePayment");
            log.LogInformation("Processing payment request...");

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            
            var orderRequest = JsonConvert.DeserializeObject<CreateOrderRequest>(requestBody);
            if (orderRequest == null)
            {
                log.LogError("Failed to deserialize order request or request is null.");
                var response = req.CreateResponse(HttpStatusCode.BadRequest);
                await response.WriteAsJsonAsync(new { success = false, error = "Invalid request format." });
                return response;
            }

            var accessToken = System.Environment.GetEnvironmentVariable("Square__AccessToken");
            if (string.IsNullOrEmpty(accessToken))
            {
                log.LogError("Square credentials missing.");
                var response = req.CreateResponse(HttpStatusCode.BadRequest);
                await response.WriteAsJsonAsync(new { success = false, error = "Square credentials missing." });
                return response;
            }

            try
            {
                var SquareEnvironment = System.Environment.GetEnvironmentVariable("Square__Environment").ToLower();
                var client = new SquareClient.Builder()
                    .BearerAuthCredentials(
                        new BearerAuthModel.Builder(accessToken).Build()
                    )
                    .Environment(SquareEnvironment == "production" ? Square.Environment.Production : Square.Environment.Sandbox)
                    .Build();

                var money = new Money(
                    amount: (long)(orderRequest.Total * 100),
                    currency: "USD"
                );

                var paymentRequest = new CreatePaymentRequest(
                    sourceId: orderRequest.PaymentToken,
                    idempotencyKey: Guid.NewGuid().ToString(),
                    amountMoney: money
                );

                var response = await client.PaymentsApi.CreatePaymentAsync(paymentRequest);
                if (response.Payment != null && response.Payment.Status == "COMPLETED")
                {
                    // Send email notification
                    await SendOrderConfirmationEmail(orderRequest, response.Payment.Id, log);
                    
                    var successResponse = req.CreateResponse(HttpStatusCode.OK);
                    await successResponse.WriteAsJsonAsync(new { 
                        success = true, 
                        orderNumber = response.Payment.Id, 
                        message = "Payment processed successfully." 
                    });
                    return successResponse;
                }
                else
                {
                    log.LogError($"Payment failed. Status: {response.Payment?.Status}, Errors: {JsonConvert.SerializeObject(response.Errors)}");
                    var failResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                    await failResponse.WriteAsJsonAsync(new { success = false, error = "Payment failed.", details = response.Errors });
                    return failResponse;
                }
            }
            catch (Square.Exceptions.ApiException apiEx)
            {
                log.LogError($"Square API Exception: {apiEx.Message}");
                log.LogError($"Response Code: {apiEx.ResponseCode}");
                log.LogError($"Response Body: {apiEx.HttpContext?.Response?.Body}");
                
                var errorResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await errorResponse.WriteAsJsonAsync(new { 
                    success = false, 
                    error = "Square API error", 
                    message = apiEx.Message,
                    responseCode = apiEx.ResponseCode,
                    responseBody = apiEx.HttpContext?.Response?.Body
                });
                return errorResponse;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error processing payment.");
                var errorResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await errorResponse.WriteAsJsonAsync(new { success = false, error = ex.Message });
                return errorResponse;
            }
        }

        private static async Task SendOrderConfirmationEmail(CreateOrderRequest order, string orderNumber, ILogger log)
        {
            try
            {
                log.LogInformation("Preparing to send order confirmation email...");
                var connectionString = System.Environment.GetEnvironmentVariable("AzureCommunicationServices__ConnectionString");
                var fromEmail = System.Environment.GetEnvironmentVariable("AzureCommunicationServices__FromEmail");
                var replyToEmail = System.Environment.GetEnvironmentVariable("AzureCommunicationServices__ReplyToEmail");
                
                
                if (string.IsNullOrEmpty(connectionString) || string.IsNullOrEmpty(fromEmail))
                {
                    log.LogWarning("Email configuration missing. Skipping email notification.");
                    return;
                }

                // Try basic client creation first
                log.LogInformation("Creating EmailClient...");
                var emailClient = new EmailClient(connectionString);
                log.LogInformation("Email client initialized.");

                // Create full email content
                log.LogInformation("Creating order confirmation email...");
                var subject = $"Misa's Thai Street Cuisine Order Confirmation";
                var htmlContent = CreateOrderEmailHtml(order, orderNumber);
                var plainTextContent = CreateOrderEmailText(order, orderNumber);

                var emailMessage = new EmailMessage(
                    senderAddress: fromEmail,
                    recipientAddress: order.CustomerEmail,
                    content: new EmailContent(subject)
                    {
                        PlainText = plainTextContent,
                        Html = htmlContent
                    });

                // Add Reply-To header if configured
                if (!string.IsNullOrEmpty(replyToEmail))
                {
                    emailMessage.ReplyTo.Add(new EmailAddress(replyToEmail));
                }

                log.LogInformation("Attempting to send email...");
                // Send to customer
                var customerEmailResult = await emailClient.SendAsync(Azure.WaitUntil.Completed, emailMessage);
                log.LogInformation("Email send completed successfully.");

                // Send to restaurant (optional)
                var restaurantEmail = System.Environment.GetEnvironmentVariable("Restaurant__NotificationEmail");
                if (!string.IsNullOrEmpty(restaurantEmail))
                {
                    var restaurantMessage = new EmailMessage(
                        senderAddress: fromEmail,
                        recipientAddress: restaurantEmail,
                        content: new EmailContent($"New Order from {order.CustomerName} - {orderNumber}")
                        {
                            PlainText = $"New order received from {order.CustomerName}\n\n" + plainTextContent,
                            Html = $"<h2>New Order Received</h2><p>From: {order.CustomerName}</p>" + htmlContent
                        });

                    var restaurantEmailResult = await emailClient.SendAsync(Azure.WaitUntil.Completed, restaurantMessage);
                    log.LogInformation("Restaurant notification email sent successfully.");
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to send email notification. Exception details: {ExceptionType}: {ExceptionMessage}", ex.GetType().Name, ex.Message);
                if (ex.InnerException != null)
                {
                    log.LogError("Inner exception: {InnerExceptionType}: {InnerExceptionMessage}", ex.InnerException.GetType().Name, ex.InnerException.Message);
                }
                // Don't throw - email failure shouldn't break the payment process
            }
        }

        private static string CreateOrderEmailHtml(CreateOrderRequest order, string orderNumber)
        {

            var itemsHtml = string.Empty;
            foreach (var item in order.Items)
            {
                var servesText = item.SelectedServes.HasValue ? $" (Serves {item.SelectedServes.Value})" : "";
                itemsHtml += $"<tr><td>{item.ItemName}{servesText}</td><td>{item.Quantity}</td><td>${item.Price:F2}</td><td>${(item.Price * item.Quantity):F2}</td></tr>";
                if (item.UpgradePhadThai48Qty > 0)
                {
                    var upgrade48Price = 18m;
                    var upgrade48Total = item.UpgradePhadThai48Qty * upgrade48Price;
                    itemsHtml += $"<tr><td>Upgrade: Pad Thai (48 oz)</td><td>{item.UpgradePhadThai48Qty}</td><td>${upgrade48Price:F2}</td><td>${upgrade48Total:F2}</td></tr>";
                }
                if (item.UpgradePhadThai24Qty > 0)
                {
                    var upgrade24Price = 9m;
                    var upgrade24Total = item.UpgradePhadThai24Qty * upgrade24Price;
                    itemsHtml += $"<tr><td>Upgrade: Pad Thai (24 oz)</td><td>{item.UpgradePhadThai24Qty}</td><td>${upgrade24Price:F2}</td><td>${upgrade24Total:F2}</td></tr>";
                }
            }

            var tipHtml = string.Empty;
            if (order.TipAmount > 0)
            {
                tipHtml = $"<tr><td colspan='3' style='text-align: right;'>Tip: </td><td>${order.TipAmount:F2}</td></tr>";
            }
            var grandTotal = order.Total;

            var consentText = order.ConsentToUpdates 
            ? "Yes - You will receive promotional emails and text messages about special offers, new menu items, and restaurant updates." 
            : "No - You will not receive promotional communications.";
            // Parse delivery date for calendar event (assume format MM/dd/yyyy or yyyy-MM-dd)
            DateTime deliveryDate;
            string deliveryDateString = order.DeliveryDate;
            if (!DateTime.TryParse(order.DeliveryDate, out deliveryDate))
            {
                deliveryDate = DateTime.Now.Date;
            }
            // Delivery window: 5:00 PM – 7:00 PM
            var start = new DateTime(deliveryDate.Year, deliveryDate.Month, deliveryDate.Day, 17, 0, 0);
            var end = new DateTime(deliveryDate.Year, deliveryDate.Month, deliveryDate.Day, 19, 0, 0);
            string startUtc = start.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");
            string endUtc = end.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");
            string calendarTitle = Uri.EscapeDataString("Misa's Thai Delivery");
            string calendarDetails = Uri.EscapeDataString($"Order #: {orderNumber} | Address: {order.DeliveryAddress}");
            string calendarLocation = Uri.EscapeDataString(order.DeliveryAddress);
            string googleCalUrl = $"https://www.google.com/calendar/render?action=TEMPLATE&text={calendarTitle}&dates={startUtc}/{endUtc}&details={calendarDetails}&location={calendarLocation}&sf=true&output=xml";

            // ICS file content (RFC 5545)
            string icsContent = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Misa's Thai Street Cuisine//EN\r\nBEGIN:VEVENT\r\nUID:{Guid.NewGuid()}\r\nDTSTAMP:{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}\r\nDTSTART:{startUtc}\r\nDTEND:{endUtc}\r\nSUMMARY:Misa's Thai Delivery\r\nDESCRIPTION:Order #: {orderNumber} | Address: {order.DeliveryAddress}\r\nLOCATION:{order.DeliveryAddress}\r\nEND:VEVENT\r\nEND:VCALENDAR";
            string icsBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(icsContent));
            string icsDataUrl = $"data:text/calendar;base64,{icsBase64}";

            return $@"
<html>
<body style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;'>
    <div style='font-size: 2.2em; color: #ee6900; font-weight: bold; text-align: center; margin-top: 30px; margin-bottom: 20px;'>
        Thank you for your order!
    </div>
    <p>Hi {order.CustomerName},</p>
    <p>Thank you so much for your order — we’re excited to cook for you! Your payment has been processed successfully, and your order is confirmed.</p>
    <h3>🚗 Delivery Details</h3>
    <p><strong>Delivery Address:</strong> {order.DeliveryAddress}</p>
    <p><strong>Delivery Date:</strong> <a href='{googleCalUrl}' style='color:#1976d2; text-decoration:underline; font-weight:600;' target='_blank' title='Add to Google Calendar'>{order.DeliveryDate} 📅
    </a></p>
    <p><strong>Estimated Window:</strong> 5:00 PM – 7:00 PM</p>
    <p>We’ll finalize our routes after the order deadline and text you a more precise delivery window shortly after.</p>
    <h3>📦 Order Summary</h3>
    <p><strong>Order #:</strong> {orderNumber}</p>
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
            {tipHtml}
            <tr style='background-color: #f8f9fa;'>
                <td colspan='3' style='text-align: right; font-weight: bold;'>Total: </td>
                <td style='font-weight: bold;'>${grandTotal:F2}</td>
            </tr>
        </tbody>
    </table>
    <h3>📣 Marketing Preferences</h3>
    <p><strong> Marketing Communications:</strong> {consentText}</p>
    {(order.ConsentToUpdates ? "<p style='font-size: 12px; color: #666;'><em>You can unsubscribe at any time. Standard message and data rates may apply for text messages.</em></p>" : "")}
    <h3>👤 Customer Info</h3>
    <p><strong>Name:</strong> {order.CustomerName}<br/>
    <strong>Email:</strong> {order.CustomerEmail}<br/>
    <strong>Phone:</strong> {order.CustomerPhone}</p>
    {(!string.IsNullOrEmpty(order.AdditionalInformation) ? $@"
    <h3>Additional Information</h3>
    <p style='background-color: #f8f9fa; padding: 15px; border-left: 4px solid #ee6900; margin: 20px 0;'>{order.AdditionalInformation}</p>" : "")
    }
    <h3>💬 Questions or Changes?</h3>
    <p>We’re happy to help! Just email us at msthaistreetcuisine@gmail.com or text us at 904-315-4884.</p>
    <p>We can’t wait for you to enjoy your Thai favorites — made fresh with authentic herbs and spices! 🧡</p>
    <p>Warmly,<br/>
    Misa’s Thai Street Cuisine<br/>
    1301 N Orange Ave, Suite 102<br/>
    Green Cove Springs, FL 32043<br/>
    msthaistreetcuisine@gmail.com<br/>
    904-315-4884</p>
</body>
</html>";
        }

        private static string CreateOrderEmailText(CreateOrderRequest order, string orderNumber)
        {

            var itemsLines = new System.Collections.Generic.List<string>();
            foreach (var item in order.Items)
            {
                var servesText = item.SelectedServes.HasValue ? $" (Serves {item.SelectedServes.Value})" : "";
                itemsLines.Add($"{item.ItemName}{servesText}\t{item.Quantity}\t${item.Price:F2}\t${(item.Price * item.Quantity):F2}");
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
            }

            if (order.TipAmount > 0)
            {
                itemsLines.Add($"Tip\t\t\t${order.TipAmount:F2}");
            }
            var grandTotal = order.Total;
            var itemsText = string.Join("\n", itemsLines);

            var consentText = order.ConsentToUpdates 
            ? "Yes - You will receive promotional emails and text messages about special offers, new menu items, and restaurant updates." 
            : "No - You will not receive promotional communications.";
            return $@"
==============================
THANK YOU FOR YOUR ORDER
==============================

Hi {order.CustomerName},

Thank you so much for your order — we’re excited to cook for you! Your payment has been processed successfully, and your order is confirmed.

🚗 Delivery Details
Delivery Address: {order.DeliveryAddress}
Delivery Date: {order.DeliveryDate}
Estimated Window: 5:00 PM – 7:00 PM

We’ll finalize our routes after the order deadline and text you a more precise delivery window shortly after.

📦 Order Summary
Order #: {orderNumber}
Item\tQty\tPrice\tTotal
{itemsText}
Total: ${grandTotal:F2}

{(string.IsNullOrEmpty(order.AdditionalInformation) ? "" :
    "------------------------------\n" +
    "ADDITIONAL INFORMATION PROVIDED BY YOU:\n" +
    order.AdditionalInformation +
    "\n------------------------------\n")}

Marketing Preferences
Marketing Communications: {consentText}
{(order.ConsentToUpdates ? "You can unsubscribe at any time. Standard message and data rates may apply for text messages.\n" : "")}

👤 Customer Info
Name: {order.CustomerName}
Email: {order.CustomerEmail}
Phone: {order.CustomerPhone}

💬 Questions or Changes?
We’re happy to help! Just email us at msthaistreetcuisine@gmail.com or text us at 904-315-4884.

We can’t wait for you to enjoy your Thai favorites — made fresh with authentic herbs and spices! 🧡

Warmly,
Misa’s Thai Street Cuisine
1301 N Orange Ave, Suite 102
Green Cove Springs, FL 32043
msthaistreetcuisine@gmail.com
904-315-4884
";
        }

        // Model for deserializing order request
        public class CreateOrderRequest
        {
            public string CustomerName { get; set; } = string.Empty;
            public string CustomerEmail { get; set; } = string.Empty;
            public string CustomerPhone { get; set; } = string.Empty;
            public bool ConsentToUpdates { get; set; }
            public string DeliveryAddress { get; set; } = string.Empty;
            public string DeliveryDate { get; set; } = string.Empty;
            public decimal Total { get; set; }
            public string PaymentToken { get; set; } = string.Empty;
            public string AdditionalInformation { get; set; } = string.Empty;
            public List<OrderItemRequest> Items { get; set; } = new();
            public decimal TipAmount { get; set; }
        }

        public class OrderItemRequest
        {
            public string ItemName { get; set; } = string.Empty;
            public string Category { get; set; } = string.Empty;
            public decimal Price { get; set; }
            public int Quantity { get; set; }
            public int? SelectedServes { get; set; }
            public int UpgradePhadThai24Qty { get; set; }
            public int UpgradePhadThai48Qty { get; set; }
        }
    }
}