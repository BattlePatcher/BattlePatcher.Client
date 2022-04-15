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

using BattlePatcher.Client.Forms;

using IWshRuntimeLibrary;

using Microsoft.Win32;

using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

using Application = System.Windows.Forms.Application;
using File = System.IO.File;

namespace BattlePatcher.Client
{
    public class BattlePatcherConfig
    {
        public string GamePath { get; set; } = string.Empty;
        public bool RunOnStartup { get; set; } = true;
        public bool StartAfterUpdate { get; set; } = false;
        public int InjectionDelay { get; set; } = 2500;
    }

    public static class Program
    {
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
        private static MenuItem changeInjectionDelayItem;
        private static MenuItem exitItem;
        private static NotifyIcon notifyIcon;

        public static BattlePatcherConfig Config;

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
                        withConfigSave(() => Config.StartAfterUpdate = true);

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

            File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(Config, Formatting.Indented));
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
                    FileName = Path.Combine(Config.GamePath, "BattleBit.exe"),
                    WorkingDirectory = Config.GamePath
                });

                var hasGameAssembly = false;

                while (!hasGameAssembly)
                {
                    var newProcess = Process.GetProcessById(battleBit.Id);

                    if (newProcess == null && !battleBit.HasExited)
                    {
                        continue;
                    }
                    else if (battleBit.HasExited)
                    {
                        MessageBox.Show(
                            "There was a problem while trying to launch the game, please try launching " +
                            "the game again and if the problem persists, do not hesitate to ask for " +
                            "help in our Discord server.", "BattlePatcher", MessageBoxButtons.OK, MessageBoxIcon.Information);

                        startGameItem.Enabled = true;
                        exitItem.Enabled = true;

                        return;
                    }

                    foreach (ProcessModule module in newProcess.Modules)
                    {
                        var fileName = Path.GetFileName(module.FileName);

                        if (fileName.Equals("GameAssembly.dll"))
                        {
                            hasGameAssembly = true;
                            break;
                        }
                    }

                    if (!hasGameAssembly)
                        Thread.Sleep(100);
                }

                Thread.Sleep(Config.InjectionDelay);

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

        private static void configureStartup()
        {
            using (var regKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true))
            {
                if (runOnStartupItem == null || runOnStartupItem.Checked)
                    regKey.SetValue("BattlePatcher", Application.ExecutablePath);
                else
                    regKey.DeleteValue("BattlePatcher", false);
            }

            if (runOnStartupItem != null)
                withConfigSave(() => Config.RunOnStartup = runOnStartupItem.Checked);
        }

        private static void runOnStartupHandler(object sender, EventArgs args)
        {
            runOnStartupItem.Checked = !runOnStartupItem.Checked;

            configureStartup();
        }

        private static void changeInjectionDelayHandler(object sender, EventArgs args)
        {
            var promptForm = new ChangeInjectionDelayForm();

            if (promptForm.ShowDialog() == DialogResult.OK)
            {
                withConfigSave(() => Config.InjectionDelay = promptForm.InputDelay);

                changeInjectionDelayItem.Text = $"Change injection delay ({Config.InjectionDelay})";

                MessageBox.Show(
                    $"Injection delay was successfuly changed to {Config.InjectionDelay}",
                    "BattlePatcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
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

                if (File.Exists(clientPath))
                    File.Delete(clientPath);

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

                        if (File.Exists(previousExecutablePath))
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
                        if (File.Exists(previousExecutablePath))
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
                    Config = new BattlePatcherConfig();

                    configureStartup();
                });
            }

            Config = JsonConvert.DeserializeObject<BattlePatcherConfig>(File.ReadAllText(ConfigPath));

            menu = new ContextMenu();
            startGameItem = new MenuItem("Start BattleBit", startGameHandler);
            runOnStartupItem = new MenuItem("Run on startup", runOnStartupHandler);
            changeInjectionDelayItem = new MenuItem($"Change injection delay ({Config.InjectionDelay})", changeInjectionDelayHandler);
            exitItem = new MenuItem("Exit", exitHandler);

            startGameItem.Enabled = false;

            using (var regKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", false))
            {
                runOnStartupItem.Checked = regKey.GetValue("BattlePatcher") != null;
            }

            menu.MenuItems.Add(0, startGameItem);
            menu.MenuItems.Add(1, runOnStartupItem);
            menu.MenuItems.Add(2, changeInjectionDelayItem);
            menu.MenuItems.Add(3, exitItem);

            notifyIcon = new NotifyIcon
            {
                Text = "BattlePatcher",
                Visible = true,
                Icon = Properties.Resources.BattlePatcher,
                ContextMenu = menu
            };

            notifyIcon.DoubleClick += startGameHandler;

            var thread = new Thread(() =>
            {
                if (string.IsNullOrEmpty(Config.GamePath))
                {
                    MessageBox.Show(
                        "This seems to be your first time running BattlePatcher. We will try to " +
                        "launch BattleBit automatically for you to detect where it's located, but " +
                        "if it doesn't open after a while please try launching it through Steam yourself.",
                        "First time setup", MessageBoxButtons.OK, MessageBoxIcon.Information);

                    Process.Start("steam://run/1611740");

                    var battleBitProcesses = Process.GetProcessesByName("BattleBit");

                    for (var i = 0; i < 60 && battleBitProcesses.Length < 1; i++)
                    {
                        Thread.Sleep(500);

                        battleBitProcesses = Process.GetProcessesByName("BattleBit");
                    }

                    if (battleBitProcesses.Length < 1)
                    {
                        MessageBox.Show(
                            "Failed to detect BattleBit, please launch the game through Steam" +
                            "and then press OK.", "First time setup", MessageBoxButtons.OK, MessageBoxIcon.Information);

                        while (battleBitProcesses.Length < 1)
                        {
                            Thread.Sleep(500);

                            battleBitProcesses = Process.GetProcessesByName("BattleBit");
                        }
                    }

                    Thread.Sleep(500);

                    var battleBit = battleBitProcesses[0];

                    withConfigSave(() =>
                    {
                        var fileNameBuffer = new StringBuilder(1024);
                        var fileNameBufferLength = (uint)fileNameBuffer.Capacity + 1;
                        var filePath = QueryFullProcessImageName(battleBit.Handle, 0, fileNameBuffer, ref fileNameBufferLength)
                            ? fileNameBuffer.ToString() : null;

                        Config.GamePath = Path.GetDirectoryName(filePath);
                        Config.RunOnStartup = false;
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

                    runOnStartupItem.Checked = true;

                    configureStartup();

                    MessageBox.Show(
                        $"BattleBit was found in \"{Config.GamePath}\". You can now double click the " +
                        $"BattlePatcher icon in your system tray (bottom right of your screen) to start the " +
                        $"game. Have fun!", "BattlePatcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                startGameItem.Enabled = true;

                if (Config.StartAfterUpdate)
                {
                    withConfigSave(() => Config.StartAfterUpdate = false);

                    startGameHandler(null, null);
                }
            });

            thread.Start();

            Application.Run();
        }
    }
}
