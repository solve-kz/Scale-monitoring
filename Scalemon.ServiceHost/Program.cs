using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scalemon.DataAccess;
using Scalemon.FSM;
using Scalemon.SerialLink;
using Scalemon.SignalBus;
using Serilog;
using System.IO;
using System.Xml;

var config = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false)
    .Build();
var MainLogPath = config["Logging:filePath:MainLogPath"] ?? "logs\\main.log";
var DetailedLogPath = config["Logging:filePath:DetailedLogPath"] ?? "logs\\detailed.log";

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(config)
    .WriteTo.File(MainLogPath, rollingInterval: RollingInterval.Day, restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information)
    .WriteTo.File(DetailedLogPath, rollingInterval: RollingInterval.Day, restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Debug)
    .CreateLogger();

IHost host = Host.CreateDefaultBuilder(args)
    .UseWindowsService()
    .UseSerilog()
    .ConfigureServices((context, services) =>
    {
        services.AddSingleton<IConfiguration>(config);

        // добавление обработчиков
        services.AddSingleton<IScaleProcessor, ScaleProcessor>();
        services.AddSingleton<IStateMachine, StateMachine>();
        services.AddSingleton<IDataAccess, DataAccess>();
        services.AddSingleton<ISignalBus, SignalBus>();

        services.AddHostedService<ScalemonService>();
    })
    .Build();

host.Run();
