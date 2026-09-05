using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Radzen;
using Scalemon.ApiService.Controllers;
using Scalemon.ApiService.Services;
using Scalemon.Common;
using Scalemon.Common.Auth;
using Scalemon.Common.Logging;
using Scalemon.FSM;
using Scalemon.SerialLink;
using Scalemon.SignalBus;
using Scalemon.SqlDataAccess;
using Scalemon.WebApp;              // ISettingsSource, JsonFileSettingsSource, ApiClient (если у тебя в этом неймспейсе)
using Scalemon.WebApp.Components;
using Scalemon.WebApp.Data;
using Scalemon.WebApp.Models;
using Scalemon.ServiceHost.Http;
using Scalemon.ServiceHost.Logging;
using Scalemon.ServiceHost.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

const long LogUploadLimitBytes = 256L * 1024 * 1024;
const string PersistentSettingsPath = @"C:\Scalemon\Scalemon.settings.json";

// --- 1. СОЗДАНИЕ УНИВЕРСАЛЬНОГО ПОСТРОИТЕЛЯ ПРИЛОЖЕНИЯ ---
// WebApplication.CreateBuilder подходит и для служб, и для веб-серверов.
var builder = WebApplication.CreateBuilder(args);
Directory.CreateDirectory(Path.GetDirectoryName(PersistentSettingsPath)!);
builder.Configuration.AddJsonFile(PersistentSettingsPath, optional: true, reloadOnChange: true);
var config = builder.Configuration;

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = LogUploadLimitBytes;
});

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = LogUploadLimitBytes;
});

var logDatabaseProvider = new DailyLogDatabaseProvider(config["Logging:Database:MainDatabasePath"]);
logDatabaseProvider.EnsureCurrentDatabase();


// --- 2. НАСТРОЙКА ЛОГИРОВАНИЯ (SERILOG) ---
var levelSwitch = new LoggingLevelSwitch();
levelSwitch.MinimumLevel = Enum.Parse<LogEventLevel>(config["Logging:Level:Default"] ?? "Information", ignoreCase: true);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(config)
    .MinimumLevel.ControlledBy(levelSwitch)
    // ↓ глушим болтливый HttpClient
    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http.SocketsHttpHandler", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Extensions.Http", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http.HttpClient.ApiClient", LogEventLevel.Warning) // точечно для вашего typed-клиента
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Sink(new SqliteLogSink(logDatabaseProvider))
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Host.UseSerilog(); // Используем Serilog для всего хоста
builder.Services.AddSingleton(levelSwitch);


// --- 3. РЕГИСТРАЦИЯ СЕРВИСОВ ПРИЛОЖЕНИЯ ---

// Основные настройки
builder.Services.AddOptions<ServiceSettings>().Bind(config);
builder.Services.Configure<WeightRegisterReviewOptions>(config.GetSection("WeightRegisterReview"));
builder.Services.AddSingleton(logDatabaseProvider);
builder.Services.AddSingleton<SqliteLogRepository>();
builder.Services.AddSingleton<ISlaughterModeState, SlaughterModeState>();
builder.Services.AddSingleton<IWeighingModeStore>(sp =>
{
    var path = sp.GetRequiredService<IOptions<ServiceSettings>>().Value.DatabaseSettings.WeighingModeDatabasePath;
    return new SqliteWeighingModeStore(path);
});

// Фоновые сервисы (ядро системы)
builder.Services.AddSingleton<IScaleProcessor>(sp =>
{
    var settings = sp.GetRequiredService<IOptions<ServiceSettings>>().Value.ScaleSettings;
    var driver = new SerialPortScaleDriver100(sp.GetRequiredService<ILogger<SerialPortScaleDriver100>>());
    return new ScaleProcessor(
        sp.GetRequiredService<ILogger<ScaleProcessor>>(), driver, settings.PortName,
        settings.PollingIntervalMs);
});

builder.Services.AddSingleton<IDataAccess>(sp =>
{
    var settings = sp.GetRequiredService<IOptions<ServiceSettings>>().Value.DatabaseSettings;
    return new SqlDataAccess(
        sp.GetRequiredService<ILogger<SqlDataAccess>>(), settings.ConnectionString, settings.TableName,
        settings.MaxRetryQueueSize, settings.AlarmSize, sp.GetRequiredService<IHostApplicationLifetime>(),
        sp.GetRequiredService<ISlaughterModeState>(), sp.GetRequiredService<IWeighingModeStore>());
});

builder.Services.AddSingleton<ISignalBus>(sp =>
{
    var settings = sp.GetRequiredService<IOptions<ServiceSettings>>().Value.PlcSettings;
    return new SignalBus(
        sp.GetRequiredService<ILogger<SignalBus>>(), settings.PortName, settings.BaudRate, settings.ReconnectIntervalMs);
});

builder.Services.AddSingleton<IScaleStateMachine>(sp =>
{
    var log = sp.GetRequiredService<ILogger<PlateauZeroStateMachine>>();
    var settings = sp.GetRequiredService<IOptions<ServiceSettings>>().Value;
    var systemSettings = settings.SystemSettings;
    systemSettings.ValidateWeighing();

    var db = sp.GetRequiredService<IDataAccess>();
    var scale = sp.GetRequiredService<IScaleProcessor>();

    var core = new PlateauZeroStateMachine(
        systemSettings,
        log,
        onRecordAsync: net => db.SaveWeighingAsync(net),
        correctResidualAsync: (request, ct) => request.Command switch
        {
            ResidualCorrectionCommand.SetZero =>
                scale.ResetToZeroAsync(systemSettings.CommandTimeoutMs, ct),
            ResidualCorrectionCommand.SetTare =>
                scale.SetTareAsync(request.WeightKg, systemSettings.CommandTimeoutMs, ct),
            ResidualCorrectionCommand.TareCurrentWeight =>
                scale.TareCurrentWeightAsync(systemSettings.CommandTimeoutMs, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(request.Command))
        });

    return new PlateauZeroFsmAdapter(core, log);
});

// Главный фоновый сервис, который всё связывает
builder.Services.AddHostedService<ScalemonService>();

// Добавляем сервис аутентификации для WebApp
builder.Services.AddSingleton<SqliteAuthService>();
builder.Services.AddSingleton<IAuthService>(sp => sp.GetRequiredService<SqliteAuthService>());
builder.Services.AddSingleton<IUsersStore>(sp => sp.GetRequiredService<SqliteAuthService>());



// Сервисы для Web-части
builder.Services.AddControllers().AddApplicationPart(typeof(ServiceApiController).Assembly);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "ScalemonAuth";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromDays(365);
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            }
            else if (context.HttpContext.User.Identity?.IsAuthenticated == true)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
            }
            else
            {
                var returnUrl = context.Request.Path + context.Request.QueryString;
                var redirectUri = options.LoginPath + "?returnUrl=" + Uri.EscapeDataString(returnUrl);
                context.Response.Redirect(redirectUri);
            }

            return Task.CompletedTask;
        };

        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
            }
            else if (context.HttpContext.User.Identity?.IsAuthenticated == true)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
            }
            else
            {
                var returnUrl = context.Request.Path + context.Request.QueryString;
                var redirectUri = options.LoginPath + "?returnUrl=" + Uri.EscapeDataString(returnUrl);
                context.Response.Redirect(redirectUri);
            }

            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

// Сервисы для Blazor и Radzen UI
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddRadzenComponents();

// Добавляем сервис для сохранения темы в cookie
builder.Services.AddRadzenCookieThemeService(options =>
{
    options.Name = "ScalemonTheme"; // Имя cookie
    options.Duration = TimeSpan.FromDays(365); // Срок жизни cookie
});

// Библиотечные сервисы UI
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();                        // если ApiClient использует HttpClient
builder.Services.AddHttpClient(nameof(OpenAiWeightRegisterRecognizer), client =>
{
    // Тайм-аут каждого листа задаётся WeightRegisterReview:Recognition:RequestTimeoutSeconds.
    client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
});
builder.Services.AddScoped<ApiClient>();                 // если он есть и используется из компонентов
builder.Services.AddTransient<ForwardAuthCookiesHandler>();
builder.Services.AddHttpClient<ApiClient>((sp, http) =>
{
    // same-origin базовый адрес
    var ctx = sp.GetRequiredService<IHttpContextAccessor>().HttpContext;
    if (ctx?.Request is { } r)
    {
        http.BaseAddress = new Uri($"{r.Scheme}://{r.Host}{r.PathBase}/");
    }
    else
    {
        // надёжный фолбэк на appsettings
        var s = sp.GetRequiredService<IOptions<ServiceSettings>>().Value.Api;
        var basePath = (s.BasePath ?? "").Trim('/');
        var uri = s.Port > 0
            ? $"{s.Scheme}://{s.Host}:{s.Port}/{basePath}"
            : $"{s.Scheme}://{s.Host}/{basePath}";
        http.BaseAddress = new Uri(uri);
    }

    // Basic для контроллеров (политика ApiBasic)
    var cfg = sp.GetRequiredService<IOptions<ServiceSettings>>().Value.Authentication.Basic;
    var raw = $"{cfg.Username}:{cfg.Password}";
    http.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
}).AddHttpMessageHandler<ForwardAuthCookiesHandler>();

// Источник настроек UI (если используешь JsonFileSettingsSource)
builder.Services.AddScoped<ISettingsSource, JsonFileSettingsSource>();
builder.Services.Configure<JsonFileSettingsSource.WebAppOptions>(
    builder.Configuration.GetSection("WebApp"));

// ВОТ ГЛАВНОЕ: регистрация сервиса данных, который требует Monitoring
builder.Services.AddScoped<IWeighingDataService, SqlWeighingDataService>();
builder.Services.AddScoped<IWeighingEditLogService, SqlWeighingEditLogService>();
builder.Services.AddSingleton<WeightRegisterRecognitionCancellationRegistry>();
builder.Services.AddSingleton<IWeightRegisterReviewService, JsonWeightRegisterReviewService>();
builder.Services.AddSingleton<IRegisterImageUploadPreprocessor, RegisterImageUploadPreprocessor>();
builder.Services.AddSingleton<WeightRegisterComparisonEngine>();
builder.Services.AddSingleton<IVideoArchiveService, ConfiguredVideoArchiveService>();
builder.Services.AddSingleton<IWeightRegisterRecognizer, OpenAiWeightRegisterRecognizer>();
builder.Services.AddHostedService<WeightRegisterRecognitionWorker>();

// Настройка для запуска в качестве службы Windows
builder.Host.UseWindowsService();


// --- 4. ПОСТРОЕНИЕ И КОНФИГУРАЦИЯ КОНВЕЙЕРА HTTP-ЗАПРОСОВ ---
var app = builder.Build();

await app.Services.GetRequiredService<SqliteAuthService>().EnsureInitializedAsync();
try
{
    await app.Services.GetRequiredService<IWeighingModeStore>().EnsureInitializedAsync();
}
catch (Exception ex)
{
    app.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("WeighingModeStore")
        .LogError(ex, "Журнал режимов недоступен; новые взвешивания будут поставлены в очередь до восстановления журнала");
}

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseStaticFiles();
app.UseRouting();

// Swagger (только для удобства разработки и тестирования)
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Scalemon API v1"));

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

// Конечные точки (API и Blazor UI)
app.MapControllers();

app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode();


// --- 5. ЗАПУСК ПРИЛОЖЕНИЯ ---
app.Run();

