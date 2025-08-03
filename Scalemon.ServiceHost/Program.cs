using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scalemon.Common;
using Scalemon.SqlDataAccess;
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
        services.AddSingleton<IDataAccess>(sp =>
                                            new SqlDataAccess(
                                            sp.GetRequiredService<ILogger<SqlDataAccess >>(),       // logger
                                            sp.GetRequiredService<IConfiguration>()["DatabaseSettings:ConnectionString"],
                                            sp.GetRequiredService<IConfiguration>()["DatabaseSettings:TableName"],
                                            sp.GetRequiredService<IConfiguration>()["DatabaseSettings:MaxRetryQueueSize"],
                                            sp.GetRequiredService<IConfiguration>()["DatabaseSettings:AlarmSize"],
                                            sp.GetRequiredService<IHostApplicationLifetime>() // <-- Добавляем зависимость
                                            ));
        services.AddSingleton<ISignalBus, SignalBus>();
        services.AddSingleton<IScaleStateMachine>(sp =>
        new ScaleStateMachine(
        sp.GetRequiredService<IConfiguration>(),                    // config
        sp.GetRequiredService<ILogger<ScaleStateMachine>>(),       // logger
        onConnected: () => sp.GetRequiredService<ISignalBus>().SendAsync(ArduinoSignalCode.LinkOn ),
        onDisconnected: () => sp.GetRequiredService<ISignalBus>().SendAsync(ArduinoSignalCode.LinkOff),
        onUnstable: () => sp.GetRequiredService<ISignalBus>().SendAsync(ArduinoSignalCode.Unstable ),        
        onResetToZero: () => sp.GetRequiredService<IScaleProcessor>().ResetToZeroAsync(),
        onZeroState: () => sp.GetRequiredService<ISignalBus>().SendAsync(ArduinoSignalCode.Idle ),
        onInvalidWeight: () => sp.GetRequiredService<ISignalBus>().SendAsync(ArduinoSignalCode.YellowRedOn  ),
        onError: () => sp.GetRequiredService<ISignalBus>().SendAsync(ArduinoSignalCode.RedOn  ),
        onResetAlarm: () => sp.GetRequiredService<ISignalBus>().SendAsync(ArduinoSignalCode .AlarmOff ),
        onRecord: async raw =>        {
            var dbLogger = sp.GetRequiredService<ILogger<ScalemonService>>();
            try
            {
                // Правильно дождаться завершения асинхронной вставки
                await sp.GetRequiredService<IDataAccess>()
                    .SaveWeighingAsync(raw);
                // Сигнал отправляется только после успешной записи
                await sp.GetRequiredService<ISignalBus>()
                    .SendAsync(ArduinoSignalCode.Completed);
            }
            catch (Exception ex)
            {
                dbLogger.LogError(ex, "Не удалось записать взвешивание");
            }            
        }
    )
);
        services.AddHostedService<ScalemonService>();
    })
    .Build();

host.Run();
