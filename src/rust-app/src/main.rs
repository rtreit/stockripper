use actix_web::{get, App, HttpResponse, HttpServer, Responder};
use serde::{Serialize, Deserialize};
use reqwest;
use uuid::Uuid;
use tokio::time::{sleep, Duration};

#[derive(Serialize, Deserialize)]
struct HealthCheckResponse {
    status: String,
}

#[derive(Serialize, Deserialize)]
struct MailWorkerRequest {
    input: serde_json::Value,
    session_id: String,
}

#[derive(Serialize, Deserialize)]
struct MailWorkerResponse {
    status: String,
}

#[get("/health")]
async fn health_check() -> impl Responder {
    let response = HealthCheckResponse {
        status: "Rust says: I am healthy!!!".to_string(),
    };
    HttpResponse::Ok().json(response)
}

async fn call_balanced() -> Result<(), String> {
    let balanced_url = "http://localhost:5000/agents/balanced";
    let session_id = Uuid::new_v4().to_string();

    // Construct the `input` request for the agent
    let input_request = "Hello from the Rust container! Look up the wikipedia article for a random number and send a summary to randyt@outlook.com with the subject 'Hello from Rust!' and explain that I (the Rust agent) called you with the request. Include any debugging details about the request you can glean as well as your process of solving the challenge.".to_string();

    // Convert the input_request to serde_json::Value
    let input_request_json = serde_json::json!(input_request);

    // Build the full request to the balanced
    let request_body = MailWorkerRequest {
        input: input_request_json,
        session_id,
    };

    let client = reqwest::Client::new();

    // Retry logic with a maximum of 5 attempts
    for attempt in 1..=5 {
        match client
            .post(balanced_url)
            .json(&request_body)
            .send()
            .await
        {
            Ok(response) if response.status().is_success() => {
                println!("Mailworker task submitted successfully.");
                return Ok(());
            }
            Ok(response) => {
                eprintln!(
                    "Attempt {} failed: Mailworker API responded with status: {}",
                    attempt,
                    response.status()
                );
            }
            Err(err) => {
                eprintln!("Attempt {} failed: Error calling balanced: {}", attempt, err);
            }
        }

        // Wait for 5 seconds before the next attempt
        if attempt < 5 {
            sleep(Duration::from_secs(5)).await;
            println!("Retrying... Attempt {}", attempt + 1);
        }
    }

    Err("Failed to call balanced after 5 attempts.".to_string())
}

#[actix_web::main]
async fn main() -> std::io::Result<()> {
    // On startup, call the balanced agent
    tokio::spawn(async {
        match call_balanced().await {
            Ok(_) => println!("Mailworker task initiated."),
            Err(err) => eprintln!("Error calling balanced: {}", err),
        }
    });

    HttpServer::new(|| {
        App::new()
            .service(health_check)
    })
    .bind("0.0.0.0:5002")?
    .run()
    .await
}
