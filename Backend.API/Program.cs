using Backend.Database.Relational;
using Backend.Interfaces.Repositories;
using Backend.Repositories;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

namespace Backend.API;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();

        // Add services to the container.
        builder.Services.AddDbContextPool<WorkflowDbContext>(options =>
        {
            options.UseSqlServer(builder.Configuration.GetConnectionString("SqlServer"));
        });

        builder.Services.AddScoped<IWorkflowTaskRepository, WorkflowTaskRepository>();
        builder.Services.AddScoped<IWorkflowBlueprintRepository, WorkflowBlueprintRepository>();

        builder.Services.AddControllers();

builder.Services.AddScoped<Backend.API.Validation.Blueprints.IBlueprintVersionValidator>(sp =>
    new Backend.API.Validation.Blueprints.BlueprintVersionValidatorPipeline(
        new Backend.API.Validation.Blueprints.IBlueprintVersionValidator[]
        {
            new Backend.API.Validation.Blueprints.BlueprintVersionGraphValidator()
        }));
        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        builder.Services.AddOpenApi();

        builder.Services.AddCors(options =>
        {
            options.AddPolicy("AllowAngularDevClient", policy =>
            {
                policy.WithOrigins("http://localhost:4200") // Angular dev server
                      .AllowAnyHeader()
                      .AllowAnyMethod()
                      .AllowCredentials(); // optional, if you're using cookies/auth
            });
        });

        var app = builder.Build();

        app.MapDefaultEndpoints();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();

            // Map the Scalar UI (default route is typically /scalar/v1)
            app.MapScalarApiReference();
        }

        app.UseAuthorization();
        app.UseCors("AllowAngularDevClient");

        app.MapControllers();

        app.Run();
    }
}
