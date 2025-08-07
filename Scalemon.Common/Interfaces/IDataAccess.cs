using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Scalemon.Common
{
    /// <summary>
    /// Делегат события ошибки базы данных.
    /// </summary>
    public delegate void DatabaseFailedEventHandler(Exception ex);

    /// <summary>
    /// Делегат события восстановления базы данных.
    /// </summary>
    public delegate void DatabaseRestoredEventHandler();

    /// <summary>
    /// Интерфейс доступа к данным для записи взвешиваний.
    /// Поддерживает асинхронную запись, удаление и уведомление о сбоях БД.
    /// </summary>
    public interface IDataAccess : IDisposable
    {
        /// <summary>
        /// Асинхронно сохраняет значение веса и текущую отметку времени в базу данных.
        /// </summary>
        /// <param name="weight">Значение веса для сохранения.</param>
        Task SaveWeighingAsync(decimal weight);

        /// <summary>
        /// Асинхронно удаляет последнюю сделанную запись (используется для отката ошибочных действий).
        /// </summary>
        Task DeleteLastWeighingAsync();

        /// <summary>
        /// Событие, возникающее при сбое подключения или выполнения запроса к базе данных.
        /// </summary>
        event DatabaseFailedEventHandler DatabaseFailed;

        /// <summary>
        /// Событие, возникающее при успешном восстановлении связи с базой данных после сбоя.
        /// </summary>
        event DatabaseRestoredEventHandler DatabaseRestored;
    }
}
