using System;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Crest.Services;

/// <summary>
/// Speaker volume, a live output peak for the visualizer, and a mute switch that
/// mutes every active microphone in Windows itself (so it works in every app).
/// All COM calls happen on the UI thread; callbacks only carry values across.
/// </summary>
public sealed class AudioService : IMMNotificationClient, IDisposable
{
    private readonly Dispatcher _ui;
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _render;

    public event Action<float, bool>? VolumeChanged;
    public event Action<bool>? MicMuteChanged;

    public AudioService(Dispatcher ui)
    {
        _ui = ui;
        try { _enumerator.RegisterEndpointNotificationCallback(this); } catch { /* audio service missing */ }
        BindRender();
    }

    private void BindRender()
    {
        try
        {
            if (_render != null)
            {
                _render.AudioEndpointVolume.OnVolumeNotification -= OnVolume;
                _render.Dispose();
            }
            _render = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _render.AudioEndpointVolume.OnVolumeNotification += OnVolume;
        }
        catch
        {
            _render = null;
        }
    }

    private void OnVolume(AudioVolumeNotificationData data)
    {
        float v = data.MasterVolume;
        bool muted = data.Muted;
        _ui.BeginInvoke(() => VolumeChanged?.Invoke(v, muted));
    }

    public float Volume
    {
        get
        {
            try { return _render?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0; }
            catch { return 0; }
        }
        set
        {
            try
            {
                if (_render == null) return;
                _render.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f);
                if (value > 0 && _render.AudioEndpointVolume.Mute) _render.AudioEndpointVolume.Mute = false;
            }
            catch { /* device went away */ }
        }
    }

    public bool IsOutputMuted
    {
        get
        {
            try { return _render?.AudioEndpointVolume.Mute ?? false; }
            catch { return false; }
        }
        set
        {
            try { if (_render != null) _render.AudioEndpointVolume.Mute = value; }
            catch { /* device went away */ }
        }
    }

    public bool ToggleOutputMute() => IsOutputMuted = !IsOutputMuted;

    public float Peak
    {
        get
        {
            try { return _render?.AudioMeterInformation.MasterPeakValue ?? 0; }
            catch { return 0; }
        }
    }

    public bool IsMicMuted
    {
        get
        {
            try
            {
                bool any = false;
                foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    any = true;
                    if (!d.AudioEndpointVolume.Mute) return false;
                }
                return any;
            }
            catch { return false; }
        }
    }

    public bool HasMicrophone
    {
        get
        {
            try { return _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Count > 0; }
            catch { return false; }
        }
    }

    /// <returns>The new muted state.</returns>
    public bool ToggleMic()
    {
        bool target = !IsMicMuted;
        try
        {
            foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                d.AudioEndpointVolume.Mute = target;
        }
        catch { /* ignore */ }
        MicMuteChanged?.Invoke(target);
        return target;
    }

    // IMMNotificationClient
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia)
            _ui.BeginInvoke(BindRender);
        if (flow == DataFlow.Capture)
            _ui.BeginInvoke(() => MicMuteChanged?.Invoke(IsMicMuted));
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        if (_render != null)
        {
            try { _render.AudioEndpointVolume.OnVolumeNotification -= OnVolume; } catch { }
            _render.Dispose();
        }
        _enumerator.Dispose();
    }
}
