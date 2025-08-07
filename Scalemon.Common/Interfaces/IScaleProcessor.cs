using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Scalemon.Common
{
    /// <summary>
    /// Определяет контракт для процессора весов, который управляет драйвером, считывает вес и передает события конечному автомату.
    /// </summary>
    public interface IScaleProcessor : IDisposable
    {
        /// <summary>
        /// Запускает процессор весов.
        /// </summary>
        void Start();

        /// <summary>
        /// Останавливает процессор весов.
        /// </summary>
        void Stop();

        /// <summary>
        /// Выполняет асинхронный сброс веса на ноль.
        /// </summary>
        Task ResetToZeroAsync();

        /// <summary>Подписывает обработчик на новое стабильное значение веса.</summary>
        void SubscribeWeightReceived(Func<decimal, Task> handler);
        /// <summary>Отписывает обработчик от события веса.</summary>
        void UnsubscribeWeightReceived(Func<decimal, Task> handler);

        void SubscribeUnstable(Func<Task> handler);
        void UnsubscribeUnstable(Func<Task> handler);

        void SubscribeConnectionLost(Func<Task> handler);
        void UnsubscribeConnectionLost(Func<Task> handler);

        void SubscribeConnectionEstablished(Func<Task> handler);
        void UnsubscribeConnectionEstablished(Func<Task> handler);

        void SubscribeScaleAlarm(Func<Task> handler);
        void UnsubscribeScaleAlarm(Func<Task> handler);

    }
}