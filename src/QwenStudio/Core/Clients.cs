using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace QwenStudio.Core
{
    public sealed class Client
    {
        public string Ip;
        /// <summary>Process name for a local client, host name for a LAN one (resolved in the background), or null.</summary>
        public string Name;
        public bool Local;
        public DateTime FirstSeen, LastSeen;
        /// <summary>When it last opened a new connection (a request).</summary>
        internal DateTime LastNew;
        public int Rejected;
        /// <summary>Has an open connection right now.</summary>
        public bool Open;
        public string Title => Name != null ? $"{Ip} ({Name})" : Ip;
        public string Short => Local && Name != null ? Name : Ip;
    }

    /// <summary>
    /// Who talks to the model server. llama-server logs client addresses only in verbose mode, so this reads the
    /// Windows TCP table every 250 ms: every connection to the server port gives the remote IP (including closed
    /// ones still in TIME_WAIT, so short requests are caught too). A loopback client is named by its process while the
    /// connection is open; loopback leftovers without a process (Studio's own /health polls) are ignored.
    /// Kept in memory for 24 hours.
    /// </summary>
    public sealed class ClientTracker
    {
        const int AF_INET = 2, TCP_TABLE_OWNER_PID_ALL = 5, LISTEN = 2, ESTABLISHED = 5;
        [DllImport("iphlpapi.dll")]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

        readonly object gate = new();
        readonly Dictionary<string, Client> clients = new();
        readonly Dictionary<int, string> procNames = new();
        readonly HashSet<string> resolving = new();
        HashSet<(uint, int)> seen = new();
        readonly int ownPid = Environment.ProcessId;
        public int Port;

        public void Start(CancellationToken ct) => Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { if (Port > 0) Scan(); } catch { }
                await Task.Delay(250, ct).ContinueWith(_ => { });
            }
        });

        struct Row { public int State, LocalPort, RemotePort, Pid; public uint LocalAddr, RemoteAddr; }

        static List<Row> Table()
        {
            var rows = new List<Row>();
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0) return rows;
                int n = Marshal.ReadInt32(buf);
                for (int i = 0; i < n; i++)
                {
                    // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid — six DWORDs
                    var r = buf + 4 + i * 24;
                    rows.Add(new Row
                    {
                        State = Marshal.ReadInt32(r),
                        LocalAddr = (uint)Marshal.ReadInt32(r + 4),
                        LocalPort = PortOf(Marshal.ReadInt32(r + 8)),
                        RemoteAddr = (uint)Marshal.ReadInt32(r + 12),
                        RemotePort = PortOf(Marshal.ReadInt32(r + 16)),
                        Pid = Marshal.ReadInt32(r + 20),
                    });
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return rows;
        }

        static int PortOf(int p) => ((p & 0xFF) << 8) | ((p >> 8) & 0xFF);
        static bool Loopback(uint addr) => (addr & 0xFF) == 127;

        void Scan()
        {
            var rows = Table();
            var now = DateTime.Now;
            var open = new HashSet<string>();
            var nowSeen = new HashSet<(uint, int)>();
            foreach (var s in rows)
            {
                if (s.LocalPort != Port || s.State == LISTEN) continue;
                // a closed connection lingers in TIME_WAIT for up to 2 minutes: it counts once, not as "still here"
                var conn = (s.RemoteAddr, s.RemotePort);
                nowSeen.Add(conn);
                if (s.State != ESTABLISHED && seen.Contains(conn)) continue;
                string ip = new IPAddress(s.RemoteAddr).ToString(), name = null;
                bool local = Loopback(s.RemoteAddr);
                if (local)
                {
                    // the client's own end of a loopback connection carries its PID while it is open
                    var c = rows.FirstOrDefault(r => r.LocalPort == s.RemotePort && r.RemotePort == Port && Loopback(r.LocalAddr) && r.Pid != 0);
                    if (c.Pid == 0 || c.Pid == ownPid) continue;
                    name = ProcName(c.Pid);
                }
                var key = local ? "local:" + name : ip;
                if (s.State == ESTABLISHED) open.Add(key);
                Touch(key, ip, name, local, now, !seen.Contains(conn));
            }
            seen = nowSeen;
            lock (gate)
            {
                foreach (var kv in clients) kv.Value.Open = open.Contains(kv.Key);
                foreach (var k in clients.Where(kv => now - kv.Value.LastSeen > TimeSpan.FromHours(24)).Select(kv => kv.Key).ToList())
                    clients.Remove(k);
            }
        }

        string ProcName(int pid)
        {
            if (procNames.TryGetValue(pid, out var n)) return n;
            try { n = Process.GetProcessById(pid).ProcessName; } catch { n = "PID " + pid; }
            return procNames[pid] = n;
        }

        void Touch(string key, string ip, string name, bool local, DateTime now, bool newConn)
        {
            bool fresh;
            lock (gate)
            {
                fresh = !clients.TryGetValue(key, out var c);
                if (fresh) clients[key] = c = new Client { Ip = ip, Name = name, Local = local, FirstSeen = now };
                c.LastSeen = now;
                if (newConn) c.LastNew = now;
            }
            if (fresh && !local) Resolve(key, ip);
        }

        void Resolve(string key, string ip)
        {
            lock (gate) if (!resolving.Add(ip)) return;
            Task.Run(async () =>
            {
                try
                {
                    var lookup = Dns.GetHostEntryAsync(ip);
                    if (await Task.WhenAny(lookup, Task.Delay(3000)) != lookup) return;
                    var host = lookup.Result.HostName;
                    if (string.IsNullOrEmpty(host) || host == ip) return;
                    host = host.Split('.')[0];
                    lock (gate) if (clients.TryGetValue(key, out var c)) c.Name = host;
                }
                catch { }
            });
        }

        /// <summary>Snapshot, most recent first.</summary>
        public List<Client> All()
        {
            lock (gate) return clients.Values.OrderByDescending(c => c.LastSeen)
                .Select(c => new Client { Ip = c.Ip, Name = c.Name, Local = c.Local, FirstSeen = c.FirstSeen, LastSeen = c.LastSeen, Rejected = c.Rejected, Open = c.Open })
                .ToList();
        }

        /// <summary>
        /// The server logged "Invalid API Key": blame the client that opened a connection around that moment, if it is
        /// the only one. Returns it, or null when nobody or several clients connected then.
        /// </summary>
        public Client Blame(DateTime at)
        {
            lock (gate)
            {
                var near = clients.Values.Where(c => (c.LastNew - at).Duration() < TimeSpan.FromSeconds(3)).ToList();
                if (near.Count != 1) return null;
                near[0].Rejected++;
                return near[0];
            }
        }
    }
}
