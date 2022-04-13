using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

using IWshRuntimeLibrary;

using Microsoft.Win32;

using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

using Application = System.Windows.Forms.Application;
using File = System.IO.File;

namespace BattlePatcher.Client
{
    public static class Program
    {
        private class Config
        {
            public string GamePath { get; set; }
            public bool RunOnStartup { get; set; }
            public bool StartAfterUpdate { get; set; }
        }

        private class GithubReleaseAsset
        {
            public string BrowserDownloadUrl { get; set; }
        }

        private class GithubRelease
        {
            public string Name { get; set; }
            public DateTime CreatedAt { get; set; }
            public GithubReleaseAsset[] Assets { get; set; }
        }

        private static readonly WebClient WebClient = new WebClient();
        private static readonly Uri BaseUri = new Uri("https://bb-update.doink.dev");
        private static readonly Uri GithubBaseUri = new Uri("https://api.github.com");

        private static readonly string BattlePatcherPath = getBattlePatcherDirectory();
        private static readonly string ConfigPath = Path.Combine(BattlePatcherPath, "Config.json");

        private static DateTime lastReleaseTime;

        private static ContextMenu menu;
        private static MenuItem startGameItem;
        private static MenuItem runOnStartupItem;
        private static MenuItem exitItem;
        private static NotifyIcon notifyIcon;
        private static Config config;

        private static string calculateChecksum(string filePath)
        {
            using (var sha = SHA256.Create())
            {
                using (var fileStream = File.OpenRead(filePath))
                {
                    return BitConverter
                        .ToString(sha.ComputeHash(fileStream))
                        .Replace("-", string.Empty)
                        .ToLower();
                }
            }
        }

        private static void updatePrerequisiteFiles()
        {
            var fileHashesJson = WebClient.DownloadString(new Uri(BaseUri, "/static/checksums.json"));
            var fileHashes = JsonConvert.DeserializeObject<Dictionary<string, string>>(fileHashesJson);
            var filesToDownload = new Dictionary<Uri, string>();

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

                    if (!checksum.Equals(kvp.Value))
                        download = true;
                }

                if (download)
                {
                    var uri = new Uri(BaseUri, $"/static/{kvp.Key}");

                    filesToDownload.Add(uri, filePath);
                }
            }


            foreach (var kvp in filesToDownload)
            {
                if (File.Exists(kvp.Value))
                    File.Delete(kvp.Value);

                WebClient.DownloadFile(kvp.Key, kvp.Value);
            }
        }

        private static bool tryUpdateClient()
        {
            WebClient.Headers.Add(HttpRequestHeader.UserAgent, "BattlePatcher.Client");

            var latestReleaseJson = WebClient.DownloadString(new Uri(GithubBaseUri, "/repos/BattlePatcher/BattlePatcher.Client/releases/latest"));
            var latestRelease = JsonConvert.DeserializeObject<GithubRelease>(latestReleaseJson, new JsonSerializerSettings
            {
                ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() }
            });

            if (latestRelease.CreatedAt > lastReleaseTime)
            {
                var updatedName = Path.GetRandomFileName();
                var updatedPath = Path.Combine(BattlePatcherPath, updatedName);

                WebClient.DownloadFile(latestRelease.Assets[0].BrowserDownloadUrl, updatedPath);

                var currentChecksum = calculateChecksum(Application.ExecutablePath);
                var updatedChecksum = calculateChecksum(updatedPath);

                if (!currentChecksum.Equals(updatedChecksum))
                {
                    var updateResult = MessageBox.Show(
                        $"A new client release ({latestRelease.Name}) is available on GitHub! Press OK to " +
                        $"automatically update, or press Cancel to skip this version or update it later " +
                        $"yourself.", "BattlePatcher", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);

                    if (updateResult == DialogResult.OK)
                    {
                        withConfigSave(() => config.StartAfterUpdate = true);

                        var temporaryPath = Path.Combine(BattlePatcherPath,
                            $"BattlePatcher.Client-{updatedChecksum.Substring(0, 8)}.exe");

                        File.Move(updatedPath, temporaryPath);

                        Process.Start(new ProcessStartInfo
                        {
                            FileName = temporaryPath,
                            WorkingDirectory = BattlePatcherPath,
                            Arguments = $"removeOld {Process.GetCurrentProcess().Id} {Application.ExecutablePath}"
                        });

                        notifyIcon.Visible = false;

                        Application.Exit();

                        return false;
                    }
                    else
                    {
                        File.Delete(updatedPath);

                        return false;
                    }
                }
                else
                {
                    File.Delete(updatedPath);

                    lastReleaseTime = latestRelease.CreatedAt;
                }
            }

            return true;
        }

        private static bool backgroundUpdateCheck()
        {
            updatePrerequisiteFiles();

#if DEBUG
            return true;
#else
            return tryUpdateClient();
#endif

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
                if (!backgroundUpdateCheck())
                {
                    return;
                }

                startGameItem.Enabled = false;
                exitItem.Enabled = false;

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

                startGameItem.Enabled = true;
                exitItem.Enabled = true;
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
#if !DEBUG
            if (Path.GetDirectoryName(Application.ExecutablePath) != BattlePatcherPath)
            {
                var clientPath = Path.Combine(BattlePatcherPath, "BattlePatcher.Client.exe");

                File.Copy(Application.ExecutablePath, clientPath);

                Process.Start(new ProcessStartInfo
                {
                    FileName = clientPath,
                    WorkingDirectory = BattlePatcherPath,
                    Arguments = $"removeTemporary {Process.GetCurrentProcess().Id} {Application.ExecutablePath}"
                });

                return;
            }
#endif

            if (args.Length >= 3)
            {
                var previousProcessId = int.Parse(args[1]);
                var previousExecutablePath = args[2];

                try
                {
                    var previousClient = Process.GetProcessById(previousProcessId);

                    while (previousClient != null && !previousClient.HasExited)
                    {
                        Thread.Sleep(100);
                    }
                }
                catch (Exception)
                {
                    // :)
                }

                switch (args[0])
                {
                    case "removeOld":
                        var clientPath = Path.Combine(BattlePatcherPath, "BattlePatcher.Client.exe");

                        File.Delete(previousExecutablePath);
                        File.Copy(Application.ExecutablePath, clientPath);

                        Process.Start(new ProcessStartInfo
                        {
                            FileName = clientPath,
                            WorkingDirectory = BattlePatcherPath,
                            Arguments = $"removeTemporary {Process.GetCurrentProcess().Id} {Application.ExecutablePath}"
                        });

                        break;

                    case "removeTemporary":
                        File.Delete(previousExecutablePath);
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = Application.ExecutablePath,
                            WorkingDirectory = BattlePatcherPath
                        });

                        break;
                }

                Environment.Exit(0);
            }

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
                        StartAfterUpdate = false
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

                var shell = new WshShell();
                var shortcutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "BattlePatcher Client.lnk");
                var shortcut = shell.CreateShortcut(shortcutPath);

                shortcut.TargetPath = Application.ExecutablePath;
                shortcut.Save();

                MessageBox.Show(
                    $"BattleBit was found in \"{config.GamePath}\". You can now " +
                    $"right click the BattlePatcher icon in your system tray to configure " +
                    $"it to run on startup or double click it to start the game. Have fun!", "BattlePatcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            menu = new ContextMenu();

            startGameItem = new MenuItem("Start BattleBit", startGameHandler);
            runOnStartupItem = new MenuItem("Run on startup", runOnStartupHandler);
            exitItem = new MenuItem("Exit", exitHandler);

            using (var regKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", false))
            {
                runOnStartupItem.Checked = regKey.GetValue("BattlePatcher") != null;
            }

            menu.MenuItems.Add(0, startGameItem);
            menu.MenuItems.Add(1, runOnStartupItem);
            menu.MenuItems.Add(2, exitItem);

            notifyIcon = new NotifyIcon
            {
                Text = "BattlePatcher",
                Visible = true,
                Icon = Properties.Resources.BattlePatcher,
                ContextMenu = menu
            };

            notifyIcon.DoubleClick += startGameHandler;

            if (config.StartAfterUpdate)
            {
                config.StartAfterUpdate = false;

                startGameHandler(null, null);
            }

            Application.Run();
        }
    }
}
