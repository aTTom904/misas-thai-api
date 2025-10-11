using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace misas_thai_api
{
    public static class GetConfig
    {
        [Function("GetConfig")]
        public static async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)] HttpRequestData req,
            FunctionContext executionContext)
        {
            var log = executionContext.GetLogger("GetConfig");
            log.LogInformation("Configuration request received.");

            try
            {
                // Create response with proper CORS headers
                var response = req.CreateResponse(HttpStatusCode.OK);
                
                // Add CORS headers for Blazor WebAssembly
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization");

                // Get configuration values from environment variables
                var config = new
                {
                    Square = new
                    {
                        ApplicationId = Environment.GetEnvironmentVariable("Square__ApplicationId") ?? "",
                        Environment = Environment.GetEnvironmentVariable("Square__Environment") ?? "sandbox",
                        LocationId = Environment.GetEnvironmentVariable("Square__LocationId") ?? ""
                    },
                    GoogleMaps = new
                    {
                        ApiKey = Environment.GetEnvironmentVariable("GoogleMaps__ApiKey") ?? ""
                    },
                    Api = new
                    {
                        BaseUrl = Environment.GetEnvironmentVariable("Api__BaseUrl") ?? ""
                    }
                };

                log.LogInformation("Configuration response prepared successfully.");
                await response.WriteAsJsonAsync(config);
                return response;
            }
            catch (Exception ex)
            {
                log.LogError($"Error retrieving configuration: {ex.Message}");
                
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                errorResponse.Headers.Add("Access-Control-Allow-Origin", "*");
                
                await errorResponse.WriteAsJsonAsync(new 
                { 
                    error = "Failed to retrieve configuration",
                    message = ex.Message 
                });
                
                return errorResponse;
            }
        }

        [Function("GetConfigOptions")]
        public static async Task<HttpResponseData> HandleOptions(
            [HttpTrigger(AuthorizationLevel.Anonymous, "options", Route = null)] HttpRequestData req,
            FunctionContext executionContext)
        {
            var log = executionContext.GetLogger("GetConfigOptions");
            log.LogInformation("CORS preflight request received.");

            var response = req.CreateResponse(HttpStatusCode.OK);
            
            // CORS preflight response headers
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization");
            response.Headers.Add("Access-Control-Max-Age", "86400");

            return response;
        }
    }
}