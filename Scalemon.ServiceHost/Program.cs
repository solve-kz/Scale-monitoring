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

//
// Entry point: настраиваем конфигурацию, логирование, зависимости и запускаем службу
//
var config = new ConfigurationBuilder()
    // Задаём базовую директорию для поиска файлов конфигурации
    .SetBasePath(Directory.GetCurrentDirectory())
    // Загружаем обязательный JSON-файл с настройками
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

// Читаем из конфигурации пути для логов (с запасными значениями)
var MainLogPath = config["Logging:filePath:MainLogPath"]
                  ?? "logs\\main.log";
var DetailedLogPath = config["Logging:filePath:DetailedLogPath"]
                      ?? "logs\\detailed.log";

// Настройка Serilog: читаем правила из конфигурации и добавляем два файла логов
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(config)
    // Основной лог: уровень Information и выше
    .WriteTo.File(
        MainLogPath,
        rollingInterval: RollingInterval.Day,
        restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information)
    // Подробный лог: уровень Debug и выше
    .WriteTo.File(
        DetailedLogPath,
        rollingInterval: RollingInterval.Day,
        restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Debug)
    .CreateLogger();

// Создаём Generic Host для Windows Service
IHost host = Host.CreateDefaultBuilder(args)
    // Подключаем поддержку запуска в качестве Windows Service
    .UseWindowsService()
    // Подменяем встроенное логирование на Serilog
    .UseSerilog()
    // Регистрируем сервисы в контейнере DI
    .ConfigureServices((context, services) =>
    {
        // Регистрируем IConfiguration как singleton, чтобы получать настройки в других классах
        services.AddSingleton<IConfiguration>(config);

        // Основные компоненты системы:

        // 1) Процессор опроса весов
        services.AddSingleton<IScaleProcessor, ScaleProcessor>();

        // 2) Хранилище данных (SqlDataAccess)
        //    Передаём вручную параметры из конфигурации при создании
        services.AddSingleton<IDataAccess>(sp =>
            new SqlDataAccess(
                sp.GetRequiredService<ILogger<SqlDataAccess>>(),           // ILogger
                sp.GetRequiredService<IConfiguration>()["DatabaseSettings:ConnectionString"],
                sp.GetRequiredService<IConfiguration>()["DatabaseSettings:TableName"],
                sp.GetRequiredService<IConfiguration>()["DatabaseSettings:MaxRetryQueueSize"],
                sp.GetRequiredService<IConfiguration>()["DatabaseSettings:AlarmSize"],
                sp.GetRequiredService<IHostApplicationLifetime>()          // токен остановки приложения
            )
        );

        // 3) Сервис обмена сигналами с Arduino
        services.AddSingleton<ISignalBus, SignalBus>();

        // 4) Конечный автомат состояний весов (FSM)
        services.AddSingleton<IScaleStateMachine>(sp =>
            new ScaleStateMachine(
                sp.GetRequiredService<IConfiguration>(),                    // IConfiguration
                sp.GetRequiredService<ILogger<ScaleStateMachine>>(),       // ILogger
                                                                           // Обработчики переходов автомата — отправляем соответствующий код Arduino
                onConnected: () => sp.GetRequiredService<ISignalBus>()
                                          .SendAsync(ArduinoSignalCode.LinkOn),
                onDisconnected: () => sp.GetRequiredService<ISignalBus>()
                                          .SendAsync(ArduinoSignalCode.LinkOff),
                onUnstable: () => sp.GetRequiredService<ISignalBus>()
                                          .SendAsync(ArduinoSignalCode.Unstable),
                onResetToZero: () => sp.GetRequiredService<IScaleProcessor>()
                                          .ResetToZeroAsync(),
                onZeroState: () => sp.GetRequiredService<ISignalBus>()
                                          .SendAsync(ArduinoSignalCode.Idle),
                onInvalidWeight: () => sp.GetRequiredService<ISignalBus>()
                                          .SendAsync(ArduinoSignalCode.YellowRedOn),
                onError: () => sp.GetRequiredService<ISignalBus>()
                                          .SendAsync(ArduinoSignalCode.RedOn),
                onResetAlarm: () => sp.GetRequiredService<ISignalBus>()
                                          .SendAsync(ArduinoSignalCode.AlarmOff),
                // При успешной записи веса в базу — сигнал «Completed»
                onRecord: async raw =>
                {
                    var dbLogger = sp.GetRequiredService<ILogger<ScalemonService>>();
                    try
                    {
                        // Сохраняем взвешивание в БД
                        await sp.GetRequiredService<IDataAccess>()
                                .SaveWeighingAsync(raw);
                        // Только после успеха — отправляем сигнал Arduino
                        await sp.GetRequiredService<ISignalBus>()
                                .SendAsync(ArduinoSignalCode.Completed);
                    }
                    catch (Exception ex)
                    {
                        // Если не удалось записать — логируем ошибку
                        dbLogger.LogError(ex, "Не удалось записать взвешивание");
                    }
                }
            )
        );

        // 5) Хостируемый фоновой сервис, который связывает всё вместе
        services.AddHostedService<ScalemonService>();
    })
    .Build();

// Запускаем службу (блокирующий вызов)
host.Run();
