using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;

using Microsoft.Win32;

using Bloxstrap.Models.SettingTasks.Base;

namespace Bloxstrap
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public const string ProjectName = "Bloxstrap";
        public const string ProjectOwner = "pizzaboxer";
        public const string ProjectRepository = "the-the-1/bloxstrap-with-multi-instance-launching";
        public const string ProjectDownloadLink = "https://bloxstraplabs.com";
        public const string ProjectHelpLink = "https://github.com/pizzaboxer/bloxstrap/wiki";
        public const string ProjectSupportLink = "https://github.com/pizzaboxer/bloxstrap/issues/new";

        public const string RobloxPlayerAppName = "RobloxPlayerBeta";
        public const string RobloxStudioAppName = "RobloxStudioBeta";

        // simple shorthand for extremely frequently used and long string - this goes under HKCU
        public const string UninstallKey = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{ProjectName}";

        public static LaunchSettings LaunchSettings { get; private set; } = null!;

        public static BuildMetadataAttribute BuildMetadata = Assembly.GetExecutingAssembly().GetCustomAttribute<BuildMetadataAttribute>()!;
        public static string Version = Assembly.GetExecutingAssembly().GetName().Version!.ToString();

        public static bool IsActionBuild => !String.IsNullOrEmpty(BuildMetadata.CommitRef);

        public static bool IsProductionBuild => IsActionBuild && BuildMetadata.CommitRef.StartsWith("tag", StringComparison.Ordinal);

        public static readonly MD5 MD5Provider = MD5.Create();

        public static readonly Logger Logger = new();

        public static readonly Dictionary<string, BaseTask> PendingSettingTasks = new();

        public static readonly JsonManager<Settings> Settings = new();

        public static readonly JsonManager<State> State = new();

        public static readonly FastFlagManager FastFlags = new();

        public static readonly HttpClient HttpClient = new(
            new HttpClientLoggingHandler(
                new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }
            )
        );

        private static bool _showingExceptionDialog = false;
        
        private static bool _terminating = false;

        public static void Terminate(ErrorCode exitCode = ErrorCode.ERROR_SUCCESS)
        {
            if (_terminating)
                return;

            int exitCodeNum = (int)exitCode;

            Logger.WriteLine("App::Terminate", $"Terminating with exit code {exitCodeNum} ({exitCode})");

            Current.Dispatcher.Invoke(() => Current.Shutdown(exitCodeNum));
            // Environment.Exit(exitCodeNum);

            _terminating = true;
        }

        void GlobalExceptionHandler(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;

            Logger.WriteLine("App::GlobalExceptionHandler", "An exception occurred");

            FinalizeExceptionHandling(e.Exception);
        }

        public static void FinalizeExceptionHandling(AggregateException ex)
        {
            foreach (var innerEx in ex.InnerExceptions)
                Logger.WriteException("App::FinalizeExceptionHandling", innerEx);

            FinalizeExceptionHandling(ex.GetBaseException(), false);
        }

        public static void FinalizeExceptionHandling(Exception ex, bool log = true)
        {
            if (log)
                Logger.WriteException("App::FinalizeExceptionHandling", ex);

            if (_showingExceptionDialog)
                return;

            _showingExceptionDialog = true;

            if (!LaunchSettings.QuietFlag.Active)
                Frontend.ShowExceptionDialog(ex);

            Terminate(ErrorCode.ERROR_INSTALL_FAILURE);
        }

        public static async Task<GithubRelease?> GetLatestRelease()
        {
            const string LOG_IDENT = "App::GetLatestRelease";

            GithubRelease? releaseInfo = null;

            try
            {
                releaseInfo = await Http.GetJson<GithubRelease>($"https://api.github.com/repos/{ProjectRepository}/releases/latest");

                if (releaseInfo is null || releaseInfo.Assets is null)
                    Logger.WriteLine(LOG_IDENT, "Encountered invalid data");
            }
            catch (Exception ex)
            {
                Logger.WriteException(LOG_IDENT, ex);
            }

            return releaseInfo;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            const string LOG_IDENT = "App::OnStartup";

            Locale.Initialize();

            base.OnStartup(e);

            Logger.WriteLine(LOG_IDENT, $"Starting {ProjectName} v{Version}");

            if (IsActionBuild)
                Logger.WriteLine(LOG_IDENT, $"Compiled {BuildMetadata.Timestamp.ToFriendlyString()} from commit {BuildMetadata.CommitHash} ({BuildMetadata.CommitRef})");
            else
                Logger.WriteLine(LOG_IDENT, $"Compiled {BuildMetadata.Timestamp.ToFriendlyString()} from {BuildMetadata.Machine}");

            Logger.WriteLine(LOG_IDENT, $"Loaded from {Paths.Process}");

            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();

            HttpClient.Timeout = TimeSpan.FromSeconds(30);
            HttpClient.DefaultRequestHeaders.Add("User-Agent", ProjectRepository);

            LaunchSettings = new LaunchSettings(e.Args);

            // installation check begins here
            using var uninstallKey = Registry.CurrentUser.OpenSubKey(UninstallKey);
            string? installLocation = null;
            bool fixInstallLocation = false;
            
            if (uninstallKey?.GetValue("InstallLocation") is string value)
            {
                if (Directory.Exists(value))
                {
                    installLocation = value;
                }
                else
                {
                    // check if user profile folder has been renamed
                    // honestly, i'll be expecting bugs from this
                    var match = Regex.Match(value, @"^[a-zA-Z]:\\Users\\([^\\]+)", RegexOptions.IgnoreCase);

                    if (match.Success)
                    {
                        string newLocation = value.Replace(match.Value, Paths.UserProfile, StringComparison.InvariantCultureIgnoreCase);

                        if (Directory.Exists(newLocation))
                        {
                            installLocation = newLocation;
                            fixInstallLocation = true;
                        }
                    }
                }
            }

            // silently change install location if we detect a portable run
            if (installLocation is null && Directory.GetParent(Paths.Process)?.FullName is string processDir)
            {
                var files = Directory.GetFiles(processDir).Select(x => Path.GetFileName(x)).ToArray();

                // check if settings.json and state.json are the only files in the folder
                if (files.Length <= 3 && files.Contains("Settings.json") && files.Contains("State.json"))
                {
                    installLocation = processDir;
                    fixInstallLocation = true;
                }
            }

            if (installLocation is null)
            {
                Logger.Initialize(true);
                LaunchHandler.LaunchInstaller();
            }
            else
            {
                if (fixInstallLocation)
                {
                    var installer = new Installer
                    {
                        InstallLocation = installLocation,
                        IsImplicitInstall = true
                    };

                    if (installer.CheckInstallLocation())
                    {
                        Logger.WriteLine(LOG_IDENT, $"Changing install location to '{installLocation}'");
                        installer.DoInstall();
                    }
                }

                Paths.Initialize(installLocation);

                // ensure executable is in the install directory
                if (Paths.Process != Paths.Application && !File.Exists(Paths.Application))
                    File.Copy(Paths.Process, Paths.Application);

                Logger.Initialize(LaunchSettings.UninstallFlag.Active);

                if (!Logger.Initialized && !Logger.NoWriteMode)
                {
                    Logger.WriteLine(LOG_IDENT, "Possible duplicate launch detected, terminating.");
                    Terminate();
                }

                Settings.Load();
                State.Load();
                FastFlags.Load();

                if (!Locale.SupportedLocales.ContainsKey(Settings.Prop.Locale))
                {
                    Settings.Prop.Locale = "nil";
                    Settings.Save();
                }

                Locale.Set(Settings.Prop.Locale);

#if !DEBUG
                if (!LaunchSettings.BypassUpdateCheck)
                    Installer.HandleUpgrade();
#endif

                if (App.Settings.Prop.ConfirmLaunches && LaunchSettings.RobloxLaunchMode == LaunchMode.Player && Mutex.TryOpenExisting("ROBLOX_singletonMutex", out var _))
                {
                    // this currently doesn't work very well since it relies on checking the existence of the singleton mutex
                    // which often hangs around for a few seconds after the window closes
                    // it would be better to have this rely on the activity tracker when we implement IPC in the planned refactoring

                    var result = Frontend.ShowMessageBox(App.Settings.Prop.MultiInstanceLaunching ? Bloxstrap.Resources.Strings.Bootstrapper_ConfirmLaunch_MultiInstanceEnabled : Bloxstrap.Resources.Strings.Bootstrapper_ConfirmLaunch, MessageBoxImage.Warning, MessageBoxButton.YesNo);

                    if (result != MessageBoxResult.Yes)
                    {
                        App.Terminate();
                        return;
                    }
                }

                // handle roblox singleton mutex for multi-instance launching
                // note we're handling it here in the main thread and NOT in the
                // bootstrapper as handling mutexes in async contexts suuuuuucks

                Mutex? singletonMutex = null;

                if (Settings.Prop.MultiInstanceLaunching && LaunchSettings.RobloxLaunchMode == LaunchMode.Player)
                {
                    Logger.WriteLine(LOG_IDENT, "Attempting to create singleton mutex...");

                    try
                    {
                        Mutex.OpenExisting("ROBLOX_singletonMutex");
                        Logger.WriteLine(LOG_IDENT, "Singleton mutex already exists.");
                    }
                    catch
                    {
                        // create the singleton mutex before the game client does
                        singletonMutex = new Mutex(true, "ROBLOX_singletonMutex");
                        Logger.WriteLine(LOG_IDENT, "Created singleton mutex.");
                    }
                }

                string CookiesFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Roblox\LocalStorage\RobloxCookies.dat");
                Logger.WriteLine(LOG_IDENT, $"RobloxCookies.dat path is: {CookiesFilePath}");

                if (LaunchSettings.RobloxLaunchMode == LaunchMode.Player) {
                    if (File.Exists(CookiesFilePath)) {
                        FileAttributes attributes = File.GetAttributes(CookiesFilePath);

                        if (Settings.Prop.FixTeleports) {
                            Logger.WriteLine(LOG_IDENT, "Attempting to apply teleport fix...");

                            if (!attributes.HasFlag(FileAttributes.ReadOnly)) {
                                Logger.WriteLine(LOG_IDENT, $"RobloxCookies.dat is writable, applying teleport fix...");

                                try {
                                    FileStream fileStream = File.Open(CookiesFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                                    fileStream.SetLength(0);
                                    fileStream.Close();
                                } catch (Exception ex) {
                                    Logger.WriteLine(LOG_IDENT, $"Failed to clear contents of RobloxCookies.dat | Exception: {ex}");
                                } finally {
                                    File.SetAttributes(CookiesFilePath, FileAttributes.ReadOnly);
                                    Logger.WriteLine(LOG_IDENT, $"Successfully applied teleport fix.");
                                }
                            } else {
                                Logger.WriteLine(LOG_IDENT, $"RobloxCookies.dat is already read-only, skipping teleport fix.");
                            }
                        } else {
                            Logger.WriteLine(LOG_IDENT, "Attempting to remove teleport fix...");

                            if (attributes.HasFlag(FileAttributes.ReadOnly)) {
                                File.SetAttributes(CookiesFilePath, attributes & ~FileAttributes.ReadOnly);
                                Logger.WriteLine(LOG_IDENT, $"Successfully removed teleport fix. (1)");
                            }
                        }
                    } else {
                        Logger.WriteLine(LOG_IDENT, $"Failed to find RobloxCookies.dat");
                        Frontend.ShowMessageBox($"Failed to find RobloxCookies.dat | Path: {CookiesFilePath}", MessageBoxImage.Error);
                    }
                }

                LaunchHandler.ProcessLaunchArgs();

                if (singletonMutex is not null)
                {
                    Logger.WriteLine(LOG_IDENT, "We have singleton mutex ownership! Running in background until all Roblox processes are closed");

                    // we've got ownership of the roblox singleton mutex!
                    // if we stop running, everything will screw up once any more roblox instances launched
                    while (Process.GetProcessesByName("RobloxPlayerBeta").Any()) 
                    {
                        Thread.Sleep(5000);
                    };

                    Logger.WriteLine(LOG_IDENT, "All Roblox processes closed!");

                    if (File.Exists(CookiesFilePath)) {
                        FileAttributes attributes = File.GetAttributes(CookiesFilePath);

                        if (attributes.HasFlag(FileAttributes.ReadOnly)) {
                            File.SetAttributes(CookiesFilePath, attributes & ~FileAttributes.ReadOnly);
                            Logger.WriteLine(LOG_IDENT, $"Successfully removed teleport fix. (2)");
                        }
                    }
                }
            }

            // you must *explicitly* call terminate when everything is done, it won't be called implicitly
        }
    }
}
