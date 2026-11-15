using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using VendorFunding.Web;
using VendorFunding.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// wwwroot/appsettings.json points at the API; the deploy pipeline writes the production URL into it.
var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? "http://localhost:5041/";
builder.Services.AddScoped(_ => new HttpClient
{
    BaseAddress = new Uri(apiBaseUrl.EndsWith('/') ? apiBaseUrl : apiBaseUrl + "/"),
    // The API scales to zero and its database auto-pauses, so the first request can take up to a minute.
    Timeout = TimeSpan.FromMinutes(2),
});
builder.Services.AddScoped<FundingApi>();

await builder.Build().RunAsync();
