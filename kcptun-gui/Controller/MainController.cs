﻿using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Generic;

using kcptun_gui.Model;
using kcptun_gui.Common;

namespace kcptun_gui.Controller
{
    public class MainController
    {
        public const string Version = "1.4";

        private IRelay _tcpRelay;
        private IRelay _udpRelay;

        private TrafficStatistics _trafficStatistics;

        public TrafficLog traffic { get; private set; } = new TrafficLog();
        public LinkedList<TrafficLog> trafficLogList { get; private set; } = new LinkedList<TrafficLog>();
        public int trafficLogSize { get; set; } = 60; // 1 minutes

        private System.Timers.Timer timer;

        public ConfigurationController ConfigController { get; private set; }
        public KCPTunnelController KCPTunnelController { get; private set; }

        public event EventHandler TrafficChanged;

        public bool IsKcptunRunning
        {
            get { return KCPTunnelController.IsRunning; }
        }

        public MainController()
        {
            ConfigController = new ConfigurationController(this);
            ConfigController.ConfigChanged += OnConfigChanged;

            KCPTunnelController = new KCPTunnelController(this);

            _trafficStatistics = TrafficStatistics.Load();
        }

        public void Start()
        {
            Reload();
        }

        public void Stop()
        {
            try
            {
                if (timer != null)
                {
                    timer.Stop();
                    timer.Dispose();
                    timer = null;
                }
                if (_tcpRelay != null)
                {
                    _tcpRelay.Stop();
                    _tcpRelay = null;
                }
                if (_udpRelay != null)
                {
                    _udpRelay.Stop();
                    _udpRelay = null;
                }
                if (KCPTunnelController.IsRunning)
                    KCPTunnelController.Stop();

                TrafficStatistics.Save(_trafficStatistics);
            }
            catch(Exception e)
            {
                Logging.LogUsefulException(e);
            }
        }

        public void Reload()
        {
            try
            {
                if (timer != null)
                {
                    timer.Stop();
                    timer.Dispose();
                    timer = null;
                }
                if (_tcpRelay != null)
                {
                    _tcpRelay.Stop();
                    _tcpRelay = null;
                }
                if (_udpRelay != null)
                {
                    _udpRelay.Stop();
                    _udpRelay = null;
                }
                if (KCPTunnelController.IsRunning)
                {
                    KCPTunnelController.Stop();
                }
                Configuration config = ConfigController.GetCurrentConfiguration();
                Server server = config.GetCurrentServer();
                traffic = _trafficStatistics.GetTrafficLog(server);
                if (config.enabled)
                {
                    KCPTunnelController.Server = server;
                    KCPTunnelController.localaddr = null;
                    KCPTunnelController.remoteaddr = null;
                    if (config.statistics_enabled)
                    {
                        RegistStatistics();
                        StartTrafficLogger();
                    }
                    KCPTunnelController.Start();
                }
            }
            catch (Exception e)
            {
                Logging.LogUsefulException(e);
            }
        }

        private void RegistLeftStatistics()
        {
            Server server = KCPTunnelController.Server;
            string[] localaddr_compns = server.localaddr.Split(':');
            IPEndPoint localEP = new IPEndPoint(
                string.IsNullOrEmpty(localaddr_compns[0]) ? IPAddress.Any : IPAddress.Parse(localaddr_compns[0]),
                Convert.ToInt32(localaddr_compns[1]));
            IPEndPoint remoteEP = new IPEndPoint(
                IPAddress.Loopback,
                Utils.GetFreePort(ProtocolType.Tcp, localEP.Port + 1));

            KCPTunnelController.localaddr = remoteEP.ToString();
            Logging.Debug($"Left: localEP={localEP.ToString()}, remoteEP={remoteEP.ToString()}");
            _tcpRelay = new TCPRelay(this, localEP, remoteEP);
            _tcpRelay.Inbound += _tcpRelay_Inbound;
            _tcpRelay.Outbound += _tcpRelay_Outbound;
            _tcpRelay.Start();
        }

        private void RegistRightStatistics()
        {
            Server server = KCPTunnelController.Server;
            string[] localaddr_compns = server.localaddr.Split(':');
            string[] remoteaddr_compns = server.remoteaddr.Split(':');
            IPEndPoint localEP = new IPEndPoint(
                IPAddress.Loopback,
                Utils.GetFreePort(ProtocolType.Udp, Convert.ToInt32(localaddr_compns[1])));
            IPEndPoint remoteEP = new IPEndPoint(
                IPAddress.Parse(remoteaddr_compns[0]),
                Convert.ToInt32(remoteaddr_compns[1]));

            KCPTunnelController.remoteaddr = localEP.ToString();
            Logging.Debug($"right: localEP={localEP.ToString()}, remoteEP={remoteEP.ToString()}");
            _udpRelay = new UDPRelay(this, localEP, remoteEP);
            _udpRelay.Inbound += _udpRelay_Inbound;
            _udpRelay.Outbound += _udpRelay_Outbound;
            _udpRelay.Start();
        }

        private void _tcpRelay_Inbound(object sender, RelayEventArgs e)
        {
            traffic.raw.onInbound(e.Value);
        }

        private void _tcpRelay_Outbound(object sender, RelayEventArgs e)
        {
            traffic.raw.onOutbound(e.Value);
        }

        private void _udpRelay_Inbound(object sender, RelayEventArgs e)
        {
            traffic.kcp.onInbound(e.Value);
        }

        private void _udpRelay_Outbound(object sender, RelayEventArgs e)
        {
            traffic.kcp.onOutbound(e.Value);
        }

        private void RegistStatistics()
        {
            RegistLeftStatistics();
            RegistRightStatistics();
        }

        private void OnConfigChanged(object sender, EventArgs e)
        {
            Reload();
        }

        private void StartTrafficLogger()
        {
            if (trafficLogList == null)
                trafficLogList = new LinkedList<TrafficLog>();
            else
                trafficLogList.Clear();
            for (int i = 0; i < trafficLogSize; i++)
            {
                trafficLogList.AddLast(new TrafficLog());
            }
            if (timer == null)
            {
                timer = new System.Timers.Timer(1000);
                timer.Elapsed += Timer_Elapsed;
                timer.AutoReset = true;
                timer.Enabled = true;
            }
            timer.Start();
       }

        private void Timer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            UpdateTrafficList();
            TrafficChanged?.Invoke(this, new EventArgs());
        }

        private void UpdateTrafficList()
        {
            TrafficLog previous = trafficLogList.Last.Value;
            TrafficLog current = new TrafficLog(
                new Traffic(traffic.raw),
                new Traffic(traffic.raw, previous.raw),
                new Traffic(traffic.kcp),
                new Traffic(traffic.kcp, previous.kcp));
            trafficLogList.AddLast(current);

            while (trafficLogList.Count > trafficLogSize) trafficLogList.RemoveFirst();
            while (trafficLogList.Count < trafficLogSize) trafficLogList.AddFirst(new TrafficLog());
        }
    }
}
