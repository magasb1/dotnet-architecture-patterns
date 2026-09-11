using Microsoft.AspNetCore.Http.Features;
using Serilog;
using StorageDemo.Api.Grpc;
using StorageDemo.Api.Controllers;
using StorageDemo.Api.Middleware;
using StorageDemo.Api.Uploads;
using StorageDemo.Core.Documents;
using StorageDemo.Infrastructure;
using StorageDemo.Infrastructure.Seeding;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

var maxUploadBytes = builder.Configuration.GetValue("Uploads:MaxBytes", 50L * 1024 * 1024);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maxUploadBytes);

builder.Services.AddSingleton<ContentTypeSniffer>();

// Reaches whichever replica owns a stream. No timeout: a live stream is meant to be held open for
// as long as the viewer watches it.
builder.Services.AddSingleton<LivePeerProxy>();
builder.Services.AddHttpClient(nameof(LivePeerProxy)).ConfigureHttpClient(
    client => client.Timeout = Timeout.InfiniteTimeSpan);

builder.Services.AddGrpc(options => options.MaxReceiveMessageSize = (int)Math.Min(maxUploadBytes, int.MaxValue));

// REST is the secondary surface, for curl, Swagger and anything that cannot speak gRPC.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// The only line that decides which storage and database implementations exist.
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment.IsDevelopment());

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGrpcService<DocumentsGrpcService>();
app.MapControllers();

// Liveness must not touch external infrastructure, or a database blip restarts healthy pods.
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") });

await InitializeAsync(app);

// Lets a Kubernetes Job migrate and seed, then exit, so replicas never migrate concurrently.
if (args.Contains("--migrate-only"))
{
    Log.Information("Migration complete; exiting because --migrate-only was passed");
    return;
}

app.Run();

static async Task InitializeAsync(WebApplication app)
{
    var providers = app.Services.GetRequiredService<ProviderInfo>();
    Log.Information(
        "Starting with {StorageProvider} file storage and {DatabaseProvider} database",
        providers.Storage,
        providers.Database);

    await using var scope = app.Services.CreateAsyncScope();

    await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

    foreach (var seeder in scope.ServiceProvider.GetServices<ISeeder>())
    {
        await seeder.SeedAsync();
    }
}

/// <summary>Exposed so the integration tests can host the real application.</summary>
public partial class Program;
