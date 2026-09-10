using Amazon;
using Amazon.S3;
using LiteDB;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using StorageDemo.Core.Coordination;
using StorageDemo.Core.Documents;
using StorageDemo.Core.Storage;
using StorageDemo.Infrastructure.Database.LiteDb;
using StorageDemo.Infrastructure.Database.PostgreSql;
using StorageDemo.Infrastructure.FileStorage.FileSystem;
using StorageDemo.Infrastructure.FileStorage.S3;
using StorageDemo.Infrastructure.HealthChecks;
using StorageDemo.Core.Streaming;
using StorageDemo.Infrastructure.Messaging;
using StorageDemo.Infrastructure.Streaming;
using StorageDemo.Infrastructure.Media;
using StorageDemo.Infrastructure.Monitoring;
using StorageDemo.Infrastructure.Seeding;

namespace StorageDemo.Infrastructure;

/// <summary>
/// The only place that knows which concrete provider is active. The two choices are read from
/// separate configuration keys so any combination of storage and database works.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment)
    {
        AddChangeFeed(services, configuration);

        // Thumbnails and media metadata, from the FFmpeg bundled with this application. The work
        // runs on a queue so an upload never waits for ffmpeg to decode a frame.
        Bind<MediaOptions>(services, configuration, MediaOptions.SectionName);

        // Applied before anything loads libav, since the path cannot change afterwards.
        if (configuration[$"{MediaOptions.SectionName}:LibraryPath"] is { Length: > 0 } libraryPath)
        {
            Ffmpeg.UseDirectory(libraryPath);
        }

        services.AddSingleton<IMediaAnalyzer, LibavMediaAnalyzer>();

        // Live streaming: MPEG-TS over whatever transport the loaded libraries carry. Off unless
        // configured, because it opens sockets on a caller's instruction.
        Bind<LiveOptions>(services, configuration, LiveOptions.SectionName);
        services.AddSingleton<LibavRemuxer>();
        services.AddSingleton<LiveStreamManager>();
        services.AddSingleton<ILiveStreamService>(sp => sp.GetRequiredService<LiveStreamManager>());
        services.AddHostedService<AnalysisWorker>();
        services.AddScoped<IDocumentService, DocumentService>();

        var storageProvider = configuration.GetValue("Storage:Provider", "FileSystem")!;
        var databaseProvider = configuration.GetValue("Database:Provider", "LiteDb")!;

        AddFileStorage(services, configuration, storageProvider);
        AddDatabase(services, configuration, databaseProvider);

        services.AddSingleton(new ProviderInfo(storageProvider, databaseProvider));

        // Watches the store for changes made outside this application: on notification from the
        // filesystem watcher or the S3 endpoint, and on an interval as the backstop.
        Bind<StorageMonitorOptions>(services, configuration, StorageMonitorOptions.SectionName);
        services.AddScoped<StorageReconciler>();
        services.AddSingleton<StorageChangeSignal>();
        services.AddHostedService<StorageMonitor>();

        if (storageProvider.Equals("FileSystem", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHostedService<FileSystemChangeWatcher>();
        }

        services.AddScoped<ISeeder, ReferenceDataSeeder>();
        if (isDevelopment)
        {
            services.AddScoped<ISeeder, DevelopmentDataSeeder>();
        }

        return services;
    }

    /// <summary>
    /// Everything that has to be shared once there is more than one replica: the change feed, the
    /// analysis queue, and the lock that keeps two replicas from scanning the store at once.
    ///
    /// In-memory is right for a single instance and is what a developer gets by default. Same
    /// independent-choice rule as storage and database: no application code knows which is active.
    /// </summary>
    private static void AddChangeFeed(IServiceCollection services, IConfiguration configuration)
    {
        Bind<MessagingOptions>(services, configuration, MessagingOptions.SectionName);

        var provider = configuration.GetValue($"{MessagingOptions.SectionName}:Provider", "InMemory")!;

        switch (provider.ToLowerInvariant())
        {
            case "inmemory":
                services.AddSingleton<IChangeFeed, InMemoryChangeFeed>();
                services.AddSingleton<IAnalysisQueue, InMemoryAnalysisQueue>();
                services.AddSingleton<IDistributedLock, InMemoryLock>();
                services.AddSingleton<ILiveSessionRegistry, InMemoryLiveSessionRegistry>();
                break;

            case "redis":
                services.AddSingleton<IConnectionMultiplexer>(sp =>
                {
                    var options = sp.GetRequiredService<IOptions<MessagingOptions>>().Value;

                    if (string.IsNullOrWhiteSpace(options.Redis.ConnectionString))
                    {
                        throw new InvalidOperationException(
                            "Messaging:Redis:ConnectionString is required when the provider is Redis.");
                    }

                    var configurationOptions = ConfigurationOptions.Parse(options.Redis.ConnectionString);

                    // The feed is a nicety; a Redis outage must not stop the service from starting.
                    configurationOptions.AbortOnConnectFail = false;

                    return ConnectionMultiplexer.Connect(configurationOptions);
                });

                services.AddSingleton<IChangeFeed, RedisChangeFeed>();
                services.AddSingleton<IAnalysisQueue, RedisAnalysisQueue>();
                services.AddSingleton<IDistributedLock, RedisLock>();
                services.AddSingleton<ILiveSessionRegistry, RedisLiveSessionRegistry>();
                services.AddHealthChecks().AddCheck<RedisHealthCheck>("messaging", tags: ["ready"]);
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown Messaging:Provider '{provider}'. Expected 'InMemory' or 'Redis'.");
        }
    }

    private static void AddFileStorage(
        IServiceCollection services,
        IConfiguration configuration,
        string provider)
    {
        switch (provider.ToLowerInvariant())
        {
            case "filesystem":
                Bind<FileSystemStorageOptions>(services, configuration, FileSystemStorageOptions.SectionName);
                services.AddSingleton<FileSystemStorage>();
                services.AddSingleton<IFileStorage>(sp => sp.GetRequiredService<FileSystemStorage>());
                services.AddHealthChecks().AddCheck<FileSystemHealthCheck>(
                    "filestorage",
                    tags: ["ready"]);
                break;

            case "s3":
                Bind<S3StorageOptions>(services, configuration, S3StorageOptions.SectionName);
                services.AddSingleton<IAmazonS3>(sp =>
                {
                    var options = sp.GetRequiredService<IOptions<S3StorageOptions>>().Value;
                    var config = new AmazonS3Config
                    {
                        RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region),
                        ForcePathStyle = options.ForcePathStyle,
                    };

                    if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
                    {
                        // MinIO and friends: an explicit endpoint overrides the region endpoint.
                        config.ServiceURL = options.ServiceUrl;
                        config.AuthenticationRegion = options.Region;
                    }

                    // Credentials come from the AWS provider chain: env, profile, IRSA, instance role.
                    return new AmazonS3Client(config);
                });
                services.AddSingleton<S3Storage>();
                services.AddSingleton<IFileStorage>(sp => sp.GetRequiredService<S3Storage>());
                services.AddHealthChecks().AddCheck<S3HealthCheck>("filestorage", tags: ["ready"]);
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown Storage:Provider '{provider}'. Expected 'FileSystem' or 'S3'.");
        }
    }

    private static void AddDatabase(
        IServiceCollection services,
        IConfiguration configuration,
        string provider)
    {
        switch (provider.ToLowerInvariant())
        {
            case "litedb":
                Bind<LiteDbOptions>(services, configuration, LiteDbOptions.SectionName);
                // Singleton: LiteDB owns the database file handle for the process lifetime.
                services.AddSingleton<ILiteDatabase>(sp =>
                {
                    var path = Path.GetFullPath(sp.GetRequiredService<IOptions<LiteDbOptions>>().Value.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    // Direct, not shared: shared mode takes a cross-process mutex and reopens the
                    // file on every operation, which measured about four times slower on the write
                    // path. Only this process touches the file; clients go through the API.
                    return new LiteDatabase($"Filename={path}");
                });
                services.AddScoped<IDocumentRepository, LiteDbDocumentRepository>();
                services.AddScoped<IDatabaseInitializer, LiteDbInitializer>();
                services.AddHealthChecks().AddCheck<LiteDbHealthCheck>("database", tags: ["ready"]);
                break;

            case "postgres":
                Bind<PostgresOptions>(services, configuration, PostgresOptions.SectionName);
                services.AddDbContext<AppDbContext>((sp, builder) =>
                {
                    var options = sp.GetRequiredService<IOptions<PostgresOptions>>().Value;
                    builder.UseNpgsql(options.ConnectionString);
                });
                services.AddScoped<IDocumentRepository, PostgresDocumentRepository>();
                services.AddScoped<IDatabaseInitializer, PostgresInitializer>();
                services.AddHealthChecks().AddCheck<PostgresHealthCheck>("database", tags: ["ready"]);
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown Database:Provider '{provider}'. Expected 'LiteDb' or 'Postgres'.");
        }
    }

    /// <summary>Binds and validates on first resolve, which startup forces. Misconfiguration fails fast.</summary>
    private static void Bind<T>(IServiceCollection services, IConfiguration configuration, string section)
        where T : class
        => services.AddOptions<T>()
            .Bind(configuration.GetSection(section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

}

/// <summary>Which providers are active, for logging and the /health payload.</summary>
public sealed record ProviderInfo(string Storage, string Database);
