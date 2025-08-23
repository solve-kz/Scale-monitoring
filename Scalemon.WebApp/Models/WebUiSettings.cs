// Models/WebUiSettings.cs
namespace Scalemon.WebApp.Models
{
    public sealed class WebUiSettings
    {
        public string AfterLoginPage { get; set; } = "/monitoring";
        public string UICulture { get; set; } = "ru-RU";
        public bool RememberLastDate { get; set; } = true;
    }
}
