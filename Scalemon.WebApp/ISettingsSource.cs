using static Scalemon.WebApp.Components.Pages.Settings;
using Scalemon.WebApp.Models;

namespace Scalemon.WebApp
{
    // общий для страницы "Настройки"
    public interface ISettingsSource
    {
        Task<SettingsDto> LoadAsync(CancellationToken ct = default);
        Task SaveAsync(SettingsDto dto, CancellationToken ct = default);
    }

}
