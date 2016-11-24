using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FtpUpdater.Properties;
using static System.String;
using static System.StringComparer;

namespace FtpUpdater
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
            IsEnabled = false
        };
        private readonly DispatcherTimer _timerFlash = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
            IsEnabled = true
        };

        private readonly Settings _settings = new Settings();
        private readonly List<string> _log = new List<string>();
        private readonly Regex _ftpListFormat = new Regex(
            @"^((?<DIR>([dD]{1}))|)(?<ATTRIBS>(.*))\s(?<SIZE>([0-9]{1,}))\s(?<DATE>((?<MONTHDAY>((?<MONTH>(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec))\s(?<DAY>([0-9\s]{2}))))\s(\s(?<YEAR>([0-9]{4}))|(?<TIME>([0-9]{2}\:[0-9]{2})))))\s(?<NAME>([A-Za-z0-9\-\._\s]{1,}))$");
        private Dictionary<string, DateTime> _fileTimes;
        private bool _readyToProcess = true;
        private DateTime _flashExpiry;
        private readonly BitmapSource _flashIcon = Imaging.CreateBitmapSourceFromHIcon(SystemIcons.Information.Handle, new Int32Rect(0, 0, 32, 32), BitmapSizeOptions.FromEmptyOptions());
        private readonly ImageSource _idleIcon = Imaging.CreateBitmapSourceFromHIcon(SystemIcons.Application.Handle, new Int32Rect(0, 0, 32, 32), BitmapSizeOptions.FromEmptyOptions());

        public MainWindow()
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase;

            InitializeComponent();
            SettingsLoad();

            Icon = _idleIcon;
            _timer.Tick += TimerOnTick;
            _timerFlash.Tick += TimerFlashOnTick;

            foreach (var arg in Regex.Matches(Environment.CommandLine, @"-(?<key>\w+)\s+(""(?<value>[^""]*)""|(?<value>\S+))").OfType<Match>()
                                     .Select(m => new { Obj = GetType().GetField(m.Groups["key"].Value, flags)?.GetValue(this), m.Groups["value"].Value })
                                     .Where(m => m.Obj != null))
            {
                bool value;

                if (arg.Obj is PasswordBox)
                {
                    ((PasswordBox)arg.Obj).Password = arg.Value;
                }
                else if (arg.Obj is CheckBox && bool.TryParse(arg.Value, out value))
                {
                    ((CheckBox)arg.Obj).IsChecked = value;
                }
                else if (arg.Obj is TextBox)
                {
                    ((TextBox)arg.Obj).Text = arg.Value;
                }
                else if (arg.Obj is Button && Compare(arg.Value, "Click", StringComparison.OrdinalIgnoreCase) == 0)
                {
                    var invokeProv = new ButtonAutomationPeer((Button)arg.Obj).GetPattern(PatternInterface.Invoke) as IInvokeProvider;
                    invokeProv?.Invoke();
                }
            }
        }

        private void SettingsLoad()
        {
            Username.Text = _settings.Username;
            Password.Password = Encoding.UTF8.GetString(Convert.FromBase64String(_settings.Password));
            FtpUrl.Text = _settings.FtpUrl;
            FtpPath.Text = _settings.FtpPath;
            LocalPath.Text = _settings.LocalPath;
            Recursive.IsChecked = _settings.Recursive;
            Exclude.Text = _settings.Exclude;
        }

        private void SettingsSave()
        {
            _settings.Username = Username.Text;
            _settings.Password = Convert.ToBase64String(Encoding.UTF8.GetBytes(Password.Password));
            _settings.FtpUrl = FtpUrl.Text;
            _settings.FtpPath = FtpPath.Text;
            _settings.LocalPath = LocalPath.Text;
            _settings.Recursive = Recursive.IsChecked ?? false;
            _settings.Exclude = Exclude.Text;
            _settings.Save();
        }

        private void Log(string log)
        {
            _log.Add($"{DateTime.Now:HHmmss} {log}");
            _log.RemoveRange(0, Math.Max(0, _log.Count - 50));

            Status.Text = Join(Environment.NewLine, _log);
            Status.ScrollToLine(_log.Count - 1);
            Application.Current.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { }));
        }

        private KeyValuePair<FtpStatusCode, string> Ftp(string method, string name, Func<byte[]> dataFunc = null)
        {
            FtpWebResponse response = null;
            string responseText = null;

            _flashExpiry = DateTime.Now.AddSeconds(5);
            Application.Current.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { }));

            try
            {
                var request = (FtpWebRequest)WebRequest.Create(Combine(_settings.FtpUrl, _settings.FtpPath, name));

                request.UseBinary = true;
                request.Method = method;
                request.KeepAlive = false;
                request.Credentials = new NetworkCredential(
                    _settings.Username,
                    Encoding.UTF8.GetString(Convert.FromBase64String(_settings.Password)));

                if (dataFunc != null)
                {
                    using (var writer = request.GetRequestStream())
                    {
                        var data = dataFunc();
                        writer.Write(data, 0, data.Length);
                        writer.Close();
                    }
                }

                response = request.GetResponse() as FtpWebResponse;

                using (var stream = response?.GetResponseStream())
                using (var reader = new StreamReader(stream))
                {
                    responseText = reader.ReadToEnd();
                }

                Log($"{method} {name} success");
            }
            catch (WebException ex)
            {
                response = ex.Response as FtpWebResponse;
                Log($"{method} {name} failure - {ex.Message}");
            }
            catch (Exception ex)
            {
                Log($"{method} {name} failure - {ex.Message}");
            }

            return new KeyValuePair<FtpStatusCode, string>(response?.StatusCode ?? FtpStatusCode.Undefined, responseText);
        }

        private string GetFtpPath(FileSystemInfo info)
        {
            return info.FullName.Substring(_settings.LocalPath.Length).Replace('\\', '/').Trim('/');
        }

        private DateTime GetFileTimeUtc(FileInfo file)
        {
            return file.CreationTimeUtc > file.LastWriteTimeUtc ? file.CreationTimeUtc : file.LastWriteTimeUtc;
        }

        private Dictionary<string, FileInfo> GetFiles()
        {
            return new DirectoryInfo(_settings.LocalPath)
                        .GetFiles("*", _settings.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                        .Where(fn => !Regex.IsMatch(GetFtpPath(fn), _settings.Exclude, RegexOptions.IgnoreCase))
                        .ToDictionary(GetFtpPath, f => f, OrdinalIgnoreCase);
        }

        private static string Combine(params string[] parts)
        {
            return Join("/", parts.Where(p => !IsNullOrEmpty(p)).Select(p => p.Trim('/'))).Trim('/');
        }

        private void UpdateTimesFromFTP_Click(object sender, RoutedEventArgs e)
        {
            SettingsSave();
            if (!_readyToProcess)
            {
                return;
            }

            UpdateTimesFromFTP.IsEnabled = UpdateAll.IsEnabled = _readyToProcess = false;

            var files = GetFiles();
            var paths = files.Select(f => GetFtpPath(f.Value.Directory)).Distinct().ToArray();
            var testfileName = Combine(paths.First(), "b77c50e1-08ce-4f85-8d05-5adff41a7bc0");
            var times = new Dictionary<string, DateTime>();
            var utcNow = DateTime.UtcNow;

            Ftp(WebRequestMethods.Ftp.UploadFile, testfileName, () => Encoding.ASCII.GetBytes(testfileName));

            foreach (var path in paths)
            {
                var details = Ftp(WebRequestMethods.Ftp.ListDirectoryDetails, path).Value;

                foreach (var detail in details.Split(new[] { "\r\n", "\r", "\n" }, 0)
                                              .Select(l => _ftpListFormat.Match(l))
                                              .Where(m => m.Success && !m.Groups["DIR"].Success))
                {
                    DateTime date;

                    if (DateTime.TryParseExact($"{detail.Groups["MONTH"]} {detail.Groups["DAY"].Value.Trim()} {detail.Groups["TIME"]}", "MMM d HH:mm", null, 0, out date) ||
                        DateTime.TryParseExact($"{detail.Groups["MONTH"]} {detail.Groups["DAY"].Value.Trim()} {detail.Groups["YEAR"]}", "MMM d yyyy", null, 0, out date))
                    {
                        times[Combine(path, detail.Groups["NAME"].Value)] = date;
                    }
                }
            }

            Ftp(WebRequestMethods.Ftp.DeleteFile, testfileName);

            foreach (var time in times.Where(t => t.Key != testfileName))
            {
                _fileTimes[time.Key] = utcNow + (time.Value - times[testfileName]);
            }

            UpdateTimesFromFTP.IsEnabled = UpdateAll.IsEnabled = _readyToProcess = true;
        }

        private void UpdateFilesOnFtp()
        {
            if (!_readyToProcess)
            {
                return;
            }

            UpdateTimesFromFTP.IsEnabled = UpdateAll.IsEnabled = _readyToProcess = false;

            var files = GetFiles();

            _fileTimes = _fileTimes ?? files.ToDictionary(f => f.Key, f => GetFileTimeUtc(f.Value), OrdinalIgnoreCase);

            foreach (var file in files)
            {
                if (!_fileTimes.ContainsKey(file.Key) || GetFileTimeUtc(file.Value) > _fileTimes[file.Key])
                {
                    var utcNow = DateTime.UtcNow;
                    var status = Ftp(WebRequestMethods.Ftp.UploadFile, file.Key, () => File.ReadAllBytes(file.Value.FullName)).Key;

                    if (status == FtpStatusCode.ActionNotTakenFilenameNotAllowed)
                    {
                        foreach (var x in Enumerable.Range(0, file.Key.Length).Where(x => file.Key[x] == '/'))
                        {
                            Ftp(WebRequestMethods.Ftp.MakeDirectory, file.Key.Substring(0, x));
                        }

                        status = Ftp(WebRequestMethods.Ftp.UploadFile, file.Key, () => File.ReadAllBytes(file.Value.FullName)).Key;
                    }

                    if (status < (FtpStatusCode)400)
                    {
                        _fileTimes[file.Key] = utcNow;
                    }
                }
            }

            foreach (var name in _fileTimes.Where(ft => !files.ContainsKey(ft.Key)).ToArray())
            {
                Ftp(WebRequestMethods.Ftp.DeleteFile, name.Key);
                _fileTimes.Remove(name.Key);
            }

            UpdateTimesFromFTP.IsEnabled = UpdateAll.IsEnabled = _readyToProcess = true;
        }

        private void TimerFlashOnTick(object sender, EventArgs e)
        {
            Icon = DateTime.Now > _flashExpiry || (_flashExpiry - DateTime.Now).Milliseconds > 500 ? _idleIcon : _flashIcon;
        }

        private void TimerOnTick(object sender, EventArgs eventArgs)
        {
            UpdateFilesOnFtp();

        }

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            SettingsSave();
            Start.IsEnabled = false;
            Stop.IsEnabled = true;
            _timer.Start();
            Log("Started");
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            SettingsSave();
            Start.IsEnabled = true;
            Stop.IsEnabled = false;
            _timer.Stop();
            Log("Stopped");
        }

        private void UpdateAll_Click(object sender, RoutedEventArgs e)
        {
            Log("Updating all");
            SettingsSave();
            _fileTimes = new Dictionary<string, DateTime>(OrdinalIgnoreCase);
            UpdateFilesOnFtp();
            Log("Updated all");
        }
    }
}
