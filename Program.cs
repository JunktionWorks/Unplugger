using System;
using System.Management;
using System.IO;
using System.Threading;
using NAudio.Wave;
using NAudio.CoreAudioApi;

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
            ThreadPool.QueueUserWorkItem(_ =>
            {
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

        bool hasAudio = _audioPath != null && File.Exists(_audioPath);

        var sessionStart = DateTime.UtcNow;
        int count = 0;

        // <<< Subimos volumen y guardamos estado original
        var volState = BoostVolumeToMax();

        try
        {
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

                while (output.PlaybackState == PlaybackState.Playing)
                {
                    if ((DateTime.UtcNow - sessionStart) >= maxTotal)
                    {
                        output.Stop(); // corte inmediato a los 30s
                        break;
                    }
                    Thread.Sleep(50);
                }

                count++;
            }
        }
        finally
        {
            // <<< Restauramos el volumen/mute original
            RestoreVolume(volState);
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

    // Guarda/ajusta volumen del dispositivo de salida por defecto (Render, Multimedia)
    static (float prevVolume, bool prevMute) BoostVolumeToMax()
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        float prev = device.AudioEndpointVolume.MasterVolumeLevelScalar; // 0.0 - 1.0
        bool prevMute = device.AudioEndpointVolume.Mute;

        // Subimos a tope y desmuteamos
        device.AudioEndpointVolume.Mute = false;
        device.AudioEndpointVolume.MasterVolumeLevelScalar = 1.0f;
        return (prev, prevMute);
    }

    static void RestoreVolume((float prevVolume, bool prevMute) state)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(state.prevVolume, 0f, 1f);
            device.AudioEndpointVolume.Mute = state.prevMute;
        }
        catch
        {
            // Si falla la restauración, no matamos la app; en aula es mejor sonar que fallar.
        }
    }


    static void Stop()
    {
        _removeWatcher?.Stop();
        _removeWatcher?.Dispose();
        _removeWatcher = null;
        Console.WriteLine("Cerrado.");
    }
}
