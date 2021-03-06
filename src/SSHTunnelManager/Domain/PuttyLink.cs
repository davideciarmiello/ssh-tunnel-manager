﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using SSHTunnelManager.Business;
using SSHTunnelManager.Properties;
using SSHTunnelManager.Util;
using log4net;

namespace SSHTunnelManager.Domain
{
    public class PuttyLink : IPuttyLink
    {
        private const string ShellStartedMessage = "Started a shell/command";
        private readonly PuttyProfile _profile;
        private readonly Config _config;

        private volatile Process _process;
        private volatile string _lastStartError;
        private volatile ELinkStatus _status = ELinkStatus.Stopped;
        private volatile bool _stopRequested;
        private readonly ManualResetEventSlim _eventStopped = new ManualResetEventSlim(true);
        private readonly ManualResetEventSlim _eventStarted = new ManualResetEventSlim(false);
        private Dictionary<TunnelInfo, ForwardingResult> _forwardingResults;
        private readonly object _forwardingResultsLock = new object();

        private readonly StringBuilder _multilineError = new StringBuilder();
        private Timer _deferredCallTimer;

        public DateTime LastStartTime { get; private set; }
        public HostInfo Host { get; private set; }

        public ELinkStatus Status
        {
            get { return _status; }
            private set
            {
                if (_status == value)
                    return;

                switch (value)
                {
                    case ELinkStatus.Starting:
                        Logger.Log.InfoFormat("[{0}] {1}", Host.Name, Resources.PuttyLink_Status_Starting);
                        break;
                    case ELinkStatus.Started:
                        Logger.Log.InfoFormat("[{0}] {1}", Host.Name, Resources.PuttyLink_Status_Started);
                        break;
                    case ELinkStatus.StartedWithWarnings:
                        Logger.Log.InfoFormat("[{0}] {1}", Host.Name, Resources.PuttyLink_Status_StartedWithWarnings);
                        break;
                    case ELinkStatus.Stopped:
                        Logger.Log.InfoFormat("[{0}] {1}", Host.Name, Resources.PuttyLink_Status_Stopped);
                        break;
                    case ELinkStatus.Waiting:
                        if (_config.RestartDelay > 0)
                            Logger.Log.InfoFormat("[{0}] {1}", Host.Name, string.Format(Resources.PuttyLink_Status_Waiting, _config.RestartDelay));
                        else
                            Logger.Log.InfoFormat("[{0}] {1}", Host.Name, Resources.PuttyLink_Status_Restarting);
                        break;
                }

                _status = value;
                onLinkStatusChanged();

                if (_status == ELinkStatus.Stopped)
                {
                    _eventStopped.Set();
                }
                else
                {
                    _eventStopped.Reset();
                }
                if (_status == ELinkStatus.Started ||
                    _status == ELinkStatus.StartedWithWarnings)
                {
                    _eventStarted.Set();
                }
                else
                {
                    _eventStarted.Reset();
                }
            }
        }

        public string LastStartError
        {
            get { return _lastStartError; }
            private set
            {
                if (value == _lastStartError)
                    return;
                _lastStartError = value; // "Reads and writes of the following data types are atomic: bool, char, byte, sbyte, short, ushort, uint, int, float, and reference types."
                if (!string.IsNullOrEmpty(value))
                {
                    Logger.Log.ErrorFormat("[{0}] {1}", Host.Name, value);
                }
            }
        }

        public Dictionary<TunnelInfo, ForwardingResult> ForwardingResults
        {
            get
            {
                lock (_forwardingResultsLock)
                {
                    return new Dictionary<TunnelInfo, ForwardingResult>(_forwardingResults);
                }
            }
        }

        /// <summary>
        /// В случае, если процесс запущен асинхронно (методом AsyncStart), события будут срабатывать из асинхронного потока.
        /// Поэтому нужно продумать Threadsafe, для изменения состояния формы лучше использовать Control.BeginInvoke()
        /// </summary>
        public event EventHandler LinkStatusChanged;

        public PuttyLink(HostInfo host, Config config, PuttyProfile profile)
        {
            if (host == null) throw new ArgumentNullException("host");
            if (config == null) throw new ArgumentNullException("config");
            if (profile == null) throw new ArgumentNullException("profile");
            Host = host;
            _config = config;
            _profile = profile;
        }

        public void AsyncStart()
        {
            if (Status != ELinkStatus.Stopped)
            {
                throw new InvalidOperationException(Resources.PuttyLink_Error_LinkAlreadyStarted);
            }

            asyncStartStack = Environment.StackTrace;
            Thread thread = new Thread(Start) { IsBackground = true };
            thread.Start();
        }

        private string asyncStartStack;
        public void Start()
        {
            if (Status != ELinkStatus.Stopped)
            {
                throw new InvalidOperationException(Resources.PuttyLink_Error_LinkAlreadyStarted + " - Stack: " + asyncStartStack);
            }

            try
            {
                ThreadContext.Properties[@"Host"] = Host;
                _stopRequested = false;
                LastStartError = "";

                bool atleastOneSuccess = false;
                for (int i = 0; i < _config.MaxAttemptsCount || _config.DelayInsteadStop; ++i)
                {
                    // At least one success for AutoRestart enabling.
                    // Do not restart if process stopped by Stop() method.
                    // Reset Attempts count if last attempt was successful.
                    Status = ELinkStatus.Starting;
                    var success = startOnce();
                    atleastOneSuccess = atleastOneSuccess || success;
                    if (_stopRequested)
                        break;
                    if (!_config.RestartEnabled)
                        break;
                    //if (_stopRequested || !atleastOneSuccess || !_config.RestartEnabled)
                    //    break;
                    if (success)
                        i = 0;
                    if (i >= _config.MaxAttemptsCount && _config.RestartDelay > 0)
                    {
                        Status = ELinkStatus.Waiting;
                        for (int j = 0; j < _config.RestartDelay * 2; j++)
                        {
                            if (_stopRequested)
                                break;
                            Thread.Sleep(500);
                        }
                    }
                    else
                    {
                        Logger.Log.InfoFormat("[{0}] {1}", Host.Name, Resources.PuttyLink_Status_Restarting);
                    }
                }
            }
            catch (Exception e)
            {
                LastStartError = e.Message;
            }
            finally
            {
                Status = ELinkStatus.Stopped;
            }
        }

        public void Stop()
        {
            stop();
        }

        private void stop()
        {
            if (Status == ELinkStatus.Stopped)
                return;
            _stopRequested = true;
            try
            {
                _deferredCallTimer?.Dispose();
                _process.Kill();
                _multilineError.Clear();
            }
            catch (Exception)
            {
                Status = ELinkStatus.Stopped;
            }
        }

        public bool WaitForStop(int seconds = 10)
        {
            return _eventStopped.Wait(seconds * 1000);
        }

        public bool WaitForStart(int seconds = 20)
        {
            return _eventStarted.Wait(seconds * 1000);
        }

        private void onLinkStatusChanged()
        {
            if (LinkStatusChanged != null)
                LinkStatusChanged(this, EventArgs.Empty);
        }

        private void writeLineStdIn(string text)
        {
            lock (_process.StandardInput)
            {
                _process.StandardInput.WriteLine(text);
            }
        }

        /// <summary>
        /// Start attemt to establish link once.
        /// </summary>
        /// <returns>Link was successfully started.</returns>
        private bool startOnce()
        {
            // fill results dic
            lock (_forwardingResultsLock)
            {
                _forwardingResults = Host.Tunnels.ToDictionary(t => t, t => ForwardingResult.CreateSuccess());
            }

            // Процесс
            _process = new Process
            {
                StartInfo =
                                   {
                                       FileName = ConsoleTools.PLinkLocation,
                                       CreateNoWindow = true,
                                       UseShellExecute = false,
                                       RedirectStandardError = true,
                                       RedirectStandardOutput = true,
                                       RedirectStandardInput = true,
                                       Arguments = ConsoleTools.PuttyArguments(Host, _profile, Host.AuthType, true)
                                   }
            };

            _process.ErrorDataReceived += errorDataHandler;
            _process.Start();
            _process.BeginErrorReadLine();

            _process.StandardInput.AutoFlush = true;

            var buffer = new StringBuilder();
            bool passwordProvided = false;
            bool passphraseForKeyProvided = false;
            while (!_process.HasExited)
            {
                while (_process.StandardOutput.Peek() >= 0)
                {
                    char c = (char)_process.StandardOutput.Read();
                    buffer.Append(c);
                }

                _process.StandardOutput.DiscardBufferedData();
                string data = buffer.ToString().ToLower();

                buffer.Clear();

                if (data.Contains(@"login as:"))
                {
                    // invalid username provided
                    stop();
                    // _process.StandardInput.WriteLine(username);
                    LastStartError = Resources.PuttyLink_Error_InvalidUsername;
                }
                else if (data.Contains(@"password:") && !passwordProvided)
                {
                    writeLineStdIn(Host.Password);
                    passwordProvided = true;
                }
                else if (data.Contains(@"passphrase for key") && !passphraseForKeyProvided)
                {
                    writeLineStdIn(Host.Password);
                    passphraseForKeyProvided = true;
                }
                else
                {
                    foreach (var line in data.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        Logger.Log.Debug(line);
                    }
                }
            }

            PrivateKeysStorage.RemovePrivateKey(Host);
            return Status == ELinkStatus.Started || Status == ELinkStatus.StartedWithWarnings;
        }

        private void writeStdIn(string text)
        {
            lock (_process.StandardInput)
            {
                _process.StandardInput.Write(text);
            }
        }

        private void errorDataHandler(object o, DataReceivedEventArgs args)
        {
            if (_stopRequested)
                return;
            if (args.Data == null)
                return;
            ThreadContext.Properties[@"Host"] = Host; // Set up context for working thread
            Logger.Log.Debug(args.Data);
            // LOCAL tunnels error
            var m = Regex.Match(args.Data, @"Local port (?<srcPort>\d+) forwarding to (?<dstHost>[^:]+):(?<dstPort>\d+) failed: (?<errorString>.*)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var srcPort = m.Groups["srcPort"].Value;
                var dstHost = m.Groups["dstHost"].Value;
                var dstPort = m.Groups["dstPort"].Value;
                var errorString = m.Groups["errorString"].Value;
                var tunnel = Host.Tunnels.FirstOrDefault(
                    t => t.LocalPort == srcPort && t.RemoteHostname == dstHost && t.RemotePort == dstPort && t.Type == TunnelType.Local);
                if (tunnel != null)
                {
                    lock (_forwardingResultsLock)
                    {
                        _forwardingResults[tunnel] = ForwardingResult.CreateFailed(errorString);
                    }

                    Logger.Log.WarnFormat("[{0}] [{1}] {2}", Host.Name, tunnel.SimpleString, errorString);
                }
            }
            // DYNAMIC tunnels error
            m = Regex.Match(args.Data, @"Local port (?<srcPort>\d+) SOCKS dynamic forwarding failed: (?<errorString>.*)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var srcPort = m.Groups["srcPort"].Value;
                var errorString = m.Groups["errorString"].Value;
                var tunnel = Host.Tunnels.FirstOrDefault(
                    t => t.LocalPort == srcPort && t.Type == TunnelType.Dynamic);
                if (tunnel != null)
                {
                    lock (_forwardingResultsLock)
                    {
                        _forwardingResults[tunnel] = ForwardingResult.CreateFailed(errorString);
                    }
                    Logger.Log.WarnFormat("[{0}] [{1}] {2}", Host.Name, tunnel.SimpleString, errorString);
                }
            }
            // Accept certificate
            if (args.Data.Contains(@"The server's host key is not cached in the registry."))
            {
                writeStdIn(@"y");
            }
            // Unable to open connection:
            if (args.Data.Contains(@"Unable to open connection:"))
            {
                _multilineError.Append(@"Unable to open connection: ");
                return;
            }
            // Access denied error
            if (args.Data.Contains(@"Access denied"))
            {
                // Неверный пароль (Доступ запрещен)
                LastStartError = @"Access Denied";
                stop();
            }
            // Access granted
            if (args.Data.Contains(@"Access granted"))
            {
                // Доступ открыт, можно удалить ключ
                PrivateKeysStorage.RemovePrivateKey(Host);
                Logger.Log.Debug(string.Format("Access granted called: {0}", Host));

                // Make delay for a couple of seconds and set status to 'Started' if a shell is not supposed to be started
                if (string.IsNullOrWhiteSpace(Host.RemoteCommand))
                {
                    _deferredCallTimer = new Timer(
                        delegate
                        {
                            bool forwardingFailed;
                            lock (_forwardingResultsLock)
                            {
                                forwardingFailed = _forwardingResults.Any(p => !p.Value.Success);
                            }

                            ThreadContext.Properties[@"Host"] = Host;
                            Logger.Log.Debug(string.Format("Delegate called: {0}", Host));
                            Status = forwardingFailed
                                         ? ELinkStatus.StartedWithWarnings
                                         : ELinkStatus.Started;
                            ThreadContext.Properties[@"Host"] = null;
                        },
                        null,
                        1500,
                        Timeout.Infinite);
                }
            }

            // Fatal errors
            m = Regex.Match(args.Data, @"^FATAL ERROR:\s*(?<msg>.*)$");
            if (m.Success)
            {
                LastStartError = m.Groups["msg"].Value;
            }

            // connection establishing
            if (args.Data.Contains(ShellStartedMessage))
            {
                bool forwardingFails;
                lock (_forwardingResultsLock)
                {
                    forwardingFails = _forwardingResults.Any(p => !p.Value.Success);
                }

                Status = forwardingFails ? ELinkStatus.StartedWithWarnings : ELinkStatus.Started;

                // Start a command to be executed after connection establishment
                if (!string.IsNullOrWhiteSpace(Host.RemoteCommand))
                {
                    _deferredCallTimer = new Timer(
                        delegate
                        {
                            ThreadContext.Properties[@"Host"] = Host;
                            writeLineStdIn(Host.RemoteCommand);
                            ThreadContext.Properties[@"Host"] = null;
                        },
                        null,
                        1000,
                        Timeout.Infinite);
                }
            }

            // multiline error?
            if (_multilineError.Length > 0)
            {
                _multilineError.Append(args.Data);
                LastStartError = _multilineError.ToString();
                _multilineError.Clear();
                stop();
            }

            ThreadContext.Properties[@"Host"] = null;
        }
    }
}
