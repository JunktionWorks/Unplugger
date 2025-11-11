using System;
using System.Linq;
using System.Management;
using System.IO;
using System.Threading;
using NAudio.Wave;

class Program
{
    static ManagementEventWatcher? _removeWatcher;

    // Reproductor global
    static string? _audioPath;
    static DateTime _lastPlayed = DateTime.MinValue;
    static readonly TimeSpan _cooldown = TimeSpan.FromSeconds(2);

    static void Main()
    {
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Stop(); };

        _audioPath = GetAudioPath();
        if (!File.Exists(_audioPath))
        {
            Console.Error.WriteLine($"No encontré el audio en: {_audioPath}");
            Console.Error.WriteLine("Coloca 'alarm.mp3' junto al .exe o en C:\\ProgramData\\Unplugger\\alarm.mp3");
        }
        else
        {
            Console.WriteLine($"Audio cargado: {_audioPath}");
        }

        // Suscripción WMI a remociones USB
        var removeQuery = new WqlEventQuery(
            "__InstanceDeletionEvent",
            new TimeSpan(0, 0, 2),
            "TargetInstance ISA 'Win32_USBControllerDevice'"
        );

        _removeWatcher = new ManagementEventWatcher(removeQuery);
        _removeWatcher.EventArrived += OnUsbRemoved;
        _removeWatcher.Start();

        Console.WriteLine("Escuchando desconexiones USB... Ctrl+C para salir.");
        while (_removeWatcher != null)
        {
            Thread.Sleep(500);
        }
    }

    static void OnUsbRemoved(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (now - _lastPlayed < _cooldown)
                return;

            _lastPlayed = now;
            PlayAlert();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERR] {ex.Message}");
        }
    }

    static void PlayAlert()
    {
        if (_audioPath == null || !File.Exists(_audioPath))
        {
            Console.Beep(1000, 300);
            return;
        }

        try
        {
            using var audioFile = new AudioFileReader(_audioPath);
            using var outputDevice = new WaveOutEvent();
            outputDevice.Init(audioFile);
            outputDevice.Play();

            // Esperar hasta que termine (bloqueante)
            while (outputDevice.PlaybackState == PlaybackState.Playing)
            {
                Thread.Sleep(100);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AUDIO] {ex.Message}");
            Console.Beep(1000, 300);
        }
    }

    static string GetAudioPath()
    {
        // 1) Junto al ejecutable
        var exeDir = AppContext.BaseDirectory;
        var local = Path.Combine(exeDir, "alarm.mp3");
        if (File.Exists(local)) return local;

        // 2) Carpeta de datos compartidos
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
