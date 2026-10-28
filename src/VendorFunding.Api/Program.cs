using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
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
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseHttpsRedirection();

// API docs stay on in every environment because this is a portfolio demo.
app.MapOpenApi();
app.MapScalarApiReference(options => options.WithTitle("Vendor Funding Tracker API"));
app.MapGet("/", () => Results.Redirect("/scalar")).ExcludeFromDescription();

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
