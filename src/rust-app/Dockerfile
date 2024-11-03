# Use the official Rust image as the base image
FROM rust:latest

# Create a new directory for the application
WORKDIR /usr/src/app

# Copy the Cargo.toml and Cargo.lock files
COPY Cargo.toml Cargo.lock ./

# Create the application build files, such as dependencies
RUN cargo fetch

# Copy the source code into the container
COPY . .

# Build the application
RUN cargo build --release

# Set the startup command to run the application
CMD ["cargo", "run", "--release"]

# Expose port 5002
EXPOSE 5002
