using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Scalemon.Common.Enums;

namespace Scalemon.Common
{
    /// <summary>
    /// Интерфейс для управления сигнализацией через Arduino.
    /// Отправка команд и обработка событий подключения.
    /// </summary>
    public interface ISignalBus
    {
        /// <summary>Событие установления подключения к Arduino.</summary>
        event Action ConnectionEstablished;

        /// <summary>Событие потери подключения к Arduino.</summary>
        event Action ConnectionLost;

        /// <summary>Подписаться на нажатие кнопки.</summary>
        void SubscribeButtonPressed(Func<Task> handler);

        /// <summary>Отписаться от нажатия кнопки.</summary>
        void UnsubscribeButtonPressed(Func<Task> handler);

        /// <summary>Отправляет команду на Arduino.</summary>
        Task SendAsync(ArduinoSignalCode cmd);

        /// <summary>Запускает обработку событий и подключение.</summary>
        void Start();

        /// <summary>Останавливает обработку и отключает порт.</summary>
        void Stop();
    }
}
