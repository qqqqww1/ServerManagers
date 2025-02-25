﻿using NLog;
using NLog.Config;
using NLog.Layouts;
using NLog.Targets;
using ServerManagerTool.Common;
using ServerManagerTool.Common.Lib;
using ServerManagerTool.Common.Model;
using ServerManagerTool.Common.Utils;
using ServerManagerTool.Delegates;
using ServerManagerTool.Enums;
using ServerManagerTool.Plugin.Common;
using ServerManagerTool.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WPFSharp.Globalizer;

namespace ServerManagerTool.Lib
{
    internal class ServerApp
    {
        private readonly GlobalizedApplication _globalizer = GlobalizedApplication.Instance;
        private readonly PluginHelper _pluginHelper = PluginHelper.Instance;

        public const int MUTEX_TIMEOUT = 5;         // 5 minutes
        public const int MUTEX_ATTEMPTDELAY = 5000; // 5 seconds
        public const int BACKUP_DELETEINTERVAL = 7; // 7 days

        private const int STEAM_MAXRETRIES = 10;
        private const int RCON_MAXRETRIES = 3;
        private const int FILECOPY_MAXRETRIES = 3;

        public const int EXITCODE_NORMALEXIT = 0;
        private const int EXITCODE_EXITWITHERRORS = 98;
        public const int EXITCODE_CANCELLED = 99;
        // generic codes
        private const int EXITCODE_UNKNOWNERROR = 991;
        private const int EXITCODE_UNKNOWNTHREADERROR = 992;
        private const int EXITCODE_BADPROFILE = 993;
        private const int EXITCODE_PROFILENOTFOUND = 994;
        private const int EXITCODE_BADARGUMENT = 995;

        private const int EXITCODE_AUTOUPDATENOTENABLED = 1001;
        private const int EXITCODE_AUTOSHUTDOWNNOTENABLED = 1002;
        private const int EXITCODE_AUTOBACKUPNOTENABLED = 1003;

        private const int EXITCODE_PROCESSSKIPPED = 1010;
        private const int EXITCODE_PROCESSALREADYRUNNING = 1011;
        private const int EXITCODE_INVALIDDATADIRECTORY = 1012;
        private const int EXITCODE_INVALIDCACHEDIRECTORY = 1013;
        private const int EXITCODE_CACHENOTFOUND = 1005;
        private const int EXITCODE_STEAMCMDNOTFOUND = 1006;
        // update cache codes
        private const int EXITCODE_CACHESERVERUPDATEFAILED = 2001;

        private const int EXITCODE_CACHEMODUPDATEFAILED = 2101;
        private const int EXITCODE_CACHEMODDETAILSDOWNLOADFAILED = 2102;
        // update file codes
        private const int EXITCODE_SERVERUPDATEFAILED = 3001;
        private const int EXITCODE_MODUPDATEFAILED = 3002;
        // shutdown codes
        private const int EXITCODE_SHUTDOWN_GETCMDLINEFAILED = 4001;
        private const int EXITCODE_SHUTDOWN_TIMEOUT = 4002;
        private const int EXITCODE_SHUTDOWN_BADENDPOINT = 4003;
        private const int EXITCODE_SHUTDOWN_SERVERNOTFOUND = 4004;
        // restart code
        private const int EXITCODE_RESTART_FAILED = 5001;
        private const int EXITCODE_RESTART_BADLAUNCHER = 5002;
        private const int EXITCODE_RESTART_NOSTEAMCLIENT = 5003;

        public const string LOGPREFIX_AUTOBACKUP = "#AutoBackupLogs";
        public const string LOGPREFIX_AUTOSHUTDOWN = "#AutoShutdownLogs";
        public const string LOGPREFIX_AUTOUPDATE = "#AutoUpdateLogs";

        private const int DIRECTORIES_PER_LINE = 200;

        private static DateTime _startTime = DateTime.Now;
        private static Dictionary<ServerProfileSnapshot, ServerProfile> _profiles = null;

        private static Logger _loggerManager;
        private Logger _loggerBranch;
        private Logger _loggerProfile;

        private ServerProfileSnapshot _profile = null;
        private QueryMaster.Rcon _rconConsole = null;
        private bool _serverRunning = false;

        public bool BackupWorldFile = Config.Default.BackupWorldFile;
        public bool CheckForOnlinePlayers = Config.Default.ServerShutdown_CheckForOnlinePlayers;
        public bool SendMessages = Config.Default.ServerShutdown_SendShutdownMessages;
        public bool DeleteOldBackupFiles = Config.Default.AutoBackup_DeleteOldFiles;
        public int ExitCode = EXITCODE_NORMALEXIT;
        public bool OutputLogs = false;
        public bool SendAlerts = false;
        public bool SendEmails = false;
        public string ShutdownReason = null;
        public string UpdateReason = null;
        public ServerProcessType ServerProcess = ServerProcessType.Unknown;
        public int ShutdownInterval = Config.Default.ServerShutdown_GracePeriod;
        public ProgressDelegate ProgressCallback = null;
        public ProcessWindowStyle SteamCMDProcessWindowStyle = ProcessWindowStyle.Minimized;
        public ServerStatusChangeDelegate ServerStatusChangeCallback = null;

        public ServerApp(bool resetStartTime = false)
        {
            if (resetStartTime)
                _startTime = DateTime.Now;
        }

        private void BackupServer(CancellationToken cancellationToken)
        {
            if (_profile == null || _profile.SotFEnabled)
            {
                ExitCode = EXITCODE_BADPROFILE;
                return;
            }

            var emailMessage = new StringBuilder();

            LogProfileMessage("------------------------");
            LogProfileMessage("Started server backup...");
            LogProfileMessage("------------------------");
            LogProfileMessage($"Server Manager version: {App.Instance.Version}");

            emailMessage.AppendLine("Server Manager Backup Summary:");
            emailMessage.AppendLine();
            emailMessage.AppendLine($"Server Manager version: {App.Instance.Version}");

            // Find the server process.
            Process process = GetServerProcess();
            if (process != null)
            {
                _serverRunning = true;
                LogProfileMessage("");
                LogProfileMessage($"Server process found PID {process.Id}.");
            }

            if (_serverRunning)
            {
                try
                {
                    emailMessage.AppendLine();

                    var sent = false;

                    // perform a world save
                    if (!string.IsNullOrWhiteSpace(Config.Default.ServerBackup_WorldSaveMessage))
                    {
                        ProcessAlert(AlertType.Backup, Config.Default.ServerBackup_WorldSaveMessage);
                        sent = SendMessageAsync(Config.Default.ServerBackup_WorldSaveMessage, cancellationToken).Result;
                        if (sent)
                        {
                            emailMessage.AppendLine("sent server save message.");
                        }
                    }

                    sent = SendCommandAsync(Config.Default.ServerSaveCommand, false).Result;
                    if (sent)
                    {
                        emailMessage.AppendLine("sent server save command.");
                        Task.Delay(Config.Default.ServerShutdown_WorldSaveDelay * 1000).Wait();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"RCON> {Config.Default.ServerSaveCommand} command.\r\n{ex.Message}");
                }
            }

            if (ExitCode != EXITCODE_NORMALEXIT)
                return;
            if (cancellationToken.IsCancellationRequested)
            {
                ExitCode = EXITCODE_CANCELLED;
                return;
            }

            // make a backup of the current profile and config files.
            CreateProfileBackupArchiveFile(_profile);

            if (ExitCode != EXITCODE_NORMALEXIT)
                return;
            if (cancellationToken.IsCancellationRequested)
            {
                ExitCode = EXITCODE_CANCELLED;
                return;
            }

            // make a backup of the current world file.
            CreateServerBackupArchiveFile(emailMessage, _profile);

            if (ExitCode != EXITCODE_NORMALEXIT)
                return;
            if (cancellationToken.IsCancellationRequested)
            {
                ExitCode = EXITCODE_CANCELLED;
                return;
            }

            if (Config.Default.EmailNotify_AutoBackup)
            {
                emailMessage.AppendLine();
                emailMessage.AppendLine("See attached log file more details.");
                SendEmail($"{_profile.ProfileName} auto backup finished", emailMessage.ToString(), true);
            }

            LogProfileMessage("-----------------------");
            LogProfileMessage("Finished server backup.");
            LogProfileMessage("-----------------------");

            ExitCode = EXITCODE_NORMALEXIT;
        }

        private void ShutdownServer(bool restartServer, bool updateServer, bool steamCmdRemoveQuit, CancellationToken cancellationToken)
        {
            if (_profile == null)
            {
                ExitCode = EXITCODE_BADPROFILE;
                return;
            }

            if (restartServer)
            {
                LogProfileMessage("-------------------------");
                LogProfileMessage("Started server restart...");
                LogProfileMessage("-------------------------");
            }
            else
            {
                LogProfileMessage("--------------------------");
                LogProfileMessage("Started server shutdown...");
                LogProfileMessage("--------------------------");
            }
            LogProfileMessage($"Server Manager version: {App.Instance.Version}");

            // stop the server
            LogProfileMessage("");
            StopServer(cancellationToken);

            if (ExitCode != EXITCODE_NORMALEXIT)
                return;
            if (cancellationToken.IsCancellationRequested)
            {
                ExitCode = EXITCODE_CANCELLED;
                return;
            }

            ServerStatusChangeCallback?.Invoke(ServerStatus.Stopped);

            // make a backup of the current profile and config files.
            CreateProfileBackupArchiveFile(_profile);

            if (ExitCode != EXITCODE_NORMALEXIT)
                return;

            if (BackupWorldFile)
            {
                // make a backup of the current world file.
                CreateServerBackupArchiveFile(null, _profile);

                if (ExitCode != EXITCODE_NORMALEXIT)
                    return;
            }

            if (updateServer)
            {
                try
                {
                    LogProfileMessage("");
                    ServerStatusChangeCallback?.Invoke(ServerStatus.Updating);
                    UpgradeLocal(true, true, steamCmdRemoveQuit, cancellationToken);
                }
                finally
                {
                    ServerStatusChangeCallback?.Invoke(ServerStatus.Stopped);
                }
            }

            if (ExitCode != EXITCODE_NORMALEXIT)
                return;

            // check if this is a shutdown only, or a shutdown and restart.
            if (restartServer)
            {
                LogProfileMessage("");
                StartServer();

                if (ExitCode != EXITCODE_NORMALEXIT)
                    return;

                LogProfileMessage("------------------------");
                LogProfileMessage("Finished server restart.");
                LogProfileMessage("------------------------");
            }
            else
            {
                LogProfileMessage("-------------------------");
                LogProfileMessage("Finished server shutdown.");
                LogProfileMessage("-------------------------");
            }

            ExitCode = EXITCODE_NORMALEXIT;
        }

        private void StartServer()
        {
            if (_profile == null)
            {
                ExitCode = EXITCODE_BADPROFILE;
                return;
            }

            // check if the server was previously running.
            if (!_serverRunning)
            {
                if (_profile.AutoRestartIfShutdown)
                {
                    LogProfileMessage("Server was not running, server will be started as the setting to restart if shutdown is TRUE.");
                }
                else
                {
                    LogProfileMessage("Server was not running, server will not be started.");

                    ExitCode = EXITCODE_NORMALEXIT;
                    return;
                }
            }

            // Find the server process.
            Process process = GetServerProcess();

            if (process == null)
            {
                LogProfileMessage("");
                LogProfileMessage("Starting server...");

                CheckServerWorldFileExists(_profile);

                var startInfo = new ProcessStartInfo()
                {
                    FileName = GetLauncherFile(),
                    UseShellExecute = true,
                };

                process = Process.Start(startInfo);
                if (process == null)
                {
                    LogProfileError("Starting server failed.");
                    ExitCode = EXITCODE_RESTART_FAILED;
                    return;
                }

                LogProfileMessage("Started server successfully.");
                LogProfileMessage("");

                // update the profile's last started time
                _profile.LastStarted = DateTime.Now;

                if (Config.Default.EmailNotify_ShutdownRestart)
                    SendEmail($"{_profile.ProfileName} server started", Config.Default.Alert_ServerStartedMessage, false);

                var startupMessage = Config.Default.Alert_ServerStartedMessage;
                if (Config.Default.Alert_ServerStartedMessageIncludeIPandPort)
                    startupMessage += $" {Config.Default.MachinePublicIP}:{_profile.QueryPort}";
                ProcessAlert(AlertType.Startup, startupMessage);

                if (_profile.ForceRespawnDinos)
                    ProcessAlert(AlertType.Startup, Config.Default.Alert_ForceRespawnDinos);
            }
            else
            {
                LogProfileMessage("Server start was aborted, server instance already running.");
            }

            ExitCode = EXITCODE_NORMALEXIT;
        }

        private void StopServer(CancellationToken cancellationToken)
        {
            _serverRunning = false;

            if (_profile == null)
            {
                ExitCode = EXITCODE_BADPROFILE;
                return;
            }

            // Find the server process.
            Process process = GetServerProcess();

            // check if the process was found
            if (process == null)
            {
                LogProfileMessage("Server process not found, server not started.");

                // process not found, server is not running
                ExitCode = EXITCODE_NORMALEXIT;
                return;
            }

            _serverRunning = true;
            ServerStatusChangeCallback?.Invoke(ServerStatus.Stopping);
            LogProfileMessage($"Server process found PID {process.Id}.");

            QueryMaster.Server gameServer = null;
            bool sent = false;

            try
            {
                // create a connection to the server
                var endPoint = new IPEndPoint(_profile.ServerIPAddress, _profile.QueryPort);
                gameServer = QueryMaster.ServerQuery.GetServerInstance(QueryMaster.EngineType.Source, endPoint);

                // check if there is a shutdown reason
                if (!string.IsNullOrWhiteSpace(ShutdownReason) && !Config.Default.ServerShutdown_AllMessagesShowReason)
                {
                    LogProfileMessage("Sending shutdown reason...");

                    ProcessAlert(AlertType.ShutdownReason, ShutdownReason);
                    SendMessageAsync(ShutdownReason, cancellationToken).Wait();
                }

                LogProfileMessage("Starting shutdown timer...");

                var minutesLeft = ShutdownInterval;
                if (ServerProcess == ServerProcessType.Stop)
                {
                    LogProfileMessage($"Server shutdown type is {ServerProcess}, shutdown timer cancelled.");
                    minutesLeft = 0;
                }
                else if (!CheckForOnlinePlayers)
                {
                    LogProfileMessage("CheckForOnlinePlayers disabled, shutdown timer will not perform online player check.");
                }

                while (minutesLeft > 0)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        LogProfileMessage("Cancelling shutdown...");

                        if (!string.IsNullOrWhiteSpace(Config.Default.ServerShutdown_CancelMessage))
                        {
                            ProcessAlert(AlertType.Shutdown, Config.Default.ServerShutdown_CancelMessage);
                            SendMessageAsync(Config.Default.ServerShutdown_CancelMessage, cancellationToken).Wait();
                        }

                        ExitCode = EXITCODE_CANCELLED;
                        return;
                    }

                    if (CheckForOnlinePlayers)
                    {
                        try
                        {
                            var playerInfo = gameServer?.GetPlayers()?.Where(p => !string.IsNullOrWhiteSpace(p.Name?.Trim()));
                            var playerCount = playerInfo?.Count() ?? -1;

                            // check if anyone is logged into the server
                            if (playerCount <= 0)
                            {
                                LogProfileMessage("No online players, shutdown timer cancelled.");
                                break;
                            }

                            LogProfileMessage($"Online players: {playerCount}.");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error getting/displaying online players.\r\n{ex.Message}");
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"CheckForOnlinePlayers disabled, shutdown timer cancelled.");
                    }

                    var message = string.Empty;
                    if (minutesLeft > 5)
                    {
                        // check if we have just started the countdown
                        if (minutesLeft == ShutdownInterval)
                        {
                            message = Config.Default.ServerShutdown_GraceMessage1.Replace("{minutes}", minutesLeft.ToString());
                            if (!string.IsNullOrWhiteSpace(UpdateReason))
                                message += $"\n\n{UpdateReason}";
                        }
                        else
                        {
                            var interval = GetShutdownCheckInterval(minutesLeft);
                            Math.DivRem(minutesLeft, interval, out int remainder);

                            if (remainder == 0)
                            {
                                message = Config.Default.ServerShutdown_GraceMessage1.Replace("{minutes}", minutesLeft.ToString());
                                if (!string.IsNullOrWhiteSpace(UpdateReason))
                                    message += $"\n\n{UpdateReason}";
                            }
                        }
                    }
                    else if (minutesLeft > 1)
                    {
                        message = Config.Default.ServerShutdown_GraceMessage1.Replace("{minutes}", minutesLeft.ToString());
                        if (!string.IsNullOrWhiteSpace(UpdateReason))
                            message += $"\n\n{UpdateReason}";
                    }
                    else
                    {
                        message = Config.Default.ServerShutdown_GraceMessage2;
                        if (!string.IsNullOrWhiteSpace(UpdateReason))
                            message += $"\n\n{UpdateReason}";
                    }

                    sent = false;
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        ProcessAlert(AlertType.ShutdownMessage, message);

                        // check if there is a shutdown reason
                        if (!string.IsNullOrWhiteSpace(ShutdownReason) && Config.Default.ServerShutdown_AllMessagesShowReason)
                        {
                            ProcessAlert(AlertType.ShutdownReason, ShutdownReason);

                            message = $"{message}\r\n{ShutdownReason}";
                        }

                        sent = SendMessageAsync(message, cancellationToken).Result;
                    }

                    minutesLeft--;
                    try
                    {
                        var delay = sent ? 60000 - Config.Default.SendMessageDelay : 60000;
                        Task.Delay(delay, cancellationToken).Wait(cancellationToken);
                    }
                    catch { }
                }

                // check if we need to perform a world save (not required for SotF servers)
                if (Config.Default.ServerShutdown_EnableWorldSave && !_profile.SotFEnabled)
                {
                    try
                    {
                        // perform a world save
                        if (!string.IsNullOrWhiteSpace(Config.Default.ServerShutdown_WorldSaveMessage))
                        {
                            LogProfileMessage(Config.Default.ServerShutdown_WorldSaveMessage);
                            ProcessAlert(AlertType.ShutdownMessage, Config.Default.ServerShutdown_WorldSaveMessage);
                            SendMessageAsync(Config.Default.ServerShutdown_WorldSaveMessage, cancellationToken).Wait(cancellationToken);
                        }

                        if (SendCommandAsync(Config.Default.ServerSaveCommand, false).Result)
                        {
                            try
                            {
                                Task.Delay(Config.Default.ServerShutdown_WorldSaveDelay * 1000, cancellationToken).Wait(cancellationToken);
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"RCON> {Config.Default.ServerSaveCommand} command.\r\n{ex.Message}");
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    LogProfileMessage("Cancelling shutdown...");

                    if (!string.IsNullOrWhiteSpace(Config.Default.ServerShutdown_CancelMessage))
                    {
                        ProcessAlert(AlertType.Shutdown, Config.Default.ServerShutdown_CancelMessage);
                        SendMessageAsync(Config.Default.ServerShutdown_CancelMessage, cancellationToken).Wait();
                    }

                    ExitCode = EXITCODE_CANCELLED;
                    return;
                }

                // send the final shutdown message
                if (!string.IsNullOrWhiteSpace(Config.Default.ServerShutdown_GraceMessage3))
                {
                    var message = Config.Default.ServerShutdown_GraceMessage3;
                    ProcessAlert(AlertType.ShutdownMessage, message);

                    // check if there is a shutdown reason
                    if (!string.IsNullOrWhiteSpace(ShutdownReason) && Config.Default.ServerShutdown_AllMessagesShowReason)
                    {
                        ProcessAlert(AlertType.ShutdownReason, ShutdownReason);

                        message = $"{message}\r\n{ShutdownReason}";
                    }

                    SendMessageAsync(message, cancellationToken).Wait();
                }
            }
            finally
            {
                gameServer?.Dispose();
                gameServer = null;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                LogProfileMessage("Cancelling shutdown...");

                if (!string.IsNullOrWhiteSpace(Config.Default.ServerShutdown_CancelMessage))
                {
                    ProcessAlert(AlertType.Shutdown, Config.Default.ServerShutdown_CancelMessage);
                    SendMessageAsync(Config.Default.ServerShutdown_CancelMessage, cancellationToken).Wait();
                }

                ExitCode = EXITCODE_CANCELLED;
                return;
            }

            try
            {
                // Stop the server
                LogProfileMessage("");
                LogProfileMessage("Stopping server...");
                ProcessAlert(AlertType.Shutdown, Config.Default.Alert_ServerShutdownMessage);

                var ts = new TaskCompletionSource<bool>();
                void handler(object s, EventArgs e) => ts.TrySetResult(true);
                process.EnableRaisingEvents = true;
                process.Exited += handler;

                // Method 1 - Shutdown Command
                if (_profile.RCONEnabled && Config.Default.ServerShutdown_UseShutdownCommand)
                {
                    try
                    {
                        sent = SendCommandAsync(Config.Default.ServerShutdownCommand, false).Result;
                        if (sent)
                        {
                            Task.Delay(10000).Wait();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"RCON> {Config.Default.ServerShutdownCommand} command.\r\n{ex.Message}");
                    }

                    if (sent && !process.HasExited)
                    {
                        ts.Task.Wait(60000);   // 1 minute
                    }

                    if (process.HasExited)
                    {
                        LogProfileMessage($"Exited server successfully.");
                        LogProfileMessage("");
                        ExitCode = EXITCODE_NORMALEXIT;
                        return;
                    }

                    LogProfileMessage("Exiting server timed out, attempting to close the server.");
                }

                // Method 2 - Close the process
                sent = process.CloseMainWindow();

                if (sent && !process.HasExited)
                {
                    ts.Task.Wait(60000);   // 1 minute
                }

                if (process.HasExited)
                {
                    LogProfileMessage("Closed server successfully.");
                    LogProfileMessage("");
                    ExitCode = EXITCODE_NORMALEXIT;
                    return;
                }

                // Attempt 3 - Send CNTL-C
                LogProfileMessage("Closing server timed out, attempting to stop the server.");

                ProcessUtils.SendStopAsync(process).Wait();

                if (ts.Task.Result)
                {
                    LogProfileMessage("Stopped server successfully.");
                    LogProfileMessage("");
                    ExitCode = EXITCODE_NORMALEXIT;
                    return;
                }

                // Attempt 4 - Kill the process
                LogProfileMessage("Stopping server timed out, attempting to kill the server.");

                // try to kill the server
                process.Kill();

                if (!process.HasExited)
                {
                    ts.Task.Wait(60000);   // 1 minute
                }

                if (ts.Task.Result)
                {
                    LogProfileMessage("Killed server successfully.");
                    LogProfileMessage("");
                    ExitCode = EXITCODE_NORMALEXIT;
                    return;
                }
            }
            finally
            {
                if (process.HasExited)
                {
                    process.Close();

                    CheckServerWorldFileExists(_profile);

                    if (Config.Default.EmailNotify_ShutdownRestart)
                        SendEmail($"{_profile.ProfileName} server shutdown", $"The server has been shutdown to perform the {ServerProcess} process.", false);
                }
            }

            // killing the server did not work, cancel the update
            LogProfileError("Killing server timed out.");
            ExitCode = EXITCODE_SHUTDOWN_TIMEOUT;
        }

        private void UpgradeLocal(bool validate, bool updateMods, bool steamCmdRemoveQuit, CancellationToken cancellationToken)
        {
            if (_profile == null)
            {
                ExitCode = EXITCODE_BADPROFILE;
                return;
            }

            try
            {
                var steamCmdFile = SteamCmdUpdater.GetSteamCmdFile(Config.Default.DataDir);
                if (string.IsNullOrWhiteSpace(steamCmdFile) || !File.Exists(steamCmdFile))
                {
                    LogProfileError($"SteamCMD could not be found. Expected location is {steamCmdFile}");
                    ExitCode = EXITCODE_STEAMCMDNOTFOUND;
                    return;
                }

                // record the start time of the process, this is used to determine if any files changed in the download process.
                var startTime = DateTime.Now;

                var gotNewVersion = false;
                var downloadSuccessful = false;
                var success = false;

                // *********************
                // Server Update Section
                // *********************

                LogProfileMessage("\r\n");
                LogProfileMessage("Starting server update.");
                LogProfileMessage("Updating server from steam.\r\n");

                downloadSuccessful = !Config.Default.SteamCmdRedirectOutput;
                void serverOutputHandler(object s, DataReceivedEventArgs e)
                {
                    var dataValue = e.Data ?? string.Empty;
                    LogProfileMessage(dataValue);
                    if (!gotNewVersion && dataValue.Contains("downloading,"))
                    {
                        gotNewVersion = true;
                    }
                    if (dataValue.StartsWith("Success!"))
                    {
                        downloadSuccessful = true;
                    }
                }

                var steamCmdArgs = SteamUtils.BuildSteamCmdArguments(steamCmdRemoveQuit, Config.Default.SteamCmdInstallServerArgsFormat, Config.Default.SteamCmd_AnonymousUsername, _profile.InstallDirectory, string.Empty, _profile.SotFEnabled ? Config.Default.AppIdServer_SotF : Config.Default.AppIdServer, string.Empty, validate ? "validate" : string.Empty);
                var workingDirectory = Config.Default.DataDir;

                if (steamCmdRemoveQuit)
                    SteamCMDProcessWindowStyle = ProcessWindowStyle.Normal;

                success = ServerUpdater.UpgradeServerAsync(steamCmdFile, steamCmdArgs, workingDirectory, null, null, _profile.InstallDirectory, Config.Default.SteamCmdRedirectOutput ? (DataReceivedEventHandler)serverOutputHandler : null, cancellationToken, SteamCMDProcessWindowStyle).Result;
                if (success && downloadSuccessful)
                {
                    LogProfileMessage("Finished server update.");

                    if (Directory.Exists(_profile.InstallDirectory))
                    {
                        if (!Config.Default.SteamCmdRedirectOutput)
                            // check if any of the server files have changed.
                            gotNewVersion = HasNewServerVersion(_profile.InstallDirectory, startTime);

                        LogProfileMessage($"New server version - {gotNewVersion.ToString().ToUpperInvariant()}.");
                    }

                    LogProfileMessage("\r\n");
                }
                else
                {
                    success = false;
                    LogProfileMessage("****************************");
                    LogProfileMessage("ERROR: Failed server update.");
                    LogProfileMessage("****************************");
                    LogProfileMessage("Check steamcmd logs for more information why the server update failed.\r\n");

                    if (Config.Default.SteamCmdRedirectOutput)
                        LogProfileMessage($"If the server update keeps failing try disabling the '{_globalizer.GetResourceString("GlobalSettings_SteamCmdRedirectOutputLabel")}' option in the settings window.\r\n");

                    ExitCode = EXITCODE_SERVERUPDATEFAILED;
                }

                // check if we need to update the mods
                if (updateMods)
                {
                    if (success)
                    {
                        // ******************
                        // Mod Update Section
                        // ******************

                        // build a list of mods to be processed
                        var modIdList = new List<string>();
                        if (!string.IsNullOrWhiteSpace(_profile.ServerMapModId))
                            modIdList.Add(_profile.ServerMapModId);
                        if (!string.IsNullOrWhiteSpace(_profile.TotalConversionModId))
                            modIdList.Add(_profile.TotalConversionModId);
                        modIdList.AddRange(_profile.ServerModIds);

                        modIdList = ModUtils.ValidateModList(modIdList);

                        // get the details of the mods to be processed.
                        var modDetails = SteamUtils.GetSteamModDetails(modIdList);
                        var forceUpdateMods = Config.Default.ServerUpdate_ForceUpdateModsIfNoSteamInfo || string.IsNullOrWhiteSpace(SteamUtils.SteamWebApiKey);

                        // check if the mod details were retrieved
                        if (modDetails == null && forceUpdateMods)
                        {
                            modDetails = new PublishedFileDetailsResponse();
                        }

                        if (modDetails != null)
                        {
                            // create a new list for any failed mod updates
                            var failedMods = new List<string>(modIdList.Count);

                            for (var index = 0; index < modIdList.Count; index++)
                            {
                                var modId = modIdList[index];
                                var modTitle = modId;
                                var modSuccess = false;
                                gotNewVersion = false;
                                downloadSuccessful = false;

                                LogProfileMessage($"Started processing mod {index + 1} of {modIdList.Count}.");
                                LogProfileMessage($"Mod {modId}.");

                                // check if the steam information was downloaded
                                var modDetail = modDetails.publishedfiledetails?.FirstOrDefault(m => m.publishedfileid.Equals(modId, StringComparison.OrdinalIgnoreCase));
                                modTitle = $"{modId} - {modDetail?.title ?? "<unknown>"}";

                                if (modDetail != null)
                                    LogProfileMessage($"{modDetail.title}.\r\n");

                                var modCachePath = ModUtils.GetModCachePath(modId, _profile.SotFEnabled);
                                var cacheTimeFile = ModUtils.GetLatestModCacheTimeFile(modId, _profile.SotFEnabled);
                                var modPath = ModUtils.GetModPath(_profile.InstallDirectory, modId);
                                var modTimeFile = ModUtils.GetLatestModTimeFile(_profile.InstallDirectory, modId);

                                var modCacheLastUpdated = 0;
                                var downloadMod = true;
                                var copyMod = true;
                                var updateError = false;

                                if (downloadMod)
                                {
                                    // check if the mod needs to be downloaded, or force the download.
                                    if (Config.Default.ServerUpdate_ForceUpdateMods)
                                    {
                                        LogProfileMessage("Forcing mod download - Server Manager setting is TRUE.");
                                    }
                                    else if (modDetail == null)
                                    {
                                        if (forceUpdateMods)
                                        {
                                            LogProfileMessage("Forcing mod download - Mod details not available and Server Manager setting is TRUE.");
                                        }
                                        else
                                        {
                                            // no steam information downloaded, display an error, mod might no longer be available
                                            LogProfileMessage("*******************************************************************");
                                            LogProfileMessage("ERROR: Mod cannot be updated, unable to download steam information.");
                                            LogProfileMessage("*******************************************************************");

                                            LogProfileMessage($"If the mod update keeps failing try enabling the '{_globalizer.GetResourceString("GlobalSettings_ForceUpdateModsIfNoSteamInfoLabel")}' option in the settings window.\r\n");

                                            downloadMod = false;
                                            copyMod = false;
                                            updateError = true;
                                        }
                                    }
                                    else
                                    {
                                        // check if the mod detail record is valid (private mod).
                                        if (modDetail.time_updated <= 0)
                                        {
                                            LogProfileMessage("Forcing mod download - mod is private.");
                                        }
                                        else
                                        {
                                            modCacheLastUpdated = ModUtils.GetModLatestTime(cacheTimeFile);
                                            if (modCacheLastUpdated <= 0)
                                            {
                                                LogProfileMessage("Forcing mod download - mod cache is not versioned.");
                                            }
                                            else
                                            {
                                                var steamLastUpdated = modDetail.time_updated;
                                                if (steamLastUpdated <= modCacheLastUpdated)
                                                {
                                                    LogProfileMessage("Skipping mod download - mod cache has the latest version.");
                                                    downloadMod = false;
                                                }
                                            }
                                        }
                                    }

                                    if (downloadMod)
                                    {
                                        // mod will be downloaded
                                        downloadSuccessful = !Config.Default.SteamCmdRedirectOutput;
                                        DataReceivedEventHandler modOutputHandler = (s, e) =>
                                        {
                                            var dataValue = e.Data ?? string.Empty;
                                            LogProfileMessage(dataValue);
                                            if (dataValue.StartsWith("Success."))
                                            {
                                                downloadSuccessful = true;
                                            }
                                        };

                                        LogProfileMessage("Starting mod download.\r\n");

                                        steamCmdArgs = string.Empty;
                                        if (Config.Default.SteamCmd_UseAnonymousCredentials)
                                            steamCmdArgs = SteamUtils.BuildSteamCmdArguments(steamCmdRemoveQuit, Config.Default.SteamCmdInstallModArgsFormat, Config.Default.SteamCmd_AnonymousUsername, _profile.SotFEnabled ? Config.Default.AppId_SotF : Config.Default.AppId, modId);
                                        else
                                            steamCmdArgs = SteamUtils.BuildSteamCmdArguments(steamCmdRemoveQuit, Config.Default.SteamCmdInstallModArgsFormat, Config.Default.SteamCmd_Username, _profile.SotFEnabled ? Config.Default.AppId_SotF : Config.Default.AppId, modId);

                                        modSuccess = ServerUpdater.UpgradeModsAsync(steamCmdFile, steamCmdArgs, workingDirectory, null, null, Config.Default.SteamCmdRedirectOutput ? modOutputHandler : null, cancellationToken, SteamCMDProcessWindowStyle).Result;
                                        if (modSuccess && downloadSuccessful)
                                        {
                                            LogProfileMessage("Finished mod download.");
                                            copyMod = true;

                                            if (Directory.Exists(modCachePath))
                                            {
                                                // check if any of the mod files have changed.
                                                gotNewVersion = new DirectoryInfo(modCachePath).GetFiles("*.*", SearchOption.AllDirectories).Any(file => file.LastWriteTime >= startTime);

                                                LogProfileMessage($"New mod version - {gotNewVersion.ToString().ToUpperInvariant()}.");

                                                var steamLastUpdated = modDetail?.time_updated.ToString() ?? string.Empty;
                                                if (modDetail == null || modDetail.time_updated <= 0)
                                                {
                                                    // get the version number from the steamcmd workshop file.
                                                    steamLastUpdated = ModUtils.GetSteamWorkshopLatestTime(ModUtils.GetSteamWorkshopFile(_profile.SotFEnabled), modId).ToString();
                                                }

                                                // update the last updated file with the steam updated time.
                                                File.WriteAllText(cacheTimeFile, steamLastUpdated);

                                                LogProfileMessage($"Mod Cache version: {steamLastUpdated}\r\n");
                                            }
                                        }
                                        else
                                        {
                                            modSuccess = false;
                                            LogProfileMessage("***************************");
                                            LogProfileMessage("ERROR: Mod download failed.");
                                            LogProfileMessage("***************************\r\n");
                                            LogProfileMessage("Check steamcmd logs for more information why the mod update failed.\r\n");

                                            if (Config.Default.SteamCmdRedirectOutput)
                                                LogProfileMessage($"If the mod update keeps failing try disabling the '{_globalizer.GetResourceString("GlobalSettings_SteamCmdRedirectOutputLabel")}' option in the settings window.\r\n");
                                            copyMod = false;

                                            ExitCode = EXITCODE_MODUPDATEFAILED;
                                        }
                                    }
                                    else
                                        modSuccess = !updateError;
                                }
                                else
                                    modSuccess = !updateError;

                                if (copyMod)
                                {
                                    // check if the mod needs to be copied, or force the copy.
                                    if (Config.Default.ServerUpdate_ForceCopyMods)
                                    {
                                        LogProfileMessage("Forcing mod copy - Server Manager setting is TRUE.");
                                    }
                                    else
                                    {
                                        // check the mod version against the cache version.
                                        var modLastUpdated = ModUtils.GetModLatestTime(modTimeFile);
                                        if (modLastUpdated <= 0)
                                        {
                                            LogProfileMessage("Forcing mod copy - mod is not versioned.");
                                        }
                                        else
                                        {
                                            modCacheLastUpdated = ModUtils.GetModLatestTime(cacheTimeFile);
                                            if (modCacheLastUpdated <= modLastUpdated)
                                            {
                                                LogProfileMessage("Skipping mod copy - mod has the latest version.");
                                                LogProfileMessage($"Mod version: {modLastUpdated}");
                                                copyMod = false;
                                            }
                                        }
                                    }

                                    if (copyMod)
                                    {
                                        try
                                        {
                                            if (Directory.Exists(modCachePath))
                                            {
                                                LogProfileMessage("Started mod copy.");
                                                int count = 0;
                                                Task.Run(() => ModUtils.CopyMod(modCachePath, modPath, modId, (p, m, n) =>
                                                {
                                                    count++;
                                                    ProgressCallback?.Invoke(0, ".", count % DIRECTORIES_PER_LINE == 0);
                                                }), cancellationToken).Wait();
                                                LogProfileMessage("\r\n");
                                                LogProfileMessage("Finished mod copy.");

                                                var modLastUpdated = ModUtils.GetModLatestTime(modTimeFile);
                                                LogProfileMessage($"Mod version: {modLastUpdated}");
                                            }
                                            else
                                            {
                                                modSuccess = false;
                                                LogProfileMessage("****************************************************");
                                                LogProfileMessage("ERROR: Mod cache was not found, mod was not updated.");
                                                LogProfileMessage("****************************************************");
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            modSuccess = false;
                                            LogProfileMessage("***********************");
                                            LogProfileMessage($"ERROR: Failed mod copy.\r\n{ex.Message}");
                                            LogProfileMessage("***********************");
                                        }
                                    }
                                }

                                if (!modSuccess)
                                {
                                    success = false;
                                    failedMods.Add($"{index + 1} of {modIdList.Count} - {modTitle}");

                                    ExitCode = EXITCODE_MODUPDATEFAILED;
                                }

                                LogProfileMessage($"Finished processing mod {modId}.\r\n");
                            }

                            if (failedMods.Count > 0)
                            {
                                LogProfileMessage("**************************************************************************");
                                LogProfileMessage("ERROR: The following mods failed the update, check above for more details.");
                                foreach (var failedMod in failedMods)
                                    LogProfileMessage(failedMod);
                                LogProfileMessage("**************************************************************************");
                            }
                        }
                        else
                        {
                            success = false;
                            // no steam information downloaded, display an error
                            LogProfileMessage("********************************************************************");
                            LogProfileMessage("ERROR: Mods cannot be updated, unable to download steam information.");
                            LogProfileMessage("********************************************************************\r\n");

                            if (!Config.Default.ServerUpdate_ForceUpdateModsIfNoSteamInfo)
                                LogProfileMessage($"If the mod update keeps failing try enabling the '{_globalizer.GetResourceString("GlobalSettings_ForceUpdateModsIfNoSteamInfoLabel")}' option in the settings window.\r\n");

                            ExitCode = EXITCODE_MODUPDATEFAILED;
                        }
                    }
                    else
                    {
                        LogProfileMessage("***********************************************************");
                        LogProfileMessage("ERROR: Mods were not processed as server update had errors.");
                        LogProfileMessage("***********************************************************\r\n");

                        ExitCode = EXITCODE_SERVERUPDATEFAILED;
                    }
                }

                LogProfileMessage("Finished upgrade process.");
            }
            catch (TaskCanceledException)
            {
                ExitCode = EXITCODE_CANCELLED;
            }
        }

        private void UpdateFiles()
        {
            if (_profile == null)
            {
                ExitCode = EXITCODE_BADPROFILE;
                return;
            }

            var alertMessage = new StringBuilder();
            var emailMessage = new StringBuilder();

            LogProfileMessage("------------------------");
            LogProfileMessage("Started server update...");
            LogProfileMessage("------------------------");
            LogProfileMessage($"Server Manager version: {App.Instance.Version}");
            LogProfileMessage($"Server branch: {GetBranchName(_profile.BranchName)}");
            LogProfileMessage($"Profile Name: {_profile.ProfileName}");

            // check if the server needs to be updated
            var serverCacheLastUpdated = GetServerLatestTime(GetServerCacheTimeFile(_profile?.BranchName));
            var serverLastUpdated = GetServerLatestTime(GetServerTimeFile());
            var updateServer = serverCacheLastUpdated > serverLastUpdated;

            // check if any of the mods need to be updated
            var updateModIds = new List<string>();
            var modIdList = GetModList();

            // cycle through each mod.
            foreach (var modId in modIdList)
            {
                // check if the mod needs to be updated.
                var modCacheLastUpdated = ModUtils.GetModLatestTime(ModUtils.GetLatestModCacheTimeFile(modId, false));
                var modLastUpdated = ModUtils.GetModLatestTime(ModUtils.GetLatestModTimeFile(_profile.InstallDirectory, modId));
                if (modCacheLastUpdated > modLastUpdated || modLastUpdated == 0)
                    updateModIds.Add(modId);
            }

            if (ExitCode != EXITCODE_NORMALEXIT)
                return;

            if (updateServer || updateModIds.Count > 0)
            {
                updateModIds = ModUtils.ValidateModList(updateModIds);
                var modDetails = SteamUtils.GetSteamModDetails(updateModIds);

                UpdateReason = string.Empty;
                if (Config.Default.AutoUpdate_ShowUpdateReason)
                {
                    var delimiter = string.Empty;

                    // create the update message to broadcast 
                    if (!string.IsNullOrWhiteSpace(Config.Default.AutoUpdate_UpdateReasonPrefix))
                    {
                        UpdateReason += $"{Config.Default.AutoUpdate_UpdateReasonPrefix.Trim()}";
                        delimiter = " ";
                    }

                    if (updateServer)
                    {
                        UpdateReason += $"{delimiter}{_globalizer.GetResourceString("GlobalSettings_AutoUpdate_GameServerLabel")}";
                        delimiter = ", ";
                    }
                    if (updateModIds.Count > 0)
                    {
                        for (var index = 0; index < updateModIds.Count; index++)
                        {
                            if (index == 5)
                            {
                                UpdateReason += "...";
                                break;
                            }

                            var modId = updateModIds[index];
                            var modName = modDetails?.publishedfiledetails?.FirstOrDefault(m => m.publishedfileid == modId)?.title ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(modName))
                                UpdateReason += $"{delimiter}{modId}";
                            else
                                UpdateReason += $"{delimiter}{modName}";
                            delimiter = ", ";
                        }
                    }
                }

                // stop the server
                LogProfileMessage("");
                StopServer(CancellationToken.None);

                if (ExitCode != EXITCODE_NORMALEXIT)
                    return;

                ServerStatusChangeCallback?.Invoke(ServerStatus.Stopped);

                emailMessage.AppendLine("Update Summary:");
                emailMessage.AppendLine();
                emailMessage.AppendLine($"Server Manager version: {App.Instance.Version}");

                // make a backup of the current profile and config files.
                CreateProfileBackupArchiveFile(_profile);

                if (ExitCode != EXITCODE_NORMALEXIT)
                    return;

                if (BackupWorldFile)
                {
                    // make a backup of the current world file.
                    CreateServerBackupArchiveFile(emailMessage, _profile);

                    if (ExitCode != EXITCODE_NORMALEXIT)
                        return;
                }

                Mutex mutex = null;
                bool createdNew = false;

                alertMessage.AppendLine();
                if (!string.IsNullOrWhiteSpace(Config.Default.Alert_UpdateResults))
                    alertMessage.AppendLine(Config.Default.Alert_UpdateResults);

                // check if the server needs to be updated
                LogProfileMessage("");
                if (updateServer)
                {
                    Task.Delay(5000).Wait();

                    LogProfileMessage("Updating server from cache...");

                    emailMessage.AppendLine();
                    emailMessage.AppendLine("Game Server Update:");

                    try
                    {
                        var cacheFolder = GetServerCacheFolder(_profile?.BranchName);

                        if (Directory.Exists(cacheFolder))
                        {
                            LogProfileMessage($"Smart cache copy: {Config.Default.AutoUpdate_UseSmartCopy}.");

                            // update the server files from the cache.
                            DirectoryCopy(cacheFolder, _profile.InstallDirectory, true, Config.Default.AutoUpdate_UseSmartCopy, null);

                            if (Config.Default.AutoUpdate_VerifyServerAfterUpdate)
                            {
                                // perform a steamcmd validate to confirm all the files
                                LogProfileMessage("Validating server files (*new*).");
                                UpgradeLocal(true, false, false, CancellationToken.None);
                                LogProfileMessage("Validated server files (*new*).");
                            }

                            // update the version number
                            _profile.LastInstalledVersion = GetServerVersion(GetServerVersionFile()).ToString();

                            LogProfileMessage("Updated server from cache. See patch notes.");
                            LogProfileMessage(Config.Default.AppPatchNotesUrl);

                            if (!string.IsNullOrWhiteSpace(Config.Default.Alert_ServerUpdate))
                                alertMessage.AppendLine(Config.Default.Alert_ServerUpdate);

                            emailMessage.AppendLine();
                            emailMessage.AppendLine("Updated server from cache. See patch notes.");
                            emailMessage.AppendLine(Config.Default.AppPatchNotesUrl);

                            _profile.ServerUpdated = true;
                        }
                        else
                        {
                            LogProfileMessage("Server cache was not found, server was not updated from cache.");
                            ExitCode = EXITCODE_SERVERUPDATEFAILED;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogProfileError($"Unable to update the server from cache.\r\n{ex.Message}");
                        ExitCode = EXITCODE_SERVERUPDATEFAILED;
                    }
                }
                else
                {
                    LogProfileMessage("Server is already up to date, no update required.");
                }

                var serverVersion = GetServerVersion(GetServerVersionFile()).ToString();
                LogProfileMessage($"Server version: {serverVersion}");

                emailMessage.AppendLine($"Server version: {serverVersion}");

                if (ExitCode != EXITCODE_NORMALEXIT)
                    return;

                // check if the mods need to be updated
                LogProfileMessage("");
                if (updateModIds.Count > 0)
                {
                    Task.Delay(5000).Wait();

                    LogProfileMessage($"Updating {updateModIds.Count} mods from cache...");

                    emailMessage.AppendLine();
                    emailMessage.AppendLine("Mod Updates:");

                    try
                    {
                        // update the mod files from the cache.
                        for (var index = 0; index < updateModIds.Count; index++)
                        {
                            var modId = updateModIds[index];
                            var modCachePath = ModUtils.GetModCachePath(modId, false);
                            var modPath = ModUtils.GetModPath(_profile.InstallDirectory, modId);
                            var modName = modDetails?.publishedfiledetails?.FirstOrDefault(m => m.publishedfileid == modId)?.title ?? string.Empty;

                            try
                            {
                                if (Directory.Exists(modCachePath))
                                {
                                    // try to establish a mutex for the mod cache.
                                    mutex = new Mutex(true, GetMutexName(modCachePath), out createdNew);
                                    if (!createdNew)
                                        createdNew = mutex.WaitOne(new TimeSpan(0, MUTEX_TIMEOUT, 0));

                                    // check if the mutex was established
                                    if (createdNew)
                                    {
                                        LogProfileMessage($"Started mod update from cache {index + 1} of {updateModIds.Count}...");
                                        LogProfileMessage($"Mod Name: {modName} (Mod ID: {modId})");

                                        alertMessage.AppendLine($"{modName} ({modId})");

                                        emailMessage.AppendLine();
                                        emailMessage.AppendLine($"{modName} ({modId})");

                                        ModUtils.CopyMod(modCachePath, modPath, modId, null);

                                        var modLastUpdated = ModUtils.GetModLatestTime(ModUtils.GetLatestModTimeFile(_profile.InstallDirectory, modId));
                                        LogProfileMessage($"Mod {modId} version: {modLastUpdated}.");

                                        LogProfileMessage($"Workshop page: http://steamcommunity.com/sharedfiles/filedetails/?id={modId}");
                                        LogProfileMessage($"Change notes: http://steamcommunity.com/sharedfiles/filedetails/changelog/{modId}");

                                        emailMessage.AppendLine($"Workshop page: http://steamcommunity.com/sharedfiles/filedetails/?id={modId}");
                                        emailMessage.AppendLine($"Change notes: http://steamcommunity.com/sharedfiles/filedetails/changelog/{modId}");

                                        LogProfileMessage($"Finished mod {modId} update from cache.");
                                    }
                                    else
                                    {
                                        ExitCode = EXITCODE_PROCESSALREADYRUNNING;
                                        LogProfileMessage("Mod not updated, could not lock mod cache.");
                                    }
                                }
                                else
                                {
                                    LogProfileError($"Mod {modId} cache was not found, mod was not updated from cache.");
                                    ExitCode = EXITCODE_MODUPDATEFAILED;
                                }
                            }
                            catch (Exception ex)
                            {
                                LogProfileError($"Unable to update mod {modId} from cache.\r\n{ex.Message}");
                                ExitCode = EXITCODE_MODUPDATEFAILED;
                            }
                            finally
                            {
                                if (mutex != null)
                                {
                                    if (createdNew)
                                    {
                                        mutex.ReleaseMutex();
                                        mutex.Dispose();
                                    }
                                    mutex = null;
                                }
                            }
                        }

                        if (ExitCode == EXITCODE_NORMALEXIT)
                            LogProfileMessage($"Updated {updateModIds.Count} mods from cache.");
                        else
                            LogProfileMessage($"Updated {updateModIds.Count} mods from cache BUT there were errors.");
                    }
                    catch (Exception ex)
                    {
                        LogProfileError($"Unable to update the mods from cache.\r\n{ex.Message}");
                        ExitCode = EXITCODE_MODUPDATEFAILED;
                    }
                }
                else
                {
                    if (modIdList.Count > 0)
                        LogProfileMessage("Mods are already up to date, no updates required.");
                }

                if (ExitCode != EXITCODE_NORMALEXIT)
                    return;

                LogProfileMessage("");
                if (Config.Default.AutoUpdate_OverrideServerStartup)
                {
                    if (_serverRunning)
                        LogProfileMessage("The auto-update override server startup option is enabled, server will not be restarted.");
                    else
                        LogProfileMessage("The auto-update override server startup option is enabled, server will not be started.");
                }
                else
                {
                    // restart the server
                    StartServer();
                }

                if (Config.Default.EmailNotify_AutoUpdate)
                {
                    emailMessage.AppendLine();
                    emailMessage.AppendLine("See attached log file more details.");
                    SendEmail($"{_profile.ProfileName} auto update finished", emailMessage.ToString(), true);
                }

                ProcessAlert(AlertType.UpdateResults, alertMessage.ToString());
            }
            else
            {
                LogProfileMessage("");
                if (modIdList.Count > 0)
                    LogProfileMessage("The server and mods files are already up to date, no updates required.");
                else
                    LogProfileMessage("The server files are already up to date, no updates required.");

                var serverVersion = GetServerVersion(GetServerVersionFile()).ToString();
                LogProfileMessage($"Server version: {serverVersion}");

                _serverRunning = GetServerProcess() != null;

                LogProfileMessage("");
                if (Config.Default.AutoUpdate_OverrideServerStartup)
                {
                    if (!_serverRunning)
                        LogProfileMessage("The auto-update override server startup option is enabled, server will not be started.");
                }
                else
                {
                    // restart the server
                    StartServer();
                }
            }

            if (ExitCode != EXITCODE_NORMALEXIT)
                return;

            LogProfileMessage("-----------------------");
            LogProfileMessage("Finished server update.");
            LogProfileMessage("-----------------------");

            ExitCode = EXITCODE_NORMALEXIT;
        }

        private void UpdateModCache()
        {
            // get a list of mods to be processed
            var modIdList = GetModList();

            // check if there are any mods to be processed
            if (modIdList.Count == 0)
            {
                ExitCode = EXITCODE_NORMALEXIT;
                return;
            }

            LogMessage("");
            LogMessage("----------------------------");
            LogMessage("Starting mod cache update...");
            LogMessage("----------------------------");
            LogMessage($"Server Manager version: {App.Instance.Version}");

            LogMessage($"Downloading mod information for {modIdList.Count} mods from steam.");

            var forceUpdateMods = Config.Default.ServerUpdate_ForceUpdateModsIfNoSteamInfo || string.IsNullOrWhiteSpace(SteamUtils.SteamWebApiKey);

            // get the details of the mods to be processed.
            var modDetails = SteamUtils.GetSteamModDetails(modIdList);
            if (modDetails == null)
            {
                if (forceUpdateMods)
                {
                    LogMessage($"Unable to download mod information from steam.");
                    LogMessage("");
                }
                else
                {
                    LogError("Mods cannot be updated, unable to download steam information.");
                    LogMessage($"If the mod update keeps failing try enabling the '{_globalizer.GetResourceString("GlobalSettings_ForceUpdateModsIfNoSteamInfoLabel")}' option in the settings window.");
                    ExitCode = EXITCODE_CACHEMODDETAILSDOWNLOADFAILED;
                    return;
                }
            }
            else
            {
                LogMessage($"Downloaded mod information for {modIdList.Count} mods from steam.");
                LogMessage("");
            }

            // cycle through each mod finding which needs to be updated.
            var updateModIds = new List<string>();
            if (modDetails == null)
            {
                if (forceUpdateMods)
                {
                    LogMessage("All mods will be updated - unable to download steam information and force mod update is TRUE.");

                    updateModIds.AddRange(modIdList);
                    modDetails = new PublishedFileDetailsResponse();
                }
            }
            else
            {
                if (Config.Default.ServerUpdate_ForceUpdateMods)
                {
                    LogMessage("All mods will be updated - force mod update is TRUE.");
                    updateModIds.AddRange(modIdList);
                }
                else
                {
                    LogMessage("Mods will be selectively updated - force mod update is FALSE.");

                    foreach (var modId in modIdList)
                    {
                        var modDetail = modDetails.publishedfiledetails?.FirstOrDefault(m => m.publishedfileid.Equals(modId, StringComparison.OrdinalIgnoreCase));
                        if (modDetail == null)
                        {
                            LogMessage($"Mod {modId} will not be updated - unable to download steam information.");
                            continue;
                        }

                        if (modDetail.time_updated == 0)
                        {
                            LogMessage($"Mod {modId} will be updated - mod is private.");
                            updateModIds.Add(modId);
                        }
                        else
                        {
                            var cacheTimeFile = ModUtils.GetLatestModCacheTimeFile(modId, false);

                            // check if the mod needs to be updated
                            var steamLastUpdated = modDetail.time_updated;
                            var modCacheLastUpdated = ModUtils.GetModLatestTime(cacheTimeFile);
                            if (steamLastUpdated > modCacheLastUpdated)
                            {
                                LogMessage($"Mod {modId} will be updated - new version found.");
                                updateModIds.Add(modId);
                            }
                            else if (modCacheLastUpdated == 0)
                            {
                                LogMessage($"Mod {modId} will be updated - cache not versioned.");
                                updateModIds.Add(modId);
                            }
                            else
                            {
                                LogMessage($"Mod {modId} update skipped - cache contains the latest version.");
                            }
                        }
                    }
                }
            }

            var steamCmdFile = SteamCmdUpdater.GetSteamCmdFile(Config.Default.DataDir);
            if (string.IsNullOrWhiteSpace(steamCmdFile) || !File.Exists(steamCmdFile))
            {
                LogError($"SteamCMD could not be found. Expected location is {steamCmdFile}");
                ExitCode = EXITCODE_STEAMCMDNOTFOUND;
                return;
            }

            // cycle through each mod id.
            for (var index = 0; index < updateModIds.Count; index++)
            {
                var modId = updateModIds[index];
                var modDetail = modDetails.publishedfiledetails?.FirstOrDefault(m => m.publishedfileid.Equals(modId, StringComparison.OrdinalIgnoreCase));

                var cacheTimeFile = ModUtils.GetLatestModCacheTimeFile(modId, false);
                var modCachePath = ModUtils.GetModCachePath(modId, false);

                var downloadSuccessful = false;

                DataReceivedEventHandler modOutputHandler = (s, e) =>
                {
                    var dataValue = e.Data ?? string.Empty;
                    LogMessage(dataValue);
                    if (dataValue.StartsWith("Success."))
                    {
                        downloadSuccessful = true;
                    }
                };

                LogMessage("");
                LogMessage($"Started mod cache update {index + 1} of {updateModIds.Count}");
                LogMessage($"{modId} - {modDetail?.title ?? "<unknown>"}");

                var attempt = 0;
                while (true)
                {
                    attempt++;
                    downloadSuccessful = !Config.Default.SteamCmdRedirectOutput;

                    // update the mod cache
                    var steamCmdArgs = string.Empty;
                    if (Config.Default.SteamCmd_UseAnonymousCredentials)
                        steamCmdArgs = SteamUtils.BuildSteamCmdArguments(false, Config.Default.SteamCmdInstallModArgsFormat, Config.Default.SteamCmd_AnonymousUsername, Config.Default.AppId, modId);
                    else
                        steamCmdArgs = SteamUtils.BuildSteamCmdArguments(false, Config.Default.SteamCmdInstallModArgsFormat, Config.Default.SteamCmd_Username, Config.Default.AppId, modId);
                    var workingDirectory = Config.Default.DataDir;

                    var success = ServerUpdater.UpgradeModsAsync(steamCmdFile, steamCmdArgs, workingDirectory, null, null, Config.Default.SteamCmdRedirectOutput ? modOutputHandler : null, CancellationToken.None, SteamCMDProcessWindowStyle).Result;
                    if (success && downloadSuccessful)
                        // download was successful, exit loop and continue.
                        break;

                    // download was not successful, log a failed attempt.
                    var logError = $"Mod {modId} cache update failed";
                    if (Config.Default.AutoUpdate_RetryOnFail)
                        logError += $" - attempt {attempt}.";
                    LogError(logError);

                    // check if we have reached the max failed attempt limit.
                    if (!Config.Default.AutoUpdate_RetryOnFail || attempt >= STEAM_MAXRETRIES)
                    {
                        // failed max limit reached
                        if (Config.Default.SteamCmdRedirectOutput)
                        {
                            LogMessage("Check steamcmd logs for more information why the mod cache update failed.\r\n");
                            LogMessage($"If the mod cache update keeps failing try disabling the '{_globalizer.GetResourceString("GlobalSettings_SteamCmdRedirectOutputLabel")}' option in the Server Manager settings window.");
                        }

                        ExitCode = EXITCODE_CACHEMODUPDATEFAILED;
                        return;
                    }

                    Task.Delay(5000).Wait();
                }

                // check if any of the mod files have changed.
                if (Directory.Exists(modCachePath))
                {
                    var gotNewVersion = new DirectoryInfo(modCachePath).GetFiles("*.*", SearchOption.AllDirectories).Any(file => file.LastWriteTime >= _startTime);

                    if (gotNewVersion)
                        LogMessage("***** New version downloaded. *****");
                    else
                        LogMessage("No new version.");

                    var steamLastUpdated = modDetail?.time_updated.ToString() ?? string.Empty;
                    if (modDetail == null || modDetail.time_updated <= 0)
                    {
                        // get the version number from the steamcmd workshop file.
                        steamLastUpdated = ModUtils.GetSteamWorkshopLatestTime(ModUtils.GetSteamWorkshopFile(false), modId).ToString();
                    }

                    File.WriteAllText(cacheTimeFile, steamLastUpdated);
                    LogMessage($"Mod {modId} cache version: {steamLastUpdated}");
                }
                else
                    LogMessage($"Mod {modId} cache does not exist.");

                LogMessage($"Finished mod {modId} cache update.");
            }

            LogMessage("---------------------------");
            LogMessage("Finished mod cache update.");
            LogMessage("---------------------------");
            LogMessage("");
            ExitCode = EXITCODE_NORMALEXIT;
        }

        private void UpdateServerCache(string branchName, string branchPassword)
        {
            LogBranchMessage(branchName, "-------------------------------");
            LogBranchMessage(branchName, "Starting server cache update...");
            LogBranchMessage(branchName, "-------------------------------");
            LogBranchMessage(branchName, $"Server Manager version: {App.Instance.Version}");
            LogBranchMessage(branchName, $"Server branch: {GetBranchName(branchName)}");

            var gotNewVersion = false;
            var downloadSuccessful = false;

            var steamCmdFile = SteamCmdUpdater.GetSteamCmdFile(Config.Default.DataDir);
            if (string.IsNullOrWhiteSpace(steamCmdFile) || !File.Exists(steamCmdFile))
            {
                LogBranchError(branchName, $"SteamCMD could not be found. Expected location is {steamCmdFile}");
                ExitCode = EXITCODE_STEAMCMDNOTFOUND;
                return;
            }

            DataReceivedEventHandler serverOutputHandler = (s, e) =>
            {
                var dataValue = e.Data ?? string.Empty;
                LogBranchMessage(branchName, dataValue);
                if (!gotNewVersion && dataValue.Contains("downloading,"))
                {
                    gotNewVersion = true;
                }
                if (dataValue.StartsWith("Success!"))
                {
                    downloadSuccessful = true;
                }
            };

            // create the branch arguments
            var steamCmdInstallServerBetaArgs = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(branchName))
            {
                steamCmdInstallServerBetaArgs.AppendFormat(Config.Default.SteamCmdInstallServerBetaNameArgsFormat, branchName);
                if (!string.IsNullOrWhiteSpace(branchPassword))
                {
                    steamCmdInstallServerBetaArgs.Append(" ");
                    steamCmdInstallServerBetaArgs.AppendFormat(Config.Default.SteamCmdInstallServerBetaPasswordArgsFormat, branchPassword);
                }
            }

            var cacheFolder = GetServerCacheFolder(branchName);

            LogBranchMessage(branchName, "Server update started.");

            var attempt = 0;
            while (true)
            {
                attempt++;
                downloadSuccessful = !Config.Default.SteamCmdRedirectOutput;
                gotNewVersion = false;

                // update the server cache
                var validate = Config.Default.AutoUpdate_ValidateServerFiles;
                var steamCmdArgs = SteamUtils.BuildSteamCmdArguments(false, Config.Default.SteamCmdInstallServerArgsFormat, Config.Default.SteamCmd_AnonymousUsername, cacheFolder, Config.Default.AppIdServer, steamCmdInstallServerBetaArgs.ToString(), validate ? "validate" : string.Empty);
                var workingDirectory = Config.Default.DataDir;

                var success = ServerUpdater.UpgradeServerAsync(steamCmdFile, steamCmdArgs, workingDirectory, null, null, cacheFolder, Config.Default.SteamCmdRedirectOutput ? serverOutputHandler : null, CancellationToken.None, SteamCMDProcessWindowStyle).Result;
                if (success && downloadSuccessful)
                    // download was successful, exit loop and continue.
                    break;

                // download was not successful, log a failed attempt.
                var logError = "Server cache update failed";
                if (Config.Default.AutoUpdate_RetryOnFail)
                    logError += $" - attempt {attempt}.";
                LogBranchError(branchName, logError);

                // check if we have reached the max failed attempt limit.
                if (!Config.Default.AutoUpdate_RetryOnFail || attempt >= STEAM_MAXRETRIES)
                {
                    // failed max limit reached
                    if (Config.Default.SteamCmdRedirectOutput)
                    {
                        LogBranchMessage(branchName, $"Check steamcmd logs for more information why the server cache update failed.\r\n");
                        LogBranchMessage(branchName, $"If the server cache update keeps failing try disabling the '{_globalizer.GetResourceString("GlobalSettings_SteamCmdRedirectOutputLabel")}' option in the ASM settings window.");
                    }

                    ExitCode = EXITCODE_CACHESERVERUPDATEFAILED;
                    return;
                }

                Task.Delay(5000).Wait();
            }

            if (Directory.Exists(cacheFolder))
            {
                if (!Config.Default.SteamCmdRedirectOutput)
                    // check if any of the server files have changed.
                    gotNewVersion = HasNewServerVersion(cacheFolder, _startTime);

                if (gotNewVersion)
                {
                    LogBranchMessage(branchName, "***** New version downloaded. *****");

                    var latestCacheTimeFile = GetServerCacheTimeFile(branchName);
                    File.WriteAllText(latestCacheTimeFile, _startTime.ToString("o", CultureInfo.CurrentCulture));
                }
                else
                    LogBranchMessage(branchName, "No new version.");
            }
            else
                LogBranchMessage(branchName, $"Server cache does not exist.");

            var cacheVersion = GetServerVersion(GetServerCacheVersionFile(branchName)).ToString();
            LogBranchMessage(branchName, $"Server cache version: {cacheVersion}");

            LogBranchMessage(branchName, "-----------------------------");
            LogBranchMessage(branchName, "Finished server cache update.");
            LogBranchMessage(branchName, "-----------------------------");
            LogBranchMessage(branchName, "");
            ExitCode = EXITCODE_NORMALEXIT;
        }

        public void CheckServerWorldFileExists(ServerProfileSnapshot profile)
        {
            // do nothing if profile is null or SotF
            if (profile == null || profile.SotFEnabled)
                return;

            var oldProfile = _profile;

            try
            {
                _profile = profile;

                // check if the server save folder exists
                var saveFolder = GetServerSaveFolder();
                if (!Directory.Exists(saveFolder))
                    // save folder does not exist, exit
                    return;

                // check if the server save file exists
                var worldFile = GetServerWorldFile();
                if (File.Exists(worldFile))
                    // save file exists, exit
                    return;

                // save file does not exist, check for temp save file
                var tempWorldFile = Path.ChangeExtension(worldFile, ".tmp");
                if (!File.Exists(tempWorldFile))
                    // temp save file does not exist, exit
                    return;

                // temp save file exists, rename temp file to server save file
                File.Move(tempWorldFile, worldFile);
                LogProfileMessage("Server save file restored from temporary file (.tmp).");
            }
            finally
            {
                _profile = oldProfile;
            }
        }

        public void CreateProfileBackupArchiveFile(ServerProfileSnapshot profile)
        {
            // do nothing if profile is null
            if (profile == null)
                return;

            var oldProfile = _profile;

            try
            {
                _profile = profile;

                // create the backup file.
                try
                {
                    LogProfileMessage("");
                    LogProfileMessage("Back up profile and config files started...");

                    var backupFolder = GetProfileBackupFolder(_profile);
                    var backupFileName = $"{_startTime:yyyyMMdd_HHmmss}{Config.Default.BackupExtension}";
                    var backupFile = IOUtils.NormalizePath(Path.Combine(backupFolder, backupFileName));

                    var profileFile = GetProfileFile(_profile);
                    var gameIniFile = IOUtils.NormalizePath(Path.Combine(GetProfileServerConfigDir(_profile), Config.Default.ServerGameConfigFile));
                    var gusIniFile = IOUtils.NormalizePath(Path.Combine(GetProfileServerConfigDir(_profile), Config.Default.ServerGameUserSettingsConfigFile));
                    var launcherFile = GetLauncherFile();

                    if (!Directory.Exists(backupFolder))
                        Directory.CreateDirectory(backupFolder);

                    if (File.Exists(backupFile))
                        File.Delete(backupFile);

                    var files = new List<string>();
                    if (File.Exists(profileFile))
                        files.Add(profileFile);

                    if (File.Exists(gameIniFile))
                        files.Add(gameIniFile);

                    if (File.Exists(gusIniFile))
                        files.Add(gusIniFile);

                    if (File.Exists(launcherFile))
                        files.Add(launcherFile);

                    var comment = new StringBuilder();
                    comment.AppendLine($"Windows Platform: {Environment.OSVersion.Platform}");
                    comment.AppendLine($"Windows Version: {Environment.OSVersion.VersionString}");
                    comment.AppendLine($"Server Manager Version: {App.Instance.Version}");
                    comment.AppendLine($"Server Manager Key: {Config.Default.ServerManagerCode}");
                    comment.AppendLine($"Config Directory: {Config.Default.ConfigDirectory}");
                    comment.AppendLine($"Server Directory: {_profile.InstallDirectory}");
                    comment.AppendLine($"Profile Name: {_profile.ProfileName}");
                    comment.AppendLine($"SotF Server: {_profile.SotFEnabled}");
                    comment.AppendLine($"PGM Server: {_profile.PGM_Enabled}");
                    comment.AppendLine($"Process: {ServerProcess}");

                    ZipUtils.ZipFiles(backupFile, files, comment.ToString(), false);

                    LogProfileMessage($"Backup file created - {backupFile}");
                }
                catch (Exception ex)
                {
                    LogProfileError($"Error backing up profile and config files.\r\n{ex.Message}", false);
                }
                finally
                {
                    LogProfileMessage("Back up profile and config files finished.");
                }

                // delete the old backup files
                if (DeleteOldBackupFiles)
                {
                    try
                    {
                        var deleteInterval = Config.Default.AutoBackup_EnableBackup ? Config.Default.AutoBackup_DeleteInterval : BACKUP_DELETEINTERVAL;

                        LogProfileMessage("");
                        LogProfileMessage("Delete old profile backup files started...");

                        var backupFolder = GetProfileBackupFolder(_profile);
                        if (Directory.Exists(backupFolder))
                        {
                            var backupFileFilter = $"*{Config.Default.BackupExtension}";
                            var backupDateFilter = DateTime.Now.AddDays(-deleteInterval);

                            var backupFiles = new DirectoryInfo(backupFolder).GetFiles(backupFileFilter).Where(f => f.LastWriteTime < backupDateFilter);
                            foreach (var backupFile in backupFiles)
                            {
                                try
                                {
                                    LogProfileMessage($"{backupFile.Name} was deleted, last updated {backupFile.CreationTime}.");
                                    backupFile.Delete();
                                }
                                catch
                                {
                                    // if unable to delete, do not bother
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogProfileError($"Error deleting old profile backup files.\r\n{ex.Message}", false);
                    }
                    finally
                    {
                        LogProfileMessage("Delete old profile backup files finished.");
                    }

                    // cleanup any backup folders from old backup process
                    try
                    {
                        var backupFolder = GetProfileBackupFolder(_profile);
                        if (Directory.Exists(backupFolder))
                        {
                            var oldBackupFolders = new DirectoryInfo(backupFolder).GetDirectories();
                            foreach (var oldBackupFolder in oldBackupFolders)
                            {
                                oldBackupFolder.Delete(true);
                            }
                        }
                    }
                    catch
                    {
                        // if unable to delete, do not bother
                    }
                }
            }
            finally
            {
                _profile = oldProfile;
            }
        }

        public void CreateServerBackupArchiveFile(StringBuilder emailMessage, ServerProfileSnapshot profile)
        {
            // do nothing if profile is null or SotF
            if (profile == null || profile.SotFEnabled)
                return;

            var oldProfile = _profile;

            try
            {
                _profile = profile;

                LogProfileMessage("");

                // check if the servers save folder exists
                var saveFolder = GetServerSaveFolder();
                if (Directory.Exists(saveFolder))
                {
                    // make a backup of the current world file.
                    var worldFile = GetServerWorldFile();
                    if (File.Exists(worldFile))
                    {
                        try
                        {
                            LogProfileMessage("Back up world files started...");

                            var backupFolder = GetServerBackupFolder(_profile);
                            var mapName = ServerProfile.GetProfileMapFileName(_profile.ServerMap, _profile.PGM_Enabled, _profile.PGM_Name);
                            var backupFileName = $"{mapName}_{_startTime:yyyyMMdd_HHmmss}{Config.Default.BackupExtension}";
                            var backupFile = IOUtils.NormalizePath(Path.Combine(backupFolder, backupFileName));

                            if (!Directory.Exists(backupFolder))
                                Directory.CreateDirectory(backupFolder);

                            if (File.Exists(backupFile))
                                File.Delete(backupFile);

                            var files = new List<string>
                            {
                                worldFile
                            };

                            // get the player files
                            var saveFolderInfo = new DirectoryInfo(saveFolder);
                            var playerFileFilter = $"*{Config.Default.PlayerFileExtension}";
                            var playerFiles = saveFolderInfo.GetFiles(playerFileFilter, SearchOption.TopDirectoryOnly);
                            foreach (var file in playerFiles)
                            {
                                files.Add(file.FullName);
                            }

                            // get the tribe files
                            var tribeFileFilter = $"*{Config.Default.TribeFileExtension}";
                            var tribeFiles = saveFolderInfo.GetFiles(tribeFileFilter, SearchOption.TopDirectoryOnly);
                            foreach (var file in tribeFiles)
                            {
                                files.Add(file.FullName);
                            }

                            // get the tribute tribe files
                            var tributeTribeFileFilter = $"*{Config.Default.TributeTribeFileExtension}";
                            var tributeTribeFiles = saveFolderInfo.GetFiles(tributeTribeFileFilter, SearchOption.TopDirectoryOnly);
                            foreach (var file in tributeTribeFiles)
                            {
                                files.Add(file.FullName);
                            }

                            var comment = new StringBuilder();
                            comment.AppendLine($"Windows Platform: {Environment.OSVersion.Platform}");
                            comment.AppendLine($"Windows Version: {Environment.OSVersion.VersionString}");
                            comment.AppendLine($"Server Manager Version: {App.Instance.Version}");
                            comment.AppendLine($"Server Manager Key: {Config.Default.ServerManagerCode}");
                            comment.AppendLine($"Config Directory: {Config.Default.ConfigDirectory}");
                            comment.AppendLine($"Server Directory: {_profile.InstallDirectory}");
                            comment.AppendLine($"Profile Name: {_profile.ProfileName}");
                            comment.AppendLine($"SotF Server: {_profile.SotFEnabled}");
                            comment.AppendLine($"PGM Server: {_profile.PGM_Enabled}");
                            comment.AppendLine($"Process: {ServerProcess}");

                            ZipUtils.ZipFiles(backupFile, files, comment.ToString(), false);

                            LogProfileMessage($"Backed up world files - {saveFolder}");
                            LogProfileMessage($"Backup file created - {backupFile}");

                            emailMessage?.AppendLine();
                            emailMessage?.AppendLine("Backed up world files.");
                            emailMessage?.AppendLine(saveFolder);

                            emailMessage?.AppendLine();
                            emailMessage?.AppendLine("Backup file created.");
                            emailMessage?.AppendLine(backupFile);
                        }
                        catch (Exception ex)
                        {
                            LogProfileError($"Error backing up world files.\r\n{ex.Message}", false);

                            emailMessage?.AppendLine();
                            emailMessage?.AppendLine("Error backing up world files.");
                            emailMessage?.AppendLine(ex.Message);
                        }
                        finally
                        {
                            LogProfileMessage("Back up world files finished.");
                        }
                    }
                    else
                    {
                        LogProfileMessage($"Server save file does not exist or could not be found '{worldFile}'.");
                        LogProfileMessage($"Backup not performed.");

                        emailMessage?.AppendLine();
                        emailMessage?.AppendLine($"Server save file does not exist or could not be found.");
                        emailMessage?.AppendLine(worldFile);

                        emailMessage?.AppendLine();
                        emailMessage?.AppendLine("Backup not performed.");
                    }
                }
                else
                {
                    LogProfileMessage($"Server save folder does not exist or could not be found '{saveFolder}'.");
                    LogProfileMessage($"Backup not performed.");

                    emailMessage?.AppendLine();
                    emailMessage?.AppendLine($"Server save folder does not exist or could not be found.");
                    emailMessage?.AppendLine(saveFolder);

                    emailMessage?.AppendLine();
                    emailMessage?.AppendLine("Backup not performed.");
                }

                // delete the old backup files
                if (DeleteOldBackupFiles)
                {
                    try
                    {
                        var deleteInterval = Config.Default.AutoBackup_EnableBackup ? Config.Default.AutoBackup_DeleteInterval : BACKUP_DELETEINTERVAL;

                        LogProfileMessage("");
                        LogProfileMessage("Delete old server backup files started...");

                        var backupFolder = GetServerBackupFolder(_profile);
                        if (Directory.Exists(backupFolder))
                        {
                            var mapName = ServerProfile.GetProfileMapFileName(_profile.ServerMap, _profile.PGM_Enabled, _profile.PGM_Name);
                            var backupFileFilter = $"{mapName}_*{Config.Default.BackupExtension}";
                            var backupDateFilter = DateTime.Now.AddDays(-deleteInterval);

                            var backupFiles = new DirectoryInfo(backupFolder).GetFiles(backupFileFilter).Where(f => f.LastWriteTime < backupDateFilter);
                            foreach (var backupFile in backupFiles)
                            {
                                try
                                {
                                    LogProfileMessage($"{backupFile.Name} was deleted, last updated {backupFile.CreationTime}.");
                                    backupFile.Delete();
                                }
                                catch
                                {
                                    // if unable to delete, do not bother
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogProfileError($"Error deleting old server backup files.\r\n{ex.Message}", false);
                    }
                    finally
                    {
                        LogProfileMessage("Delete old server backup files finished.");
                    }
                }
            }
            finally
            {
                _profile = oldProfile;
            }
        }

        public static void DirectoryCopy(string sourceFolder, string destinationFolder, bool copySubFolders, bool useSmartCopy, ProgressDelegate progressCallback)
        {
            var directory = new DirectoryInfo(sourceFolder);
            if (!directory.Exists)
                return;

            // If the destination directory doesn't exist, create it.
            if (!Directory.Exists(destinationFolder))
            {
                Directory.CreateDirectory(destinationFolder);
            }

            // If copying subdirectories, copy them and their contents to new location.
            if (copySubFolders)
            {
                var subDirectories = directory.GetDirectories();

                foreach (var subDirectory in subDirectories)
                {
                    var tempDirectory = Path.Combine(destinationFolder, subDirectory.Name);
                    DirectoryCopy(subDirectory.FullName, tempDirectory, copySubFolders, useSmartCopy, progressCallback);
                }
            }

            progressCallback?.Invoke(0, directory.FullName);

            // Get the files in the directory and copy them to the new location.
            var files = directory.GetFiles();

            foreach (var file in files)
            {
                if (!file.Exists)
                    continue;

                // check if the destination file is newer
                var destFile = new FileInfo(Path.Combine(destinationFolder, file.Name));
                if (useSmartCopy && destFile.Exists && destFile.LastWriteTime >= file.LastWriteTime && destFile.Length == file.Length)
                    continue;

                // destination file does not exist, or is older. Override with the source file.
                while (true)
                {
                    var retries = 0;
                    try
                    {
                        file.CopyTo(destFile.FullName, true);
                        break;
                    }
                    catch (IOException)
                    {
                        retries++;
                        if (retries >= FILECOPY_MAXRETRIES) throw;
                        Task.Delay(5000).Wait();
                    }
                }
            }
        }

        public static string GetBranchName(string branchName) => string.IsNullOrWhiteSpace(branchName) ? Config.Default.DefaultServerBranchName : branchName;

        private string GetLauncherFile() => IOUtils.NormalizePath(Path.Combine(GetProfileServerConfigDir(_profile), Config.Default.LauncherFile));

        private static string GetLogFolder(string logType) => IOUtils.NormalizePath(Path.Combine(App.GetLogFolder(), logType));

        private static Logger GetLogger(string logFilePath, string logType, string logName)
        {
#if DEBUG
            return GetLogger(logFilePath, logType, logName ?? string.Empty, LogLevel.Debug, LogLevel.Fatal);
#else
            return GetLogger(logFilePath, logType, logName ?? string.Empty, LogLevel.Info, LogLevel.Fatal);
#endif
        }

        private static Logger GetLogger(string logFilePath, string logType, string logName, LogLevel minLevel, LogLevel maxLevel)
        {
            if (string.IsNullOrWhiteSpace(logFilePath) || string.IsNullOrWhiteSpace(logType) || string.IsNullOrWhiteSpace(logName))
                return null;

            var loggerName = $"{logType}_{logName}".Replace(" ", "_");

            if (LogManager.Configuration.FindTargetByName(loggerName) is null)
            {
                var logFile = new FileTarget(loggerName)
                {
                    FileName = Path.Combine(logFilePath, $"{logName}.log"),
                    Layout = "${time} [${level:uppercase=true}] ${message}",
                    ArchiveFileName = Path.Combine(logFilePath, $"{logName}.{{#}}.log"),
                    ArchiveNumbering = ArchiveNumberingMode.DateAndSequence,
                    ArchiveEvery = FileArchivePeriod.Day,
                    ArchiveDateFormat = "yyyyMMdd",
                    ArchiveOldFileOnStartup = true,
                    MaxArchiveFiles = Config.Default.LoggingMaxArchiveFiles,
                    MaxArchiveDays = Config.Default.LoggingMaxArchiveDays,
                    CreateDirs = true,
                };
                LogManager.Configuration.AddTarget(loggerName, logFile);

                var rule = new LoggingRule(loggerName, minLevel, maxLevel, logFile);
                LogManager.Configuration.LoggingRules.Add(rule);
                LogManager.ReconfigExistingLoggers();
            }

            return LogManager.GetLogger(loggerName);
        }

        private List<string> GetModList()
        {
            var modIdList = new List<string>();

            // check if we need to update the mods.
            if (Config.Default.ServerUpdate_UpdateModsWhenUpdatingServer)
            {
                if (_profile == null)
                {
                    // get all the mods for all the profiles.
                    foreach (var profile in _profiles.Keys)
                    {
                        // check if the profile is included int he auto update.
                        if (!profile.EnableAutoUpdate)
                            continue;

                        if (!string.IsNullOrWhiteSpace(profile.ServerMapModId))
                            modIdList.Add(profile.ServerMapModId);

                        if (!string.IsNullOrWhiteSpace(profile.TotalConversionModId))
                            modIdList.Add(profile.TotalConversionModId);

                        modIdList.AddRange(profile.ServerModIds);
                    }
                }
                else
                {
                    // get all the mods for only the specified profile.
                    if (!string.IsNullOrWhiteSpace(_profile.ServerMapModId))
                        modIdList.Add(_profile.ServerMapModId);

                    if (!string.IsNullOrWhiteSpace(_profile.TotalConversionModId))
                        modIdList.Add(_profile.TotalConversionModId);

                    modIdList.AddRange(_profile.ServerModIds);
                }
            }

            return ModUtils.ValidateModList(modIdList);
        }

        private static string GetProfileBackupFolder(ServerProfileSnapshot profile)
        {
            if (string.IsNullOrWhiteSpace(Config.Default.BackupPath))
                return IOUtils.NormalizePath(Path.Combine(Config.Default.ConfigDirectory, Config.Default.BackupDir, profile.ProfileId.ToLower()));

            return IOUtils.NormalizePath(Path.Combine(Config.Default.BackupPath, Config.Default.ProfilesDir, profile.ProfileId.ToLower()));
        }

        private static string GetProfileFile(ServerProfileSnapshot profile) => IOUtils.NormalizePath(Path.Combine(Config.Default.ConfigDirectory, $"{profile.ProfileId.ToLower()}{Config.Default.ProfileExtension}"));

        private string GetProfileLogFolder(string profileId, string logType) => IOUtils.NormalizePath(Path.Combine(App.GetProfileLogFolder(profileId), logType));

        public static string GetProfileServerConfigDir(ServerProfile profile) => Path.Combine(profile.InstallDirectory, Config.Default.ServerConfigRelativePath);

        public static string GetProfileServerConfigDir(ServerProfileSnapshot profile) => Path.Combine(profile.InstallDirectory, Config.Default.ServerConfigRelativePath);

        public static string GetMutexName(string directory)
        {
            using (var hashAlgo = MD5.Create())
            {
                StringBuilder builder = new StringBuilder();

                var hashStr = Encoding.UTF8.GetBytes(directory ?? Assembly.GetExecutingAssembly().Location);
                var hash = hashAlgo.ComputeHash(hashStr);
                foreach (var b in hash)
                {
                    builder.Append(b.ToString("x2"));
                }

                return builder.ToString();
            }
        }

        public static string GetServerBackupFolder(ServerProfile profile)
        {
            if (string.IsNullOrWhiteSpace(Config.Default.BackupPath))
                return IOUtils.NormalizePath(Path.Combine(Config.Default.DataDir, Config.Default.ServersInstallDir, Config.Default.BackupDir, profile.ProfileID.ToLower()));

            return IOUtils.NormalizePath(Path.Combine(Config.Default.BackupPath, Config.Default.ServersInstallDir, profile.ProfileID.ToLower()));
        }

        public static string GetServerBackupFolder(ServerProfileSnapshot profile)
        {
            if (string.IsNullOrWhiteSpace(Config.Default.BackupPath))
                return IOUtils.NormalizePath(Path.Combine(Config.Default.DataDir, Config.Default.ServersInstallDir, Config.Default.BackupDir, profile.ProfileId.ToLower()));

            return IOUtils.NormalizePath(Path.Combine(Config.Default.BackupPath, Config.Default.ServersInstallDir, profile.ProfileId.ToLower()));
        }

        private static string GetServerCacheFolder(string branchName) => IOUtils.NormalizePath(Path.Combine(Config.Default.AutoUpdate_CacheDir, $"{Config.Default.ServerBranchFolderPrefix}{GetBranchName(branchName)}"));

        private static string GetServerCacheTimeFile(string branchName) => IOUtils.NormalizePath(Path.Combine(GetServerCacheFolder(branchName), Config.Default.LastUpdatedTimeFile));

        private static string GetServerCacheVersionFile(string branchName) => IOUtils.NormalizePath(Path.Combine(GetServerCacheFolder(branchName), Config.Default.VersionFile));

        private string GetServerExecutableFile() => IOUtils.NormalizePath(Path.Combine(_profile.InstallDirectory, Config.Default.ServerBinaryRelativePath, Config.Default.ServerExe));

        private DateTime GetServerLatestTime(string timeFile)
        {
            try
            {
                if (!File.Exists(timeFile))
                    return DateTime.MinValue;

                var value = File.ReadAllText(timeFile);
                return DateTime.Parse(value, CultureInfo.CurrentCulture, DateTimeStyles.RoundtripKind);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private Process GetServerProcess()
        {
            // Find the server process.
            var expectedPath = GetServerExecutableFile();
            var runningProcesses = Process.GetProcessesByName(Config.Default.ServerProcessName);

            Process process = null;
            foreach (var runningProcess in runningProcesses)
            {
                var runningPath = ProcessUtils.GetMainModuleFilepath(runningProcess.Id);
                if (string.Equals(expectedPath, runningPath, StringComparison.OrdinalIgnoreCase))
                {
                    process = runningProcess;
                    break;
                }
            }

            return process;
        }

        private string GetServerTimeFile() => IOUtils.NormalizePath(Path.Combine(_profile.InstallDirectory, Config.Default.LastUpdatedTimeFile));

        private string GetServerSaveFolder() => IOUtils.NormalizePath(ServerProfile.GetProfileSavePath(_profile.InstallDirectory, _profile.AltSaveDirectoryName, _profile.PGM_Enabled, _profile.PGM_Name));

        private string GetServerVersionFile() => IOUtils.NormalizePath(Path.Combine(_profile.InstallDirectory, Config.Default.VersionFile));

        public static Version GetServerVersion(string versionFile)
        {
            if (!string.IsNullOrWhiteSpace(versionFile) && File.Exists(versionFile))
            {
                try
                {
                    var fileValue = File.ReadAllText(versionFile);

                    if (!string.IsNullOrWhiteSpace(fileValue))
                    {
                        string versionString = fileValue.ToString();
                        if (versionString.IndexOf('.') == -1)
                            versionString = versionString + ".0";

                        if (Version.TryParse(versionString, out Version version))
                            return version;
                    }
                }
                catch (Exception)
                {
                    // do nothing, just leave
                }
            }

            return new Version(0, 0);
        }

        private string GetServerWorldFile()
        {
            var profileSaveFolder = GetServerSaveFolder();
            var mapName = ServerProfile.GetProfileMapFileName(_profile.ServerMap, _profile.PGM_Enabled, _profile.PGM_Name);
            return IOUtils.NormalizePath(Path.Combine(profileSaveFolder, $"{mapName}{Config.Default.MapExtension}"));
        }

        private int GetShutdownCheckInterval(int minutesLeft)
        {
            if (minutesLeft >= 30)
                return 30;
            if (minutesLeft >= 15)
                return 15;
            if (minutesLeft >= 5)
                return 5;
            return 1;
        }

        public static bool HasNewServerVersion(string directory, DateTime checkTime)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return false;

            // check if any of the files have changed in the root folder.
            var hasNewVersion = new DirectoryInfo(directory).GetFiles("*.*", SearchOption.TopDirectoryOnly).Any(file => file.LastWriteTime >= checkTime);
            if (!hasNewVersion)
            {
                // get a list of the sub folders.
                var folders = new DirectoryInfo(directory).GetDirectories();
                foreach (var folder in folders)
                {
                    // do not include the steamapps folder in the check
                    if (folder.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
                        continue;

                    hasNewVersion = folder.GetFiles("*.*", SearchOption.AllDirectories).Any(file => file.LastWriteTime >= checkTime);
                    if (hasNewVersion)
                        break;
                }
            }

            return hasNewVersion;
        }

        private static void LoadProfiles()
        {
            if (_profiles != null)
            {
                _profiles.Clear();
                _profiles = null;
            }

            var profiles = new Dictionary<ServerProfileSnapshot, ServerProfile>();

            ServerRuntime.EnableUpdateModStatus = false;
            ServerProfile.EnableServerFilesWatcher = false;

            foreach (var profileFile in Directory.EnumerateFiles(Config.Default.ConfigDirectory, "*" + Config.Default.ProfileExtension))
            {
                try
                {
                    var profile = ServerProfile.LoadFromProfileFileBasic(profileFile, null);
                    profiles.Add(ServerProfileSnapshot.Create(profile), profile);
                }
                catch (Exception ex)
                {
                    LogMessage($"The profile at {profileFile} failed to load.\r\n{ex.Message}\r\n{ex.StackTrace}");
                }
            }

            _profiles = profiles.OrderBy(p => p.Value?.SortKey).ToDictionary(i => i.Key, v => v.Value);
        }

        private static void LogError(string error)
        {
            if (string.IsNullOrWhiteSpace(error))
                return;

            _loggerManager?.Error(error);

            Debug.WriteLine($"[ERROR] {error}");
        }

        private static void LogMessage(string message)
        {
            message = message ?? string.Empty;

            _loggerManager?.Info(message);

            Debug.WriteLine($"[INFO] {message}");
        }

        private void LogBranchError(string branchName, string error, bool includeProgressCallback = true)
        {
            if (string.IsNullOrWhiteSpace(error))
                return;

            _loggerBranch?.Error(error);

            if (includeProgressCallback)
                ProgressCallback?.Invoke(0, $"[ERROR] {error}");

            Debug.WriteLine($"[ERROR] (Branch {GetBranchName(branchName) ?? "unknown"}) {error}");
        }

        private void LogBranchMessage(string branchName, string message, bool includeProgressCallback = true)
        {
            message = message ?? string.Empty;

            _loggerBranch?.Info(message);

            if (includeProgressCallback)
                ProgressCallback?.Invoke(0, $"{message}");

            Debug.WriteLine($"[INFO] (Branch {GetBranchName(branchName) ?? "unknown"}) {message}");
        }

        private void LogProfileDebug(string message, bool includeProgressCallback = true)
        {
            message = message ?? string.Empty;

            _loggerProfile?.Debug(message);

            if (includeProgressCallback)
                ProgressCallback?.Invoke(0, $"{message}");

            Debug.WriteLine($"[DEBUG] (Profile {_profile?.ProfileName ?? "unknown"}) {message}");
        }

        private void LogProfileError(string error, bool includeProgressCallback = true)
        {
            if (string.IsNullOrWhiteSpace(error))
                return;

            _loggerProfile?.Error(error);

            if (includeProgressCallback)
                ProgressCallback?.Invoke(0, $"[ERROR] {error}");

            Debug.WriteLine($"[ERROR] (Profile {_profile?.ProfileName ?? "unknown"}) {error}");
        }

        private void LogProfileMessage(string message, bool includeProgressCallback = true)
        {
            message = message ?? string.Empty;

            _loggerProfile?.Info(message);

            if (includeProgressCallback)
                ProgressCallback?.Invoke(0, $"{message}");

            Debug.WriteLine($"[INFO] (Profile {_profile?.ProfileName ?? "unknown"}) {message}");
        }

        private void ProcessAlert(AlertType alertType, string alertMessage)
        {
            if (_pluginHelper == null || !SendAlerts || string.IsNullOrWhiteSpace(alertMessage))
                return;

            if (_pluginHelper.ProcessAlert(alertType, _profile?.ProfileName ?? String.Empty, alertMessage))
            {
                LogProfileMessage($"Alert message sent - {alertType}: {alertMessage}", false);
            }
        }

        private async Task<bool> SendCommandAsync(string command, bool retryIfFailed)
        {
            if (_profile == null || !_profile.RCONEnabled)
                return false;
            if (string.IsNullOrWhiteSpace(command))
                return false;

            int retries = 0;
            int rconRetries = 0;
            int maxRetries = retryIfFailed ? RCON_MAXRETRIES : 1;

            try
            {
                while (retries < maxRetries && rconRetries < RCON_MAXRETRIES)
                {
                    SetupRconConsole();

                    if (_rconConsole == null)
                    {
                        LogProfileMessage($"RCON> {command} - attempt {rconRetries + 1} (a).", false);
                        LogProfileMessage("RCON connection could not be created.", false);
                        rconRetries++;
                    }
                    else
                    {
                        rconRetries = 0;
                        try
                        {
                            _rconConsole.SendCommand(command);
                            LogProfileMessage($"RCON> {command}");

                            return true;
                        }
                        catch (Exception ex)
                        {
                            LogProfileMessage($"RCON> {command} - attempt {retries + 1} (b).", false);
                            LogProfileMessage($"{ex.Message}", false);
                            LogProfileMessage($"{ex.StackTrace}", false);
                        }

                        retries++;
                    }
                }
            }
            finally
            {
                CloseRconConsole();
            }

            return false;
        }

        private async Task<bool> SendMessageAsync(string message, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(message) || !SendMessages)
                return false;

            var sent = await SendCommandAsync($"{Config.Default.RCON_MessageCommand.ToLower()} {message}", false);

            if (sent)
            {
                try
                {
                    Task.Delay(Config.Default.SendMessageDelay, token).Wait(token);
                }
                catch { }
            }

            return sent;
        }

        private void SendEmail(string subject, string body, bool includeLogFile, bool isBodyHtml = false)
        {
            if (!SendEmails)
                return;

            try
            {
                var email = new EmailUtil()
                {
                    EnableSsl = Config.Default.Email_UseSSL,
                    MailServer = Config.Default.Email_Host,
                    Port = Config.Default.Email_Port,
                    UseDefaultCredentials = Config.Default.Email_UseDetaultCredentials,
                    Credentials = Config.Default.Email_UseDetaultCredentials ? null : new NetworkCredential(Config.Default.Email_Username, Config.Default.Email_Password),
                };

                StringBuilder messageBody = new StringBuilder(body);
                Attachment attachment = null;

                if (includeLogFile && _loggerProfile != null)
                {
                    var fileTarget = LogManager.Configuration.FindTargetByName(_loggerProfile.Name) as FileTarget;
                    var fileLayout = fileTarget?.FileName as SimpleLayout;
                    var logFile = fileLayout?.Text ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(logFile) && File.Exists(logFile))
                    {
                        attachment = new Attachment(logFile);
                    }
                }

                email.SendEmail(Config.Default.Email_From, Config.Default.Email_To?.Split(','), subject, messageBody.ToString(), isBodyHtml, new[] { attachment });

                LogProfileMessage($"Email Sent - {subject}\r\n{body}");
            }
            catch (Exception ex)
            {
                LogProfileError($"Unable to send email.\r\n{ex.Message}", false);
            }
        }

        private void CloseRconConsole()
        {
            if (_rconConsole != null)
            {
                _rconConsole.Dispose();
                _rconConsole = null;

                Task.Delay(1000).Wait();
            }
        }

        private void SetupRconConsole()
        {
            CloseRconConsole();

            if (_profile == null || !_profile.RCONEnabled)
                return;

            try
            {
                var endPoint = new IPEndPoint(_profile.ServerIPAddress, _profile.RCONPort);
                var server = QueryMaster.ServerQuery.GetServerInstance(QueryMaster.EngineType.Source, endPoint, sendTimeOut: 10000, receiveTimeOut: 10000);
                if (server == null)
                {
                    LogProfileDebug($"FAILED: {nameof(SetupRconConsole)} - ServerQuery could not be created.", false);
                    return;
                }

                LogProfileDebug($"SUCCESS: {nameof(SetupRconConsole)} - ServerQuery was created.", false);

                Task.Delay(1000).Wait();

                _rconConsole = server.GetControl(_profile.RCONPassword);
                if (_rconConsole == null)
                {
                    LogProfileDebug($"FAILED: {nameof(SetupRconConsole)} - RconConsole could not be created ({_profile.RCONPassword}).", false);
                    return;
                }

                LogProfileDebug($"SUCCESS: {nameof(SetupRconConsole)} - RconConsole was created ({_profile.RCONPassword}).", false);
            }
            catch (Exception ex)
            {
                LogProfileDebug($"ERROR: {nameof(SetupRconConsole)}\r\n{ex.Message}", false);
                LogProfileDebug($"ERROR: {nameof(SetupRconConsole)}\r\n{ex.StackTrace}", false);
            }
        }

        public int PerformProfileBackup(ServerProfileSnapshot profile, CancellationToken cancellationToken)
        {
            _profile = profile;

            if (_profile == null || _profile.SotFEnabled)
                return EXITCODE_NORMALEXIT;

            ExitCode = EXITCODE_NORMALEXIT;

            Mutex mutex = null;
            var createdNew = false;

            if (OutputLogs)
                _loggerProfile = GetLogger(GetProfileLogFolder(profile.ProfileId, LOGPREFIX_AUTOBACKUP), $"{LOGPREFIX_AUTOBACKUP}_{profile.ProfileId}", "Backup");

            try
            {
                // try to establish a mutex for the profile.
                mutex = new Mutex(true, GetMutexName(_profile.InstallDirectory), out createdNew);
                if (!createdNew)
                    createdNew = mutex.WaitOne(new TimeSpan(0, MUTEX_TIMEOUT, 0));

                // check if the mutex was established
                if (createdNew)
                {
                    BackupServer(cancellationToken);

                    if (ExitCode != EXITCODE_NORMALEXIT)
                    {
                        if (Config.Default.EmailNotify_AutoBackup)
                            SendEmail($"{_profile.ProfileName} server backup", Config.Default.Alert_BackupProcessError, true);
                        ProcessAlert(AlertType.Error, Config.Default.Alert_BackupProcessError);
                    }
                }
                else
                {
                    ExitCode = EXITCODE_PROCESSALREADYRUNNING;
                    LogProfileMessage("Cancelled server backup process, could not lock server.");
                }
            }
            catch (Exception ex)
            {
                LogProfileError(ex.Message);
                if (ex.InnerException != null)
                    LogProfileMessage($"InnerException - {ex.InnerException.Message}");
                LogProfileMessage($"StackTrace\r\n{ex.StackTrace}");

                if (Config.Default.EmailNotify_AutoBackup)
                    SendEmail($"{_profile.ProfileName} server update", Config.Default.Alert_BackupProcessError, true);
                ProcessAlert(AlertType.Error, Config.Default.Alert_BackupProcessError);
                ExitCode = EXITCODE_UNKNOWNTHREADERROR;
            }
            finally
            {
                if (mutex != null)
                {
                    if (createdNew)
                    {
                        mutex.ReleaseMutex();
                        mutex.Dispose();
                    }
                }
            }

            ServerStatusChangeCallback?.Invoke(ServerStatus.Unknown);

            LogProfileMessage("");
            LogProfileMessage($"Exitcode = {ExitCode}");
            return ExitCode;
        }

        public int PerformProfileShutdown(ServerProfileSnapshot profile, bool performRestart, bool performUpdate, bool checkGracePeriod, bool steamCmdRemoveQuit, CancellationToken cancellationToken)
        {
            _profile = profile;

            if (_profile == null)
                return EXITCODE_NORMALEXIT;

            ExitCode = EXITCODE_NORMALEXIT;

            Mutex mutex = null;
            var createdNew = false;

            if (OutputLogs)
                _loggerProfile = GetLogger(GetProfileLogFolder(profile.ProfileId, LOGPREFIX_AUTOSHUTDOWN), $"{LOGPREFIX_AUTOSHUTDOWN}_{profile.ProfileId}", "Shutdown");

            try
            {
                // check if within the shutdown grace period (only performed when restarting the server)
                if (performRestart && checkGracePeriod && Config.Default.AutoRestart_EnabledGracePeriod && profile.LastStarted.AddMinutes(Config.Default.AutoRestart_GracePeriod) > DateTime.Now)
                {
                    ExitCode = EXITCODE_PROCESSSKIPPED;
                    LogProfileMessage($"Cancelled server restart process, server was last started at ({profile.LastStarted:yyyy-MM-dd HH:mm:ss}) and is within the grace period ({Config.Default.AutoRestart_GracePeriod} minutes).");
                }
                else
                {
                    // try to establish a mutex for the profile.
                    mutex = new Mutex(true, GetMutexName(_profile.InstallDirectory), out createdNew);
                    if (!createdNew)
                        createdNew = mutex.WaitOne(new TimeSpan(0, MUTEX_TIMEOUT, 0));

                    // check if the mutex was established
                    if (createdNew)
                    {
                        ShutdownServer(performRestart, performUpdate, steamCmdRemoveQuit, cancellationToken);

                        if (ExitCode != EXITCODE_NORMALEXIT)
                        {
                            if (Config.Default.EmailNotify_AutoRestart)
                            {
                                if (performRestart)
                                    SendEmail($"{_profile.ProfileName} server restart", Config.Default.Alert_RestartProcessError, true);
                                else
                                    SendEmail($"{_profile.ProfileName} server shutdown", Config.Default.Alert_ShutdownProcessError, true);
                            }
                            if (performRestart)
                                ProcessAlert(AlertType.Error, Config.Default.Alert_RestartProcessError);
                            else
                                ProcessAlert(AlertType.Error, Config.Default.Alert_ShutdownProcessError);
                        }
                    }
                    else
                    {
                        ExitCode = EXITCODE_PROCESSALREADYRUNNING;
                        if (performRestart)
                            LogProfileMessage("Cancelled server restart process, could not lock server.");
                        else
                            LogProfileMessage("Cancelled server shutdown process, could not lock server.");
                    }
                }
            }
            catch (Exception ex)
            {
                LogProfileError(ex.Message);
                if (ex.InnerException != null)
                    LogProfileMessage($"InnerException - {ex.InnerException.Message}");
                LogProfileMessage($"StackTrace\r\n{ex.StackTrace}");

                if (Config.Default.EmailNotify_AutoRestart)
                {
                    if (performRestart)
                        SendEmail($"{_profile.ProfileName} server restart", Config.Default.Alert_RestartProcessError, true);
                    else
                        SendEmail($"{_profile.ProfileName} server shutdown", Config.Default.Alert_ShutdownProcessError, true);
                }
                if (performRestart)
                    ProcessAlert(AlertType.Error, Config.Default.Alert_RestartProcessError);
                else
                    ProcessAlert(AlertType.Error, Config.Default.Alert_ShutdownProcessError);
                ExitCode = EXITCODE_UNKNOWNTHREADERROR;
            }
            finally
            {
                if (mutex != null)
                {
                    if (createdNew)
                    {
                        mutex.ReleaseMutex();
                        mutex.Dispose();
                    }
                }
            }

            ServerStatusChangeCallback?.Invoke(ServerStatus.Unknown);

            LogProfileMessage("");
            LogProfileMessage($"Exitcode = {ExitCode}");
            return ExitCode;
        }

        public int PerformProfileUpdate(BranchSnapshot branch, ServerProfileSnapshot profile)
        {
            _profile = profile;

            if (_profile == null || _profile.SotFEnabled)
                return EXITCODE_NORMALEXIT;

            ExitCode = EXITCODE_NORMALEXIT;

            Mutex mutex = null;
            var createdNew = false;

            if (OutputLogs)
            {
                _loggerBranch = GetLogger(GetLogFolder(LOGPREFIX_AUTOUPDATE), $"{LOGPREFIX_AUTOUPDATE}", $"BranchUpdate_{GetBranchName(branch.BranchName)}");
                _loggerProfile = GetLogger(GetProfileLogFolder(profile.ProfileId, LOGPREFIX_AUTOUPDATE), $"{LOGPREFIX_AUTOUPDATE}_{profile.ProfileId}", "Update");
            }

            try
            {
                LogBranchMessage(branch.BranchName, $"[{_profile.ProfileName}] Started server update process.");

                // try to establish a mutex for the profile.
                mutex = new Mutex(true, GetMutexName(_profile.InstallDirectory), out createdNew);
                if (!createdNew)
                    createdNew = mutex.WaitOne(new TimeSpan(0, MUTEX_TIMEOUT, 0));

                // check if the mutex was established
                if (createdNew)
                {
                    UpdateFiles();

                    LogBranchMessage(branch.BranchName, $"[{_profile.ProfileName}] Finished server update process.");

                    if (ExitCode != EXITCODE_NORMALEXIT)
                    {
                        if (Config.Default.EmailNotify_AutoUpdate)
                            SendEmail($"{_profile.ProfileName} server update", Config.Default.Alert_UpdateProcessError, true);
                        ProcessAlert(AlertType.Error, Config.Default.Alert_UpdateProcessError);
                    }
                }
                else
                {
                    ExitCode = EXITCODE_PROCESSALREADYRUNNING;
                    LogBranchMessage(branch.BranchName, $"[{_profile.ProfileName}] Cancelled server update process, could not lock server.");
                }
            }
            catch (Exception ex)
            {
                LogProfileError(ex.Message);
                LogProfileError(ex.GetType().ToString());
                if (ex.InnerException != null)
                {
                    LogProfileMessage($"InnerException - {ex.InnerException.Message}");
                    LogProfileMessage(ex.InnerException.GetType().ToString());
                }
                LogProfileMessage($"StackTrace\r\n{ex.StackTrace}");

                if (Config.Default.EmailNotify_AutoUpdate)
                    SendEmail($"{_profile.ProfileName} server update", Config.Default.Alert_UpdateProcessError, true);
                ProcessAlert(AlertType.Error, Config.Default.Alert_UpdateProcessError);
                ExitCode = EXITCODE_UNKNOWNTHREADERROR;
            }
            finally
            {
                if (mutex != null)
                {
                    if (createdNew)
                    {
                        mutex.ReleaseMutex();
                        mutex.Dispose();
                    }
                }
            }

            ServerStatusChangeCallback?.Invoke(ServerStatus.Unknown);

            LogProfileMessage("");
            LogProfileMessage($"Exitcode = {ExitCode}");
            return ExitCode;
        }

        public int PerformServerBranchUpdate(BranchSnapshot branch)
        {
            if (branch == null)
                return EXITCODE_NORMALEXIT;

            ExitCode = EXITCODE_NORMALEXIT;

            Mutex mutex = null;
            var createdNew = false;

            if (OutputLogs)
                _loggerBranch = GetLogger(GetLogFolder(LOGPREFIX_AUTOUPDATE), $"{LOGPREFIX_AUTOUPDATE}", $"BranchUpdate_{GetBranchName(branch.BranchName)}");

            try
            {
                LogMessage($"[{GetBranchName(branch.BranchName)}] Started branch update process.");

                var cacheFolder = GetServerCacheFolder(branch.BranchName);

                // try to establish a mutex for the profile.
                mutex = new Mutex(true, GetMutexName(cacheFolder), out createdNew);
                if (!createdNew)
                    createdNew = mutex.WaitOne(new TimeSpan(0, MUTEX_TIMEOUT, 0));

                // check if the mutex was established
                if (createdNew)
                {
                    // update the server cache for the branch
                    UpdateServerCache(branch.BranchName, branch.BranchPassword);

                    if (ExitCode != EXITCODE_NORMALEXIT)
                    {
                        if (Config.Default.EmailNotify_AutoUpdate)
                            SendEmail($"{GetBranchName(branch.BranchName)} branch update", Config.Default.Alert_UpdateProcessError, true);
                        ProcessAlert(AlertType.Error, Config.Default.Alert_UpdateProcessError);
                    }

                    if (ExitCode == EXITCODE_NORMALEXIT)
                    {
                        // get the profile associated with the branch
                        var profiles = _profiles.Keys.Where(p => p.EnableAutoUpdate && string.Equals(p.BranchName, branch.BranchName, StringComparison.OrdinalIgnoreCase));
                        var profileExitCodes = new ConcurrentDictionary<ServerProfileSnapshot, int>();

                        if (Config.Default.AutoUpdate_ParallelUpdate)
                        {
                            Parallel.ForEach(profiles, profile =>
                            {
                                var app = new ServerApp
                                {
                                    OutputLogs = OutputLogs,
                                    SendAlerts = SendAlerts,
                                    SendEmails = SendEmails,
                                    ServerProcess = ServerProcess,
                                    SteamCMDProcessWindowStyle = ProcessWindowStyle.Hidden
                                };
                                app.PerformProfileUpdate(branch, profile);
                                profileExitCodes.TryAdd(profile, app.ExitCode);
                            });
                        }
                        else
                        {
                            var delay = 0;
                            foreach (var profile in profiles)
                            {
                                if (delay > 0)
                                    Task.Delay(delay * 1000).Wait();
                                delay = Math.Max(0, Config.Default.AutoUpdate_SequencialDelayPeriod);

                                var app = new ServerApp
                                {
                                    OutputLogs = OutputLogs,
                                    SendAlerts = SendAlerts,
                                    SendEmails = SendEmails,
                                    ServerProcess = ServerProcess,
                                    SteamCMDProcessWindowStyle = ProcessWindowStyle.Hidden
                                };
                                app.PerformProfileUpdate(branch, profile);
                                profileExitCodes.TryAdd(profile, app.ExitCode);
                            }
                        }

                        if (profileExitCodes.Any(c => !c.Value.Equals(EXITCODE_NORMALEXIT)))
                            ExitCode = EXITCODE_EXITWITHERRORS;
                    }

                    LogMessage($"[{GetBranchName(branch.BranchName)}] Finished branch update process.");
                }
                else
                {
                    ExitCode = EXITCODE_PROCESSALREADYRUNNING;
                    LogMessage($"[{GetBranchName(branch.BranchName)}] Cancelled branch update process, could not lock branch folder.");
                }
            }
            catch (Exception ex)
            {
                LogBranchError(branch.BranchName, ex.Message);
                LogBranchError(branch.BranchName, ex.GetType().ToString());
                if (ex.InnerException != null)
                {
                    LogBranchMessage(branch.BranchName, $"InnerException - {ex.InnerException.Message}");
                    LogBranchMessage(branch.BranchName, ex.InnerException.GetType().ToString());
                }
                LogBranchMessage(branch.BranchName, $"StackTrace\r\n{ex.StackTrace}");

                if (Config.Default.EmailNotify_AutoUpdate)
                    SendEmail($"{GetBranchName(branch.BranchName)} branch update", Config.Default.Alert_UpdateProcessError, true);
                ProcessAlert(AlertType.Error, Config.Default.Alert_UpdateProcessError);
                ExitCode = EXITCODE_UNKNOWNTHREADERROR;
            }
            finally
            {
                if (mutex != null)
                {
                    if (createdNew)
                    {
                        mutex.ReleaseMutex();
                        mutex.Dispose();
                    }
                    mutex = null;
                }
            }

            LogBranchMessage(branch.BranchName, "");
            LogBranchMessage(branch.BranchName, $"Exitcode = {ExitCode}");
            return ExitCode;
        }

        public static int PerformAutoBackup()
        {
            int exitCode = EXITCODE_NORMALEXIT;

            _loggerManager = GetLogger(GetLogFolder(LOGPREFIX_AUTOBACKUP), LOGPREFIX_AUTOBACKUP, "AutoBackup");

            try
            {
                // check if a data directory has been setup.
                if (string.IsNullOrWhiteSpace(Config.Default.DataDir))
                    return EXITCODE_INVALIDDATADIRECTORY;

                // load all the profiles, do this at the very start in case the user changes one or more while the process is running.
                LoadProfiles();

                var profiles = _profiles.Keys.Where(p => p.EnableAutoBackup);
                var exitCodes = new ConcurrentDictionary<ServerProfileSnapshot, int>();

                Parallel.ForEach(profiles, profile => {
                    var app = new ServerApp
                    {
                        DeleteOldBackupFiles = Config.Default.AutoBackup_DeleteOldFiles,
                        OutputLogs = true,
                        SendAlerts = true,
                        SendEmails = true,
                        ServerProcess = ServerProcessType.AutoBackup
                    };
                    app.PerformProfileBackup(profile, CancellationToken.None);
                    exitCodes.TryAdd(profile, app.ExitCode);
                });

                foreach (var profile in _profiles.Keys)
                {
                    if (profile.ServerUpdated)
                    {
                        profile.Update(_profiles[profile]);
                        _profiles[profile].SaveProfile();
                    }
                }

                if (exitCodes.Any(c => !c.Value.Equals(EXITCODE_NORMALEXIT)))
                    exitCode = EXITCODE_EXITWITHERRORS;
            }
            catch (Exception)
            {
                exitCode = EXITCODE_UNKNOWNERROR;
            }

            return exitCode;
        }

        public static int PerformAutoShutdown(string argument, ServerProcessType type)
        {
            int exitCode = EXITCODE_NORMALEXIT;

            _loggerManager = GetLogger(GetLogFolder(LOGPREFIX_AUTOSHUTDOWN), LOGPREFIX_AUTOSHUTDOWN, "AutoShutdown");

            try
            {
                // check if a data directory has been setup.
                if (string.IsNullOrWhiteSpace(Config.Default.DataDir))
                    return EXITCODE_INVALIDDATADIRECTORY;

                if (string.IsNullOrWhiteSpace(argument) || (!argument.StartsWith(Constants.ARG_AUTOSHUTDOWN1) && !argument.StartsWith(Constants.ARG_AUTOSHUTDOWN2)))
                    return EXITCODE_BADARGUMENT;

                // load all the profiles, do this at the very start in case the user changes one or more while the process is running.
                LoadProfiles();

                var profileKey = string.Empty;
                switch (type)
                {
                    case ServerProcessType.AutoShutdown1:
                        profileKey = argument?.Substring(Constants.ARG_AUTOSHUTDOWN1.Length) ?? string.Empty;
                        break;
                    case ServerProcessType.AutoShutdown2:
                        profileKey = argument?.Substring(Constants.ARG_AUTOSHUTDOWN2.Length) ?? string.Empty;
                        break;
                    default:
                        return EXITCODE_BADARGUMENT;
                }

                var profile = _profiles?.Keys.FirstOrDefault(p => p.SchedulerKey.Equals(profileKey, StringComparison.Ordinal));
                if (profile == null)
                    return EXITCODE_PROFILENOTFOUND;

                var enableAutoShutdown = false;
                var performRestart = false;
                var performUpdate = false;
                switch (type)
                {
                    case ServerProcessType.AutoShutdown1:
                        enableAutoShutdown = profile.EnableAutoShutdown1;
                        performRestart = profile.RestartAfterShutdown1;
                        performUpdate = profile.UpdateAfterShutdown1;
                        break;
                    case ServerProcessType.AutoShutdown2:
                        enableAutoShutdown = profile.EnableAutoShutdown2;
                        performRestart = profile.RestartAfterShutdown2;
                        performUpdate = profile.UpdateAfterShutdown2;
                        break;
                    default:
                        return EXITCODE_BADARGUMENT;
                }

                if (!enableAutoShutdown)
                    return EXITCODE_AUTOSHUTDOWNNOTENABLED;

                var app = new ServerApp
                {
                    OutputLogs = true,
                    SendAlerts = true,
                    SendEmails = true,
                    ServerProcess = type,
                    SteamCMDProcessWindowStyle = ProcessWindowStyle.Hidden
                };
                exitCode = app.PerformProfileShutdown(profile, performRestart, performUpdate, true, false, CancellationToken.None);

                if (profile.ServerUpdated)
                {
                    profile.Update(_profiles[profile]);
                    _profiles[profile].SaveProfile();
                }
            }
            catch (Exception)
            {
                exitCode = EXITCODE_UNKNOWNERROR;
            }

            return exitCode;
        }

        public static int PerformAutoUpdate()
        {
            int exitCode = EXITCODE_NORMALEXIT;

            Mutex mutex = null;
            bool createdNew = false;

            _loggerManager = GetLogger(GetLogFolder(LOGPREFIX_AUTOUPDATE), LOGPREFIX_AUTOUPDATE, "AutoUpdate");

            try
            {
                // check if a data directory has been setup.
                if (string.IsNullOrWhiteSpace(Config.Default.DataDir))
                    return EXITCODE_INVALIDDATADIRECTORY;

                // check if the server cache folder has been set.
                if (string.IsNullOrWhiteSpace(Config.Default.AutoUpdate_CacheDir))
                    return EXITCODE_INVALIDCACHEDIRECTORY;

                // try to establish a mutex for the application.
                mutex = new Mutex(true, GetMutexName(Config.Default.DataDir), out createdNew);
                if (!createdNew)
                    createdNew = mutex.WaitOne(new TimeSpan(0, MUTEX_TIMEOUT, 0));

                // check if the mutex was established.
                if (createdNew)
                {
                    // load all the profiles, do this at the very start in case the user changes one or more while the process is running.
                    LoadProfiles();

                    // update the mods - needs to be done before the server cache updates
                    ServerApp app = new ServerApp
                    {
                        ServerProcess = ServerProcessType.AutoUpdate,
                        SteamCMDProcessWindowStyle = ProcessWindowStyle.Hidden
                    };
                    app.UpdateModCache();
                    exitCode = app.ExitCode;

                    if (exitCode == EXITCODE_NORMALEXIT)
                    {
                        var branches = _profiles.Keys.Where(p => p.EnableAutoUpdate).Select(p => BranchSnapshot.Create(p)).Distinct(new BranchSnapshotComparer());
                        var exitCodes = new ConcurrentDictionary<BranchSnapshot, int>();

                        // update the server cache for each branch
                        if (Config.Default.AutoUpdate_ParallelUpdate)
                        {
                            Parallel.ForEach(branches, branch => {
                                app = new ServerApp
                                {
                                    OutputLogs = true,
                                    SendAlerts = true,
                                    SendEmails = true,
                                    ServerProcess = ServerProcessType.AutoUpdate,
                                    SteamCMDProcessWindowStyle = ProcessWindowStyle.Hidden
                                };
                                app.PerformServerBranchUpdate(branch);
                                exitCodes.TryAdd(branch, app.ExitCode);
                            });
                        }
                        else
                        {
                            var delay = 0;
                            foreach (var branch in branches)
                            {
                                if (delay > 0)
                                    Task.Delay(delay * 1000).Wait();
                                delay = Math.Max(0, Config.Default.AutoUpdate_SequencialDelayPeriod);

                                app = new ServerApp
                                {
                                    OutputLogs = true,
                                    SendAlerts = true,
                                    SendEmails = true,
                                    ServerProcess = ServerProcessType.AutoUpdate,
                                    SteamCMDProcessWindowStyle = ProcessWindowStyle.Hidden
                                };
                                app.PerformServerBranchUpdate(branch);
                                exitCodes.TryAdd(branch, app.ExitCode);
                            }
                        }

                        foreach (var profile in _profiles.Keys)
                        {
                            if (profile.ServerUpdated)
                            {
                                profile.Update(_profiles[profile]);
                                _profiles[profile].SaveProfile();
                            }
                        }

                        if (exitCodes.Any(c => !c.Value.Equals(EXITCODE_NORMALEXIT)))
                            exitCode = EXITCODE_EXITWITHERRORS;
                    }
                }
                else
                {
                    LogMessage("Cancelled auto update process, could not lock application.");
                    return EXITCODE_PROCESSALREADYRUNNING;
                }
            }
            catch (Exception ex)
            {
                LogError(ex.Message);
                LogError(ex.GetType().ToString());
                if (ex.InnerException != null)
                {
                    LogMessage($"InnerException - {ex.InnerException.Message}");
                    LogMessage(ex.InnerException.GetType().ToString());
                }
                LogMessage($"StackTrace\r\n{ex.StackTrace}");
                exitCode = EXITCODE_UNKNOWNERROR;
            }
            finally
            {
                if (mutex != null)
                {
                    if (createdNew)
                    {
                        mutex.ReleaseMutex();
                        mutex.Dispose();
                    }
                    mutex = null;
                }
            }

            LogMessage("");
            LogMessage($"Exitcode = {exitCode}");
            return exitCode;
        }
    }
}
