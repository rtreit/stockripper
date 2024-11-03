use actix_web::{get, App, HttpResponse, HttpServer, Responder};
use serde::{Serialize, Deserialize};

#[derive(Serialize, Deserialize)]
struct HealthCheckResponse {
    status: String,
}

#[get("/health")]
async fn health_check() -> impl Responder {
    let response = HealthCheckResponse {
        status: "Rust says: I am healthy".to_string(),
    };
    HttpResponse::Ok().json(response)
}

#[actix_web::main]
async fn main() -> std::io::Result<()> {
    HttpServer::new(|| App::new().service(health_check))
        .bind("0.0.0.0:5002")?
        .run()
        .await
}
