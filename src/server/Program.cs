using Arcadia;
using Arcadia.Tls.Crypto;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Arcadia.Hosting;
using Microsoft.Extensions.Hosting;
using Arcadia.EA;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Arcadia.Storage;
using NReco.Logging.File;
using Discord.WebSocket;
using Arcadia.Handlers;
using Microsoft.Data.Sqlite;
using System.Data;

// Floor the pool above core count: on a 2-vCPU host the default min (=cores) lets two
// blocked calls starve every timer and the UDP relay for the ~1s thread-injection delay.
ThreadPool.SetMinThreads(32, 32);

var host = Host.CreateDefaultBuilder()
    .ConfigureAppConfiguration((_, config) => config
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
        .AddJsonFile("dev.appsettings.json", optional: true, reloadOnChange: false)
    )
    .ConfigureServices((context, services) =>
    {
        var config = context.Configuration;

        services
            .Configure<ArcadiaSettings>(config.GetSection(nameof(ArcadiaSettings)))
            .Configure<FileServerSettings>(config.GetSection(nameof(FileServerSettings)))
            .Configure<Skate2Settings>(config.GetSection(nameof(Skate2Settings)))
            .Configure<DiscordSettings>(config.GetSection(nameof(DiscordSettings)))
            .Configure<DebugSettings>(config.GetSection(nameof(DebugSettings)))
            .Configure<DnsSettings>(config.GetSection(nameof(DnsSettings)))
            .Configure<LobbySettings>(config.GetSection(nameof(LobbySettings)))
            .Configure<HostOptions>(x => x.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

        services
            .AddTransient<Rc4TlsCrypto>()
            .AddSingleton<ProtoSSL>()
            .AddSingleton<SharedCounters>()
            .AddSingleton<LobbyUdpServerPool>()
            .AddSingleton<ConnectionManager>()
            .AddSingleton<UdpSessionCache>()
            .AddSingleton<RecipeBlobStore>()
            .AddSingleton<PublicIpService>()
            .AddSingleton<IPublicIpProvider>(sp => sp.GetRequiredService<PublicIpService>())
            .AddSingleton<DiscordSocketConfig>(x => new() { GatewayIntents = Discord.GatewayIntents.Guilds | Discord.GatewayIntents.GuildMessages })
            .AddSingleton<DiscordSocketClient>();

        services
            .AddTransient<IDbConnection>(x => new SqliteConnection("Data Source=arcadia.db"))
            .AddActivatedSingleton<Database>();

        services
            .AddScoped<IEAConnection, EAConnection>()
            .AddScoped<FeslHandler>()
            .AddScoped<TheaterHandler>()
            .AddScoped<MessengerHandler>();

        services
            .AddHostedService(sp => sp.GetRequiredService<PublicIpService>())
            .AddHostedService<PlasmaHostedService>()
            .AddHostedService<StaticFileHostedService>()
            .AddHostedService<DiscordHostedService>()
            .AddHostedService<DnsHostedService>();

        services.AddLogging(log =>
        {
            log.ClearProviders();
            log.AddSimpleConsole(x => {
                x.IncludeScopes = true;
                x.SingleLine = true;
                x.TimestampFormat = "[HH:mm:ss::fff] ";
                x.ColorBehavior = LoggerColorBehavior.Enabled;
            });

            // DropWrite (not the default Wait) so a stalled stdout can't backpressure the recv loop.
            services.Configure<ConsoleLoggerOptions>(x =>
            {
                x.QueueFullMode = ConsoleLoggerQueueFullMode.DropWrite;
                x.MaxQueueLength = 1024;
            });

            services.Configure<LoggerFilterOptions>(x => x.AddFilter(nameof(Microsoft), LogLevel.Warning));

            var debugSettings = config.GetSection(nameof(DebugSettings)).Get<DebugSettings>()!;
            if (debugSettings.EnableFileLogging)
            {
                services.Configure<LoggerFilterOptions>(x =>
                {
                    x.AddFilter("Arcadia.Handlers", LogLevel.Trace);
                    x.AddFilter("Arcadia.EA.EAConnection", LogLevel.Trace);
                    x.AddFilter("Arcadia.Hosting.PlasmaHostedService", LogLevel.Trace);
                    x.AddFilter("Arcadia.Hosting.StaticFileHostedService", LogLevel.Trace);
                    x.AddFilter("Arcadia.Hosting.LobbyUdpServer", LogLevel.Trace);
                });

                var startTs = DateTime.Now.Ticks;
                log.AddFile($"logs/arcadia.{startTs}.log", append: true);
            }
        });
    })
    .Build();

await host.RunAsync();