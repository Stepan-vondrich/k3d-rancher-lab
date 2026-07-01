using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Threading;

// Launcher pro k3d-rancher-lab:
//  - nastartuje Docker Desktop (pokud nebezi)
//  - spusti k3d clustery (staging, prod) + Rancher kontejner
//  - zvedne port-forwardy pro ArgoCD a Grafanu (s auto-restartem)
//  - otevre v prohlizeci vsechny 3 UI
class StartLab
{
    static readonly string Home = Environment.GetEnvironmentVariable("USERPROFILE");
    static readonly string LocalApp = Environment.GetEnvironmentVariable("LOCALAPPDATA");
    static readonly string Docker = @"C:\Program Files\Docker\Docker\resources\bin\docker.exe";
    static readonly string Kubectl = @"C:\Program Files\Docker\Docker\resources\bin\kubectl.exe";
    static readonly string DockerDesktop = @"C:\Program Files\Docker\Docker\Docker Desktop.exe";
    static string KubeStaging { get { return Home + @"\.kube\k3d-lab\staging.yaml"; } }
    static string KubeProd { get { return Home + @"\.kube\k3d-lab\prod.yaml"; } }

    static volatile bool shuttingDown = false;
    static readonly List<Process> children = new List<Process>();

    // ukonci existujici kubectl port-forwardy (aby re-run mel volne porty)
    static void KillStalePortForwards()
    {
        try
        {
            using (var s = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='kubectl.exe'"))
            foreach (ManagementObject mo in s.Get())
            {
                var cl = mo["CommandLine"] as string;
                if (cl != null && cl.Contains("port-forward"))
                    try { Process.GetProcessById(Convert.ToInt32(mo["ProcessId"])).Kill(); } catch { }
            }
        }
        catch { }
    }

    static void Shutdown()
    {
        shuttingDown = true;
        lock (children) foreach (var p in children) try { if (!p.HasExited) p.Kill(); } catch { }
    }

    static string ResolveK3d()
    {
        string links = LocalApp + @"\Microsoft\WinGet\Links\k3d.exe";
        if (File.Exists(links)) return links;
        string pkgs = LocalApp + @"\Microsoft\WinGet\Packages";
        if (Directory.Exists(pkgs))
            foreach (var d in Directory.GetDirectories(pkgs, "k3d.k3d*"))
            {
                string p = Path.Combine(d, "k3d.exe");
                if (File.Exists(p)) return p;
            }
        return "k3d"; // fallback na PATH
    }
    static readonly string K3d = ResolveK3d();

    static void Info(string m) { Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine("  " + m); Console.ResetColor(); }
    static void Ok(string m) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine("  [OK] " + m); Console.ResetColor(); }
    static void Warn(string m) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine("  [!] " + m); Console.ResetColor(); }

    // spusti proces, pocka, vrati exit kod + stdout+stderr
    static int Run(string exe, string args, out string output, int timeoutMs = 180000)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        output = "";
        try
        {
            using (var p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd();
                string e = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return -1; }
                output = (o + e).Trim();
                return p.ExitCode;
            }
        }
        catch (Exception ex) { output = ex.Message; return -2; }
    }

    static bool TcpUp(int port)
    {
        try { using (var c = new TcpClient()) { var ar = c.BeginConnect("127.0.0.1", port, null, null); bool ok = ar.AsyncWaitHandle.WaitOne(1500); if (ok) c.EndConnect(ar); return ok && c.Connected; } }
        catch { return false; }
    }

    static bool HttpsPing(string url)
    {
        try
        {
            ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var r = (HttpWebRequest)WebRequest.Create(url); r.Timeout = 5000; r.AllowAutoRedirect = false;
            using (var resp = (HttpWebResponse)r.GetResponse()) return true;
        }
        catch (WebException we) { return we.Response != null; }
        catch { return false; }
    }

    // port-forward v samostatnem vlakne s auto-restartem
    static void StartPortForward(string name, string kubeconfig, string pfArgs)
    {
        var t = new Thread(() =>
        {
            while (!shuttingDown)
            {
                var psi = new ProcessStartInfo(Kubectl, "--kubeconfig \"" + kubeconfig + "\" " + pfArgs)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                try
                {
                    var p = Process.Start(psi);
                    lock (children) children.Add(p);
                    p.WaitForExit();
                    lock (children) children.Remove(p);
                }
                catch { }
                if (shuttingDown) break;
                Thread.Sleep(1500); // spadl -> restart
            }
        });
        t.IsBackground = true; t.Start();
        Info("port-forward " + name + " nastartovan (auto-restart)");
    }

    static bool WaitFor(string label, Func<bool> check, int seconds)
    {
        for (int i = 0; i < seconds; i++) { if (check()) { Ok(label); return true; } Thread.Sleep(1000); }
        Warn(label + " - timeout (" + seconds + "s)"); return false;
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); Info("otevirám " + url); }
        catch (Exception ex) { Warn("nelze otevrit " + url + ": " + ex.Message); }
    }

    static void Main(string[] args)
    {
        bool open = Array.IndexOf(args, "--no-open") < 0;
        Console.Title = "k3d-rancher-lab launcher";
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("  ===  k3d-rancher-lab  ===");
        Console.ResetColor();
        Console.WriteLine();

        // uklid po predchozim behu + graceful shutdown
        KillStalePortForwards();
        Console.CancelKeyPress += delegate (object s, ConsoleCancelEventArgs e) { Shutdown(); };
        AppDomain.CurrentDomain.ProcessExit += delegate { Shutdown(); };

        // 1) Docker Desktop
        string o;
        if (Run(Docker, "ps", out o, 15000) != 0)
        {
            Info("Docker daemon nebezi - startuji Docker Desktop...");
            try { Process.Start(new ProcessStartInfo(DockerDesktop) { UseShellExecute = true }); } catch (Exception ex) { Warn(ex.Message); }
            WaitFor("Docker daemon", () => Run(Docker, "ps", out o, 8000) == 0, 180);
        }
        else Ok("Docker daemon bezi");

        // 2) k3d clustery
        Info("startuji k3d clustery (staging, prod)...");
        Run(K3d, "cluster start staging prod", out o, 240000);
        Ok("k3d clustery spusteny");

        // 3) Rancher kontejner
        Run(Docker, "start rancher-mgmt", out o, 60000);
        Ok("Rancher kontejner spusten");

        // 4) pockej na Rancher
        WaitFor("Rancher UI (https://localhost:8443)", () => HttpsPing("https://localhost:8443/ping"), 180);

        // 5) port-forwardy
        StartPortForward("ArgoCD", KubeStaging, "-n argocd port-forward svc/argocd-server 8090:443 --address 127.0.0.1");
        StartPortForward("Grafana", KubeProd, "-n monitoring port-forward svc/grafana 3001:3000 --address 127.0.0.1");
        WaitFor("ArgoCD (https://localhost:8090)", () => TcpUp(8090), 150);
        WaitFor("Grafana (http://localhost:3001)", () => TcpUp(3001), 150);

        // 6) otevri prohlizec
        if (open)
        {
            Console.WriteLine();
            OpenUrl("https://localhost:8443");   // Rancher
            Thread.Sleep(700);
            OpenUrl("https://localhost:8090");   // ArgoCD
            Thread.Sleep(700);
            OpenUrl("http://localhost:3001/d/k8s-lab-overview"); // Grafana dashboard
        }

        // 7) info + drz nazivu
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("  UI:");
        Console.ResetColor();
        Console.WriteLine("    Rancher  https://localhost:8443   (admin)");
        Console.WriteLine("    ArgoCD   https://localhost:8090   (admin)");
        Console.WriteLine("    Grafana  http://localhost:3001    (admin / anonymous)");
        Console.WriteLine();
        Warn("Nechte toto okno otevrene - drzi port-forwardy pro ArgoCD a Grafanu.");
        Console.WriteLine("  (Zavrenim okna nebo Ctrl+C se lab UI port-forwardy zastavi; clustery bezi dal.)");

        var block = new ManualResetEvent(false);
        block.WaitOne();
    }
}
