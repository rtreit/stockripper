namespace StockRipperFS.Controllers

open Microsoft.AspNetCore.Mvc
open Microsoft.Extensions.Logging
open System.Net.Http
open System.Net.Http.Json
open StockRipperFS

type HealthCheckResponse = { Status: string }

[<ApiController>]
[<Route("[controller]")>]
type HealthController (logger : ILogger<HealthController>) =
    inherit ControllerBase()
    let client = new HttpClient()
    let agentUri = Utils.agentUri

    let PingAgentAsync =
        async {
            try
                logger.LogInformation("Pinging agent service at {Uri}", agentUri)
                let! response = client.GetFromJsonAsync<HealthCheckResponse>(agentUri) |> Async.AwaitTask
                logger.LogInformation("Received response from agent service: {Status}", response.Status)
                return response.Status
            with
            | ex ->
                logger.LogError(ex, "An error occurred while pinging the agent service")
                return $"Error occurred: {ex.Message}"
        }

    [<HttpGet>]
    member _.Get() : Async<ActionResult> =
        let context = base.HttpContext
        async {
            logger.LogInformation("Received health check request")
            match context.Request.Query with
            | query when query.ContainsKey("pingAgent") ->
                let! response = PingAgentAsync
                logger.LogInformation("Sending response: {Status}", response)
                return OkObjectResult({ Status = $"Healthy - response from pinging agent service: '{response}'" })
            | query when query.ContainsKey("simulateError") ->
                logger.LogWarning("Simulating error response")
                return BadRequestResult()
            | _ ->
                logger.LogInformation("Sending default healthy response")
                return OkObjectResult({ Status = "Healthy" })
        }
