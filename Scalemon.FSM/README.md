# Scalemon.FSM

## Назначение проекта
Библиотека `Scalemon.FSM` содержит реализации конечных автоматов для управления промышленными весами и сопутствующей периферией. Проект собирается как библиотека для .NET 8, использует DI-инфраструктуру `Microsoft.Extensions.*`, общий пакет `Scalemon.Common` и стороннюю библиотеку `Stateless` для декларативного описания автоматов состояний.【F:Scalemon.FSM/Scalemon.FSM.csproj†L1-L17】【F:Scalemon.FSM/Scalemon.FSM.vbproj†L1-L19】

Проект включает современную реализацию автомата на C# (`PlateauZeroStateMachine`), адаптер к интерфейсу весов, а также legacy-реализацию на VB.NET (`ScaleStateMachine`) вместе с сгенерированной инфраструктурой пространства имён `My`. Ниже приведено подробное описание каждого файла.

## Файлы C#

### `PlateauZeroStateMachine.cs`
Класс `PlateauZeroStateMachine` реализует автомат, который фиксирует плато, определяет корректность взвешивания и выполняет автоматический сброс нуля. Он предназначен для запуска поверх цикла опроса весов и работает в рамках перечисления `FsmState` из `Scalemon.Common.Enums`.【F:Scalemon.FSM/PlateauZeroStateMachine.cs†L9-L47】

**Конфигурация и зависимости.** Все пороги и тайм-ауты автомата задаются через неизменяемую запись `Settings`. Она определяет диапазон нуля, остатка, допустимого отрицательного веса, минимальный продукт, параметры стабильности и поведение авто-ноля.【F:Scalemon.FSM/PlateauZeroStateMachine.cs†L27-L36】 В конструктор внедряются логгер, шина сигналов `ISignalBus`, делегат записи результата и колбэк для команды авто-ноля.【F:Scalemon.FSM/PlateauZeroStateMachine.cs†L38-L76】 При создании автомат валидирует конфигурацию и записывает настройки в журнал.【F:Scalemon.FSM/PlateauZeroStateMachine.cs†L77-L85】

**Состояния и внешние события.** Автомат отслеживает связь с весами (`SetConnection`), аварийный режим (`SetAlarmAsync`) и основной поток измерений (`OnSampleAsync`). Методы гарантируют корректные переходы в состояния `Disconnected` и `Alarm`, а также ограничивают шум в логах для повторяющихся нулевых значений.【F:Scalemon.FSM/PlateauZeroStateMachine.cs†L92-L160】

**Основная логика обработки веса.** `OnSampleAsync` делит обработку на три этапа: классификация текущего веса, контроль тайм-аута авто-ноля и переходы между состояниями (ноль, взвешивание, ожидание разгрузки, ошибка и др.). Переходы учитывают подтверждённое плато, остаток, отрицательный вес, повторные попытки сброса и специальные сценарии «invalid weight», которые приводят к состоянию `InvalidWeightState` до стабилизации нуля.【F:Scalemon.FSM/PlateauZeroStateMachine.cs†L162-L293】

**Фиксация и обработка результатов.** Метод `PrepareRecordAsync` обрабатывает пик веса, рассчитывает нетто с округлением по шагу, формирует флаги (`ResidualTared`, `NegativeTared`), вызывает внешнюю запись и решает, нужно ли повторно тарировать весы. При ошибках записи автомат безопасно возвращается в состояние готовности без авто-ноля.【F:Scalemon.FSM/PlateauZeroStateMachine.cs†L296-L348】 Команда авто-ноля отправляется через `SendTareAndWait`, который обновляет состояние, ловит ошибки отправки и заводит таймер ожидания нулевого веса.【F:Scalemon.FSM/PlateauZeroStateMachine.cs†L350-L366】

**Вспомогательные механизмы.** Метод `Transition` централизует смену состояний, ведёт журнал и сбрасывает счётчики стабильности; при разрыве связи он также отправляет сигнал на Arduino для отключения индикации.【F:Scalemon.FSM/PlateauZeroStateMachine.cs†L368-L420】【F:Scalemon.FSM/PlateauZeroStateMachine.cs†L382-L405】 Классификация веса и функции проверки стабильности используют заданные пороги и счётчики, выделяя категории `Zero`, `ResidualPos`, `Negative`, `InvalidLight` и `ValidHeavy`. Отдельный предикат `IsInvalidLightStable` применяют для входа и выхода из состояния ошибки веса.【F:Scalemon.FSM/PlateauZeroStateMachine.cs†L423-L460】

### `PlateauZeroFsmAdapter.cs`
Адаптер `PlateauZeroFsmAdapter` реализует интерфейс `IScaleStateMachine`, делегируя события ядру `PlateauZeroStateMachine`. Он служит мостом между современным автоматом и старым контрактом, а также логирует события базы данных и нажатия кнопки, чтобы сохранить совместимость с существующей инфраструктурой.【F:Scalemon.FSM/PlateauZeroFsmAdapter.cs†L9-L31】

## Файлы VB.NET

### `ScaleStateMachine.vb`
`ScaleStateMachine` — прежняя реализация автомата весов на VB.NET. Она хранится для обратной совместимости и также реализует `Scalemon.Common.IScaleStateMachine`. Класс использует библиотеку `Stateless` для конфигурации состояний и семафор `SemaphoreSlim` для последовательной обработки событий.【F:Scalemon.FSM/ScaleStateMachine.vb†L1-L52】

**Конфигурация автомата.** Конструктор принимает набор делегатов (подключение, нестабильность, сброс нуля, сигнализация, запись и т.д.), параметры гистерезиса и тайм-аут семафора. Он настраивает переходы между состояниями `Disconnected`, `Connected`, `Unstable`, `Stabilized` и вложенными категориями веса (`NegativeWeight`, `ZeroWeight`, `LightWeight`, `InvalidWeight`, `Recorded`, `ErrorAfterWeighing`). Состояния ошибок (`ScaleError`, `DatabaseError`) обрабатывают аппаратные и инфраструктурные сбои, активируя внешние колбэки и разрешая выход после восстановления.【F:Scalemon.FSM/ScaleStateMachine.vb†L27-L150】

**Определение категорий веса.** Метод `DetermineStateFromWeight` разбивает сырые значения на диапазоны: отрицательные, ноль, малый положительный, валидный продукт или ошибочный сценарий «после взвешивания». Состояние `Recorded` достигается только после фиксации нуля перед следующим взвешиванием.【F:Scalemon.FSM/ScaleStateMachine.vb†L153-L170】

**Обработка событий.** Для всех публичных методов (`OnScaleConnectedAsync`, `OnScaleDisconnectedAsync`, `OnScaleUnstableAsync`, `OnScaleAlarmAsync`, `OnWeightReceivedAsync`, `OnButtonPressedAsync`, `OnDatabaseFailedAsync`, `OnDatabaseRestoredAsync`) используется один и тот же шаблон: попытка захвата семафора с тайм-аутом, логирование критических сбоёв и вызов соответствующего триггера автомата. Попытки авто-ноля выполняются в `HandleResetAttemptAsync`, где исключения сохраняются, логируются и приводят к переходу в состояние аппаратной ошибки через триггер `ScaleAlarm`.【F:Scalemon.FSM/ScaleStateMachine.vb†L172-L285】

## Сгенерированные файлы `My`
Папка `My Project` содержит три файла, автоматически созданные при сборке VB.NET-проекта. Они поддерживают функциональность пространства имён `Microsoft.VisualBasic.My` и не требуют ручного редактирования:

- `MyNamespace.Static.1.Designer.cs` описывает классы `MyApplication`, `MyComputer` и модуль `MyProject`, обеспечивающие доступ к стандартной инфраструктуре VB (Application, Computer). Код управляется набором директив компиляции `_MYTYPE` и выбирает базовый класс в зависимости от типа приложения.【F:Scalemon.FSM/My Project/MyNamespace.Static.1.Designer.cs†L1-L109】
- `MyNamespace.Static.2.Designer.cs` содержит вспомогательный класс `InternalXmlHelper` для работы с XML и атрибутами пространств имён, используемый генератором при поддержке `My.Settings` и `My.Resources`. Методы предоставляют безопасные операции чтения/записи значений и манипуляции пространствами имён.【F:Scalemon.FSM/My Project/MyNamespace.Static.2.Designer.cs†L1-L200】
- `MyNamespace.Static.3.Designer.cs` объявляет атрибут `Microsoft.VisualBasic.Embedded`, применяемый к сгенерированным классам для пометки встроенных ресурсов VB.【F:Scalemon.FSM/My Project/MyNamespace.Static.3.Designer.cs†L1-L16】

## Когда использовать конкретные реализации
- `PlateauZeroStateMachine` рекомендуется для современных сценариев с расширенной логикой фиксации плато, авто-нолем и интеграцией с Arduino-сигнализацией.
- `PlateauZeroFsmAdapter` облегчает миграцию: он позволяет использовать новое ядро там, где ожидается интерфейс `IScaleStateMachine` от VB-версии.
- `ScaleStateMachine` полезен как эталон исходного поведения или на старых площадках, где критичны зависимость от VB и генераторов `My`.

Документ можно расширять по мере развития автомата или появления дополнительных реализаций FSM.
