using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Clipper.Models;
using Clipper.Services;
using System.IO;
using System;
namespace Clipper;

public partial class MainWindow : Window
{
    private const int HotkeyId = 9001;
    private const int WmHotkey = 0x0312;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly SettingsStore _settings = new();
    private readonly RecorderService _recorder = new();
    private readonly ClipLengthOption[] _clipOptions =
    {
        new("15 Sekunden", 15),
        new("30 Sekunden", 30),
        new("1 Minute", 60),
        new("5 Minuten", 300),
        new("10 Minuten", 600),
        new("15 Minuten", 900),
    };

    private HwndSource? _source;
    private bool _hotkeyRegistered;
    private bool _recording;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _settings.Load();

        DurationComboBox.ItemsSource = _clipOptions;
        DurationComboBox.SelectedIndex = Math.Max(0, Array.FindIndex(_clipOptions, x => x.Seconds == _settings.SelectedClipSeconds));
        DurationComboBox.SelectionChanged += DurationComboBox_SelectionChanged;

        UpdateStatus("Starte Aufnahme ...");

        try
        {
            await _recorder.StartAsync();
            _recording = true;
            ToggleButton.Content = "Stop";
            UpdateStatus($"Aufnahme läuft. Speicherort: {_recorder.OutputDirectory}");
        }
        catch (Exception ex)
        {
            _recording = false;
            ToggleButton.Content = "Start";
            UpdateStatus(ex.Message);
            MessageBox.Show(this, ex.Message, "Clipper", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        _source = HwndSource.FromHwnd(helper.Handle);
        _source?.AddHook(WndProc);

        _hotkeyRegistered = RegisterHotKey(helper.Handle, HotkeyId, 0, 0x77); // F8
        if (!_hotkeyRegistered)
        {
            UpdateStatus("F8 konnte nicht registriert werden. Nutze den Button zum Speichern.");
        }
    }

    private async void DurationComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DurationComboBox.SelectedItem is ClipLengthOption option)
        {
            _settings.SelectedClipSeconds = option.Seconds;
            _settings.Save();
            UpdateStatus($"Clip-Dauer gesetzt auf {option.Display}.");
            await Task.CompletedTask;
        }
    }

    private async void ToggleButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_recording)
            {
                await _recorder.StopAsync();
                _recording = false;
                ToggleButton.Content = "Start";
                UpdateStatus("Aufnahme gestoppt.");
            }
            else
            {
                await _recorder.StartAsync();
                _recording = true;
                ToggleButton.Content = "Stop";
                UpdateStatus("Aufnahme läuft.");
            }
        }
        catch (Exception ex)
        {
            UpdateStatus(ex.Message);
            MessageBox.Show(this, ex.Message, "Clipper", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_recorder.OutputDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = _recorder.OutputDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            UpdateStatus(ex.Message);
            MessageBox.Show(this, ex.Message, "Clipper", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        await Task.CompletedTask;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            _ = CaptureClipAsync();
        }

        return IntPtr.Zero;
    }

    private async Task CaptureClipAsync()
    {
        if (!_recording)
        {
            UpdateStatus("Aufnahme ist gestoppt. Starte sie zuerst wieder.");
            return;
        }

        var seconds = _settings.SelectedClipSeconds;
        try
        {
            UpdateStatus($"Speichere letzten {seconds} Sekunden Clip ...");
            var file = await _recorder.SaveClipAsync(seconds);
            _recording = true;
            ToggleButton.Content = "Stop";
            UpdateStatus($"Clip gespeichert: {file}");
        }
        catch (Exception ex)
        {
            UpdateStatus(ex.Message);
            MessageBox.Show(this, ex.Message, "Clipper", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateStatus(string message)
    {
        StatusText.Text = message;
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            if (_hotkeyRegistered)
            {
                var helper = new WindowInteropHelper(this);
                UnregisterHotKey(helper.Handle, HotkeyId);
            }

            await _recorder.StopAsync();
            _recorder.Dispose();
        }
        catch
        {
            // ignore on shutdown
        }
    }
}
