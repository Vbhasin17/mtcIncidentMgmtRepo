using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;
using Azure.Identity;
using System.Threading.Tasks;
using System.Net;

namespace InventoryFunctionApp
{
    public class GetInventoryDetails
    {
        private readonly ILogger _logger;

        public GetInventoryDetails(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<GetInventoryDetails>();
        }

        [Function("GetInventoryDetails")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequestData req)
        {
            _logger.LogInformation("Processing inventory request.");

            try
            {
                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                string terminal = query["terminal"];
                string period = query["period"];

                // if (string.IsNullOrWhiteSpace(terminal))
                // {
                //     var badRequest = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
                //     await badRequest.WriteStringAsync("Missing required query parameter: terminal");
                //     return badRequest;
                // }

                // Default to current period (today’s date)
                // if (string.IsNullOrWhiteSpace(period))
                // {
                //     period = DateTime.UtcNow.AddYears(-1).ToString("yyyy-MM-dd");
                // }

                _logger.LogInformation($"Fetching inventory for terminal '{terminal}' and period '{period}'...");

                var resultList = new List<object>();

                string connectionString = $"Server=tcp:inventorymgmtserver.database.windows.net,1433;Database=InventoryManagementDB;Encrypt=True;TrustServerCertificate=False;";

                // Use DefaultAzureCredential (Managed Identity)
                var credential = new DefaultAzureCredential();

                using (var connection = new SqlConnection(connectionString))
                {
                    // Assign the token from DefaultAzureCredential
                    var token = await credential.GetTokenAsync(
                        new Azure.Core.TokenRequestContext(
                            new[] { "https://database.windows.net/.default" }
                        )
                    );
                    connection.AccessToken = token.Token;

                    await connection.OpenAsync();

                    string sql = @"
                    SELECT top 500
                        i.DeliveryTerm, i.Counterparty, i.Terminal_Location, 
                        i.Period, i.Inventory_Quantity_MB, i.Total_Demand_MB, 
                        i.PriceType, i.Avg_Cost_per_Barrel_USD, i.Market_Price_per_Barrel_USD,
                        t.City, t.State
                    FROM [dbo].[Inventory_and_Demand] i
                    INNER JOIN [dbo].[Terminal_Master] t
                        ON i.Terminal_Location = t.Terminal_Location
                    WHERE 
                    (@terminal IS NULL OR i.Terminal_Location = @terminal)
                    AND (
                        @period IS NOT NULL AND CONVERT(date, i.Period) = CONVERT(date, @period)
                    OR
                        @period IS NULL AND CONVERT(date, i.Period) BETWEEN DATEADD(YEAR, -1, GETDATE()) AND GETDATE()
                    )
                    ORDER BY i.Period DESC";

                    using (var command = new SqlCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@terminal", string.IsNullOrEmpty(terminal) ? DBNull.Value : terminal);
                        command.Parameters.AddWithValue("@period", string.IsNullOrEmpty(period) ? DBNull.Value : period);
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                resultList.Add(new
                                {
                                    DeliveryTerm = reader["DeliveryTerm"]?.ToString(),
                                    CounterParty = reader["CounterParty"]?.ToString(),
                                    TerminalLocation = reader["Terminal_Location"]?.ToString(),
                                    Period = reader["Period"]?.ToString(),
                                    Inventory = reader["Inventory_Quantity_MB"]?.ToString(),
                                    Demand = reader["Total_Demand_MB"]?.ToString(),
                                    PriceType = reader["PriceType"]?.ToString(),
                                    AvgCost = reader["Avg_Cost_per_Barrel_USD"]?.ToString(),
                                    MarketPrice = reader["Market_Price_per_Barrel_USD"]?.ToString(),
                                    City = reader["City"]?.ToString(),
                                    State = reader["State"]?.ToString()
                                });
                            }
                        }
                    }
                }

                var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
                await response.WriteAsJsonAsync(resultList);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error fetching inventory data, Message : {ex.Message} StackTrace : {ex.StackTrace}");
                return req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);

            }
        }
    }
}
