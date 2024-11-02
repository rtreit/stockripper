namespace StockRipperFS.Controllers

open Microsoft.AspNetCore.Mvc
open Microsoft.Extensions.Logging
open System.Net.Http
open System.Net.Http.Json
open StockRipperFS
open System
open Microsoft.AspNetCore.Mvc
open Microsoft.AspNetCore.Routing
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
                let! response = client.GetFromJsonAsync<HealthCheckResponse>(agentUri) |> Async.AwaitTask
                return response.Status
            with
            | ex ->
                return $"Error occurred: {ex.Message}"
        }

    [<HttpGet>]
    member _.Get() : Async<ActionResult> =
        let context = base.HttpContext
        async {
            match context.Request.Query with
            | query when query.ContainsKey("pingAgent") ->
                let! response = PingAgentAsync
                return OkObjectResult({ Status = $"Healthy - response from pinging agent service: '{response}'" })
            | query when query.ContainsKey("simulateError") ->
                return BadRequestResult()
            | _ ->
                return OkObjectResult({ Status = "Healthy" })
        }

