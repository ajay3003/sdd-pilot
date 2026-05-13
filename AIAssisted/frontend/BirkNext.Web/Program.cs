using BirkNext.Web;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services
    .AddBirkNextClient()
    .ConfigureHttpClient(client =>
        client.BaseAddress = new Uri("http://localhost:5000/graphql"));

builder.Services.AddSingleton<IExtractionConfiguration, ExtractionConfiguration>();
builder.Services.AddScoped<IScenarioExtractionService, ScenarioExtractionService>();

await builder.Build().RunAsync();
