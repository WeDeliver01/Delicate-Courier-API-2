// This code is part of a .NET 8.0 application.
// This is the main entry point for the DelicateCouriers application host.
// It sets up the distributed application with necessary services and configurations.
// It uses local PostgreSQL databases for development purposes.
// We can define all services and their dependencies here.
var builder = DistributedApplication.CreateBuilder(args);

// Add PostgreSQL container for local development.
var postgres = builder.AddPostgres("postgres");

// Create the database from the Postgres server
var delicatedb = postgres.AddDatabase("delicatedb");

// Add the API with reference to the database
var api = builder.AddProject<Projects.DelicateCouriers_ApiService>("apiservice")
    .WithReference(delicatedb)
    .WithExternalHttpEndpoints();

// Add the Next.js frontend
var frontend = builder.AddNpmApp("frontend", "../../delicate-couriers-frontend")
    .WithReference(api)
    .WithHttpEndpoint(env: "PORT")
    .WithExternalHttpEndpoints();

builder.Build().Run();