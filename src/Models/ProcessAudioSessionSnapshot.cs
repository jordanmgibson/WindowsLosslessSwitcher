namespace WindowsLosslessSwitcher.Models;

/// <summary>Activity and peak sampled together from a process's audio sessions.</summary>
public readonly record struct ProcessAudioSessionSnapshot(bool? IsActive, float? Peak);
