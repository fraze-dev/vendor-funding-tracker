using System.Text.Json.Serialization;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using VendorFunding.Api;
using VendorFunding.Api.Endpoints;
using VendorFunding.Api.Services;
using VendorFunding.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Read the connection string when the DbContext is created (not at startup) so tests can override it.
builder.Services.AddDbContext<VendorFundingDbContext>((services, options) =>
    options.UseSqlServer(
        services.GetRequiredService<IConfiguration>().GetConnectionString("VendorFunding")
            ?? throw new InvalidOperationException("Connection string 'VendorFunding' is not configured."),
        sql => sql.EnableRetryOnFailure()));

builder.Services.AddScoped<FundingSummaryService>();
builder.Services.AddHealthChecks().AddDbContextCheck<VendorFundingDbContext>("database");

// Send logs, traces and metrics to Application Insights when deployed (Azure sets this variable).
if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services.AddOpenTelemetry().UseAzureMonitor();
}

// Locally the app migrates and seeds at startup (below). When deployed, the pipeline migrates and this seeds.
if (builder.Configuration.GetValue<bool>("Database:SeedDemoData") &&
    !builder.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    builder.Services.AddHostedService<DemoDataBackgroundSeeder>();
}

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<BadRequestExceptionHandler>();
// Outside Development, ASP.NET returns a bare 400 for unreadable bodies; throwing lets the handler above explain why.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
// No UseHttpsRedirection: locally we run plain HTTP, and Azure Container Apps handles HTTPS.

// API docs stay on in every environment because this is a portfolio demo.
app.MapOpenApi();
app.MapScalarApiReference(options => options.WithTitle("Vendor Funding Tracker API"));
app.MapGet("/", () => Results.Redirect("/scalar")).ExcludeFromDescription();

// /alive only proves the process is up; Azure uses it to restart a hung container.
// /health also checks the database, which can take a while to resume from auto-pause.
app.MapHealthChecks("/alive", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health");
app.MapVendorEndpoints();
app.MapSalesEndpoints();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<VendorFundingDbContext>();
    await db.Database.MigrateAsync();

    if (app.Configuration.GetValue<bool>("Database:SeedDemoData"))
    {
        await DemoDataSeeder.SeedAsync(db);
    }
}

app.Run();
