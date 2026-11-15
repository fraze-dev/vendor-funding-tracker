using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VendorFunding.Infrastructure.Persistence;
using VendorFunding.Infrastructure.SalesImports;

var builder = FunctionsApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// Same database and retry policy as the API, signed in with this app's own managed identity.
builder.Services.AddDbContext<VendorFundingDbContext>(options => options.UseSqlServer(
    config.GetConnectionString("VendorFunding")
        ?? throw new InvalidOperationException("Connection string 'VendorFunding' is not configured."),
    sql => sql.EnableRetryOnFailure()));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(SalesFileStore.FromSettings(
    config["SalesStorage:ConnectionString"], config["SalesStorage:BlobServiceUri"], config["AZURE_CLIENT_ID"]));
builder.Services.AddScoped<SalesImportProcessor>();

builder.Services.AddHttpClient("VendorFundingApi", client =>
{
    client.BaseAddress = new Uri(config["VendorFundingApi:BaseUrl"] ?? "http://localhost:5041");
    // The API and database may both be waking up from scale-to-zero / auto-pause.
    client.Timeout = TimeSpan.FromMinutes(5);
});

builder.Build().Run();
