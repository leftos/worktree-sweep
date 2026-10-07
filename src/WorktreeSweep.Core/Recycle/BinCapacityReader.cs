using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using Windows.Win32;

namespace WorktreeSweep.Recycle;

/// <summary>Reads a volume's Recycle Bin settings from <c>HKCU\...\Explorer\BitBucket\Volume\{GUID}</c>.</summary>
public static class BinCapacityReader
{
    private const string BitBucketVolumes = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume";

    /// <summary>The mount point buffer, in characters; a mount point is a drive root or a folder path.</summary>
    private const int MountPointLength = 1024;

    /// <summary>The volume name buffer, in characters; <c>\\?\Volume{GUID}\</c> is 49.</summary>
    private const int VolumeNameLength = 128;

    /// <summary>Reads the Recycle Bin settings of the volume <paramref name="path"/> is on.</summary>
    /// <param name="path">A path on the volume.</param>
    /// <returns>
    /// The settings; <see langword="null"/> when the volume's key, <c>MaxCapacity</c> or <c>NukeOnDelete</c> is missing, or either
    /// value is not a DWORD.
    /// </returns>
    /// <exception cref="IOException">
    /// The only exception it throws, naming <paramref name="path"/> and the step that failed: the volume cannot be found or named, its
    /// name holds no <c>{GUID}</c>, or the key or a value cannot be read. The inner exception, when there is one, is the
    /// <see cref="Win32Exception"/>, <see cref="SecurityException"/>, <see cref="UnauthorizedAccessException"/> or
    /// <see cref="IOException"/> that step threw.
    /// </exception>
    public static BinCapacity? Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string keyName = $@"{BitBucketVolumes}\{GuidOf(VolumeName(path), path)}";
        using RegistryKey? volume = OpenKey(keyName, path);
        Func<string, object?>? readValue = volume is null ? null : name => ReadValue(volume, name, path);
        return FromValues($@"{Registry.CurrentUser.Name}\{keyName}", readValue);
    }

    /// <summary>The <c>{GUID}</c> in a volume name such as <c>\\?\Volume{GUID}\</c>.</summary>
    /// <param name="volumeName">The volume's name.</param>
    /// <param name="path">The path on the volume, for the error message.</param>
    /// <returns>The GUID with its braces.</returns>
    /// <exception cref="IOException"><paramref name="volumeName"/> holds no <c>{GUID}</c>.</exception>
    internal static string GuidOf(string volumeName, string path)
    {
        int start = volumeName.IndexOf('{', StringComparison.Ordinal);
        int end = volumeName.IndexOf('}', StringComparison.Ordinal);
        return start >= 0 && end > start
            ? volumeName[start..(end + 1)]
            : throw new IOException($"volume name {volumeName} of {path} has no {{GUID}}");
    }

    /// <summary>The settings held in a volume's key.</summary>
    /// <param name="keyName">The key's full name, for the warning a value of another type gives.</param>
    /// <param name="readValue">
    /// Reads a value of the key by name, giving <see langword="null"/> when the value is missing; itself <see langword="null"/> when
    /// the key is missing.
    /// </param>
    /// <returns>
    /// The settings; <see langword="null"/> when the key, <c>MaxCapacity</c> or <c>NukeOnDelete</c> is missing, or either value is not
    /// a DWORD.
    /// </returns>
    internal static BinCapacity? FromValues(string keyName, Func<string, object?>? readValue)
    {
        if (readValue is null)
        {
            return null;
        }
        uint? maxCapacity = ReadDword(keyName, readValue, "MaxCapacity");
        uint? nukeOnDelete = ReadDword(keyName, readValue, "NukeOnDelete");
        return maxCapacity is null || nukeOnDelete is null ? null : new BinCapacity(maxCapacity.Value, nukeOnDelete.Value != 0);
    }

    /// <summary>The name of the volume <paramref name="path"/> is on, such as <c>\\?\Volume{GUID}\</c>.</summary>
    private static string VolumeName(string path)
    {
        Span<char> mountPointBuffer = stackalloc char[MountPointLength];
        if (!PInvoke.GetVolumePathName(path, mountPointBuffer))
        {
            throw Win32Failure($"cannot find the volume of {path}");
        }
        string mountPoint = UpToNul(mountPointBuffer);
        Span<char> volumeBuffer = stackalloc char[VolumeNameLength];
        if (!PInvoke.GetVolumeNameForVolumeMountPoint(mountPoint, volumeBuffer))
        {
            throw Win32Failure($"cannot name the volume mounted at {mountPoint}, the volume of {path}");
        }
        return UpToNul(volumeBuffer);
    }

    /// <summary>An <see cref="IOException"/> for the Win32 call that just failed, holding its error as the inner exception.</summary>
    private static IOException Win32Failure(string message)
    {
        var error = new Win32Exception(Marshal.GetLastPInvokeError());
        return new IOException($"{message}: {error.Message}", error);
    }

    /// <summary>Opens <c>HKCU\</c><paramref name="keyName"/> to read; <see langword="null"/> when it is missing.</summary>
    private static RegistryKey? OpenKey(string keyName, string path)
    {
        try
        {
            return Registry.CurrentUser.OpenSubKey(keyName);
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException)
        {
            throw new IOException($@"cannot open HKCU\{keyName}, the Recycle Bin settings of {path}: {error.Message}", error);
        }
    }

    /// <summary>A value of <paramref name="key"/>; <see langword="null"/> when it is missing.</summary>
    private static object? ReadValue(RegistryKey key, string name, string path)
    {
        try
        {
            return key.GetValue(name);
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException)
        {
            throw new IOException($@"cannot read {key.Name}\{name}, a Recycle Bin setting of {path}: {error.Message}", error);
        }
    }

    /// <summary>
    /// A <c>REG_DWORD</c>; <see langword="null"/> when the value is missing, or of another type, which is traced as a warning.
    /// </summary>
    private static uint? ReadDword(string keyName, Func<string, object?> readValue, string name)
    {
        object? value = readValue(name);
        if (value is int dword)
        {
            return unchecked((uint)dword);
        }
        if (value is not null)
        {
            Trace.TraceWarning($"{keyName}\\{name} is not a DWORD; treating the Recycle Bin size as unknown");
        }
        return null;
    }

    private static string UpToNul(ReadOnlySpan<char> buffer)
    {
        int end = buffer.IndexOf('\0');
        return new string(end < 0 ? buffer : buffer[..end]);
    }
}
