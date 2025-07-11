var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Backend_API>("backend-api");

builder.Build().Run();
