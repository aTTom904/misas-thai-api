using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using Newtonsoft.Json;
using Azure.Communication.Email;

namespace misas_thai_api
{
    public static class SendEmail
    {
        [Function("SendEmail")]
        public static async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestData req,
            FunctionContext executionContext)
        {
            var log = executionContext.GetLogger("SendEmail");
            log.LogInformation("Processing email send request...");

            try
            {
                string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                var emailRequest = JsonConvert.DeserializeObject<EmailRequest>(requestBody);

                if (emailRequest == null || string.IsNullOrEmpty(emailRequest.To))
                {
                    log.LogError("Invalid email request");
                    var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badResponse.WriteAsJsonAsync(new { success = false, error = "Invalid email request" });
                    return badResponse;
                }

                var connectionString = System.Environment.GetEnvironmentVariable("AzureCommunicationServices__ConnectionString");
                var fromEmail = System.Environment.GetEnvironmentVariable("AzureCommunicationServices__FromEmail");

                if (string.IsNullOrEmpty(connectionString) || string.IsNullOrEmpty(fromEmail))
                {
                    log.LogError("Email service not configured");
                    var configResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                    await configResponse.WriteAsJsonAsync(new { success = false, error = "Email service not configured" });
                    return configResponse;
                }

                var emailClient = new EmailClient(connectionString);

                var emailMessage = new EmailMessage(
                    senderAddress: fromEmail,
                    recipientAddress: emailRequest.To,
                    content: new EmailContent(emailRequest.Subject)
                    {
                        PlainText = emailRequest.PlainTextBody,
                        Html = emailRequest.HtmlBody
                    });

                if (!string.IsNullOrEmpty(emailRequest.ReplyTo))
                {
                    emailMessage.ReplyTo.Add(new EmailAddress(emailRequest.ReplyTo));
                }

                log.LogInformation("Sending email to {Recipient}", emailRequest.To);
                var emailResult = await emailClient.SendAsync(Azure.WaitUntil.Completed, emailMessage);
                
                log.LogInformation("Email sent successfully to {Recipient}", emailRequest.To);
                var successResponse = req.CreateResponse(HttpStatusCode.OK);
                await successResponse.WriteAsJsonAsync(new { success = true, message = "Email sent successfully" });
                return successResponse;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error sending email");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, error = ex.Message });
                return errorResponse;
            }
        }

        public class EmailRequest
        {
            public string To { get; set; } = string.Empty;
            public string? ReplyTo { get; set; }
            public string Subject { get; set; } = string.Empty;
            public string HtmlBody { get; set; } = string.Empty;
            public string PlainTextBody { get; set; } = string.Empty;
        }
    }
}
