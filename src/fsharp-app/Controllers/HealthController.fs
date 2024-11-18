namespace StockripperFS.Controllers

open Microsoft.AspNetCore.Mvc
open Microsoft.Extensions.Logging
open System.Net.Http
open System.Net.Http.Json
open StockripperFS

type HealthCheckResponse = { Status: string }

[<ApiController>]
[<Route("[controller]")>]
type HealthController (logger : ILogger<HealthController>) =
    inherit ControllerBase()
    let client = new HttpClient()
    let agentUri = Utils.agentUri
    let rustUri = Utils.rustUri

    let PingAgentAsync =
        async {
            try
                logger.LogInformation("Pinging agent service at {Uri}", agentUri) |> ignore
                let! response = client.GetFromJsonAsync<HealthCheckResponse>(agentUri) |> Async.AwaitTask
                logger.LogInformation("Received response from agent service: {Status}", response.Status) |> ignore
                response.Status
            with
            | ex ->
                logger.LogError(ex, "An error occurred while pinging the agent service") |> ignore
                $"Error occurred: {ex.Message}"
        }

    let PingRustAsync =
        async {
            try
                logger.LogInformation("Pinging rust service at {Uri}", rustUri) |> ignore
                let! response = client.GetFromJsonAsync<HealthCheckResponse>(rustUri) |> Async.AwaitTask
                logger.LogInformation("Received response from rust service: {Status}", response.Status) |> ignore
                response.Status
            with
            | ex ->
                logger.LogError(ex, "An error occurred while pinging the rust service") |> ignore
                $"Error occurred: {ex.Message}"                
        }

    [<HttpGet>]
    member _.Get() : Async<ActionResult> =
        let context = base.HttpContext
        async {
            logger.LogInformation("Received health check request") |> ignore
            match context.Request.Query with
            | query when query.ContainsKey("pingAgent") ->
                let! agentResponse = PingAgentAsync
                let! rustResponse = PingRustAsync
                let combinedStatus = $"Agent: {agentResponse}, Rust: {rustResponse}"
                logger.LogInformation("Sending combined response: {Status}", combinedStatus) |> ignore
                return OkObjectResult({ Status = $"Healthy - response from services: '{combinedStatus}'" })
            | query when query.ContainsKey("simulateError") ->
                logger.LogWarning("Simulating error response") |> ignore
                return BadRequestResult()
            | _ ->
                logger.LogInformation("Sending default healthy response") |> ignore
                return OkObjectResult({ Status = "Healthy" })
        }
