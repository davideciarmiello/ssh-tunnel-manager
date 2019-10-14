using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using SSHTunnelManager.Business;
using SSHTunnelManager.Properties;
using SSHTunnelManager.Util;

namespace SSHTunnelManager.Domain
{
    public class ConsoleTools
    {
        public const string PLinkLocation = "Tools\\plink.exe";
        public const string PuttyLocation = "Tools\\putty.exe";
        public const string PsftpLocation = "Tools\\psftp.exe";
        public const string FileZillaLocation = "Tools\\FileZilla\\filezilla.exe";

        public static void StartPutty(HostInfo host, PuttyProfile profile, bool addTunnels)
        {
            var fileName = Path.Combine(Util.Helper.StartupPath, PuttyLocation);
            var args = PuttyArguments(host, profile, host.AuthType, addTunnels);
            Process.Start(fileName, args);
        }

        public static void StartPsftp(HostInfo host)
        {
            var fileName = Path.Combine(Util.Helper.StartupPath, PsftpLocation);
            var args = psftpArguments(host);
            Process.Start(fileName, args);
        }

        public static void StartFileZilla(HostInfo host)
        {
            var fileName = Path.Combine(Util.Helper.StartupPath, FileZillaLocation);
            var args = string.Format(@"sftp://{0}:{1}@{2}:{3}", host.Username, host.Password, host.Hostname, host.Port);
            Process.Start(fileName, args);
        }

        public static string PuttyArguments(HostInfo host, PuttyProfile profile, AuthenticationType authType, bool addTunnels)
        {
            // example: -ssh -load _stm_preset_ username@domainName -P 22 -pw password -D 5000 -L 44333:username.dyndns.org:44333

            string profileArg = "";
            if (profile != null)
            {
                profileArg = @" -load " + profile.Name;
            }

            var startShellOption = "";
            if (string.IsNullOrWhiteSpace(host.RemoteCommand) && addTunnels)
            {
                startShellOption = " -N";
            }

            string args;
            switch (authType)
            {
                case AuthenticationType.None:
                    args = String.Format(@"-ssh{0} {1}@{2} -P {3} -v{4}", profileArg, host.Username, host.Hostname,
                                         host.Port, startShellOption);
                    //Logger.Log.DebugFormat(@"plink.exe {0}", args);
                    break;
                case AuthenticationType.Password:
                    args = String.Format(@"-ssh{0} {1}@{2} -P {3} -pw {4} -v{5}", profileArg, host.Username, host.Hostname,
                                         host.Port, host.Password, startShellOption);
                    //Logger.Log.DebugFormat(@"plink.exe -ssh{0} {1}@{2} -P {3} -pw ******** -v -N", profileArg, host.Username,host.Hostname, host.Port);
                    break;
                case AuthenticationType.PrivateKey:
                    args = String.Format(@"-ssh{0} {1}@{2} -P {3} -i ""{4}"" -v{5}", profileArg, host.Username, host.Hostname,
                                         host.Port, PrivateKeysStorage.CreatePrivateKey(host).Filename, startShellOption);
                    //Logger.Log.DebugFormat(@"plink.exe {0}", args);
                    break;
                default:
                    throw new ArgumentOutOfRangeException("authType");
            }

            if (addTunnels)
            {
                var sb = new StringBuilder(args);
                foreach (var tunnelArg in host.Tunnels.Select(tunnelArguments))
                {
                    sb.Append(tunnelArg);
                }

                args = sb.ToString();
            }

            Logger.Log.DebugFormat(@"plink.exe {0}", args.Replace("-pw " + host.Password + " ", "-pw ********* "));

            if (addTunnels && host.Tunnels.Any())
            {
                var ports = host.Tunnels.Select(x => x.LocalPort).ToList();
                var ipGP = IPGlobalProperties.GetIPGlobalProperties();
                var endpoints = ipGP.GetActiveTcpListeners();
                var portUsed = endpoints.Select(x => x.Port.ToString()).Distinct().Where(x => ports.Contains(x)).ToList();
                if (portUsed.Any())
                {
                    var portsDetail = NetStatPortsAndProcessNames.GetNetStatPorts().ToLookup(x => x.port_number);
                    if (portUsed.Count == 1)
                        throw new Exception("La porta " + portUsed[0] + " è già in uso da '" + portsDetail[portUsed[0]].Select(p => p.process_name).FirstOrDefault() + "'.");
                    portUsed = portUsed.Select(x =>
                        x + " (" + portsDetail[x].Select(p => p.process_name).FirstOrDefault() + ")").ToList();
                    throw new Exception("Le seguenti porto sono già in uso: " + string.Join(", ", portUsed));
                }
            }

            return args;
        }

        private static string psftpArguments(HostInfo host)
        {
            string args;
            switch (host.AuthType)
            {
                case AuthenticationType.Password:
                    args = String.Format(@"{0}@{1} -P {2} -pw {3} -batch", host.Username, host.Hostname, host.Port,
                                         host.Password);
                    break;
                case AuthenticationType.PrivateKey:
                    args = String.Format(@"{0}@{1} -P {2} -i {3} -batch", host.Username, host.Hostname, host.Port,
                                         PrivateKeysStorage.CreatePrivateKey(host).Filename);
                    break;
                default:
                    throw new ArgumentOutOfRangeException("authType");
            }
            return args;
        }

        private static string tunnelArguments(TunnelInfo tunnel)
        {
            if (tunnel == null) throw new ArgumentNullException("tunnel");
            switch (tunnel.Type)
            {
                case TunnelType.Local:
                    return String.Format(@" -L {0}:{1}:{2}", tunnel.LocalPort, tunnel.RemoteHostname, tunnel.RemotePort);
                case TunnelType.Remote:
                    return String.Format(@" -R {0}:{1}:{2}", tunnel.LocalPort, tunnel.RemoteHostname, tunnel.RemotePort);
                case TunnelType.Dynamic:
                    return String.Format(@" -D {0}", tunnel.LocalPort);
                default:
                    throw new FormatException(Resources.ConsoleTools_Error_InvalidTunnelType);
            }
        }
    }
}
