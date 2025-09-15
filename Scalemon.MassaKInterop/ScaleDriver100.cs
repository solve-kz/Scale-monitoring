using System;
using Microsoft.VisualBasic.CompilerServices;

namespace Scalemon.MassaKInterop
{

    public class ScaleDriver100 : IScaleDriver
    {

        private MassaKDriver100.Scales _scale;
        private long _response;


        public ScaleDriver100()
        {
            _scale = new MassaKDriver100.Scales();
            _response = 0L;
        }

        public string PortConnection
        {
            get
            {
                return _scale.Connection;
            }
            set
            {
                _scale.Connection = value;
            }
        }

        public decimal Weight
        {
            get
            {
                return _scale.Weight / 100m;
            }
        }

        public bool Stable
        {
            get
            {
                return _scale.Stable == 1;
            }
        }

        public string LastResponseText
        {
            get
            {
                string _message;
                switch (_response)
                {
                    case 0L:
                        {
                            _message = "Ошибок нет";
                            break;
                        }

                    case 1L:
                        {
                            _message = "Связь с весами не установлена";
                            break;
                        }

                    case 2L:
                        {
                            _message = "Ошибка обмена данных с весами";
                            break;
                        }

                    case 3L:
                        {
                            _message = "Весы не готовы к передаче данных";
                            break;
                        }

                    case 4L:
                        {
                            _message = "Параметр не поддерживается весами";
                            break;
                        }

                    case 5L:
                        {
                            _message = "Установка параметра невозможна";
                            break;
                        }

                    case 7L:
                        {
                            _message = "Невозможно выполнить команду или команда не поддерживается";
                            break;
                        }

                    case 8L:
                        {
                            _message = "Нагрузка на весовом устройстве превышает НПВ";
                            break;
                        }

                    case 9L:
                        {
                            _message = "Весовое устройство не в режиме взвешивания";
                            break;
                        }

                    case 10L:
                        {
                            _message = "Ошибка входных данных";
                            break;
                        }

                    case 11L:
                        {
                            _message = "Ошибка сохранения данных";
                            break;
                        }

                    case 16L:
                        {
                            _message = "Интерфейс Wi-Fi не поддерживается";
                            break;
                        }

                    case 17L:
                        {
                            _message = "Интерфейс Ethernet не поддерживается";
                            break;
                        }

                    case 21L:
                        {
                            _message = "Установка нуля невозможна из-за наличия нагрузки на платформе";
                            break;
                        }

                    case 23L:
                        {
                            _message = "Нет связи с модулем взвешивания";
                            break;
                        }

                    case 24L:
                        {
                            _message = "Установлена нагрузка на платформу при включении весового устройства";
                            break;
                        }

                    case 25L:
                        {
                            _message = "Весовое устройство неисправно";
                            break;
                        }

                    default:
                        {
                            _message = "Неописанная в документации ошибка";
                            break;
                        }
                }
                return _message;

            }
        }

        public long LastResponseNum
        {
            get
            {
                return _response;
            }
        }

        public bool isConnected
        {
            get
            {
                switch (_response)
                {
                    case 1L:
                    case 2L:
                    case 3L:
                    case 23L:
                        {
                            return false;
                        }

                    default:
                        {
                            return true;
                        }
                }
            }
        }

        public bool isScaleAlarm
        {
            get
            {
                switch (_response)
                {
                    case 0L:
                    case 1L:
                    case 2L:
                    case 3L:
                    case 23L:
                        {
                            return false;
                        }

                    default:
                        {
                            return true;
                        }
                }
            }
        }

        public void OpenConnection()
        {
            try
            {
                _response = _scale.OpenConnection();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Ошибка драйвера OpenConnection(): {LastResponseText}", ex);
            }

        }

        public void CloseConnection()
        {
            try
            {
                _response = _scale.CloseConnection();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Ошибка драйвера CloseConnection(): {LastResponseText}", ex);
            }

        }

        public void SetToZero()
        {
            try
            {
                var result = _scale.SetZero();
                _response = Convert.ToInt64(result);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Ошибка драйвера SetToZero(): {LastResponseText}", ex);
            }

        }

        public void ReadWeight()
        {
            try
            {
                _response = _scale.ReadWeight();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Ошибка драйвера ReadWeight(): {LastResponseText}", ex);
            }

        }
    }
}