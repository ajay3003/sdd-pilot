using BirkNext.Web;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services
    .AddBirkNextClient()
    .ConfigureHttpClient(client =>
        client.BaseAddress = new Uri("http://localhost:5000/graphql"));

builder.Services.AddSingleton<IExtractionConfiguration, ExtractionConfiguration>();
builder.Services.Configure<ExtractionRuleConfiguration>(
    builder.Configuration.GetSection("ExtractionRules"));
builder.Services.AddTransient<ExtractionRuleSetCompiler>();
builder.Services.AddSingleton<IExtractionRuleEngine>(sp =>
{
    var compiler = sp.GetRequiredService<ExtractionRuleSetCompiler>();
    var ruleConfig = sp.GetRequiredService<IOptions<ExtractionRuleConfiguration>>().Value;
    var extractConfig = sp.GetRequiredService<IExtractionConfiguration>();
    var compiled = compiler.Compile(ExtractionRuleSet.Default(), ruleConfig);
    return new ExtractionRuleEngine(compiled, extractConfig);
});
builder.Services.AddScoped<IScenarioExtractionService>(sp =>
    new ScenarioExtractionService(
        sp.GetRequiredService<IExtractionConfiguration>(),
        sp.GetRequiredService<IExtractionRuleEngine>(),
        sp.GetRequiredService<ILogger<ScenarioExtractionService>>()));

await builder.Build().RunAsync();
