using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using Microsoft.Win32;
using Newtonsoft.Json;

namespace BattlePatcher.Client
{
    public static class Program
    {
        private class Config
        {
            public string GamePath { get; set; }
            public bool RunOnStartup { get; set; }
        }

        private static readonly WebClient WebClient = new WebClient();
        private static readonly Mutex UpdateMutex = new Mutex();
        private static readonly Uri BaseUri = new Uri("https://bb.doink.dev");

        private static readonly string BattlePatcherPath = getBattlePatcherDirectory();
        private static readonly string ConfigPath = Path.Combine(BattlePatcherPath, "Config.json");

        private static ContextMenu menu;
        private static MenuItem startGameItem;
        private static MenuItem runOnStartupItem;
        private static NotifyIcon notifyIcon;
        private static Config config;

        private static string calculateChecksum(string filePath)
        {
            using (var sha = SHA256.Create())
            {
                using (var fileStream = File.OpenRead(filePath))
                    return BitConverter.ToString(sha.ComputeHash(fileStream));
            }
        }

        private static async Task downloadRequiredFiles(List<string> fileNames)
        {
            var downloadTasks = fileNames.Select(fileName =>
            {
                var filePath = Path.Combine(BattlePatcherPath, fileName);
                var fileUrl = new Uri(BaseUri, $"/static/{fileName}");

                if (File.Exists(filePath))
                    File.Delete(filePath);

                return WebClient.DownloadFileTaskAsync(fileUrl, filePath);
            });

            await Task.WhenAll(downloadTasks);
        }

        private static void backgroundUpdateCheck()
        {
            while (true)
            {
                UpdateMutex.WaitOne();

                var fileHashesString = WebClient.DownloadString(new Uri(BaseUri, "/static/checksums.json"));
                var fileHashes = JsonConvert.DeserializeObject<Dictionary<string, string>>(fileHashesString);
                var filesToDownload = new List<string>();

                foreach (var kvp in fileHashes)
                {
                    var download = false;
                    var filePath = Path.Combine(BattlePatcherPath, kvp.Key);

                    if (!File.Exists(filePath))
                    {
                        download = true;
                    }
                    else
                    {
                        var checksum = calculateChecksum(filePath);

                        if (!checksum.Equals(kvp.Value, StringComparison.CurrentCultureIgnoreCase))
                            download = true;
                    }

                    if (download)
                        filesToDownload.Add(kvp.Key);
                }

                if (filesToDownload.Count > 0)
                {
                    var downloadTask = downloadRequiredFiles(filesToDownload);

                    downloadTask.Wait();
                }

                UpdateMutex.ReleaseMutex();

                Thread.Sleep(TimeSpan.FromMinutes(5));
            }
        }

        private static string getBattlePatcherDirectory()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var battlePatcher = Path.Combine(appData, "BattlePatcher");

            if (!Directory.Exists(battlePatcher))
                Directory.CreateDirectory(battlePatcher);

            return battlePatcher;
        }

        private static void withConfigSave(Action action)
        {
            action();

            File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(config, Formatting.Indented));
        }

        private static void startGameHandler(object sender, EventArgs args)
        {
            var thread = new Thread(() =>
            {
                startGameItem.Enabled = false;

                UpdateMutex.WaitOne();

                var battleBit = Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(config.GamePath, "BattleBit.exe"),
                    WorkingDirectory = config.GamePath
                });

                var kernelLibrary = Native.GetModuleHandle("kernel32.dll");
                var loadLibrary = Native.GetProcAddress(kernelLibrary, "LoadLibraryA");

                var injectorPath = Path.Combine(BattlePatcherPath, "BattlePatcher.dll");
                var allocatedMemory = Native.VirtualAllocEx(battleBit.Handle, IntPtr.Zero, (uint)injectorPath.Length + 1, 0x00001000, 4);

                Native.WriteProcessMemory(battleBit.Handle, allocatedMemory,
                    Encoding.Default.GetBytes(injectorPath), (uint)injectorPath.Length + 1, out var bytesWritten);

                var threadHandle = Native.CreateRemoteThread(battleBit.Handle, IntPtr.Zero, 0,
                    loadLibrary, allocatedMemory, 0, IntPtr.Zero);

                Native.WaitForSingleObject(battleBit.Handle, 0xFFFFFFFF);
                Native.CloseHandle(threadHandle);
                Native.VirtualFreeEx(battleBit.Handle, allocatedMemory, injectorPath.Length + 1, 0x8000);
                Native.CloseHandle(battleBit.Handle);

                try
                {
                    battleBit.WaitForExit();
                }
                catch (Exception)
                {
                    // :)
                }

                UpdateMutex.ReleaseMutex();

                startGameItem.Enabled = true;
            });

            thread.Start();
        }

        private static void runOnStartupHandler(object sender, EventArgs args)
        {
            runOnStartupItem.Checked = !runOnStartupItem.Checked;

            using (var regKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true))
            {
                if (runOnStartupItem.Checked)
                    regKey.SetValue("BattlePatcher", Application.ExecutablePath);
                else
                    regKey.DeleteValue("BattlePatcher", false);
            }

            withConfigSave(() => config.RunOnStartup = runOnStartupItem.Checked);
        }

        private static void exitHandler(object sender, EventArgs args)
        {
            notifyIcon.Visible = false;

            Application.Exit();
        }

        [DllImport("Kernel32")]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

        [STAThread]
        public static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (!File.Exists(ConfigPath))
            {
                withConfigSave(() =>
                {
                    config = new Config
                    {
                        GamePath = string.Empty,
                        RunOnStartup = false,
                    };
                });
            }

            config = JsonConvert.DeserializeObject<Config>(File.ReadAllText(ConfigPath));

            if (string.IsNullOrEmpty(config.GamePath))
            {
                var messageBoxResult = MessageBox.Show(
                    "This seems to be your first time running BattlePatcher. " +
                    "We need to determine where your game is located, to do that we ask you to launch the " +
                    "game through Steam and press OK. Pressing Cancel will close BattlePatcher and let you " +
                    "setup finish later.", "First time setup", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);

                if (messageBoxResult != DialogResult.OK)
                {
                    MessageBox.Show(
                            "You cancelled the initial setup, if you need any help don't hesitate to ask in our Discord.",
                            "BattlePatcher", MessageBoxButtons.OK, MessageBoxIcon.Information);

                    return;
                }

                var battleBitProcesses = Process.GetProcessesByName("BattleBit");

                while (battleBitProcesses.Length < 1)
                {
                    Thread.Sleep(500);

                    battleBitProcesses = Process.GetProcessesByName("BattleBit");
                }

                Thread.Sleep(500);

                var battleBit = battleBitProcesses[0];

                withConfigSave(() =>
                {
                    var fileNameBuffer = new StringBuilder(1024);
                    var fileNameBufferLength = (uint)fileNameBuffer.Capacity + 1;
                    var filePath = QueryFullProcessImageName(battleBit.Handle, 0, fileNameBuffer, ref fileNameBufferLength)
                        ? fileNameBuffer.ToString() : null;

                    config.GamePath = Path.GetDirectoryName(filePath);
                    config.RunOnStartup = false;
                });

                battleBit.Kill();

                var eacLauncher = Process.GetProcessesByName("BattleBitEAC");

                foreach (var launcher in eacLauncher)
                {
                    launcher.Kill();
                }

                MessageBox.Show(
                    $"BattleBit was found in \"{config.GamePath}\". You can now " +
                    $"right click the BattlePatcher icon in your system tray to configure " +
                    $"it to run on startup and to start the game. Have fun!", "BattlePatcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            var updateThread = new Thread(backgroundUpdateCheck);

            updateThread.Start();

            menu = new ContextMenu();

            startGameItem = new MenuItem("Start BattleBit", startGameHandler);
            runOnStartupItem = new MenuItem("Run on startup", runOnStartupHandler);

            using (var regKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", false))
            {
                runOnStartupItem.Checked = regKey.GetValue("BattlePatcher") != null;
            }

            menu.MenuItems.Add(0, startGameItem);
            menu.MenuItems.Add(1, runOnStartupItem);
            menu.MenuItems.Add(2, new MenuItem("Exit", exitHandler));

            notifyIcon = new NotifyIcon
            {
                Text = "BattlePatcher",
                Visible = true,
                Icon = Properties.Resources.BattlePatcher,
                ContextMenu = menu
            };

            Application.Run();

            updateThread.Abort();
        }
    }
}
