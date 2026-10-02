using Keryhe.Telemetry.TestDataGenerator;
using Keryhe.Telemetry.TestDataGenerator.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Tenant API keys live in user secrets. The host only loads them in the Development environment, so load
// them regardless, then re-apply environment variables and the command line so those still win.
builder.Configuration.AddUserSecrets(typeof(GeneratorWorker).Assembly, optional: true);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});
// The generator's own logging stays on the console; what it simulates is sent through OTLP, not through ILogger.

builder.Services.Configure<GeneratorOptions>(builder.Configuration.GetSection(GeneratorOptions.SectionName));
builder.Services.AddHostedService<GeneratorWorker>();

await builder.Build().RunAsync();
return Environment.ExitCode;
