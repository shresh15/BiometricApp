using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Net.NetworkInformation;
using System.IO;
using System.Threading;

namespace BiometricApp
{
    public partial class Form1 : Form
    {
        // FIX 1: All three functions must come from the SAME DLL.
        // SBXPCDLL.dll is the newer/correct version — use it for everything.
        // Using SBXPCDLL.dll for Disconnect/GetGeneralLogData while using
        // SBXPCDLL.dll for Connect causes mismatched internal SDK state
        // and will cause AccessViolationException or silent failures.

        [DllImport("SBXPCDLL.dll", EntryPoint = "_ConnectTcpip",
            CharSet = CharSet.Ansi,
            CallingConvention = CallingConvention.StdCall,
            SetLastError = true)]
        public static extern int _ConnectTcpip(
            int machineNo,
            [MarshalAs(UnmanagedType.LPStr)] string ip,
            int port,
            int password);

        // FIX 1 APPLIED: Changed from SBXPCDLL.dll → SBXPCDLL.dll
        [DllImport("SBXPCDLL.dll", EntryPoint = "Disconnect",
            CharSet = CharSet.Ansi,
            CallingConvention = CallingConvention.StdCall,
            SetLastError = true)]
        public static extern void Disconnect();

        // FIX 1 APPLIED: Changed from SBXPCDLL.dll → SBXPCDLL.dll
        [DllImport("SBXPCDLL.dll", EntryPoint = "GetGeneralLogData",
            CharSet = CharSet.Ansi,
            CallingConvention = CallingConvention.StdCall,
            SetLastError = true)]
        public static extern int GetGeneralLogData(
            ref int enrollNumber,
            ref int verifyMode,
            ref int inOutMode,
            ref int year,
            ref int month,
            ref int day,
            ref int hour,
            ref int minute,
            ref int second
        );

        // FIX 2: Track connection state so we don't disconnect when never connected.
        private bool _isConnected = false;

        public Form1()
        {
            InitializeComponent();
            btnFetch.Enabled = false;
            lblStatus.Text = "Status: Disconnected";
            this.FormClosing += Form1_FormClosing;
            Application.ThreadException += Application_ThreadException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        }

        private readonly object _sdkLock = new object();

        private void btnConnect_Click(object sender, EventArgs e)
        {
            ConnectToDeviceAsync();
        }

        private async void ConnectToDeviceAsync()
        {
            const int maxAttempts = 3;

            if (!ValidateInput(out string ip, out int port))
                return;

            try
            {
                btnConnect.Enabled = false;
                btnFetch.Enabled = false;
                lblStatus.Text = "Status: Connecting...";

                Log($"Attempting connection to {ip}:{port}");

                bool pingOk = await PingDeviceAsync(ip);
                if (pingOk)
                {
                    MessageBox.Show($"Ping to {ip} succeeded.", "Ping",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    var dr = MessageBox.Show(
                        $"Ping to {ip} failed. Device may be unreachable or ICMP blocked. " +
                        "Continue attempting to connect?",
                        "Ping Failed", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    Log($"Ping to {ip} failed.");
                    if (dr == DialogResult.No)
                    {
                        lblStatus.Text = "Status: Ping Failed";
                        btnConnect.Enabled = true;
                        return;
                    }
                }

                int lastResult = -1;
                bool connected = false;

                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    lblStatus.Text = $"Status: Connecting... (Attempt {attempt}/{maxAttempts})";
                    Log($"Connect attempt {attempt} to {ip}:{port}");

                    // FIX 3: Run the blocking native SDK call on a background thread
                    // so it never freezes the UI. Task.Run offloads it safely.
                    int result = await Task.Run(() =>
                    {
                        lock (_sdkLock)
                        {
                            return _ConnectTcpip(1, ip, port, 0);
                        }
                    });

                    lastResult = result;
                    Log($"ConnectTCP returned: {lastResult}");

                    if (lastResult == 1)
                    {
                        connected = true;
                        break;
                    }

                    await Task.Delay(1000);
                }

                if (connected)
                {
                    _isConnected = true; // FIX 2: mark state
                    lblStatus.Text = "Status: Connected";
                    btnFetch.Enabled = true;
                    MessageBox.Show("Connected Successfully", "Connection",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    Log($"Connected to device {ip}:{port}");
                }
                else
                {
                    lblStatus.Text = "Status: Connection Failed";
                    btnFetch.Enabled = false;
                    string details =
                        $"Failed to connect after {maxAttempts} attempts. " +
                        $"Return code: {lastResult}.\n\n" +
                        "Possible causes:\n" +
                        "• Wrong IP address or port\n" +
                        "• Device is offline or not on same LAN\n" +
                        "• Port blocked by firewall\n" +
                        "• SDK DLL version mismatch (check SBXPCDLL.dll is present)\n" +
                        "• Device password is non-zero (currently sending 0)";
                    MessageBox.Show(details, "Connection Failed",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Log(details);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error while connecting: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Log($"Exception during ConnectToDeviceAsync: {ex}");
            }
            finally
            {
                btnConnect.Enabled = true;
            }
        }

        private void btnDisconnect_Click(object sender, EventArgs e)
        {
            try
            {
                // FIX 2: Only call Disconnect if we are actually connected.
                // Calling it when not connected can crash with an AccessViolation.
                if (_isConnected)
                {
                    lock (_sdkLock)
                    {
                        Disconnect();
                    }
                    _isConnected = false;
                }

                lblStatus.Text = "Status: Disconnected";
                btnFetch.Enabled = false;
                MessageBox.Show("Disconnected", "Connection",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error while disconnecting: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private bool ValidateInput(out string ip, out int port)
        {
            ip = txtIP.Text?.Trim();
            port = 0;
            if (string.IsNullOrEmpty(ip))
            {
                MessageBox.Show("Please enter an IP address or hostname.", "Validation",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            if (!System.Net.IPAddress.TryParse(ip, out var parsedAddress))
            {
                try
                {
                    var hostEntry = System.Net.Dns.GetHostEntry(ip);
                    var ipv4 = hostEntry.AddressList.FirstOrDefault(
                        a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                    if (ipv4 != null)
                    {
                        parsedAddress = ipv4;
                        ip = ipv4.ToString();
                        Log($"Resolved hostname '{txtIP.Text}' to {ip}");
                    }
                    else
                    {
                        MessageBox.Show("Hostname resolved but no IPv4 address found.",
                            "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Log($"DNS resolution failed for '{ip}': {ex.Message}");
                    MessageBox.Show(
                        "The IP/hostname could not be resolved. Please check the address.",
                        "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }

            ip = parsedAddress.ToString();

            if (!int.TryParse(txtPort.Text, out port) || port <= 0 || port > 65535)
            {
                MessageBox.Show("Please enter a valid port number (1-65535).", "Validation",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            return true;
        }

        private Task<bool> PingDeviceAsync(string ip)
        {
            return Task.Run(() =>
            {
                try
                {
                    using (var ping = new Ping())
                    {
                        var reply = ping.Send(ip, 1000);
                        return reply != null && reply.Status == IPStatus.Success;
                    }
                }
                catch (Exception ex)
                {
                    Log($"Ping exception: {ex}");
                    return false;
                }
            });
        }

        private readonly object _logLock = new object();
        private void Log(string message)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log.txt");
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                lock (_logLock)
                {
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch { }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                // FIX 2: Guard with _isConnected to avoid crashing on close
                // when connection was never established.
                if (_isConnected)
                {
                    lock (_sdkLock)
                    {
                        Disconnect();
                    }
                    Log("Disconnected on form closing.");
                }
            }
            catch (Exception ex)
            {
                Log($"Exception during disconnect on close: {ex}");
            }
        }

        private void Application_ThreadException(object sender,
            System.Threading.ThreadExceptionEventArgs e)
        {
            try { Log($"UI thread exception: {e.Exception}"); } catch { }
        }

        private void CurrentDomain_UnhandledException(object sender,
            UnhandledExceptionEventArgs e)
        {
            try { Log($"Unhandled exception: {e.ExceptionObject}"); } catch { }
        }

        private async void btnFetch_Click(object sender, EventArgs e)
        {
            // FIX 4: Made async. The original was synchronous — GetGeneralLogData
            // in a tight while-loop on the UI thread freezes the entire window.

            try
            {
                btnFetch.Enabled = false;
                btnConnect.Enabled = false;
                btnDisconnect.Enabled = false;

                dataGridView1.Rows.Clear();

                // FIX 5: Removed Thread.Sleep(200) from the UI thread.
                // Replaced with await Task.Delay so the UI remains responsive.
                await Task.Delay(200);

                // FIX 6: Run the entire fetch loop on a background thread.
                // Collect results into a list, then marshal back to the UI thread
                // to update the DataGridView. Direct cross-thread UI access throws
                // InvalidOperationException in WinForms.
                var rows = await Task.Run(() =>
                {
                    var results = new List<(string enroll, string date, string time, string status)>();

                    int enroll = 0, verify = 0, inoutMode = 0;
                    int year = 0, month = 0, day = 0;
                    int hour = 0, minute = 0, second = 0;

                    while (true)
                    {
                        int ret;
                        lock (_sdkLock)
                        {
                            ret = GetGeneralLogData(
                                ref enroll, ref verify, ref inoutMode,
                                ref year, ref month, ref day,
                                ref hour, ref minute, ref second);
                        }

                        if (ret != 1)
                            break;

                        string dateStr = $"{day:D2}/{month:D2}/{year:D4}";
                        string timeStr = $"{hour:D2}:{minute:D2}:{second:D2}";
                        string statusStr = (inoutMode == 0) ? "IN" : "OUT";

                        results.Add((enroll.ToString(), dateStr, timeStr, statusStr));
                    }

                    return results;
                });

                // Back on the UI thread — safe to update controls here
                foreach (var (enrollNo, date, time, status) in rows)
                {
                    dataGridView1.Rows.Add(enrollNo, date, time, status);
                }

                if (rows.Count > 0)
                {
                    MessageBox.Show($"Fetched {rows.Count} record(s).", "Fetch",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show("No records found.", "Fetch",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error while fetching data: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Log($"Exception during btnFetch_Click: {ex}");
            }
            finally
            {
                btnFetch.Enabled = true;
                btnConnect.Enabled = true;
                btnDisconnect.Enabled = true;
            }
        }

        private void btnClear_Click(object sender, EventArgs e)
        {
            try
            {
                dataGridView1.Rows.Clear();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error while clearing data: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}