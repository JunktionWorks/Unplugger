using System;
using System.Management;
using System.IO;
using System.Threading;
using NAudio.Wave;

class Program
{
    static ManagementEventWatcher? _removeWatcher;

    static string? _audioPath;

    // >>> NUEVO: flag de sesión para impedir solapamientos
    static int _isPlayingSession = 0; // 0 = libre, 1 = reproduciendo

    static void Main()
    {
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Stop(); };

        _audioPath = GetAudioPath();
        if (!File.Exists(_audioPath))
        {
            Console.Error.WriteLine($"No encontré el audio en: {_audioPath}");
            Console.Error.WriteLine("Coloca 'alarm.mp3' junto al .exe o en C:\\ProgramData\\Unplugger\\alarm.mp3");
            // seguimos escuchando eventos igual (hará beep si no hay mp3)
        }
        else
        {
            Console.WriteLine($"Audio listo: {_audioPath}");
        }

        var removeQuery = new WqlEventQuery(
            "__InstanceDeletionEvent",
            new TimeSpan(0, 0, 2),
            "TargetInstance ISA 'Win32_USBControllerDevice'"
        );

        _removeWatcher = new ManagementEventWatcher(removeQuery);
        _removeWatcher.EventArrived += OnUsbRemoved;
        _removeWatcher.Start();

        Console.WriteLine("Escuchando desconexiones USB... Ctrl+C para salir.");
        while (_removeWatcher != null) Thread.Sleep(500);
    }

    static void OnUsbRemoved(object sender, EventArrivedEventArgs e)
    {
        try
        {
            // >>> NUEVO: si ya hay una sesión sonando, ignoramos este evento
            if (Interlocked.Exchange(ref _isPlayingSession, 1) == 1)
                return;

            // Disparamos la sesión en background para no bloquear el watcher
            ThreadPool.QueueUserWorkItem(_ => {
                try { PlayAlertSession(); }
                finally { Interlocked.Exchange(ref _isPlayingSession, 0); }
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERR] {ex.Message}");
            Interlocked.Exchange(ref _isPlayingSession, 0);
        }
    }

    // >>> NUEVO: sesión con límites duros (4 repeticiones o 30s)
    static void PlayAlertSession()
    {
        const int maxRepeats = 4;
        TimeSpan maxTotal = TimeSpan.FromSeconds(30);

        // Si no hay mp3, beep como fallback pero igual respeta límites
        bool hasAudio = _audioPath != null && File.Exists(_audioPath);

        var sessionStart = DateTime.UtcNow;
        int count = 0;

        while (count < maxRepeats && (DateTime.UtcNow - sessionStart) < maxTotal)
        {
            if (!hasAudio)
            {
                Console.Beep(1000, 300);
                count++;
                continue;
            }

            using var audioFile = new AudioFileReader(_audioPath!);
            using var output = new WaveOutEvent();
            output.Init(audioFile);
            output.Play();

            // Espera activa mientras suena, pero corta al cumplir 30s totales
            while (output.PlaybackState == PlaybackState.Playing)
            {
                if ((DateTime.UtcNow - sessionStart) >= maxTotal)
                {
                    // Corte inmediato al llegar a 30s
                    output.Stop();
                    break;
                }
                Thread.Sleep(50);
            }

            count++;
        }
    }

    static string GetAudioPath()
    {
        var exeDir = AppContext.BaseDirectory;
        var local = Path.Combine(exeDir, "alarm.mp3");
        if (File.Exists(local)) return local;

        var shared = @"C:\ProgramData\Unplugger\alarm.mp3";
        Directory.CreateDirectory(Path.GetDirectoryName(shared)!);
        return shared;
    }

    static void Stop()
    {
        _removeWatcher?.Stop();
        _removeWatcher?.Dispose();
        _removeWatcher = null;
        Console.WriteLine("Cerrado.");
    }
}
