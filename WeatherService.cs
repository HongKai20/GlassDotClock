using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Threading;

namespace GlassDotClock
{
    internal sealed class WeatherHour
    {
        public DateTime Time { get; set; }
        public double Temperature { get; set; }
    }

    internal sealed class WeatherData
    {
        public string City { get; set; }
        public double Temperature { get; set; }
        public int WeatherCode { get; set; }
        public bool IsDay { get; set; }
        public int UtcOffsetSeconds { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public double Minimum { get; set; }
        public double Maximum { get; set; }
        public List<WeatherHour> Hours { get; set; }
    }

    internal sealed class WeatherService : IDisposable
    {
        private readonly Dispatcher dispatcher;
        private readonly DispatcherTimer timer;
        private volatile bool disposed;
        private bool started;

        public WeatherData Data { get; private set; }
        public string StatusText { get; private set; }
        public bool IsRefreshing { get; private set; }
        public event EventHandler Changed;

        public WeatherService(Dispatcher dispatcher)
        {
            this.dispatcher = dispatcher;
            StatusText = "正在获取天气";
            timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
            timer.Interval = TimeSpan.FromMinutes(15);
            timer.Tick += delegate { Refresh(); };
        }

        public void Start()
        {
            if (started || disposed) return;
            started = true;
            NetworkChange.NetworkAvailabilityChanged += NetworkChanged;
            Refresh();
        }

        private void NetworkChanged(object sender, NetworkAvailabilityEventArgs e)
        {
            if (!e.IsAvailable || disposed) return;
            Post(delegate { Refresh(); });
        }

        public void Refresh()
        {
            if (disposed || IsRefreshing) return;
            timer.Stop();
            IsRefreshing = true;
            if (Data == null) StatusText = "正在获取天气";
            RaiseChanged();
            ThreadPool.QueueUserWorkItem(delegate
            {
                WeatherData result = null;
                try { result = Fetch(); }
                catch (WebException) { }
                catch (IOException) { }
                catch (InvalidOperationException) { }
                catch (ArgumentException) { }
                catch (FormatException) { }
                catch (OverflowException) { }
                Post(delegate
                {
                    IsRefreshing = false;
                    if (result != null)
                    {
                        Data = result;
                        StatusText = "";
                        timer.Interval = TimeSpan.FromMinutes(15);
                    }
                    else
                    {
                        StatusText = Data == null ? "天气暂不可用 · 等待联网" : "离线 · 上次更新 " + Data.UpdatedUtc.ToLocalTime().ToString("HH:mm");
                        timer.Interval = TimeSpan.FromMinutes(2);
                    }
                    RaiseChanged();
                    timer.Start();
                });
            });
        }

        private void Post(Action action)
        {
            if (disposed || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            try
            {
                dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    if (!disposed) action();
                }));
            }
            catch (InvalidOperationException) { }
        }

        private void RaiseChanged()
        {
            EventHandler handler = Changed;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private static string DownloadText(string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = 8000;
            request.ReadWriteTimeout = 8000;
            request.UserAgent = "GlassDotClock/1.0";
            using (WebResponse response = request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream()))
            {
                string text = reader.ReadToEnd();
                if (text.Length > 262144) throw new InvalidOperationException("天气响应过大。");
                return text;
            }
        }

        private static Dictionary<string, object> Download(string url)
        {
            Dictionary<string, object> value = new JavaScriptSerializer().DeserializeObject(DownloadText(url)) as Dictionary<string, object>;
            if (value == null) throw new InvalidOperationException("天气响应格式无效。");
            return value;
        }

        private static string PublicIpv4()
        {
            string value = DownloadText("https://checkip.amazonaws.com/").Trim();
            IPAddress address;
            if (!IPAddress.TryParse(value, out address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new InvalidOperationException("无法获取公网 IPv4 地址。");
            return address.ToString();
        }

        private static object Required(Dictionary<string, object> data, string name)
        {
            object value;
            if (!data.TryGetValue(name, out value) || value == null)
                throw new InvalidOperationException("天气数据缺少 " + name);
            return value;
        }

        private static Dictionary<string, object> Object(Dictionary<string, object> data, string name)
        {
            Dictionary<string, object> value = Required(data, name) as Dictionary<string, object>;
            if (value == null) throw new InvalidOperationException("天气对象格式无效。");
            return value;
        }

        private static object[] Array(Dictionary<string, object> data, string name)
        {
            object[] value = Required(data, name) as object[];
            if (value == null || value.Length == 0) throw new InvalidOperationException("天气列表为空。");
            return value;
        }

        private static double Number(object value)
        {
            if (value == null) throw new InvalidOperationException("气温值为空。");
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(number) || double.IsInfinity(number)) throw new InvalidOperationException("天气数值无效。");
            return number;
        }

        private static string Text(Dictionary<string, object> data, string name)
        {
            object value;
            return data.TryGetValue(name, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture).Trim() : "";
        }

        internal static WeatherData Fetch()
        {
            string publicIpv4 = PublicIpv4();
            Dictionary<string, object> location = Download("https://ipwho.is/" + publicIpv4 + "?lang=zh");
            if (!Convert.ToBoolean(Required(location, "success"), CultureInfo.InvariantCulture))
                throw new InvalidOperationException("无法定位当前城市。");
            double latitude = Number(Required(location, "latitude"));
            double longitude = Number(Required(location, "longitude"));
            if (latitude < -90 || latitude > 90 || longitude < -180 || longitude > 180)
                throw new InvalidOperationException("定位坐标无效。");
            string city = Text(location, "city");
            if (city.Length == 0) city = Text(location, "region");
            if (city.Length == 0) city = Text(location, "country");
            if (city.Length == 0) throw new InvalidOperationException("定位城市为空。");
            if (city.Length > 36) city = city.Substring(0, 36);
            string url = "https://api.open-meteo.com/v1/forecast?latitude=" + latitude.ToString("0.######", CultureInfo.InvariantCulture) +
                "&longitude=" + longitude.ToString("0.######", CultureInfo.InvariantCulture) +
                "&current=temperature_2m,weather_code,is_day&hourly=temperature_2m&daily=temperature_2m_max,temperature_2m_min&forecast_days=1&timezone=auto";
            Dictionary<string, object> forecast = Download(url);
            Dictionary<string, object> current = Object(forecast, "current");
            Dictionary<string, object> hourly = Object(forecast, "hourly");
            Dictionary<string, object> daily = Object(forecast, "daily");
            DateTime date = DateTime.ParseExact(Convert.ToString(Array(daily, "time")[0], CultureInfo.InvariantCulture), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            object[] times = Array(hourly, "time");
            object[] temperatures = Array(hourly, "temperature_2m");
            if (times.Length != temperatures.Length) throw new InvalidOperationException("逐小时气温数据不匹配。");
            List<WeatherHour> hours = new List<WeatherHour>();
            for (int i = 0; i < times.Length; i++)
            {
                DateTime time;
                if (temperatures[i] == null || !DateTime.TryParseExact(Convert.ToString(times[i], CultureInfo.InvariantCulture), "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time)) continue;
                if (time.Date == date) hours.Add(new WeatherHour { Time = time, Temperature = Number(temperatures[i]) });
            }
            hours.Sort(delegate(WeatherHour first, WeatherHour second) { return first.Time.CompareTo(second.Time); });
            if (hours.Count < 2) throw new InvalidOperationException("当日逐小时预报不足。");
            int offset = Convert.ToInt32(Required(forecast, "utc_offset_seconds"), CultureInfo.InvariantCulture);
            if (offset < -43200 || offset > 50400) throw new InvalidOperationException("天气时区无效。");
            return new WeatherData
            {
                City = city,
                Temperature = Number(Required(current, "temperature_2m")),
                WeatherCode = Convert.ToInt32(Required(current, "weather_code"), CultureInfo.InvariantCulture),
                IsDay = Convert.ToInt32(Required(current, "is_day"), CultureInfo.InvariantCulture) == 1,
                UtcOffsetSeconds = offset,
                UpdatedUtc = DateTime.UtcNow,
                Minimum = Number(Array(daily, "temperature_2m_min")[0]),
                Maximum = Number(Array(daily, "temperature_2m_max")[0]),
                Hours = hours
            };
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            timer.Stop();
            if (started) NetworkChange.NetworkAvailabilityChanged -= NetworkChanged;
        }
    }
}
