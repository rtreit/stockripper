open System
open System.Net.Http
open System.Threading.Tasks

let httpClient = new HttpClient()

let getHealthCheckAsync() =
    task {
        let url = "http://stockripper-python-app.stockripper.internal:5000/health"
        printfn "Sending request to Python container at %s" url
        try
            let! response = httpClient.GetAsync(url)
            if response.IsSuccessStatusCode then
                let! content = response.Content.ReadAsStringAsync()
                printfn "Response from Python container: %s" content
            else
                printfn "Failed to reach Python container. Status code: %d" (int response.StatusCode)
        with
        | ex -> printfn "Error calling Python container: %s" ex.Message
    }

[<EntryPoint>]
let main argv =
    printfn "Hello from F#"

    // Call the health check endpoint and print results
    getHealthCheckAsync().Wait()
    0 // return an integer exit code
