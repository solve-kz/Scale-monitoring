using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scalemon.Common;
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
        services.AddSingleton<IDataAccess, DataAccess>();
        services.AddSingleton<ISignalBus, SignalBus>();
        services.AddSingleton<IScaleStateMachine>(sp =>
        new ScaleStateMachine(
        sp.GetRequiredService<IConfiguration>(),                    // config
        sp.GetRequiredService<ILogger<ScaleStateMachine>>(),       // logger
        onConnected: () => sp.GetRequiredService<SignalBus>().Send(ArduinoSignalCode.LinkOn ),
        onDisconnected: () => sp.GetRequiredService<SignalBus>().Send(ArduinoSignalCode.LinkOff),
        onUnstable: () => sp.GetRequiredService<SignalBus>().Send(ArduinoSignalCode.Unstuble ),        
        onResetToZero: () => sp.GetRequiredService<ScaleProcessor>().ResetToZero(),
        onZeroState: () => sp.GetRequiredService<SignalBus>().Send(ArduinoSignalCode.Idle ),
        onInvalidWeight: () => sp.GetRequiredService<SignalBus>().Send(ArduinoSignalCode.ComplitedSmall ),
        onError: () => sp.GetRequiredService<SignalBus>().Send(ArduinoSignalCode.SystemAlarm ),
        onRecord: raw =>
        {
            // 1) Запись в БД
            sp.GetRequiredService<IDataAccess>()
              .SaveWeighing(raw, DateTime.Now);

            // 2) Отправка сигнала на Arduino
            sp.GetRequiredService<ISignalBus>()
              .Send(ArduinoSignalCode.Complited);
        }
    )
);
        services.AddHostedService<ScalemonService>();
    })
    .Build();

host.Run();
