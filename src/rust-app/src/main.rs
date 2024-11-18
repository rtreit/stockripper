use actix_web::{get, App, HttpResponse, HttpServer, Responder};
use serde::{Serialize, Deserialize};
use reqwest;

#[derive(Serialize, Deserialize)]
struct HealthCheckResponse {
    status: String,
}

#[derive(Serialize, Deserialize)]
struct ContainerListResponse {
    containers: Vec<String>,
}

#[get("/health")]
async fn health_check() -> impl Responder {
    let response = HealthCheckResponse {
        status: "Rust says: I am healthy!!!".to_string(),
    };
    HttpResponse::Ok().json(response)
}

#[get("/containers")]
async fn container_check() -> impl Responder {
    let response = HealthCheckResponse {
        status: "Rust says: I am maybe healthy".to_string(),
    };
    HttpResponse::Ok().json(response)
}

#[get("/containertest")]
async fn list_containers() -> impl Responder {
    // Define the Python container's URL
    let python_container_url = "http://stockripper-agent-app.stockripper.internal:5000/api/storage/containers";

    // Make an HTTP GET request to the Python container
    match reqwest::get(python_container_url).await {
        Ok(response) => {
            if response.status().is_success() {
                // Parse the JSON response from the Python container
                match response.json::<ContainerListResponse>().await {
                    Ok(container_list) => HttpResponse::Ok().json(container_list),
                    Err(err) => HttpResponse::InternalServerError()
                        .body(format!("Failed to parse JSON response: {}", err)),
                }
            } else {
                HttpResponse::InternalServerError().body(format!(
                    "Python API responded with error: {}",
                    response.status()
                ))
            }
        }
        Err(err) => HttpResponse::InternalServerError().body(format!(
            "Failed to reach Python container API: {}",
            err.to_string()
        )),
    }
}

#[actix_web::main]
async fn main() -> std::io::Result<()> {
    HttpServer::new(|| {
        App::new()
            .service(health_check)
            .service(container_check)
            .service(list_containers)
    })
    .bind("0.0.0.0:5002")?
    .run()
    .await
}
