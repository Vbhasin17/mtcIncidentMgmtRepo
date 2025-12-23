using Azure.AI.Agents.Persistent;
using Azure.AI.Projects;
using Azure.Identity;
using Markdig;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InventoryFunctionApp
{
    public class CallAgent
    {
        private readonly string _projectEndpoint = "https://ai-psinventorymanagement.services.ai.azure.com/api/projects/ai-psinventorymanagemen-project";
        private readonly string _agentId = "asst_A1Au12mo9yNQHv189rDPyEDH";
        private readonly ILogger _logger;

        public CallAgent(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<CallAgent>();
        }


        [Function(nameof(CallAgent))]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequestData req)
        {
            string userPrompt = await new StreamReader(req.Body, Encoding.UTF8).ReadToEndAsync();
            userPrompt = userPrompt?.Trim() ?? "";

            var centralZone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
            var currentDate = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, centralZone);

            userPrompt = userPrompt?.Trim() + " TodaysDate: " + currentDate;


            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            if (req.Method == "OPTIONS")
                return response;

            if (string.IsNullOrWhiteSpace(userPrompt))
            {
                await response.WriteStringAsync("Please provide a prompt in the request body.");
                return response;
            }

            try
            {
                var endpoint = new Uri(_projectEndpoint);
                AIProjectClient projectClient = new(endpoint, new DefaultAzureCredential());
                PersistentAgentsClient agentsClient = projectClient.GetPersistentAgentsClient();

                // // Step 1: Check if a threadId was passed (via query or header)
                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                string existingThreadId = query["threadId"];

                PersistentAgentThread thread;

                if (!string.IsNullOrEmpty(existingThreadId))
                {
                    // Reuse existing thread to keep context
                    _logger.LogInformation($"Thread passed by the App: {existingThreadId}");
                    thread = agentsClient.Threads.GetThread(existingThreadId);
                }
                else
                {
                    // Create a new thread for a fresh conversation
                    _logger.LogInformation("No thread passed as parameter, creating a new one");
                    thread = agentsClient.Threads.CreateThread();
                }

                // Step 2: Add user message to thread
                PersistentThreadMessage messageResponse = agentsClient.Messages.CreateMessage(
                    thread.Id,
                    MessageRole.User,
                    userPrompt);

                // // Step 3: Run the agent on that thread
                ThreadRun run = agentsClient.Runs.CreateRun(thread.Id, _agentId);

                do
                {
                    await Task.Delay(500);
                    run = agentsClient.Runs.GetRun(thread.Id, run.Id);
                }
                while (run.Status == RunStatus.Queued || run.Status == RunStatus.InProgress);

                if (run.Status != RunStatus.Completed)
                {
                    await response.WriteStringAsync($"Agent run failed: {run.LastError?.Message}");
                    return response;
                }

                // // Step 4: Get all messages for that thread (including history)
                var messages = agentsClient.Messages.GetMessages(thread.Id, order: ListSortOrder.Ascending);

                var agentResponses = string.Empty;
                foreach (var threadMessage in messages.Reverse())
                {
                    if (threadMessage.Role == MessageRole.Agent)
                    {
                        foreach (var contentItem in threadMessage.ContentItems)
                        {
                            if (contentItem is MessageTextContent textItem)
                            {
                                agentResponses = textItem.Text;
                                break;
                            }
                        }
                        if (!string.IsNullOrEmpty(agentResponses))
                            break;
                    }
                }

                string formattedHtml;
                _logger.LogInformation($"The original response is {agentResponses}");
                agentResponses = CleanAgentResponse(agentResponses);
                _logger.LogInformation($"The Cleaned response is {agentResponses}");
                if (!string.IsNullOrWhiteSpace(agentResponses) && agentResponses.Contains("DashboardType"))
                {
                    formattedHtml = agentResponses;
                    _logger.LogInformation($"Agent Response: In Dashboard Scope. {formattedHtml}");
                    var dashboard = JsonSerializer.Deserialize<Dashboard>(agentResponses, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    dashboard = SetDashBoardContent(dashboard);

                    _logger.LogInformation($"Agent Response: In Dashboard Scope. {dashboard}");

                    if (dashboard != null && !string.IsNullOrEmpty(dashboard.DashboardType))
                    {
                        var result = new
                        {
                            threadId = thread.Id,
                            action = "dashboard",
                            html = dashboard
                        };

                        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                        await response.WriteStringAsync(JsonSerializer.Serialize(result));
                    }
                }
                else if (!string.IsNullOrWhiteSpace(agentResponses) && agentResponses.TrimStart().StartsWith("["))
                {
                    int startIndex = agentResponses.IndexOf('[');
                    int endIndex = agentResponses.LastIndexOf(']');
                    string jsonArrayString = agentResponses.Substring(startIndex, endIndex - startIndex + 1);
                    var terminals = ParseTerminals(jsonArrayString);
                    //var terminals = JsonSerializer.Deserialize<List<Terminal>>(jsonArrayString);
                    formattedHtml = agentResponses;
                    _logger.LogInformation($"Agent Response: In Map Scope.  {formattedHtml}");
                    var result1 = new
                    {
                        threadId = thread.Id,
                        action = "map",
                        html = terminals
                    };
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                    await response.WriteStringAsync(JsonSerializer.Serialize(result1));
                }
                else
                {
                    var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
                    formattedHtml = Markdown.ToHtml(string.Join("\n", agentResponses), pipeline)
                        .Replace("<p>", "<p style='margin:0 0 6px 0; line-height:1.4;'>")
                        .Replace("<ul>", "<ul style='margin:4px 0; padding-left:18px; line-height:1.4;'>")
                        .Replace("<ol>", "<ol style='margin:4px 0; padding-left:18px; line-height:1.4;'>")
                        .Replace("<br>", "")
                        .Replace("\n", "");

                    _logger.LogInformation($"Agent Response: {formattedHtml}");

                    var result = new
                    {
                        threadId = thread.Id,
                        action = string.Empty,
                        html = formattedHtml
                    };
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                    await response.WriteStringAsync(JsonSerializer.Serialize(result));
                }

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogInformation($"In Catch block:  {ex.Message}");
                await response.WriteStringAsync($"Error: {ex.Message}");
                return response;
            }
        }

        private string CleanAgentResponse(string raw)
        {
            //if (string.IsNullOrWhiteSpace(raw))
            //    return raw;

            // Remove ```json or ``` at the start/end
            raw = raw.Replace("```json", "").Replace("```", "")
                .Replace("...", "")
                .Replace(",\r\n    ...", "")
                .Replace(",...", "")
                .Replace("\\", "")
                .Replace("\\\"", "\"")
                .Replace("]\"", "]")
                .Replace("]\"\r\n]", "]")
                .Replace("]\"\r\n] ", "]").Trim();

            if (raw.StartsWith("\"") && raw.EndsWith("\""))
            {
                raw = raw.Trim('"');
            }

            //Remove accidental trailing or leading brackets
            while (raw.StartsWith("[["))
                raw = raw.Substring(1);

            while (raw.EndsWith("]]"))
                raw = raw.Substring(0, raw.Length - 1);

            if (string.IsNullOrWhiteSpace(raw))
                return raw;

            raw = Regex.Replace(raw, @"\]\""\s*\]$", "]");  // Handles ]" ]
            raw = Regex.Replace(raw, @"\""\s*\]$", "]");    // Handles ]"
            raw = Regex.Replace(raw, @"\""\s*\}$", "}");    // Handles }"

            // Try to find JSON inside ```json ... ```
            var fencedMatch = Regex.Match(raw, @"```json\s*(\[.*?\]|\{.*?\})\s*```", RegexOptions.Singleline);
            if (fencedMatch.Success)
                return fencedMatch.Groups[1].Value.Trim();

            // Try to find JSON array or object in text even without ```json```
            var jsonMatch = Regex.Match(raw, @"(\{[\s\S]*\}|\[\s*(\{|\[|\"").*[\s\S]*\])", RegexOptions.Singleline);
            if (jsonMatch.Success)
                return jsonMatch.Groups[1].Value.Trim();

            // Fallback — if entire text is JSON, return it
            raw = raw.Trim();
            if (raw.StartsWith("{") || raw.StartsWith("["))
                return raw;

            return raw;
        }

        public static List<Terminal> ParseTerminals(string json)
        {
            var result = new List<Terminal>();

            // Parse JSON into JObject/JArray
            var token = JToken.Parse(json);

            // Case 1: JSON is an array of terminals
            if (token is JArray arr)
            {
                foreach (var item in arr)
                    result.Add(MapToTerminal((JObject)item));
            }
            // Case 2: JSON is a single terminal object
            else if (token is JObject obj)
            {
                result.Add(MapToTerminal(obj));
            }

            return result;
        }

        // Maps JSON to Terminal safely, even if keys differ
        private static Terminal MapToTerminal(JObject jObj)
        {
            return new Terminal
            {
                Terminal_Location = jObj.Value<string>("Terminal_Location")
                                       ?? jObj.Value<string>("Location")
                                       ?? jObj.Value<string>("Name")
                                       ?? "",

                City = jObj.Value<string>("City")
                       ?? jObj.Value<string>("Town")
                       ?? "",

                State = jObj.Value<string>("State")
                        ?? jObj.Value<string>("Province")
                        ?? "",

                Minimum_Capacity = jObj.Value<int?>("Minimum_Capacity")
                                    ?? jObj.Value<int?>("MinCap"),

                Maximum_Capacity = jObj.Value<int?>("Maximum_Capacity")
                                    ?? jObj.Value<int?>("MaxCap"),

                Latitude = jObj.Value<double?>("Latitude")
                           ?? jObj.Value<double?>("Lat")
                           ?? 0,

                Longitude = jObj.Value<double?>("Longitude")
                            ?? jObj.Value<double?>("Long")
                            ?? 0
            };
        }

        public Dashboard SetDashBoardContent(Dashboard dashboard)
        {
            var normalizedData = new List<DashboardDataItem>();

            foreach (var item in dashboard.Data)
            {
                // case 1: nested object (JObject)
                if (item.Value is JObject jObj)
                {
                    foreach (var prop in jObj.Properties())
                    {
                        normalizedData.Add(new DashboardDataItem
                        {
                            Name = $"{item.Name} - {prop.Name}",
                            Value = prop.Value.ToObject<double?>()
                        });
                    }
                }
                // case 2: System.Text.Json.JsonElement (common in Azure Function)
                else if (item.Value is JsonElement jsonElement)
                {
                    switch (jsonElement.ValueKind)
                    {
                        case JsonValueKind.Object:
                            var dict = JsonSerializer.Deserialize<Dictionary<string, double?>>(jsonElement.GetRawText());
                            foreach (var kvp in dict)
                            {
                                normalizedData.Add(new DashboardDataItem
                                {
                                    Name = $"{item.Name} - {kvp.Key}",
                                    Value = kvp.Value
                                });
                            }
                            break;

                        case JsonValueKind.Number:
                            normalizedData.Add(new DashboardDataItem
                            {
                                Name = item.Name,
                                Value = jsonElement.GetDouble()
                            });
                            break;
                    }
                }
                // fallback (already a number)
                else if (item.Value != null && double.TryParse(item.Value.ToString(), out var val))
                {
                    normalizedData.Add(new DashboardDataItem
                    {
                        Name = item.Name,
                        Value = val
                    });
                }
            }
            dashboard.Data = normalizedData;

            return dashboard;
        }


    }
}




public class Terminal
{
    public string Terminal_Location { get; set; }
    public string City { get; set; }
    public string State { get; set; }
    public int? Minimum_Capacity { get; set; }
    public int? Maximum_Capacity { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
}

public class DashboardResponse
{
    public string Action { get; set; } = "dashboard";
    public Dashboard Dashboard { get; set; }
}

public class Dashboard
{
    public string DashboardType { get; set; }  // "pie" or "bar"
    public string Title { get; set; }
    public string YAxisLabel { get; set; }
    public List<DashboardDataItem> Data { get; set; }
}

public class DashboardDataItem
{
    public string Name { get; set; }
    public object Value { get; set; }
    //public double? Value { get; set; }
}
