# Scalemon

🧠 **Scalemon** — служба Windows и набор библиотек для взаимодействия с промышленными весами и системой сигнализации на Arduino.

## 🏗 Архитектура

Проект состоит из модулей:
- `Scalemon.ServiceHost` — служба Windows (.NET 8.0)
- `Scalemon.FSM` — конечный автомат логики работы
- `Scalemon.SerialLink` — чтение данных с весов (через COM и драйвер)
- `Scalemon.SignalBus` — передача сигналов в Arduino
- `Scalemon.DataAccess` — запись данных в БД
- `Scalemon.Common` — перечисления, интерфейсы, DTO
- `Scalemon.MassaKInterop` — обёртка над COM-драйвером Massa-K
- `Scalemon.UI` — приложение для Windows Forms (.NET Framework 4.8)

## ⚙️ Установка

1. Установить драйвер **Massa-K Driver 100**
2. Убедиться, что COM-порты весов и Arduino настроены
3. Сконфигурировать `appsettings.json`
4. Запустить службу через `Scalemon.ServiceHost`

## 📝 Настройки

Пример `appsettings.json`:

```json
{
  "Logging": {
    "filePath": {
      "MainLogPath": "logs/main.log",
      "DetailedLogPath": "logs/detailed.log"
    }
  },
  "ScaleSettings": {
    "PollingIntervalMs": 200,
    "StableThreshold": 3,
    "UnstableThreshold": 3
  }
}
